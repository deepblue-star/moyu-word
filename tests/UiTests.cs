using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoyuWord;

internal static class UiTests
{
    static int checks;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); checks++; }
    static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    static IEnumerable<T> Descendants<T>(DependencyObject obj) where T : DependencyObject
    {
        if (obj is T) yield return (T)obj;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(obj, i))) yield return item;
    }
    static string Text(MainWindow window)
    {
        window.Measure(new Size(440, 550)); window.Arrange(new Rect(0, 0, 440, 550)); window.UpdateLayout();
        var content = (ContentControl)Field(window, "content"); content.ApplyTemplate(); content.UpdateLayout();
        return String.Join(" | ", Descendants<TextBlock>(content).Select(t => t.Text));
    }
    static void MonitorRestoration()
    {
        var primary = new Rect(0, 0, 1920, 1040);
        var left = new Rect(-1280, 0, 1280, 984);
        var above = new Rect(0, -1080, 1920, 1080);
        var right = new Rect(1920, 0, 1280, 1000);
        var settings = new AppSettings();
        Rect bounds = MainWindow.RestoreWindowBounds(settings, new[] { left, primary, above, right }, 1);
        Check(bounds == new Rect(1444, 80, 440, 550), "unset position retains the primary-screen default size and placement");
        settings.Left = -1000; settings.Top = 150; settings.Width = 510; settings.Height = 600;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { left, primary, above, right }, 1);
        Check(bounds == new Rect(-1000, 150, 510, 600), "saved negative X and custom size restore on the left monitor");
        settings.Left = 100; settings.Top = -900;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { left, primary, above, right }, 1);
        Check(bounds.Left == 100 && bounds.Top == -900, "saved negative Y restores on the upper monitor");
        settings.Left = -1; settings.Top = 200;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { primary }, 0);
        Check(bounds.Left == 0 && bounds.Top == 200, "a single minus-one coordinate is not the unset-position sentinel");
        settings.Left = 2400; settings.Top = 100;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { left, primary, above, right }, 1);
        Check(bounds.Left == 2400 && bounds.Top == 100, "positive secondary-monitor coordinates are preserved");
        settings.Left = -1400; settings.Top = 900;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { new Rect(-1920, 0, 1920, 1476), new Rect(0, 0, 2880, 1560) }, 1, 1.5, 1.5);
        Check(bounds == new Rect(-1280, 384, 510, 600), "150-percent system DPI converts device work areas before choosing and clamping");
        settings.Left = -1600; settings.Top = 150;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { primary }, 0);
        Check(bounds.Left == 0 && bounds.Top == 150, "removed monitor position is clamped to a remaining monitor");
        settings.Width = 4000; settings.Height = 3000;
        bounds = MainWindow.RestoreWindowBounds(settings, new[] { left }, 0);
        Check(bounds == left, "oversized saved window fits the selected monitor work area");
        bounds = MainWindow.RestoreWindowBounds(new AppSettings(), new[] { new Rect(0, 0, 320, 300) }, 0);
        Check(bounds == new Rect(0, 0, 320, 300), "small logical work area keeps default window fully visible");
    }
    [STAThread] static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "MoyuUiTests-" + Guid.NewGuid().ToString("N")); MainWindow window = null;
        try
        {
            MonitorRestoration();
            var app = new Application(); var store = new LibraryStore(temp); store.Load();
            window = new MainWindow(store); window.Show();
            Check(IntPtr.Size == 4, "UI executable runs as x86");
            Check(window.AllowsTransparency && window.Background == Brushes.Transparent, "per-pixel transparent native window configured");
            Check(Text(window).Contains("给自己"), "home screen renders");
            var words = new List<Word> { new Word { Id = "a", English = "alpha", Chinese = "甲", PartOfSpeech = "n.", Phonetic = "/a/", Example = "First example." }, new Word { Id = "b", English = "beta", Chinese = "乙" } };
            Call(window, "StartDeck", words, "Test", false);
            string card = Text(window); Check(card.Contains("alpha") && !card.Contains("甲") && !card.Contains("First example"), "new card hides translation, phonetics and example");
            window.GetType().GetField("revealed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, true); Call(window, "ShowCard", 0d);
            Check(Text(window).Contains("甲") && Text(window).Contains("First example"), "reveal shows compact details");
            Call(window, "MoveCard", 1); Check(Text(window).Contains("beta") && !Text(window).Contains("乙"), "next card resets reveal");
            Call(window, "MoveCard", 1); Check(Text(window).Contains("alpha"), "last card wraps to first");
            Call(window, "MoveCard", -1); Check(Text(window).Contains("beta"), "previous card wraps to last");
            store.ToggleFavorite(words[0]); Call(window, "ShowFavorites"); Check(Text(window).Contains("alpha") && Text(window).Contains("甲"), "favorite list shows English and Chinese");
            Call(window, "ShowWordPopup", words[0]); Check(((Grid)Field(window, "Overlay")).Visibility == Visibility.Visible, "favorite details overlay opens");
            Call(window, "CloseOverlay"); Check(((Grid)Field(window, "Overlay")).Visibility == Visibility.Collapsed, "details overlay closes");
            store.Settings.BackgroundOpacity = .25; store.Settings.TextOpacity = .7; Call(window, "ApplyOpacity");
            Check(Math.Abs(((Border)Field(window, "Backdrop")).Opacity - .25) < .001 && Math.Abs(((Grid)Field(window, "Surface")).Opacity - .7) < .001, "background and foreground alpha independent");
            store.Settings.BackgroundOpacity = 0; store.Settings.TextOpacity = 0; Call(window, "ApplyOpacity");
            Check(((Border)Field(window, "Backdrop")).Opacity == 0 && ((Grid)Field(window, "Surface")).Opacity == 0, "both layers support true zero alpha");
            bool before = window.Topmost; Call(window, "TogglePin"); Check(window.Topmost != before && store.Settings.Topmost == window.Topmost, "pin toggles and persists topmost");
            Call(window, "ShowReader"); Check(Text(window).Contains("读几页"), "PDF entry renders before file selection");
            window.Close(); window = null;
            Console.WriteLine("UI TESTS PASSED: " + checks); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (window != null) window.Close(); if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
