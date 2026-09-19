using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Runtime.InteropServices;

namespace MoyuWord
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
        [STAThread]
        public static int Main(string[] args)
        {
            using (var singleInstance = new Mutex(false, "Local\\MoyuWord.App"))
            {
            bool ownsMutex;
            try { ownsMutex = singleInstance.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                var existing = FindWindow(null, "摸鱼单词");
                if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
                return 0;
            }
            try
            {
                foreach (string argument in args) if (argument.StartsWith("--diagnostics=", StringComparison.Ordinal)) Diagnostics.Path = Path.GetFullPath(argument.Substring(14));
                Diagnostics.Record("startup", new { Bits = IntPtr.Size * 8, BaseDirectory = AppDomain.CurrentDomain.BaseDirectory });
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show("操作未完成：" + e.Exception.Message, "摸鱼单词", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true;
                };
                var store = new LibraryStore(); store.Load();
                var window = new MainWindow(store);
                app.Run(window); return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动摸鱼单词：" + ex.Message, "摸鱼单词", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
            }
            finally { singleInstance.ReleaseMutex(); }
            }
        }
    }
}
