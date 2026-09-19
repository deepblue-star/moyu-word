using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MoyuWord
{
    public partial class MainWindow
    {
        private string studyDeckKey, savedStudyDeckKey, savedStudyEnglish;
        private int savedStudyIndex = -1, studyViewIndex;
        private FrameworkElement studyCardView;
        private DateTime nextStudySaveAttempt;

        private void StartDeck(List<Word> words, string name, bool fromFavorites)
        {
            if (words.Count == 0) { Toast("这里还没有单词，先收藏几个喜欢的词吧。"); return; }
            var library = store.Libraries.FirstOrDefault(item => Object.ReferenceEquals(item.Words, words));
            string key = fromFavorites ? "favorites" : library == null ? "deck:" + name : library.Id;
            var selectedWords = new List<Word>(words);
            int? previous = store.GetStudyIndex(key, selectedWords);
            if (!previous.HasValue) { BeginStudyDeck(selectedWords, name, key, 0); return; }

            int index = previous.Value;
            var body = Heading("PICK UP WHERE YOU LEFT OFF", "继续上次？", name + "\n上次看到第 " + (index + 1) + " / " + selectedWords.Count + " 个单词");
            var word = Theme.Text(selectedWords[index].English, 28); word.Margin = new Thickness(0, 4, 0, 22); body.Children.Add(word);
            body.Children.Add(Theme.Button("继续上次", delegate { BeginStudyDeck(selectedWords, name, key, index); }, true));
            body.Children.Add(Theme.Button("从头开始", delegate { BeginStudyDeck(selectedWords, name, key, 0); }));
            body.Children.Add(Theme.Button("取消", CloseOverlay));
            OpenOverlay(Scroll(body));
        }

        private void BeginStudyDeck(List<Word> words, string name, string key, int index)
        {
            deck = words; deckName = name; studyDeckKey = key;
            cardIndex = index; revealed = false; nextStudySaveAttempt = DateTime.MinValue;
            ShowCard(12);
        }

        private void ShowJumpDialog()
        {
            if (screen != "cards" || deck.Count == 0) return;
            var body = Heading("GO TO A WORD", "跳转到第几个词？", "当前第 " + (cardIndex + 1) + " 个，共 " + deck.Count + " 个单词。");
            var input = new TextBox { Text = (cardIndex + 1).ToString(CultureInfo.InvariantCulture), FontSize = 20,
                Padding = new Thickness(12, 9, 12, 9), BorderBrush = Theme.Accent, BorderThickness = new Thickness(1),
                Background = System.Windows.Media.Brushes.Transparent, Foreground = Theme.Ink, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(input, "目标单词序号");
            AutomationProperties.SetAutomationId(input, "JumpWordNumber");
            var error = Theme.Text("请输入 1 ～ " + deck.Count + " 的整数", 12, Theme.Muted);
            error.Margin = new Thickness(0, 0, 0, 18);
            Action jump = delegate
            {
                int number;
                if (!Int32.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1 || number > deck.Count)
                {
                    error.Text = "请输入 1 ～ " + deck.Count + " 之间的整数。";
                    error.Foreground = Theme.Brush("#A85742"); input.Focus(); input.SelectAll(); return;
                }
                int offset = number - 1 - cardIndex;
                cardIndex = number - 1; revealed = false;
                ShowCard(offset == 0 ? 0 : offset > 0 ? 26 : -26);
            };
            input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { jump(); e.Handled = true; } };
            input.Loaded += delegate { input.Focus(); input.SelectAll(); };
            body.Children.Add(input); body.Children.Add(error);
            body.Children.Add(Theme.Button("跳转到此单词", jump, true));
            body.Children.Add(Theme.Button("取消", CloseOverlay));
            OpenOverlay(Scroll(body));
        }

        private void RecordCardExposure()
        {
            // Record the word actually shown, never a hidden navigation target or a modal's preview.
            if (!IsLoaded || !IsVisible || isHidden || WindowState == WindowState.Minimized || screen != "cards" ||
                Overlay.Visibility == Visibility.Visible || content.Visibility != Visibility.Visible ||
                Root.Opacity <= 0 || Surface.Opacity <= 0 || studyCardView == null || studyCardView.Opacity <= 0 ||
                content.Content != studyCardView || studyViewIndex != cardIndex || deck.Count == 0 || String.IsNullOrEmpty(studyDeckKey)) return;
            string english = deck[cardIndex].English;
            if (savedStudyDeckKey == studyDeckKey && savedStudyIndex == cardIndex && savedStudyEnglish == english) return;
            if (DateTime.UtcNow < nextStudySaveAttempt) return;
            try
            {
                store.SaveStudyPosition(studyDeckKey, deck, cardIndex);
                savedStudyDeckKey = studyDeckKey; savedStudyIndex = cardIndex; savedStudyEnglish = english;
                Diagnostics.Record("study-position", new { Deck = studyDeckKey, Index = cardIndex, Number = cardIndex + 1, English = english });
            }
            catch (IOException ex)
            {
                nextStudySaveAttempt = DateTime.UtcNow.AddSeconds(2);
                Toast("学习位置暂未保存，请检查数据目录是否可写。");
                Diagnostics.Record("study-save-error", new { Message = ex.Message });
            }
        }
    }
}
