using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Automation;

namespace MoyuWord
{
    internal static class Theme
    {
        internal static readonly Brush Ink = Brush("#303D39");
        internal static readonly Brush Muted = Brush("#78817A");
        internal static readonly Brush Accent = Brush("#527666");
        internal static readonly Brush Line = Brush("#D2D8CC");
        internal static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        internal static TextBlock Text(string text, double size = 14, Brush color = null)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }
        internal static Button Button(string text, Action action, bool primary = false, string name = null)
        {
            var b = new Button { Content = text, Foreground = primary ? Accent : Ink, Background = Brushes.Transparent,
                BorderBrush = primary ? Accent : Line, BorderThickness = new Thickness(1), Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 3, 0, 3), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 34 };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            b.MouseEnter += delegate { b.BorderBrush = Accent; };
            b.MouseLeave += delegate { b.BorderBrush = primary ? Accent : Line; };
            b.Click += delegate { if (action != null) action(); };
            AutomationProperties.SetName(b, name ?? text);
            return b;
        }
        internal static Border Panel(UIElement child, double padding = 16)
        {
            return new Border { Child = child, Padding = new Thickness(padding), BorderThickness = new Thickness(1), BorderBrush = Line, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 7, 0, 7), Background = Brushes.Transparent };
        }
        internal static StackPanel Stack(params UIElement[] items)
        {
            var stack = new StackPanel(); foreach (var item in items) stack.Children.Add(item); return stack;
        }
        internal static void Animate(UIElement element, double offset = 10)
        {
            var translate = new TranslateTransform(); element.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
    }
}
