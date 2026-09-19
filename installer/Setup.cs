using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MoyuWord.Setup
{
    internal sealed class Options
    {
        internal bool Silent, Uninstall, Worker, NoShortcuts, NoRegister;
        internal int WaitPid;
        internal string InstallDirectory;
        internal static Options Parse(string[] args)
        {
            Options result = new Options();
#if UNINSTALLER
            result.Uninstall = true;
#endif
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                if (arg == "/silent") result.Silent = true;
                else if (arg == "/uninstall") result.Uninstall = true;
                else if (arg == "/worker") result.Worker = true;
                else if (arg == "/no-shortcuts") result.NoShortcuts = true;
                else if (arg == "/no-register") result.NoRegister = true;
                else if (arg == "/install-dir" && i + 1 < args.Length) result.InstallDirectory = args[++i];
                else if (arg == "/wait-pid" && i + 1 < args.Length) result.WaitPid = Int32.Parse(args[++i]);
                else throw new ArgumentException("无法识别参数：" + args[i]);
            }
            if (String.IsNullOrEmpty(result.InstallDirectory))
                result.InstallDirectory = result.Uninstall ? AppDomain.CurrentDomain.BaseDirectory :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MoyuWord");
            result.InstallDirectory = Engine.ValidateDirectory(result.InstallDirectory);
            return result;
        }
    }

    internal static class Program
    {
        internal static int Result;
        [STAThread]
        private static int Main(string[] args)
        {
            Options options = null;
            try
            {
                options = Options.Parse(args);
                if (options.Worker && options.WaitPid > 0)
                {
                    try { using (Process parent = Process.GetProcessById(options.WaitPid)) parent.WaitForExit(30000); }
                    catch (ArgumentException) { }
                }
                if (options.Uninstall && !options.Worker && Engine.IsInside(options.InstallDirectory, Application.ExecutablePath))
                {
                    // Run from TEMP so Windows can release and remove the installed uninstaller.
                    string helper = Path.Combine(Path.GetTempPath(), "MoyuWord-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Application.ExecutablePath, helper);
                    string forwarded = "/uninstall /worker /wait-pid " + Process.GetCurrentProcess().Id +
                        " /install-dir " + Quote(options.InstallDirectory) + (options.Silent ? " /silent" : "") +
                        (options.NoRegister ? " /no-register" : "") + (options.NoShortcuts ? " /no-shortcuts" : "");
                    Process.Start(new ProcessStartInfo(helper, forwarded) { UseShellExecute = false });
                    return 0;
                }
                bool mutexCreated;
                using (Mutex mutex = new Mutex(true, "Local\\MoyuWord.Setup", out mutexCreated))
                {
                    if (!mutexCreated) throw new InvalidOperationException("另一个摸鱼单词安装或卸载程序正在运行，请稍后重试。");
                    try
                    {
                        if (options.Silent)
                        {
                            Engine engine = new Engine(options);
                            if (options.Uninstall) engine.Uninstall(); else engine.Install();
                            return 0;
                        }
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new SetupForm(options));
                        return Result;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                if (!args.Any(a => String.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)))
                    MessageBox.Show(ex.Message, "摸鱼单词", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                // Best effort only: a temporary helper may remain for Windows' normal TEMP cleanup.
                if (options != null && options.Worker) NativeMethods.MoveFileEx(Application.ExecutablePath, null, 4);
            }
        }
        internal static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("路径包含无效的引号。");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
        internal static void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "MoyuWord-setup.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    internal sealed class InstallManifest
    {
        public string Product { get; set; }
        public string InstallDirectory { get; set; }
        public List<string> Files { get; set; }
        public bool DesktopShortcut { get; set; }
        public bool StartMenuShortcut { get; set; }
        public bool Registered { get; set; }
    }

    internal sealed class Engine
    {
        private const string ManifestName = ".moyu-install.json";
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MoyuWord";
        private readonly Options options;
        private readonly string target;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        internal Engine(Options options) { this.options = options; target = options.InstallDirectory; }

        internal void Install()
        {
            using (RegistryKey framework = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                if (framework == null || Convert.ToInt32(framework.GetValue("Release", 0)) < 393295)
                    throw new InvalidOperationException("需要 Windows 10 自带的 .NET Framework 4.6 或更高版本。请通过 Windows 功能启用它；安装程序不会联网下载组件。");
            }
            CheckRunning();
            InstallManifest old = ReadManifest(false);
            if (Directory.Exists(target) && old == null && Directory.EnumerateFileSystemEntries(target).Any())
                throw new IOException("安装目录不是空文件夹，也不是由本安装程序管理的目录，请选择其他位置。\n" + target);
            string work = Path.Combine(Path.GetTempPath(), "MoyuWord-Install-" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(work, "package");
            string backup = Path.Combine(work, "backup");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(backup);
            List<string> newFiles = new List<string>();
            List<string> touched = new List<string>();
            bool committed = false;
            bool targetExisted = Directory.Exists(target);
            try
            {
                using (Stream package = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoyuWord.Package.zip"))
                {
                    if (package == null) throw new InvalidOperationException("安装包不包含应用文件，请使用完整的 MoyuWord-Setup-x86.exe。");
                    using (ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (String.IsNullOrEmpty(entry.Name)) continue;
                            string relative = entry.FullName.Replace('/', '\\');
                            string destination = Resolve(stage, relative);
                            if (relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || newFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                                throw new InvalidDataException("安装包中存在重复或保留的文件名。");
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            using (Stream input = entry.Open()) using (FileStream output = File.Create(destination)) input.CopyTo(output);
                            newFiles.Add(relative);
                        }
                    }
                }
                foreach (string required in new[] { "MoyuWord.exe", "MoyuWord.exe.config", "Uninstall.exe", @"native\pdfium.dll", @"assets\tem4.json" })
                    if (!newFiles.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("安装包缺少文件：" + required);
                List<string> managed = old == null ? new List<string>() : old.Files;
                foreach (string relative in newFiles.Concat(new[] { ManifestName }))
                {
                    string destination = Resolve(target, relative);
                    if (File.Exists(destination))
                    {
                        if (relative != ManifestName && !managed.Contains(relative, StringComparer.OrdinalIgnoreCase))
                            throw new IOException("安装将覆盖未由本程序创建的文件，已停止：" + destination);
                        using (File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        string saved = Resolve(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        File.Copy(destination, saved);
                    }
                }
                Directory.CreateDirectory(target);
                foreach (string relative in newFiles)
                {
                    string destination = Resolve(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    touched.Add(relative);
                    File.Copy(Resolve(stage, relative), destination, true);
                }
                InstallManifest manifest = new InstallManifest {
                    Product = "MoyuWord", InstallDirectory = target,
                    Files = managed.Concat(newFiles).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList(),
                    DesktopShortcut = old != null && old.DesktopShortcut,
                    StartMenuShortcut = old != null && old.StartMenuShortcut,
                    Registered = old != null && old.Registered
                };
                // Commit the file ownership list before optional shell integration.
                touched.Add(ManifestName);
                WriteManifestAtomically(Path.Combine(target, ManifestName), json.Serialize(manifest));
                committed = true;
                if (!options.NoShortcuts)
                {
                    TryOptional(delegate { manifest.DesktopShortcut = CreateShortcut(DesktopLink); });
                    TryOptional(delegate { manifest.StartMenuShortcut = CreateShortcut(StartMenuLink); });
                }
                if (!options.NoRegister) TryOptional(delegate { Register(); manifest.Registered = true; });
                WriteManifestAtomically(Path.Combine(target, ManifestName), json.Serialize(manifest));
                Program.Log("Installed: " + target);
            }
            catch
            {
                if (!committed)
                {
                    foreach (string relative in touched.AsEnumerable().Reverse())
                    {
                        try
                        {
                            string destination = Resolve(target, relative);
                            string saved = Resolve(backup, relative);
                            if (File.Exists(saved)) File.Copy(saved, destination, true);
                            else if (File.Exists(destination)) File.Delete(destination);
                        }
                        catch (Exception rollbackError) { Program.Log("Rollback: " + rollbackError.Message); }
                    }
                    if (!targetExisted) RemoveEmptyDirectories(target, touched);
                }
                throw;
            }
            finally { DeleteOwnedTree(work, Path.GetTempPath(), "MoyuWord-Install-"); }
        }

        internal void Uninstall()
        {
            InstallManifest manifest = ReadManifest(true);
            CheckRunning();
            // Preflight every owned file before deleting any of them.
            foreach (string relative in manifest.Files)
            {
                string file = Resolve(target, relative);
                if (File.Exists(file)) using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            foreach (string relative in manifest.Files)
            {
                string file = Resolve(target, relative);
                if (File.Exists(file)) File.Delete(file);
            }
            if (!options.NoShortcuts)
            {
                if (manifest.DesktopShortcut) TryOptional(delegate { DeleteShortcut(DesktopLink); });
                if (manifest.StartMenuShortcut) TryOptional(delegate { DeleteShortcut(StartMenuLink); });
            }
            if (manifest.Registered && !options.NoRegister)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    string registered = key == null ? null : key.GetValue("InstallLocation") as string;
                    if (String.Equals(registered, target, StringComparison.OrdinalIgnoreCase))
                    { key.Close(); Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false); }
                }
            }
            File.Delete(Path.Combine(target, ManifestName));
            RemoveEmptyDirectories(target, manifest.Files);
            Program.Log("Uninstalled; user data preserved: " + target);
        }

        private static void WriteManifestAtomically(string path, string content)
        {
            string destination = Path.GetFullPath(path);
            RejectReparse(destination);
            // A sibling file guarantees a same-volume rename. The ownership record remains
            // readable until all replacement bytes have reached the disk and the rename succeeds.
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                // Only this invocation's generated sibling is eligible for cleanup.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { Program.Log("Manifest temporary cleanup: " + ex.Message); }
            }
        }

        private InstallManifest ReadManifest(bool required)
        {
            string path = Resolve(target, ManifestName);
            if (!File.Exists(path))
            {
                if (required) throw new IOException("找不到此目录的安装记录，未删除任何文件。\n" + target);
                return null;
            }
            InstallManifest manifest = json.Deserialize<InstallManifest>(File.ReadAllText(path, Encoding.UTF8));
            if (manifest == null || manifest.Product != "MoyuWord" || manifest.Files == null ||
                !String.Equals(manifest.InstallDirectory, target, StringComparison.OrdinalIgnoreCase) ||
                !manifest.Files.Contains("MoyuWord.exe", StringComparer.OrdinalIgnoreCase) ||
                !manifest.Files.Contains("Uninstall.exe", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("安装记录无效，未更改任何文件。");
            foreach (string relative in manifest.Files)
            {
                Resolve(target, relative);
                if (relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装记录包含保留路径。");
            }
            return manifest;
        }

        private void CheckRunning()
        {
            foreach (Process process in Process.GetProcessesByName("MoyuWord"))
            {
                using (process)
                {
                    try
                    {
                        if (IsInside(target, process.MainModule.FileName))
                            throw new IOException("摸鱼单词正在运行，请先关闭应用后重试。安装程序不会强制结束进程。");
                    }
                    catch (System.ComponentModel.Win32Exception)
                    { throw new IOException("无法确认正在运行的摸鱼单词位置，请先关闭该应用后重试。"); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        internal static string ValidateDirectory(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length < 4 || Directory.GetParent(full) == null || full.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("请选择本机磁盘中的独立安装文件夹。");
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
            {
                string broad = Environment.GetFolderPath(folder).TrimEnd('\\');
                if (String.Equals(full, broad, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能使用系统或用户主文件夹作为安装目录。");
            }
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            if (String.Equals(full, programs, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请在 Programs 内选择独立的应用文件夹。");
            RejectReparse(full);
            return full;
        }
        internal static bool IsInside(string directory, string path)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        private static string Resolve(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("文件路径不安全。");
            foreach (string part in relative.Replace('/', '\\').Split('\\'))
                if (part.Length == 0 || part == "." || part == ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("文件路径不安全：" + relative);
            string result = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsInside(root, result)) throw new InvalidDataException("文件超出安装目录。");
            RejectReparse(result);
            return result;
        }
        private static void RejectReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("安装路径不能包含符号链接或目录联接：" + current);
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
        }
        private static void RemoveEmptyDirectories(string directory, IEnumerable<string> ownedFiles)
        {
            if (!Directory.Exists(directory)) return;
            RejectReparse(directory);
            HashSet<string> ownedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in ownedFiles)
            {
                string parent = Path.GetDirectoryName(Resolve(directory, relative));
                while (IsInside(directory, parent))
                {
                    ownedDirectories.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }
            foreach (string child in ownedDirectories.OrderByDescending(p => p.Length))
                if (Directory.Exists(child) && !Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child, false);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
        private static void DeleteOwnedTree(string directory, string parent, string prefix)
        {
            try
            {
                if (!IsInside(parent, directory) || !Path.GetFileName(directory).StartsWith(prefix, StringComparison.Ordinal))
                    throw new IOException("Refused unsafe cleanup.");
                if (Directory.Exists(directory)) { RejectReparse(directory); Directory.Delete(directory, true); }
            }
            catch (Exception ex) { Program.Log("Temporary cleanup: " + ex.Message); }
        }
        private string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "摸鱼单词.lnk"); } }
        private string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "摸鱼单词.lnk"); } }
        private bool CreateShortcut(string path)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object shortcut = null;
            try
            {
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type type = shortcut.GetType();
                string existing = type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
                if (File.Exists(path) && !String.Equals(existing, Path.Combine(target, "MoyuWord.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("同名快捷方式已存在，已保留：" + path);
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Path.Combine(target, "MoyuWord.exe") });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "摸鱼单词 · 离线单词卡与 PDF 阅读" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return true;
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
        private void DeleteShortcut(string path)
        {
            if (!File.Exists(path)) return;
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object shortcut = null;
            try
            {
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                string linked = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
                if (String.Equals(linked, Path.Combine(target, "MoyuWord.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
        private void Register()
        {
            using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(RegistryPath))
            {
                string location = existing == null ? null : existing.GetValue("InstallLocation") as string;
                if (!String.IsNullOrEmpty(location) && !String.Equals(location, target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("另一个安装位置已有卸载登记，已保留原登记。测试安装请使用 /no-register。");
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue("DisplayName", "摸鱼单词"); key.SetValue("DisplayVersion", "1.0.2");
                key.SetValue("Publisher", "MoyuWord"); key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", Path.Combine(target, "MoyuWord.exe"));
                string command = Program.Quote(Path.Combine(target, "Uninstall.exe"));
                key.SetValue("UninstallString", command);
                key.SetValue("QuietUninstallString", command + " /silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
            }
        }
        private static void TryOptional(Action action)
        { try { action(); } catch (Exception ex) { Program.Log("Shell integration warning: " + ex.Message); } }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Options options;
        private readonly Label status;
        private readonly Button action;
        private readonly Button cancel;
        private readonly CheckBox launch;
        private bool complete;
        internal SetupForm(Options options)
        {
            this.options = options;
            Text = options.Uninstall ? "卸载摸鱼单词" : "安装摸鱼单词";
            ClientSize = new Size(520, 354); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(249, 247, 242); Font = new Font("Microsoft YaHei UI", 9F);
            Label title = new Label { Text = options.Uninstall ? "告别一下，单词还在。" : "摸鱼间隙，记住一个词。", AutoSize = false,
                Location = new Point(32, 28), Size = new Size(460, 38), Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold), ForeColor = Color.FromArgb(42, 56, 48) };
            Label subtitle = new Label { Text = options.Uninstall ? "只移除程序文件；词库、收藏和设置会保留。" : "离线单词卡 · 本地 PDF · 无需管理员权限", Location = new Point(34, 80), Size = new Size(455, 30), ForeColor = Color.FromArgb(88, 96, 89) };
            Label path = new Label { Text = (options.Uninstall ? "卸载位置" : "安装位置") + "\n" + options.InstallDirectory,
                Location = new Point(34, 125), Size = new Size(454, 64), ForeColor = Color.FromArgb(78, 82, 78) };
            status = new Label { Text = options.Uninstall ? "请先关闭正在运行的摸鱼单词。" : "所有组件均已包含，安装过程无需联网。",
                Location = new Point(34, 202), Size = new Size(454, 52), ForeColor = Color.FromArgb(88, 96, 89) };
            launch = new CheckBox { Text = "完成后打开摸鱼单词", Checked = true, Location = new Point(34, 278), Size = new Size(200, 32), Visible = !options.Uninstall };
            action = new Button { Text = options.Uninstall ? "卸载" : "安装", Location = new Point(376, 280), Size = new Size(110, 38), BackColor = Color.FromArgb(48, 83, 64), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            cancel = new Button { Text = "取消", Location = new Point(266, 280), Size = new Size(98, 38), FlatStyle = FlatStyle.Flat };
            Controls.AddRange(new Control[] { title, subtitle, path, status, launch, action, cancel });
            action.Click += RunAction; cancel.Click += delegate { Program.Result = complete ? 0 : 2; Close(); };
            FormClosing += delegate { if (!complete && Program.Result == 0) Program.Result = 2; };
            AcceptButton = action; CancelButton = cancel;
        }
        private void RunAction(object sender, EventArgs e)
        {
            if (complete) { Close(); return; }
            action.Enabled = false; cancel.Enabled = false; UseWaitCursor = true;
            status.Text = options.Uninstall ? "正在移除程序文件……" : "正在安装，请稍候……";
            status.Refresh();
            try
            {
                Engine engine = new Engine(options);
                if (options.Uninstall) engine.Uninstall(); else engine.Install();
                complete = true; Program.Result = 0; action.Text = "完成";
                status.Text = options.Uninstall ? "卸载完成。你的个人词库、收藏和设置已保留。" : "安装完成，可以开始记单词了。";
                cancel.Visible = false; launch.Enabled = false;
                if (!options.Uninstall && launch.Checked)
                    Process.Start(new ProcessStartInfo(Path.Combine(options.InstallDirectory, "MoyuWord.exe")) { WorkingDirectory = options.InstallDirectory, UseShellExecute = true });
            }
            catch (Exception ex) { Program.Result = 1; status.Text = ex.Message; Program.Log(ex.ToString()); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { action.Enabled = true; cancel.Enabled = true; UseWaitCursor = false; }
        }
    }
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool MoveFileEx(string existing, string replacement, int flags);
    }
}
