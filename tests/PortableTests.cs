using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using MoyuWord;

internal static class PortableTests
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source)) CopyFolder(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private static void RunCopy(string executable, string scenario, string workingDirectory)
    {
        using (var child = Process.Start(new ProcessStartInfo(executable, scenario) { UseShellExecute = false, WorkingDirectory = workingDirectory }))
        { child.WaitForExit(); Check(child.ExitCode == 0, "copied executable " + scenario + " with unrelated working directory"); }
    }
    public static int Main(string[] args)
    {
        string root = null;
        try
        {
            if (args.Length > 0) return Worker(args[0]);
            root = Path.Combine(Path.GetTempPath(), "MoyuPortableTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string first = Path.Combine(root, "usb-a"); Directory.CreateDirectory(first);
            string executable = Path.Combine(first, "PortableTests.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, executable);
            Directory.CreateDirectory(Path.Combine(first, "assets"));
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "tem4.json"), Path.Combine(first, "assets", "tem4.json"));
            File.WriteAllText(Path.Combine(first, "portable.flag"), "portable data follows this folder");
            RunCopy(executable, "write", root);
            Check(File.Exists(Path.Combine(first, "data", "favorites.json")), "favorites saved beside portable exe");
            Check(File.Exists(Path.Combine(first, "data", "settings.json")), "settings saved beside portable exe");
            Check(Directory.GetFiles(Path.Combine(first, "data", "libraries"), "*.json").Length == 1, "imported library saved beside portable exe");
            string second = Path.Combine(root, "另一台电脑", "MoyuWord-x86"); CopyFolder(first, second);
            RunCopy(Path.Combine(second, "PortableTests.exe"), "read", root);
            var installed = new LibraryStore();
            Check(installed.DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoyuWord"), "unmarked installed app keeps original local data path");
            var key = typeof(LibraryStore).GetProperty("InstanceKey");
            Check(key != null, "instance identity is associated with data directory");
            var one = new LibraryStore(Path.Combine(root, "one")); var two = new LibraryStore(Path.Combine(root, "two"));
            Check((string)key.GetValue(one, null) != (string)key.GetValue(two, null), "portable and installed stores can run independently");
            var same = new LibraryStore(Path.Combine(root, "ONE") + Path.DirectorySeparatorChar);
            Check((string)key.GetValue(one, null) == (string)key.GetValue(same, null), "same data path has one identity despite case and trailing slash");
            Console.WriteLine("PASS: " + checks + " portable parent checks plus worker assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { if (root != null && Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static int Worker(string mode)
    {
        string appBase = AppDomain.CurrentDomain.BaseDirectory;
        var store = new LibraryStore();
        Check(store.DataDirectory == Path.Combine(appBase, "data"), "portable marker resolves relative to executable, not current directory or profile");
        var explicitStore = new LibraryStore(Path.Combine(appBase, "explicit"));
        Check(explicitStore.DataDirectory == Path.Combine(appBase, "explicit"), "explicit data directory override still honored");
        store.Load();
        if (mode == "write")
        {
            store.Settings.BackgroundOpacity = .43; store.Settings.TextOpacity = .67; store.SaveSettings();
            store.ToggleFavorite(store.Libraries[0].Words[0]);
            string csv = Path.Combine(appBase, "sample.csv"); File.WriteAllText(csv, "english,chinese\npause,暂停\n", new UTF8Encoding(false));
            store.Import(csv); File.Delete(csv);
        }
        else
        {
            Check(store.Favorites.Count == 1 && store.Favorites[0].English == "abandon", "favorites travel with copied folder");
            Check(store.Libraries.Count == 2 && store.Libraries[1].Words[0].English == "pause", "import travels with copied folder");
            Check(store.Settings.BackgroundOpacity == .43 && store.Settings.TextOpacity == .67, "settings travel with copied folder");
        }
        return 0;
    }
}
