using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MoyuWord
{
    public sealed class LibraryStore
    {
        private const int MaxFileBytes = 16 * 1024 * 1024;
        private const int MaxWords = 20000;
        private readonly JavaScriptSerializer json;
        private List<Word> favorites;
        private Dictionary<string, StudyProgress> progress;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public LibraryStore(string dataDirectory = null)
        {
            IsPortable = dataDirectory == null && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.flag"));
            DataDirectory = Path.GetFullPath(dataDirectory ?? GetDefaultDataDirectory(AppDomain.CurrentDomain.BaseDirectory));
            json = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes, RecursionLimit = 32 };
            Libraries = new List<WordLibrary>();
            Warnings = new List<string>();
            Settings = new AppSettings();
            favorites = new List<Word>();
            progress = new Dictionary<string, StudyProgress>(StringComparer.Ordinal);
        }

        public List<WordLibrary> Libraries { get; private set; }
        public AppSettings Settings { get; private set; }
        public List<string> Warnings { get; private set; }
        public List<Word> Favorites
        {
            get { List<Word> copy = new List<Word>(); foreach (Word word in favorites) copy.Add(Clone(word)); return copy; }
        }
        public string DataDirectory { get; private set; }
        public bool IsPortable { get; private set; }
        public string InstanceKey
        {
            get { return "Local\\MoyuWord.App." + Digest(DataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()); }
        }
        internal static string GetDefaultDataDirectory(string applicationDirectory)
        {
            return File.Exists(Path.Combine(applicationDirectory, "portable.flag"))
                ? Path.Combine(applicationDirectory, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoyuWord");
        }

        public void Load()
        {
            EnsureDirectories();
            Warnings.Clear();
            string builtinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "tem4.json");
            if (!File.Exists(builtinPath)) throw new InvalidDataException("未找到内置专四词库 assets\\tem4.json，请重新完整解压或安装应用。");
            WordLibrary builtin = ParseLibrary(ReadUtf8(builtinPath), ".json", "专四核心词汇 · TEM-4");
            builtin.Id = "builtin-tem4";
            List<WordLibrary> loaded = new List<WordLibrary> { builtin };
            string[] files = Directory.GetFiles(Path.Combine(DataDirectory, "libraries"), "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                try
                {
                    WordLibrary library = ParseLibrary(ReadUtf8(file), ".json", Path.GetFileNameWithoutExtension(file));
                    library.Id = LibraryId(library);
                    if (ids.Add(library.Id)) loaded.Add(library);
                }
                catch (Exception ex)
                {
                    if (!IsRecoverable(ex)) throw;
                    Warnings.Add("已跳过无法读取的词库 " + Path.GetFileName(file) + "：" + ex.Message + " 原文件已保留。");
                }
            }
            Libraries = loaded;
            Settings = LoadOptional("settings.json", delegate(string text)
            {
                AppSettings value = json.Deserialize<AppSettings>(text);
                if (value == null) throw new InvalidDataException("设置 JSON 不能为空。");
                NormalizeSettings(value);
                return value;
            }, new AppSettings());
            favorites = LoadOptional("favorites.json", delegate(string text)
            {
                List<Word> value = json.Deserialize<List<Word>>(text);
                return NormalizeWords(value, "收藏");
            }, new List<Word>());
            progress = LoadOptional("progress.json", delegate(string text)
            {
                Dictionary<string, StudyProgress> value = json.Deserialize<Dictionary<string, StudyProgress>>(text);
                if (value == null) throw new InvalidDataException("学习进度 JSON 不能为空。");
                foreach (KeyValuePair<string, StudyProgress> item in value)
                {
                    ValidateDeckId(item.Key);
                    if (item.Value == null) throw new InvalidDataException("学习进度记录不能为空。");
                    item.Value.English = Field(item.Value.English, 256, "进度单词", 0, true);
                }
                return new Dictionary<string, StudyProgress>(value, StringComparer.Ordinal);
            }, new Dictionary<string, StudyProgress>(StringComparer.Ordinal));
        }

        public int? GetStudyIndex(string deckId, IList<Word> words)
        {
            ValidateDeckId(deckId);
            if (words == null) throw new ArgumentNullException("words");
            StudyProgress saved;
            if (words.Count == 0 || !progress.TryGetValue(deckId, out saved)) return null;
            string key = EnglishKey(saved.English);
            for (int i = 0; i < words.Count; i++)
                if (words[i] != null && !String.IsNullOrWhiteSpace(words[i].English) && EnglishKey(words[i].English) == key) return i;
            return Math.Max(0, Math.Min(words.Count - 1, saved.Index));
        }

        public void SaveStudyPosition(string deckId, IList<Word> words, int index)
        {
            ValidateDeckId(deckId);
            if (words == null) throw new ArgumentNullException("words");
            if (index < 0 || index >= words.Count) throw new ArgumentOutOfRangeException("index", "学习位置必须在当前词库范围内。");
            if (words[index] == null) throw new InvalidDataException("进度单词不能为空。");
            string english = Field(words[index].English, 256, "进度单词", 0, true);
            StudyProgress saved;
            if (progress.TryGetValue(deckId, out saved) && saved.Index == index && EnglishKey(saved.English) == EnglishKey(english)) return;
            Dictionary<string, StudyProgress> next = new Dictionary<string, StudyProgress>(progress, StringComparer.Ordinal);
            next[deckId] = new StudyProgress { Index = index, English = english, UpdatedUtc = DateTime.UtcNow.ToString("o") };
            EnsureDirectories();
            WriteAtomic(Path.Combine(DataDirectory, "progress.json"), json.Serialize(next));
            progress = next;
        }

        private static void ValidateDeckId(string deckId)
        {
            if (String.IsNullOrWhiteSpace(deckId)) throw new ArgumentException("学习进度需要有效的词库标识。", "deckId");
        }

        public WordLibrary Import(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请选择 UTF-8 编码的 JSON 或 CSV 词库文件。");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".json" && extension != ".csv") throw new InvalidDataException("仅支持 UTF-8 JSON 或 CSV 文件，请检查扩展名。");
            WordLibrary library = ParseLibrary(ReadUtf8(path), extension, Path.GetFileNameWithoutExtension(path));
            library.Id = LibraryId(library);
            EnsureDirectories();
            WriteAtomic(Path.Combine(DataDirectory, "libraries", library.Id + ".json"), json.Serialize(library));
            int existing = Libraries.FindIndex(delegate(WordLibrary item) { return item.Id == library.Id; });
            if (existing >= 0) Libraries[existing] = library; else Libraries.Add(library);
            return library;
        }

        public bool IsFavorite(Word word)
        {
            if (word == null || String.IsNullOrWhiteSpace(word.English)) return false;
            string key = EnglishKey(word.English);
            return favorites.Exists(delegate(Word item) { return EnglishKey(item.English) == key; });
        }

        public bool ToggleFavorite(Word word)
        {
            Word snapshot = NormalizeWord(word, 1);
            List<Word> next = Favorites;
            string key = EnglishKey(snapshot.English);
            int index = next.FindIndex(delegate(Word item) { return EnglishKey(item.English) == key; });
            bool added = index < 0;
            if (added)
            {
                if (next.Count >= MaxWords) throw new InvalidDataException("收藏最多支持 20000 个单词，请先整理收藏。");
                next.Add(snapshot);
            }
            else next.RemoveAt(index);
            EnsureDirectories();
            WriteAtomic(Path.Combine(DataDirectory, "favorites.json"), json.Serialize(next));
            favorites = next;
            return added;
        }

        public void SaveSettings()
        {
            NormalizeSettings(Settings);
            EnsureDirectories();
            WriteAtomic(Path.Combine(DataDirectory, "settings.json"), json.Serialize(Settings));
        }

        private void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(Path.Combine(DataDirectory, "libraries"));
                // Do not silently fall back to the host profile for a read-only USB drive.
                if (IsPortable)
                {
                    string probe = Path.Combine(DataDirectory, ".write-check-" + Guid.NewGuid().ToString("N"));
                    using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                throw new IOException((IsPortable ? "便携版需要写入程序旁的 data 文件夹，请检查 U 盘是否只读、空间是否充足。未改存到其他位置：" : "无法写入本地数据目录，请检查磁盘空间和写入权限：") + DataDirectory, ex);
            }
        }

        private WordLibrary ParseLibrary(string text, string extension, string defaultName)
        {
            WordLibrary library;
            if (extension == ".csv") library = new WordLibrary { Name = defaultName, Source = "用户导入 CSV", Words = ParseCsv(text) };
            else
            {
                try
                {
                    object root = json.DeserializeObject(text);
                    if (root is object[]) library = new WordLibrary { Name = defaultName, Words = json.ConvertToType<List<Word>>(root) };
                    else if (root is Dictionary<string, object>) library = json.ConvertToType<WordLibrary>(root);
                    else throw new InvalidDataException("JSON 顶层应为含 Words 的词库对象，或单词数组。");
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    if (!IsRecoverable(ex)) throw;
                    throw new InvalidDataException("JSON 格式无效，请检查引号、逗号以及 Words 单词数组。" + ex.Message, ex);
                }
            }
            library.Name = Field(String.IsNullOrWhiteSpace(library.Name) ? defaultName : library.Name, 128, "词库名称", 0, true);
            library.Source = Field(library.Source, 2048, "来源", 0, false);
            if (String.IsNullOrWhiteSpace(library.Source)) library.Source = "用户导入 JSON";
            library.Words = NormalizeWords(library.Words, "词库");
            if (library.Words.Count == 0) throw new InvalidDataException("词库没有单词；请至少提供一条英文和中文释义。");
            return library;
        }

        private static List<Word> NormalizeWords(List<Word> words, string description)
        {
            if (words == null) throw new InvalidDataException(description + "缺少 Words 单词数组。");
            if (words.Count > MaxWords) throw new InvalidDataException(description + "最多支持 20000 条单词，请拆分文件后导入。");
            List<Word> output = new List<Word>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < words.Count; i++)
            {
                Word word = NormalizeWord(words[i], i + 1);
                if (keys.Add(EnglishKey(word.English))) output.Add(word);
            }
            return output;
        }

        private static Word NormalizeWord(Word word, int row)
        {
            if (word == null) throw new InvalidDataException("第 " + row + " 条单词为空。");
            Word output = new Word
            {
                English = Field(word.English, 256, "英文", row, true),
                Chinese = Field(word.Chinese, 2048, "中文释义", row, true),
                PartOfSpeech = Field(word.PartOfSpeech, 128, "词性", row, false),
                Phonetic = Field(word.Phonetic, 256, "音标", row, false),
                Example = Field(word.Example, 8192, "例句", row, false),
                ExampleChinese = Field(word.ExampleChinese, 8192, "例句中文", row, false)
            };
            output.Id = "word-" + Digest(EnglishKey(output.English));
            return output;
        }

        private static string Field(string text, int maxLength, string label, int row, bool required)
        {
            string value = (text ?? "").Trim();
            string prefix = row > 0 ? "第 " + row + " 条单词的" : "";
            if (required && value.Length == 0) throw new InvalidDataException(prefix + label + "不能为空，请补全后导入。");
            if (value.Length > maxLength) throw new InvalidDataException(prefix + label + "长度超过 " + maxLength + " 字符，请缩短后导入。");
            for (int i = 0; i < value.Length; i++)
                if (Char.IsControl(value[i]) && value[i] != '\r' && value[i] != '\n' && value[i] != '\t')
                    throw new InvalidDataException(prefix + label + "含不支持的控制字符，请检查 UTF-8 编码。");
            return value;
        }

        private static string EnglishKey(string english) { return english.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant(); }
        private string LibraryId(WordLibrary library) { return "import-" + Digest(json.Serialize(library.Words)); }
        private static string Digest(string text)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        private static Word Clone(Word w) { return new Word { Id = w.Id, English = w.English, Chinese = w.Chinese, PartOfSpeech = w.PartOfSpeech, Phonetic = w.Phonetic, Example = w.Example, ExampleChinese = w.ExampleChinese }; }

        private static List<Word> ParseCsv(string text)
        {
            List<List<string>> rows = CsvRows(text);
            if (rows.Count == 0) throw new InvalidDataException("CSV 文件为空，需要 english,chinese 表头。");
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows[0].Count; i++)
            {
                string name = rows[0][i].Trim();
                if (name.Length == 0 || columns.ContainsKey(name)) throw new InvalidDataException("CSV 表头为空或重复，请使用唯一的 english,chinese 等列名。");
                columns.Add(name, i);
            }
            if (!columns.ContainsKey("english") || !columns.ContainsKey("chinese")) throw new InvalidDataException("CSV 表头必须包含 english 和 chinese；可选 partOfSpeech,phonetic,example,exampleChinese。");
            List<Word> words = new List<Word>();
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> row = rows[i];
                if (row.Count != rows[0].Count) throw new InvalidDataException("CSV 第 " + (i + 1) + " 条记录的列数与表头不一致；含逗号的内容请加双引号。");
                words.Add(new Word { English = CsvValue(row, columns, "english"), Chinese = CsvValue(row, columns, "chinese"), PartOfSpeech = CsvValue(row, columns, "partOfSpeech"), Phonetic = CsvValue(row, columns, "phonetic"), Example = CsvValue(row, columns, "example"), ExampleChinese = CsvValue(row, columns, "exampleChinese") });
            }
            return words;
        }

        private static string CsvValue(List<string> row, Dictionary<string, int> columns, string name) { int i; return columns.TryGetValue(name, out i) ? row[i] : ""; }

        // RFC 4180 quoted fields, escaped quotes and multiline records; blank lines are ignored.
        private static List<List<string>> CsvRows(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false, closed = false, began = false;
            for (int i = 0; i <= text.Length; i++)
            {
                bool end = i == text.Length;
                char c = end ? '\n' : text[i];
                if (quoted)
                {
                    if (end) throw new InvalidDataException("CSV 双引号未闭合，请检查含换行或逗号的字段。");
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else { quoted = false; closed = true; }
                    }
                    else field.Append(c);
                }
                else if (c == ',' || c == '\r' || c == '\n')
                {
                    row.Add(field.ToString()); field.Length = 0; closed = false; began = false;
                    if (c != ',')
                    {
                        if (row.Count != 1 || row[0].Length != 0) rows.Add(row);
                        row = new List<string>();
                        if (!end && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        if (rows.Count > MaxWords + 1) throw new InvalidDataException("CSV 最多支持 20000 条单词，请拆分文件后导入。");
                    }
                }
                else if (c == '"')
                {
                    if (began || closed) throw new InvalidDataException("CSV 引号只能出现在字段开头；字段内的引号请写成两个双引号。");
                    quoted = true; began = true;
                }
                else
                {
                    if (closed) throw new InvalidDataException("CSV 结束引号后只能接逗号或换行。");
                    field.Append(c); began = true;
                }
                if (field.Length > 8192) throw new InvalidDataException("CSV 字段长度超过 8192 字符，请缩短后导入。");
                if (row.Count > 64) throw new InvalidDataException("CSV 列数超过 64 列，请只保留词库相关列。");
            }
            return rows;
        }

        private static string ReadUtf8(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("文件超过 16 MB，请拆分词库后导入。");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxFileBytes) throw new InvalidDataException("文件超过 16 MB，请拆分词库后导入。");
                int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                return Utf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("文件不是有效 UTF-8，请用编辑器另存为 UTF-8 后再导入。", ex); }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                throw new InvalidDataException("无法读取文件，请检查文件是否存在或被占用：" + Path.GetFileName(path), ex);
            }
        }

        private T LoadOptional<T>(string filename, Func<string, T> parse, T fallback)
        {
            string path = Path.Combine(DataDirectory, filename);
            foreach (string candidate in new string[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    T result = parse(ReadUtf8(candidate));
                    if (candidate != path) Warnings.Add(filename + " 已从备份恢复；原文件已保留。");
                    return result;
                }
                catch (Exception ex)
                {
                    if (!IsRecoverable(ex)) throw;
                    Warnings.Add("无法读取 " + Path.GetFileName(candidate) + "：" + ex.Message + " 原文件已保留。");
                }
            }
            return fallback;
        }

        private static bool IsRecoverable(Exception ex) { return ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is OverflowException; }

        private static void NormalizeSettings(AppSettings settings)
        {
            AppSettings defaults = new AppSettings();
            settings.BackgroundOpacity = Bound(settings.BackgroundOpacity, 0, 1, defaults.BackgroundOpacity);
            settings.TextOpacity = Bound(settings.TextOpacity, 0, 1, defaults.TextOpacity);
            settings.Width = Bound(settings.Width, 280, 6000, defaults.Width);
            settings.Height = Bound(settings.Height, 280, 6000, defaults.Height);
            settings.Left = Bound(settings.Left, -100000, 100000, defaults.Left);
            settings.Top = Bound(settings.Top, -100000, 100000, defaults.Top);
        }
        private static double Bound(double number, double min, double max, double fallback) { return Double.IsNaN(number) || Double.IsInfinity(number) ? fallback : Math.Max(min, Math.Min(max, number)); }

        private static void WriteAtomic(string path, string text)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] data = Utf8.GetBytes(text);
                if (data.Length > MaxFileBytes) throw new InvalidDataException("保存内容超过 16 MB，请减少单词数量或缩短例句后再试。");
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(data, 0, data.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                throw new IOException("保存失败，请检查磁盘空间和数据目录写入权限：" + Path.GetDirectoryName(path), ex);
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
    }
}
