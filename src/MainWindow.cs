using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MoyuWord
{
    public partial class MainWindow : Window
    {
        private readonly LibraryStore store;
        private Grid Root, Surface, Overlay;
        private Border Backdrop;
        private ContentControl content;
        private TextBlock status;
        private string screen = "home";
        private List<Word> deck = new List<Word>();
        private string deckName;
        private int cardIndex;
        private bool revealed;
        private PdfDocument pdf;
        private int pdfPage;
        private double pdfZoom = 1;
        private string pdfFile;
        private Image pdfImage;
        private TextBlock pdfPageLabel, pdfZoomLabel;
        private ScrollViewer pdfScroller;
        private int pdfPageCount, pdfDocumentVersion, pdfLoadVersion, pdfRenderVersion;
        private bool pdfLoading, pdfRendering, pdfRenderPending, pdfFitRequested, pdfReaderClosed;
        private CancellationTokenSource pdfLoadCancellation;
        private readonly SemaphoreSlim pdfOpenGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<int, double> pdfPageWidths = new Dictionary<int, double>();

        private sealed class LoadedPdf
        {
            internal PdfDocument Document;
            internal int PageCount;
            internal double FirstPageWidth;
        }

        private sealed class RenderedPdf
        {
            internal BitmapSource Bitmap;
            internal double PageWidth, Scale;
        }

        public MainWindow(LibraryStore store)
        {
            this.store = store;
            Title = "摸鱼单词"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true; Background = Brushes.Transparent; Topmost = store.Settings.Topmost;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            var monitors = System.Windows.Forms.Screen.AllScreens;
            var workAreas = monitors.Select(m => new Rect(m.WorkingArea.Left, m.WorkingArea.Top, m.WorkingArea.Width, m.WorkingArea.Height)).ToArray();
            int primary = Array.FindIndex(monitors, m => m.Primary);
            double scaleX, scaleY;
            // The manifest is system-DPI aware. WinForms screen rectangles are device pixels;
            // persisted WPF Left/Top/Width/Height use 96-dpi device-independent units.
            using (var desktop = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            { scaleX = desktop.DpiX / 96d; scaleY = desktop.DpiY / 96d; }
            Rect bounds = RestoreWindowBounds(store.Settings, workAreas, Math.Max(0, primary), scaleX, scaleY);
            MinWidth = Math.Min(360, bounds.Width); MinHeight = Math.Min(420, bounds.Height);
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            BuildShell(); InitializeBehavior(); ApplyOpacity(); ShowHome();
            if (store.Warnings.Count > 0) ShowError("已恢复可用数据", String.Join("\n", store.Warnings.Take(3)));
        }

        internal static Rect RestoreWindowBounds(AppSettings settings, Rect[] deviceWorkAreas, int primaryIndex, double scaleX = 1, double scaleY = 1)
        {
            Rect[] areas = deviceWorkAreas.Select(a => new Rect(a.Left / scaleX, a.Top / scaleY, a.Width / scaleX, a.Height / scaleY)).ToArray();
            Rect requested = new Rect(settings.Left, settings.Top, Math.Max(360, settings.Width), Math.Max(420, settings.Height));
            bool useDefault = settings.Left == -1 && settings.Top == -1;
            int selected = primaryIndex;
            if (!useDefault)
            {
                double bestOverlap = -1, bestDistance = Double.MaxValue;
                for (int i = 0; i < areas.Length; i++)
                {
                    Rect overlap = Rect.Intersect(requested, areas[i]);
                    double overlapArea = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                    double dx = Math.Max(0, Math.Max(areas[i].Left - requested.Right, requested.Left - areas[i].Right));
                    double dy = Math.Max(0, Math.Max(areas[i].Top - requested.Bottom, requested.Top - areas[i].Bottom));
                    double distance = dx * dx + dy * dy;
                    if (overlapArea > bestOverlap || (overlapArea == bestOverlap && distance < bestDistance))
                    { selected = i; bestOverlap = overlapArea; bestDistance = distance; }
                }
            }
            Rect area = areas[selected];
            double width = Math.Min(requested.Width, area.Width), height = Math.Min(requested.Height, area.Height);
            double left = useDefault ? area.Right - width - 36 : requested.Left;
            double top = useDefault ? area.Top + 80 : requested.Top;
            return new Rect(Math.Max(area.Left, Math.Min(left, area.Right - width)),
                Math.Max(area.Top, Math.Min(top, area.Bottom - height)), width, height);
        }

        private void BuildShell()
        {
            Root = new Grid(); Content = Root;
            Backdrop = new Border { Background = Theme.Brush("#F7F8F1"), CornerRadius = new CornerRadius(16), BorderBrush = Theme.Brush("#DCE1D5"), BorderThickness = new Thickness(1) };
            Root.Children.Add(Backdrop);
            Surface = new Grid { Margin = new Thickness(20, 8, 20, 12) };
            Surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            Surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            Root.Children.Add(Surface);
            var title = new Grid { Background = Brushes.Transparent };
            title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brand = Theme.Text("◒  摸鱼单词", 12, Theme.Accent); brand.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(brand); title.MouseLeftButtonDown += DragTitle;
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            pinButton = TitleButton(Topmost ? "●" : "♧", "固定前台 / 取消置顶", TogglePin);
            UpdatePinIcon();
            controls.Children.Add(pinButton); controls.Children.Add(TitleButton("−", "最小化", delegate { RecordCardExposure(); WindowState = WindowState.Minimized; }));
            controls.Children.Add(TitleButton("□", "放大 / 还原", ToggleMaximize)); controls.Children.Add(TitleButton("×", "关闭", Close));
            Grid.SetColumn(controls, 1); title.Children.Add(controls); Surface.Children.Add(title);
            content = new ContentControl { Margin = new Thickness(0, 12, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            Grid.SetRow(content, 1); Surface.Children.Add(content);
            var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status = Theme.Text("本地 · 离开即隐身", 10, Theme.Muted); footer.Children.Add(status);
            var settings = Theme.Button("透明度", ShowSettings, false, "调整透明度"); settings.BorderThickness = new Thickness(0); settings.FontSize = 11; settings.MinHeight = 24; settings.Padding = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(settings, 1); footer.Children.Add(settings); Grid.SetRow(footer, 2); Surface.Children.Add(footer);
            Overlay = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
            Grid.SetRow(Overlay, 1); Surface.Children.Add(Overlay);
            AddResizeGrips(Root);
        }

        private Button TitleButton(string glyph, string name, Action action)
        {
            var button = Theme.Button(glyph, action, false, name); button.Width = 31; button.Height = 30; button.MinHeight = 24;
            button.Padding = new Thickness(0); button.Margin = new Thickness(0); button.BorderThickness = new Thickness(0); button.FontSize = 14; button.ToolTip = name;
            return button;
        }
        private void SetPage(string name, UIElement page, double slide = 8)
        {
            screen = name; CloseOverlay(); content.Content = page; Theme.Animate(page, slide);
            status.Text = "本地 · 离开即隐身";
            Diagnostics.Record("page", new { Screen = screen, Word = screen == "cards" ? deck[cardIndex].English : null, Revealed = revealed });
        }
        private StackPanel Heading(string overline, string title, string subtitle, Action back = null)
        {
            var stack = new StackPanel();
            if (back != null) { var b = Theme.Button("‹ 返回", back); b.HorizontalAlignment = HorizontalAlignment.Left; b.BorderThickness = new Thickness(0); b.Margin = new Thickness(-12, -5, 0, 5); stack.Children.Add(b); }
            stack.Children.Add(Theme.Text(overline, 10, Theme.Accent));
            var heading = Theme.Text(title, 27); heading.Margin = new Thickness(0, 8, 0, 8); stack.Children.Add(heading);
            var description = Theme.Text(subtitle, 12, Theme.Muted); description.Margin = new Thickness(0, 0, 0, 16); stack.Children.Add(description); return stack;
        }
        private ScrollViewer Scroll(UIElement element)
        { return new ScrollViewer { Content = element, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 3, 0) }; }

        private Border ModeCard(string number, string title, string description, Action action)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            var index = Theme.Text(number, 13, Theme.Accent); index.VerticalAlignment = VerticalAlignment.Top; index.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(index);
            var body = Theme.Stack(Theme.Text(title, 19), Theme.Text(description, 11, Theme.Muted)); ((TextBlock)body.Children[1]).Margin = new Thickness(0, 8, 0, 0); Grid.SetColumn(body, 1); row.Children.Add(body);
            var arrow = Theme.Text("↗", 23, Theme.Accent); Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            var card = Theme.Panel(row, 20); card.Cursor = Cursors.Hand;
            card.MouseEnter += delegate { card.BorderBrush = Theme.Accent; }; card.MouseLeave += delegate { card.BorderBrush = Theme.Line; };
            // A button wrapper gives the full card keyboard and screen-reader access.
            card.Child = null;
            var button = new Button { Content = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand };
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            card.Child = button; button.Click += delegate { action(); };
            System.Windows.Automation.AutomationProperties.SetName(button, title); return card;
        }

        private void ShowHome()
        {
            RecordCardExposure();
            var stack = Heading("A LITTLE PAUSE", "给自己，一点留白。", "在工作的间隙，悄悄积累一点。\n鼠标移入显示，移开便隐身。");
            stack.Children.Add(ModeCard("01", "背单词", "一张卡片，一个新收获", ShowWordMenu));
            stack.Children.Add(ModeCard("02", "阅读", "打开本地 PDF，接着上次的页码", ShowReader));
            var hint = Theme.Text("拖动顶部移动窗口 · 拖动边缘调整大小\n右上角图钉固定前台", 11, Theme.Muted); hint.Margin = new Thickness(0, 20, 0, 0); stack.Children.Add(hint);
            SetPage("home", Scroll(stack));
        }
        private void ShowWordMenu()
        {
            var stack = Heading("WORDS, AT YOUR PACE", "慢慢记住。", "先想一想，再看答案。", ShowHome);
            stack.Children.Add(ModeCard("Aa", "开始背单词", "选择专四、导入词库或收藏", ShowDecks));
            stack.Children.Add(ModeCard("☆", "管理错题本", store.Favorites.Count + " 个收藏，值得再见一面", ShowFavorites));
            var import = Theme.Button("＋ 导入本地词库", ImportLibrary); import.Margin = new Thickness(0, 18, 0, 0); stack.Children.Add(import);
            stack.Children.Add(Theme.Text("支持 UTF-8 JSON / CSV · 不连接网络", 10, Theme.Muted)); SetPage("words", Scroll(stack));
        }
        private void ShowDecks()
        {
            RecordCardExposure();
            var stack = Heading("YOUR LIBRARIES", "选一本词库。", "每次只看一个词，按自己的节奏来。", ShowWordMenu);
            foreach (var library in store.Libraries)
            {
                var captured = library; stack.Children.Add(Theme.Button(library.ToString(), delegate { StartDeck(captured.Words, captured.Name, false); }, true));
            }
            stack.Children.Add(Theme.Button("☆ 收藏的单词 · " + store.Favorites.Count + " 词", delegate { StartDeck(store.Favorites, "收藏的单词", true); }));
            var import = Theme.Button("＋ 导入词库", ImportLibrary); import.Margin = new Thickness(0, 18, 0, 0); stack.Children.Add(import);
            SetPage("decks", Scroll(stack));
        }
        private void MoveCard(int direction)
        {
            RecordCardExposure();
            if (deck.Count == 0) return; cardIndex = (cardIndex + direction + deck.Count) % deck.Count; revealed = false; ShowCard(direction * 26);
        }
        private void ShowCard(double direction)
        {
            var word = deck[cardIndex];
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var back = Theme.Button("‹ " + deckName, ShowDecks); back.HorizontalAlignment = HorizontalAlignment.Stretch; back.BorderThickness = new Thickness(0);
            var deckLabel = Theme.Text("‹ " + deckName, 12); deckLabel.TextWrapping = TextWrapping.NoWrap; deckLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            back.Content = deckLabel; back.ToolTip = deckName; header.Children.Add(back);
            var position = new StackPanel { Orientation = Orientation.Horizontal };
            var count = Theme.Text((cardIndex + 1) + " / " + deck.Count, 11, Theme.Muted); count.Margin = new Thickness(5, 0, 8, 0); position.Children.Add(count);
            var jumpButton = Theme.Button("跳转", ShowJumpDialog); jumpButton.Padding = new Thickness(9, 7, 9, 7); position.Children.Add(jumpButton);
            Grid.SetColumn(position, 1); header.Children.Add(position); layout.Children.Add(header);
            var body = new StackPanel { Margin = new Thickness(12, 26, 12, 10), VerticalAlignment = VerticalAlignment.Center };
            var wordText = Theme.Text(word.English, word.English.Length > 20 ? 27 : 35); wordText.FontFamily = new FontFamily("Georgia"); wordText.TextAlignment = TextAlignment.Center; body.Children.Add(wordText);
            if (revealed)
            {
                var details = WordDetails(word); details.Margin = new Thickness(0, 18, 0, 0); body.Children.Add(details);
            }
            else { var prompt = Theme.Text("先在心里，想一想它的意思", 12, Theme.Muted); prompt.TextAlignment = TextAlignment.Center; prompt.Margin = new Thickness(0, 20, 0, 0); body.Children.Add(prompt); }
            var card = new Grid { Background = Brushes.Transparent };
            var scroller = Scroll(body); card.Children.Add(scroller);
            Point? gesture = null;
            card.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { gesture = e.GetPosition(card); };
            card.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (gesture.HasValue) { var delta = e.GetPosition(card) - gesture.Value; if (Math.Abs(delta.X) > 65 && Math.Abs(delta.X) > Math.Abs(delta.Y) * 1.5) MoveCard(delta.X < 0 ? 1 : -1); } gesture = null;
            };
            Grid.SetRow(card, 1); layout.Children.Add(card);
            var actions = new StackPanel();
            var main = new Grid(); main.ColumnDefinitions.Add(new ColumnDefinition()); main.ColumnDefinitions.Add(new ColumnDefinition());
            var reveal = Theme.Button(revealed ? "收起释义" : "我不认识", delegate { revealed = !revealed; ShowCard(0); }, true); reveal.Margin = new Thickness(0, 3, 5, 3); main.Children.Add(reveal);
            var favorite = Theme.Button(store.IsFavorite(word) ? "★ 已收藏" : "☆ 收藏", delegate { bool added = store.ToggleFavorite(word); ShowCard(0); Toast(added ? "已加入错题本" : "已取消收藏"); }); favorite.Margin = new Thickness(5, 3, 0, 3); Grid.SetColumn(favorite, 1); main.Children.Add(favorite); actions.Children.Add(main);
            var nav = new Grid { Margin = new Thickness(0, 12, 0, 0) }; nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) }); nav.ColumnDefinitions.Add(new ColumnDefinition()); nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            nav.Children.Add(Theme.Button("←", delegate { MoveCard(-1); }, false, "上一个单词")); var hint = Theme.Text("左右滑动 · 方向键切换", 10, Theme.Muted); hint.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(hint, 1); nav.Children.Add(hint);
            var next = Theme.Button("→", delegate { MoveCard(1); }, false, "下一个单词"); Grid.SetColumn(next, 2); nav.Children.Add(next); actions.Children.Add(nav);
            Grid.SetRow(actions, 2); layout.Children.Add(actions); SetPage("cards", layout, direction);
            studyCardView = layout; studyViewIndex = cardIndex;
            layout.Loaded += delegate { RecordCardExposure(); };
        }

        private StackPanel WordDetails(Word word)
        {
            var details = new StackPanel();
            if (!String.IsNullOrWhiteSpace(word.Phonetic)) { var phone = Theme.Text(word.Phonetic, 13, Theme.Muted); phone.FontFamily = new FontFamily("Segoe UI"); details.Children.Add(phone); }
            var meaning = Theme.Text((String.IsNullOrWhiteSpace(word.PartOfSpeech) ? "" : word.PartOfSpeech + "  ") + word.Chinese, 16); meaning.Margin = new Thickness(0, 12, 0, 12); details.Children.Add(meaning);
            if (!String.IsNullOrWhiteSpace(word.Example))
            {
                details.Children.Add(Theme.Text(word.Example, 13));
                if (!String.IsNullOrWhiteSpace(word.ExampleChinese)) { var cn = Theme.Text(word.ExampleChinese, 11, Theme.Muted); cn.Margin = new Thickness(0, 6, 0, 0); details.Children.Add(cn); }
            }
            return details;
        }

        private void ShowFavorites()
        {
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            layout.Children.Add(Heading("SAVE FOR ANOTHER DAY", "错题本", store.Favorites.Count + " 个单词 · 点开查看，再次相遇。", ShowWordMenu));
            var list = new StackPanel();
            if (store.Favorites.Count == 0) list.Children.Add(Theme.Panel(Theme.Text("这里还是空的。\n在单词卡片上点「收藏」，留给下次复习。", 13, Theme.Muted)));
            foreach (var word in store.Favorites)
            {
                var captured = word;
                var button = Theme.Button(word.English + "\n" + word.Chinese, delegate { ShowWordPopup(captured); });
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Content = Theme.Stack(Theme.Text(word.English, 17), Theme.Text(word.Chinese, 12, Theme.Muted));
                list.Children.Add(button);
            }
            var scroll = Scroll(list); Grid.SetRow(scroll, 1); layout.Children.Add(scroll); SetPage("favorites", layout);
        }
        private void ShowWordPopup(Word word)
        {
            var body = Theme.Stack(Theme.Text("收藏的单词", 10, Theme.Accent), Theme.Text(word.English, 30), WordDetails(word));
            body.Children.Add(Theme.Button("移出错题本", delegate { if (store.IsFavorite(word)) store.ToggleFavorite(word); ShowFavorites(); }));
            body.Children.Add(Theme.Button("返回列表", CloseOverlay)); OpenOverlay(Scroll(body));
        }
        private void OpenOverlay(UIElement child)
        {
            RecordCardExposure();
            content.Visibility = Visibility.Hidden; Overlay.Children.Clear(); Overlay.Children.Add(child); Overlay.Visibility = Visibility.Visible; Theme.Animate(child);
        }
        private void CloseOverlay()
        {
            if (Overlay == null) return; Overlay.Visibility = Visibility.Collapsed; Overlay.Children.Clear(); content.Visibility = Visibility.Visible;
        }

        private void ShowSettings()
        {
            var stack = Heading("MAKE IT YOURS", "轻一点，再轻一点。", "背景与文字分别调节，下方页面真实透出。", CloseOverlay);
            AddOpacitySlider(stack, "背景不透明度", store.Settings.BackgroundOpacity, delegate(double value) { store.Settings.BackgroundOpacity = value; ApplyOpacity(); });
            AddOpacitySlider(stack, "文字不透明度", store.Settings.TextOpacity, delegate(double value) { store.Settings.TextOpacity = value; ApplyOpacity(); });
            stack.Children.Add(Theme.Text("0% 完全透明  /  100% 完全可见\n文字调节也作用于按钮、图标和 PDF 内容。", 11, Theme.Muted));
            var recovery = Theme.Text("看不见窗口时：\n双击系统托盘图标恢复，或在窗口激活时按 Ctrl + Shift + R。", 11, Theme.Muted); recovery.Margin = new Thickness(0, 22, 0, 16); stack.Children.Add(recovery);
            stack.Children.Add(Theme.Button("恢复默认", delegate { RecoverVisibility(); ShowSettings(); }));
            stack.Children.Add(Theme.Button("完成", delegate { store.SaveSettings(); CloseOverlay(); }, true)); OpenOverlay(Scroll(stack));
        }
        private void AddOpacitySlider(StackPanel stack, string label, double value, Action<double> change)
        {
            var title = Theme.Text(label + "  " + Math.Round(value * 100) + "%", 13); stack.Children.Add(title);
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, TickFrequency = .01, SmallChange = .01, LargeChange = .1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 14, 0, 22) };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            slider.ValueChanged += delegate { change(slider.Value); title.Text = label + "  " + Math.Round(slider.Value * 100) + "%"; };
            slider.PreviewMouseLeftButtonUp += delegate { store.SaveSettings(); }; stack.Children.Add(slider);
        }
        private void ImportLibrary()
        {
            suspendHide = true;
            try
            {
                var dialog = new OpenFileDialog { Title = "导入本地词库", Filter = "词库文件 (*.json;*.csv)|*.json;*.csv", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                var imported = store.Import(dialog.FileName); ShowDecks(); Toast("已导入「" + imported.Name + "」· " + imported.Words.Count + " 词");
            }
            catch (Exception ex) { ShowError("导入未完成", ex.Message); }
            finally { suspendHide = false; }
        }

        private void ShowReader()
        {
            if (pdf == null)
            {
                var stack = Heading("A QUIET READING CORNER", "读几页，也很好。", "选择电脑里的 PDF，留一小块安静的空间。", ShowHome);
                stack.Children.Add(Theme.Panel(Theme.Stack(Theme.Text("▤", 42, Theme.Accent), Theme.Text("你的随身阅读角", 18), Theme.Text("支持翻页、缩放与透明阅读。\n文件始终留在这台电脑上。", 12, Theme.Muted)), 26));
                stack.Children.Add(Theme.Button("打开本地 PDF", OpenPdf, true)); SetPage("reader", Scroll(stack));
                if (pdfLoading) status.Text = "正在本地打开 PDF…";
                return;
            }
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var back = Theme.Button("‹ 主页", ShowHome); back.HorizontalAlignment = HorizontalAlignment.Left; top.Children.Add(back);
            var open = Theme.Button("打开 PDF", OpenPdf); Grid.SetColumn(open, 1); top.Children.Add(open); layout.Children.Add(top);
            var file = Theme.Text(Path.GetFileName(pdfFile), 11, Theme.Muted); file.TextTrimming = TextTrimming.CharacterEllipsis; file.TextWrapping = TextWrapping.NoWrap; file.Margin = new Thickness(0, 8, 0, 8); Grid.SetRow(file, 1); layout.Children.Add(file);
            pdfImage = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            pdfScroller = new ScrollViewer { Content = pdfImage, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent };
            Grid.SetRow(pdfScroller, 2); layout.Children.Add(pdfScroller);
            var nav = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < 7; i++) nav.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            AddAt(nav, Theme.Button("‹", delegate { ChangePdfPage(-1); }, false, "上一页"), 0);
            pdfPageLabel = Theme.Text("", 11, Theme.Muted); pdfPageLabel.HorizontalAlignment = HorizontalAlignment.Center; AddAt(nav, pdfPageLabel, 1);
            AddAt(nav, Theme.Button("›", delegate { ChangePdfPage(1); }, false, "下一页"), 2);
            AddAt(nav, Theme.Button("−", delegate { ZoomPdf(-.2); }, false, "缩小 PDF"), 3);
            pdfZoomLabel = Theme.Text("", 10, Theme.Muted); pdfZoomLabel.Margin = new Thickness(5, 0, 5, 0); AddAt(nav, pdfZoomLabel, 4);
            AddAt(nav, Theme.Button("＋", delegate { ZoomPdf(.2); }, false, "放大 PDF"), 5);
            AddAt(nav, Theme.Button("↔", FitPdf, false, "适应宽度"), 6); Grid.SetRow(nav, 3); layout.Children.Add(nav);
            SetPage("reader", layout); RenderPdf();
        }
        private void AddAt(Grid grid, UIElement item, int column) { Grid.SetColumn(item, column); grid.Children.Add(item); }
        private async void OpenPdf()
        {
            string path;
            suspendHide = true;
            try
            {
                var dialog = new OpenFileDialog { Title = "打开本地 PDF", Filter = "PDF 文档 (*.pdf)|*.pdf", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }
            catch (Exception ex) { ShowError("无法打开 PDF", ex.Message); return; }
            finally { suspendHide = false; }
            await OpenPdfPathAsync(path);
        }

        private async Task OpenPdfPathAsync(string path)
        {
            if (pdfReaderClosed) return;
            if (pdfLoadCancellation != null) { pdfLoadCancellation.Cancel(); pdfLoadCancellation.Dispose(); }
            var cancellation = new CancellationTokenSource();
            pdfLoadCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            int request = ++pdfLoadVersion;
            pdfLoading = true; status.Text = "正在本地打开 PDF…";
            try
            {
                // Cancel queued opens before they allocate another PDF buffer in this x86 process.
                await pdfOpenGate.WaitAsync(token);
                LoadedPdf loaded;
                try { loaded = await Task.Run(delegate { return LoadPdf(path, token); }); }
                finally { pdfOpenGate.Release(); }
                if (pdfReaderClosed || request != pdfLoadVersion)
                {
                    DisposePdfInBackground(loaded.Document);
                    return;
                }
                PdfDocument previous = pdf;
                pdf = loaded.Document; pdfFile = path; pdfPage = 0; pdfPageCount = loaded.PageCount;
                pdfPageWidths.Clear(); pdfPageWidths.Add(0, loaded.FirstPageWidth);
                pdfDocumentVersion++; pdfFitRequested = false;
                pdfZoom = Math.Max(.25, Math.Min(2, (ActualWidth - 64) / loaded.FirstPageWidth));
                DisposePdfInBackground(previous);
                // Navigation during a slow open stays where the user put it.
                if (screen == "reader") ShowReader();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!pdfReaderClosed && request == pdfLoadVersion)
                {
                    if (screen == "reader") ShowError("无法打开 PDF", ex.Message);
                    else Toast("PDF 未打开：" + ex.Message);
                }
            }
            finally
            {
                if (request == pdfLoadVersion)
                {
                    pdfLoading = false;
                    if (pdfLoadCancellation == cancellation) pdfLoadCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private static LoadedPdf LoadPdf(string path, CancellationToken token)
        {
            PdfDocument document = null;
            try
            {
                token.ThrowIfCancellationRequested();
                document = new PdfDocument(path);
                token.ThrowIfCancellationRequested();
                int count = document.PageCount;
                double width = document.GetPageWidth(0);
                token.ThrowIfCancellationRequested();
                return new LoadedPdf { Document = document, PageCount = count, FirstPageWidth = width };
            }
            catch { if (document != null) document.Dispose(); throw; }
        }

        private void ChangePdfPage(int offset)
        {
            if (pdf == null || pdfReaderClosed) return;
            pdfPage = Math.Max(0, Math.Min(pdfPageCount - 1, pdfPage + offset));
            pdfFitRequested = false; RenderPdf();
            if (pdfScroller != null) pdfScroller.ScrollToTop();
        }

        private void ZoomPdf(double offset)
        {
            if (pdf == null || pdfReaderClosed) return;
            pdfZoom = Math.Round(Math.Max(.25, Math.Min(3, pdfZoom + offset)), 2);
            pdfFitRequested = false; RenderPdf();
        }

        private void FitPdf()
        {
            if (pdf == null || pdfReaderClosed) return;
            double width;
            if (pdfPageWidths.TryGetValue(pdfPage, out width))
            {
                pdfZoom = Math.Max(.25, Math.Min(3, PdfViewportWidth() / width));
                pdfFitRequested = false;
            }
            else pdfFitRequested = true;
            RenderPdf();
        }

        private double PdfViewportWidth()
        {
            return Math.Max(100, pdfScroller != null && pdfScroller.ActualWidth > 0 ? pdfScroller.ActualWidth - 22 : ActualWidth - 64);
        }

        private async void RenderPdf()
        {
            if (pdf == null || pdfReaderClosed) return;
            pdfRenderVersion++; pdfRenderPending = true;
            if (pdfRendering) return;
            pdfRendering = true;
            try
            {
                // At most one render runs. Additional clicks replace the pending request.
                while (pdfRenderPending && !pdfReaderClosed && pdf != null)
                {
                    pdfRenderPending = false;
                    PdfDocument document = pdf;
                    int documentVersion = pdfDocumentVersion, request = pdfRenderVersion, page = pdfPage;
                    double scale = pdfZoom, viewportWidth = PdfViewportWidth();
                    bool fit = pdfFitRequested;
                    if (screen == "reader") status.Text = "正在本地渲染第 " + (page + 1) + " 页…";
                    RenderedPdf rendered = null;
                    Exception error = null;
                    try
                    {
                        rendered = await Task.Run(delegate
                        {
                            double width = document.GetPageWidth(page);
                            double renderScale = fit ? Math.Max(.25, Math.Min(3, viewportWidth / width)) : scale;
                            return new RenderedPdf { Bitmap = document.Render(page, renderScale), PageWidth = width, Scale = renderScale };
                        });
                    }
                    catch (Exception ex) { error = ex; }
                    if (pdfReaderClosed || documentVersion != pdfDocumentVersion || document != pdf) continue;
                    if (rendered != null) pdfPageWidths[page] = rendered.PageWidth;
                    if (request != pdfRenderVersion || screen != "reader") continue;
                    if (error != null) { ShowError("这一页未能显示", error.Message); continue; }
                    pdfZoom = rendered.Scale; pdfFitRequested = false;
                    pdfImage.Source = rendered.Bitmap;
                    pdfPageLabel.Text = (page + 1) + " / " + pdfPageCount;
                    pdfZoomLabel.Text = Math.Round(pdfZoom * 100) + "%";
                    status.Text = "本地 · 离开即隐身";
                    Diagnostics.Record("pdf", new { Page = page + 1, Pages = pdfPageCount, Scale = pdfZoom });
                }
            }
            catch (Exception ex)
            {
                if (!pdfReaderClosed && screen == "reader") ShowError("这一页未能显示", ex.Message);
            }
            finally { pdfRendering = false; }
        }

        private void ClosePdfReader()
        {
            pdfReaderClosed = true; pdfLoadVersion++; pdfDocumentVersion++; pdfRenderVersion++;
            pdfRenderPending = false;
            if (pdfLoadCancellation != null)
            {
                pdfLoadCancellation.Cancel(); pdfLoadCancellation.Dispose(); pdfLoadCancellation = null;
            }
            PdfDocument previous = pdf; pdf = null;
            DisposePdfInBackground(previous);
        }

        private static void DisposePdfInBackground(PdfDocument document)
        {
            if (document == null) return;
            Task.Run(delegate
            {
                try { document.Dispose(); }
                catch (Exception ex) { Diagnostics.Record("pdf-disposal-error", new { Message = ex.Message }); }
            });
        }
        private void Toast(string message) { status.Text = message; }
        private void ShowError(string title, string message)
        {
            var body = Heading("请稍等一下", title, message); body.Children.Add(Theme.Button("知道了", CloseOverlay, true)); OpenOverlay(Scroll(body));
        }
    }
}
