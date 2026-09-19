using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MoyuWord
{
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        private IntPtr handle;
        private DispatcherTimer hoverTimer;
        private Forms.NotifyIcon tray;
        private bool isHidden, suspendHide, isDragging;
        private DateTime revealUntil = DateTime.UtcNow.AddSeconds(3);
        private Button pinButton;

        private void InitializeBehavior()
        {
            SourceInitialized += delegate { handle = new WindowInteropHelper(this).Handle; };
            Loaded += delegate
            {
                hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                hoverTimer.Tick += delegate { PollHover(); RecordCardExposure(); }; hoverTimer.Start();
            };
            Activated += delegate { if (isHidden) { revealUntil = DateTime.UtcNow.AddSeconds(2); SetHidden(false); } };
            Closing += delegate
            {
                Diagnostics.Record("closed", new { });
                try { RecordCardExposure(); SaveWindowSettings(); }
                catch (System.IO.IOException ex) { Diagnostics.Record("settings-save-error", new { Message = ex.Message }); }
                finally
                {
                    if (hoverTimer != null) hoverTimer.Stop();
                    if (tray != null) { tray.Visible = false; tray.Dispose(); }
                    ClosePdfReader();
                }
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { RecoverVisibility(); e.Handled = true; }
                else if (e.Key == Key.Escape) { if (Overlay.Visibility == Visibility.Visible) CloseOverlay(); else ShowHome(); e.Handled = true; }
                else if (e.Key == Key.Left && screen == "cards" && Overlay.Visibility != Visibility.Visible) { MoveCard(-1); e.Handled = true; }
                else if (e.Key == Key.Right && screen == "cards" && Overlay.Visibility != Visibility.Visible) { MoveCard(1); e.Handled = true; }
            };
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "moyu.ico");
            tray = new Forms.NotifyIcon { Icon = System.IO.File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application, Text = "摸鱼单词 · 双击恢复显示", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("恢复显示 / 重置透明度", null, delegate { Dispatcher.Invoke(new Action(RecoverVisibility)); });
            menu.Items.Add("退出", null, delegate { Dispatcher.Invoke(new Action(Close)); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Dispatcher.Invoke(new Action(RecoverVisibility)); };
        }
        private void PollHover()
        {
            if (handle == IntPtr.Zero || WindowState == WindowState.Minimized) return;
            NativePoint point; NativeRect bounds;
            if (!GetCursorPos(out point) || !GetWindowRect(handle, out bounds)) return;
            bool inside = point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
            SetHidden(!inside && !suspendHide && !isDragging && DateTime.UtcNow >= revealUntil);
        }
        private void SetHidden(bool hidden)
        {
            if (hidden == isHidden) return;
            if (hidden) RecordCardExposure();
            isHidden = hidden;
            int style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, hidden ? style | 0x20 : style & ~0x20);
            Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(hidden ? 0 : 1, TimeSpan.FromMilliseconds(hidden ? 170 : 130)));
            Diagnostics.Record("hover", new { Hidden = hidden, TargetOpacity = hidden ? 0 : 1, ClickThrough = hidden, Left, Top, Width = ActualWidth, Height = ActualHeight });
        }
        private void RecoverVisibility()
        {
            store.Settings.BackgroundOpacity = .94; store.Settings.TextOpacity = 1;
            ApplyOpacity();
            WindowState = WindowState.Normal; Show(); SetHidden(false); revealUntil = DateTime.UtcNow.AddSeconds(5); Activate();
            try { store.SaveSettings(); } catch (System.IO.IOException ex) { Toast("已恢复显示；设置保存失败：" + ex.Message); }
        }
        private void SaveWindowSettings()
        {
            var rect = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!rect.IsEmpty) { store.Settings.Left = rect.Left; store.Settings.Top = rect.Top; store.Settings.Width = rect.Width; store.Settings.Height = rect.Height; }
            store.Settings.Topmost = Topmost;
            store.SaveSettings();
        }
        private void ApplyOpacity()
        {
            Backdrop.Opacity = Math.Max(0, Math.Min(1, store.Settings.BackgroundOpacity));
            Surface.Opacity = Math.Max(0, Math.Min(1, store.Settings.TextOpacity));
            Diagnostics.Record("opacity", new { Background = Backdrop.Opacity, Foreground = Surface.Opacity });
        }
        private void TogglePin()
        {
            Topmost = !Topmost; store.Settings.Topmost = Topmost;
            UpdatePinIcon(); store.SaveSettings();
            Diagnostics.Record("pin", new { Topmost });
        }
        private void UpdatePinIcon()
        {
            pinButton.Content = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M7,1 L15,1 L14,8 L18,12 L12,12 L12,20 L10,20 L10,12 L4,12 L8,8 Z"), Stroke = Theme.Accent, Fill = Topmost ? Theme.Accent : System.Windows.Media.Brushes.Transparent, StrokeThickness = 1, Width = 12, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform };
            pinButton.ToolTip = Topmost ? "取消置顶" : "固定前台";
            System.Windows.Automation.AutomationProperties.SetName(pinButton, Topmost ? "取消置顶" : "固定前台");
        }
        private void ToggleMaximize()
        {
            MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private void DragTitle(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.OriginalSource is Button) return;
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            try { isDragging = true; DragMove(); } catch (InvalidOperationException) { } finally { isDragging = false; }
        }
        private void AddResizeGrips(Grid outer)
        {
            AddGrip(outer, HorizontalAlignment.Left, VerticalAlignment.Stretch, 7, double.NaN, 1, Cursors.SizeWE);
            AddGrip(outer, HorizontalAlignment.Right, VerticalAlignment.Stretch, 7, double.NaN, 2, Cursors.SizeWE);
            AddGrip(outer, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 6, 3, Cursors.SizeNS);
            AddGrip(outer, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 6, 6, Cursors.SizeNS);
            AddGrip(outer, HorizontalAlignment.Left, VerticalAlignment.Top, 12, 12, 4, Cursors.SizeNWSE);
            AddGrip(outer, HorizontalAlignment.Right, VerticalAlignment.Top, 12, 12, 5, Cursors.SizeNESW);
            AddGrip(outer, HorizontalAlignment.Left, VerticalAlignment.Bottom, 12, 12, 7, Cursors.SizeNESW);
            AddGrip(outer, HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, 8, Cursors.SizeNWSE);
        }
        private void AddGrip(Grid grid, HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height, int edge, Cursor cursor)
        {
            var grip = new Border { Width = width, Height = height, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Background = System.Windows.Media.Brushes.Transparent, Cursor = cursor };
            grip.MouseLeftButtonDown += delegate
            {
                if (WindowState != WindowState.Normal) return;
                ReleaseCapture(); isDragging = true;
                SendMessage(handle, 0x112, new IntPtr(0xF000 + edge), IntPtr.Zero); isDragging = false;
            }; grid.Children.Add(grip);
        }
    }
}
