using System;
using System.Collections.Generic;

namespace MoyuWord
{
    public sealed class Word
    {
        public string Id { get; set; }
        public string English { get; set; }
        public string Chinese { get; set; }
        public string PartOfSpeech { get; set; }
        public string Phonetic { get; set; }
        public string Example { get; set; }
        public string ExampleChinese { get; set; }
    }

    public sealed class WordLibrary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public List<Word> Words { get; set; }
        public override string ToString() { return Name + " · " + (Words == null ? 0 : Words.Count) + " 词"; }
    }

    public sealed class AppSettings
    {
        public double BackgroundOpacity { get; set; }
        public double TextOpacity { get; set; }
        public bool Topmost { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public AppSettings()
        {
            BackgroundOpacity = 0.94; TextOpacity = 1; Topmost = true;
            Left = -1; Top = -1; Width = 440; Height = 550;
        }
    }
}
