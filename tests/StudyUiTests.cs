using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoyuWord;

internal static class StudyUiTests
{
    private static int checks;
    private static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value); }
    private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T) yield return (T)parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var item in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return item;
    }
    private static void Pump(MainWindow window)
    {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static void Click(MainWindow window, string label)
    {
        var overlay = (Grid)Field(window, "Overlay"); window.UpdateLayout();
        var button = Find<Button>(overlay).Single(b => Object.Equals(b.Content, label));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
    }
    private static int? Progress(LibraryStore store, List<Word> words)
    {
        return (int?)typeof(LibraryStore).GetMethod("GetStudyIndex").Invoke(store, new object[] { "test-study", words });
    }
    [STAThread] private static int Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "MoyuStudyUi-" + Guid.NewGuid().ToString("N"));
        MainWindow window = null;
        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "progress.json"), "{\"test-study\":{\"Index\":1,\"English\":\"beta\"}}");
            var app = new Application(); var store = new LibraryStore(path); store.Load();
            var words = new List<Word> { new Word { English = "alpha", Chinese = "甲" }, new Word { English = "beta", Chinese = "乙" }, new Word { English = "gamma", Chinese = "丙" } };
            store.Libraries.Add(new WordLibrary { Id = "test-study", Name = "Test study", Words = words });
            window = new MainWindow(store); window.Show(); Pump(window);
            ((DispatcherTimer)Field(window, "hoverTimer")).Stop();
            Set(window, "isHidden", false); ((Grid)Field(window, "Root")).BeginAnimation(UIElement.OpacityProperty, null); ((Grid)Field(window, "Root")).Opacity = 1;
            Call(window, "ShowDecks"); Call(window, "StartDeck", words, "Test study", false); Pump(window);
            Check(((Grid)Field(window, "Overlay")).Visibility == Visibility.Visible, "saved deck opens a resume confirmation instead of resetting to first word");
            Check(Progress(store, words) == 1, "opening confirmation does not overwrite stored position");
            Click(window, "取消"); Check(Progress(store, words) == 1, "cancel preserves previous progress");
            Call(window, "StartDeck", words, "Test study", false); Pump(window); Click(window, "继续上次");
            Check((int)Field(window, "cardIndex") == 1 && !(bool)Field(window, "revealed"), "resume shows the last displayed word itself with answer hidden");
            Call(window, "MoveCard", 1); Pump(window); Call(window, "RecordCardExposure");
            Check(Progress(store, words) == 2, "next visible word is saved without revealing or answering");
            Call(window, "MoveCard", -1); Pump(window); Call(window, "RecordCardExposure");
            Check(Progress(store, words) == 1, "moving backward saves latest position rather than maximum reached");
            Call(window, "ShowJumpDialog"); Pump(window);
            var overlay = (Grid)Field(window, "Overlay"); var input = Find<TextBox>(overlay).Single();
            foreach (string invalid in new[] { "", "0", "4", "abc", "1.5", "-1", "999999999999" })
            {
                input.Text = invalid; Click(window, "跳转到此单词");
                Check((int)Field(window, "cardIndex") == 1 && overlay.Visibility == Visibility.Visible && Progress(store, words) == 1, "invalid jump rejected without changing position: " + invalid);
            }
            input.Text = "3"; Click(window, "跳转到此单词"); Call(window, "RecordCardExposure");
            Check((int)Field(window, "cardIndex") == 2 && Progress(store, words) == 2, "jump accepts last one-based word number and saves visible target");
            Call(window, "ShowJumpDialog"); Pump(window); Click(window, "取消"); Check((int)Field(window, "cardIndex") == 2, "cancel jump retains current card");
            Call(window, "ShowDecks"); Call(window, "StartDeck", words, "Test study", false); Pump(window); Click(window, "从头开始"); Call(window, "RecordCardExposure");
            Check((int)Field(window, "cardIndex") == 0 && Progress(store, words) == 0, "restart explicitly resets to first displayed word");
            Set(window, "isHidden", true); ((Grid)Field(window, "Root")).Opacity = 0;
            Call(window, "MoveCard", 1); Pump(window); Call(window, "RecordCardExposure");
            Check(Progress(store, words) == 0, "hidden navigation is not recorded as exposure");
            Set(window, "isHidden", false); ((Grid)Field(window, "Root")).Opacity = 1; Call(window, "RecordCardExposure");
            Check(Progress(store, words) == 1, "returning to visible card records the displayed position");
            ((Grid)Field(window, "Surface")).Opacity = 0; Call(window, "MoveCard", 1); Pump(window); Call(window, "RecordCardExposure");
            Check(Progress(store, words) == 1, "fully transparent text is not counted as exposure");
            ((Grid)Field(window, "Surface")).Opacity = 1; Call(window, "RecordCardExposure");
            var reloaded = new LibraryStore(path); reloaded.Load(); Check(Progress(reloaded, words) == 2, "last shown word survives a fresh store instance");
            Set(window, "isHidden", true); ((Grid)Field(window, "Root")).Opacity = 0;
            Call(window, "MoveCard", -1); Pump(window);
            Set(window, "isHidden", false); ((Grid)Field(window, "Root")).Opacity = 1;
            Call(window, "SetHidden", true);
            Check(Progress(store, words) == 1, "hiding saves a just-visible word before the next polling interval");
            Call(window, "MoveCard", -1); Pump(window);
            Set(window, "isHidden", false); ((Grid)Field(window, "Root")).BeginAnimation(UIElement.OpacityProperty, null); ((Grid)Field(window, "Root")).Opacity = 1;
            var minimize = Find<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "最小化");
            minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Progress(store, words) == 0 && window.WindowState == WindowState.Minimized, "minimizing saves the just-visible current word before changing window state");
            window.Close(); window = null; Console.WriteLine("PASS: " + checks + " study UI checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (window != null) window.Close(); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
