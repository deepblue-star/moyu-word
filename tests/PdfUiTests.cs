using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoyuWord;

internal static class PdfUiTests
{
    private static int checks;
    private static object Field(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static void Set(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) field.SetValue(target, value);
    }
    private static void Call(object target, string name, params object[] values)
    { target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, values); }
    private static Task CallTask(object target, string name, params object[] values)
    { return (Task)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, values); }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static void PumpUntil(Func<bool> condition, int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += delegate { if (condition() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame);
        if (!condition()) throw new TimeoutException("Timed out waiting for background PDF work.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string fixture = Path.GetFullPath(args.Length == 0 ? "tests/fixtures/two-pages.pdf" : args[0]);
        string temporary = Path.Combine(Path.GetTempPath(), "MoyuPdfUiTests-" + Guid.NewGuid().ToString("N"));
        MainWindow window = null;
        PdfDocument document = null;
        ManualResetEvent entered = null, release = null;
        Task blocker = null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var store = new LibraryStore(temporary);
            // No built-in deck is needed: this tests PDF scheduling, not vocabulary content.
            window = new MainWindow(store) { ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Minimized };
            window.Show();
            document = new PdfDocument(fixture);
            Set(window, "pdf", document); Set(window, "pdfFile", fixture); Set(window, "pdfPageCount", 2);
            object nativeLock = typeof(PdfDocument).GetField("NativeLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            entered = new ManualResetEvent(false); release = new ManualResetEvent(false);
            ManualResetEvent firstEntered = entered, firstRelease = release;
            blocker = Task.Run(delegate { lock (nativeLock) { firstEntered.Set(); firstRelease.WaitOne(1500); } });
            if (!entered.WaitOne(2000)) throw new TimeoutException("Native test barrier did not start.");

            var elapsed = Stopwatch.StartNew();
            Call(window, "ShowReader");
            elapsed.Stop();
            Check(elapsed.ElapsedMilliseconds < 500, "reader returns without waiting for the busy native PDF lock");
            int ticks = 0;
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            heartbeat.Tick += delegate { ticks++; };
            heartbeat.Start(); PumpUntil(delegate { return ticks >= 4; }, 500); heartbeat.Stop();
            Check(ticks >= 4, "dispatcher timers remain responsive during background rendering");
            Call(window, "ChangePdfPage", 1); Call(window, "ZoomPdf", 1d); Call(window, "ZoomPdf", .2d);
            release.Set(); blocker.Wait();
            PumpUntil(delegate { return !(bool)Field(window, "pdfRendering"); }, 5000);
            var bitmap = (BitmapSource)((Image)Field(window, "pdfImage")).Source;
            Check(bitmap != null && bitmap.PixelWidth == 880 && bitmap.PixelHeight == 660, "rapid page and zoom changes finish with only the latest requested view");
            Check(((TextBlock)Field(window, "pdfPageLabel")).Text == "2 / 2", "stale page results do not overwrite the latest page label");
            Check(((TextBlock)Field(window, "pdfZoomLabel")).Text == "220%", "latest requested zoom is displayed");
            Check(bitmap.IsFrozen, "background bitmap can safely cross to the UI thread");

            entered.Dispose(); release.Dispose();
            entered = new ManualResetEvent(false); release = new ManualResetEvent(false);
            ManualResetEvent openEntered = entered, openRelease = release;
            blocker = Task.Run(delegate { lock (nativeLock) { openEntered.Set(); openRelease.WaitOne(2000); } });
            if (!entered.WaitOne(2000)) throw new TimeoutException("Open test barrier did not start.");
            Directory.CreateDirectory(temporary);
            string latestPath = Path.Combine(temporary, "latest.pdf");
            File.Copy(fixture, latestPath);
            elapsed.Restart();
            Task firstOpen = CallTask(window, "OpenPdfPathAsync", fixture);
            Task latestOpen = CallTask(window, "OpenPdfPathAsync", latestPath);
            elapsed.Stop();
            Check(elapsed.ElapsedMilliseconds < 500, "opening and replacing a PDF do not wait for the native lock");
            ticks = 0; heartbeat.Start(); PumpUntil(delegate { return ticks >= 2; }, 500); heartbeat.Stop();
            Check(ticks >= 2, "dispatcher remains responsive while a PDF opens");
            release.Set(); blocker.Wait();
            PumpUntil(delegate { return firstOpen.IsCompleted && latestOpen.IsCompleted && !(bool)Field(window, "pdfRendering"); }, 5000);
            Check(!firstOpen.IsFaulted && !latestOpen.IsFaulted && (string)Field(window, "pdfFile") == latestPath,
                "a superseded open is observed and the latest file remains selected");
            Check(((TextBlock)Field(window, "pdfPageLabel")).Text == "1 / 2", "the replacement document renders from its first page");
            object currentDocument = Field(window, "pdf");
            string invalidPath = Path.Combine(temporary, "invalid.pdf");
            File.WriteAllText(invalidPath, "Not a PDF.");
            Task invalidOpen = CallTask(window, "OpenPdfPathAsync", invalidPath);
            PumpUntil(delegate { return invalidOpen.IsCompleted; }, 5000);
            Check(!invalidOpen.IsFaulted && Object.ReferenceEquals(currentDocument, Field(window, "pdf")),
                "failed background loading is handled and preserves the current document");
            Call(window, "CloseOverlay");
            bitmap = (BitmapSource)((Image)Field(window, "pdfImage")).Source;

            entered.Dispose(); release.Dispose();
            entered = new ManualResetEvent(false); release = new ManualResetEvent(false);
            ManualResetEvent secondEntered = entered, secondRelease = release;
            blocker = Task.Run(delegate { lock (nativeLock) { secondEntered.Set(); secondRelease.WaitOne(2000); } });
            if (!entered.WaitOne(2000)) throw new TimeoutException("Close test barrier did not start.");
            Call(window, "RenderPdf");
            elapsed.Restart(); window.Close(); elapsed.Stop();
            Check(elapsed.ElapsedMilliseconds < 500, "closing does not wait for a rendering document to release its native lock");
            Check(Field(window, "pdf") == null, "closing detaches the current PDF document");
            release.Set(); blocker.Wait();
            PumpUntil(delegate { return !(bool)Field(window, "pdfRendering"); }, 5000);
            Check(Object.ReferenceEquals(bitmap, ((Image)Field(window, "pdfImage")).Source), "a result completing after close does not update the window");
            window = null;
            Console.WriteLine("PASS: " + checks + " asynchronous PDF UI checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (release != null) release.Set();
            if (blocker != null) blocker.Wait(5000);
            if (window != null) window.Close();
            if (document != null) document.Dispose();
            if (entered != null) entered.Dispose();
            if (release != null) release.Dispose();
            app.Shutdown();
        }
    }
}
