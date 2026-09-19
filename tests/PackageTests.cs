using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using MoyuWord.Setup;

internal static class PackageTests
{
    private static string root;
    private static int passed;
    private const string ManifestName = ".moyu-install.json";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "MoyuWord-package-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("valid paths resolve within an isolated installation", ValidPaths);
            Run("unsafe relative paths and broad targets are rejected", UnsafePaths);
            Run("uninstall preserves unrelated files, directories and user data", OwnedFilesOnly);
            Run("tampered ownership record is rejected before any deletion", TamperedManifest);
            Run("atomic manifest replacement preserves existing reader snapshot", AtomicReaderSnapshot);
            Run("failed manifest replacement preserves old bytes and removes staged file", FailedAtomicWrite);
            Run("failed first manifest publish preserves an existing directory", FailedFirstPublish);
            Console.WriteLine("PASS: " + passed + " packaging tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            string expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\MoyuWord-package-tests-";
            if (Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Folder()
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static object InvokeStatic(string name, params object[] args)
    {
        MethodInfo method = typeof(Engine).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Check(method != null, "Missing installer atomic-write operation: " + name);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected rejection: " + typeof(T).Name);
    }
    private static void WriteAtomic(string path, string text)
    { InvokeStatic("WriteManifestAtomically", path, text); }
    private static void ValidPaths()
    {
        string folder = Folder();
        Check(Engine.ValidateDirectory(folder) == folder, "Valid target changed unexpectedly");
        string resolved = (string)InvokeStatic("Resolve", folder, @"assets\中文词库.json");
        Check(resolved == Path.Combine(folder, "assets", "中文词库.json"), "Nested Unicode asset resolution failed");
    }
    private static void UnsafePaths()
    {
        string folder = Folder();
        foreach (string unsafePath in new[] { @"..\outside.txt", @"assets\..\outside.txt", @"C:\outside.txt",
            @"\\server\share\outside.txt", @"assets\file:stream", @"assets\bad.", @"assets\\missing-segment", "" })
        {
            string captured = unsafePath;
            Reject<InvalidDataException>(delegate { InvokeStatic("Resolve", folder, captured); });
        }
        foreach (string broad in new[] { Path.GetPathRoot(root), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            string captured = broad;
            Reject<ArgumentException>(delegate { Engine.ValidateDirectory(captured); });
        }
    }
    private static InstallManifest Fixture(string folder)
    {
        InstallManifest manifest = new InstallManifest {
            Product = "MoyuWord", InstallDirectory = folder,
            Files = new List<string> { "MoyuWord.exe", "Uninstall.exe", @"owned\payload.dll" }
        };
        foreach (string relative in manifest.Files)
        {
            string path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "owned fixture", Utf8);
        }
        SaveFixture(folder, manifest);
        return manifest;
    }
    private static void SaveFixture(string folder, InstallManifest manifest)
    { File.WriteAllText(Path.Combine(folder, ManifestName), new JavaScriptSerializer().Serialize(manifest), Utf8); }
    private static Engine Uninstaller(string folder)
    {
        return new Engine(new Options { InstallDirectory = folder, Silent = true, Uninstall = true, NoShortcuts = true, NoRegister = true });
    }
    private static void OwnedFilesOnly()
    {
        string folder = Folder();
        InstallManifest manifest = Fixture(folder);
        string unrelated = Path.Combine(folder, "personal-note.txt");
        File.WriteAllText(unrelated, "keep this note", Utf8);
        string emptyDirectory = Path.Combine(folder, "unrelated-empty-directory");
        Directory.CreateDirectory(emptyDirectory);
        string userData = Path.Combine(Folder(), "favorites.json");
        File.WriteAllText(userData, "personal learning data", Utf8);
        Uninstaller(folder).Uninstall();
        foreach (string owned in manifest.Files) Check(!File.Exists(Path.Combine(folder, owned)), "Owned file was not removed: " + owned);
        Check(!File.Exists(Path.Combine(folder, ManifestName)), "Ownership manifest survived completed removal");
        Check(!Directory.Exists(Path.Combine(folder, "owned")), "Owned empty directory was not removed");
        Check(File.ReadAllText(unrelated, Utf8) == "keep this note", "Unrelated file was changed");
        Check(Directory.Exists(emptyDirectory), "Unrelated empty directory was removed");
        Check(File.ReadAllText(userData, Utf8) == "personal learning data", "User data was changed");
    }
    private static void TamperedManifest()
    {
        string folder = Folder();
        InstallManifest manifest = Fixture(folder);
        string outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "outside owned directory", Utf8);
        manifest.Files.Add(@"..\outside.txt");
        SaveFixture(folder, manifest);
        Reject<InvalidDataException>(delegate { Uninstaller(folder).Uninstall(); });
        Check(File.ReadAllText(outside, Utf8) == "outside owned directory", "Traversal deleted an outside file");
        Check(File.Exists(Path.Combine(folder, "MoyuWord.exe")), "Uninstall changed files before validating all ownership entries");
        manifest.Files.Remove(@"..\outside.txt");
        manifest.InstallDirectory = Folder();
        SaveFixture(folder, manifest);
        Reject<InvalidDataException>(delegate { Uninstaller(folder).Uninstall(); });
        Check(File.Exists(Path.Combine(folder, "MoyuWord.exe")), "Mismatched ownership directory was accepted");
    }
    private static void AtomicReaderSnapshot()
    {
        string folder = Folder();
        string path = Path.Combine(folder, ManifestName);
        string original = "{\"Product\":\"MoyuWord\",\"Old\":true}";
        string replacement = "{\"Product\":\"MoyuWord\",\"Name\":\"摸鱼单词\",\"Old\":false}";
        WriteAtomic(path, original);
        using (FileStream existingReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            // File.WriteAllText cannot pass this case: this handle allows replacement but forbids in-place writes.
            WriteAtomic(path, replacement);
            using (StreamReader reader = new StreamReader(existingReader, Utf8))
                Check(reader.ReadToEnd() == original, "An existing reader observed a partially rewritten manifest");
            Check(File.ReadAllText(path, Utf8) == replacement, "New readers did not observe the full replacement manifest");
        }
        Check(Directory.GetFiles(folder).Length == 1, "Successful atomic publish leaked temporary files");
    }
    private static void FailedAtomicWrite()
    {
        string folder = Folder();
        string path = Path.Combine(folder, ManifestName);
        string original = "{\"Product\":\"MoyuWord\",\"Files\":[\"keep.exe\"]}";
        File.WriteAllText(path, original, Utf8);
        // No FileShare.Delete: stage writing succeeds, but Windows must reject the replacement.
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject<IOException>(delegate { WriteAtomic(path, new string('x', 128 * 1024)); });
        Check(File.ReadAllText(path, Utf8) == original, "Failed publish truncated or changed the previous ownership manifest");
        Check(Directory.GetFiles(folder).Length == 1, "Failed publish left a staged manifest behind");
    }
    private static void FailedFirstPublish()
    {
        string folder = Folder();
        string path = Path.Combine(folder, ManifestName);
        Directory.CreateDirectory(path);
        string unrelated = Path.Combine(path, "keep.txt");
        File.WriteAllText(unrelated, "keep", Utf8);
        Reject<IOException>(delegate { WriteAtomic(path, "new manifest"); });
        Check(File.ReadAllText(unrelated, Utf8) == "keep", "Failed first publish removed an unrelated directory");
        Check(Directory.GetFiles(folder).Length == 0, "Failed first publish leaked a temporary file");
    }
}
