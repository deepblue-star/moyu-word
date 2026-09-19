using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoyuWord;

internal static class StoreTests
{
    private static int passed;
    private static string root;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "MoyuWord-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("UTF-8 CSV, quoted commas/newlines and English deduplication", CsvImport);
            Run("JSON identifiers cannot alias different English words", JsonIdentity);
            Run("Favorites survive restart and removed imported file", FavoriteSnapshot);
            Run("Invalid import leaves libraries and disk unchanged", InvalidImport);
            Run("Malformed CSV quote and column count rejected", InvalidCsv);
            Run("File and field size limits enforced", Limits);
            Run("Settings persist with safe numeric bounds", SettingsPersistence);
            Run("Corrupt state recovers backup without losing original", CorruptRecovery);
            Run("Unusable state and import do not prevent startup", CorruptStartup);
            Run("Downloaded TEM-4 asset is complete and locally loadable", BuiltIn);
            Console.WriteLine("PASS: " + passed + " storage tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Folder() { string p = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static string Input(string name, string text) { string p = Path.Combine(Folder(), name); File.WriteAllText(p, text, Utf8); return p; }
    private static LibraryStore Store() { LibraryStore s = new LibraryStore(Folder()); s.Load(); return s; }
    private static void Reject(Action action, string contains)
    {
        try { action(); } catch (InvalidDataException ex) { Check(ex.Message.Contains(contains), "Unhelpful error: " + ex.Message); return; }
        throw new Exception("Invalid input accepted; expected " + contains);
    }

    private static void CsvImport()
    {
        LibraryStore store = Store();
        WordLibrary lib = store.Import(Input("中文词库.csv", "english,chinese,partOfSpeech,phonetic,example,exampleChinese\r\nApple,苹果,n.,ˈæpəl,\"An apple,\r\nplease.\",\"一个\"\"苹果\"\"。\"\r\napple,重复,n.,,,\r\npear,梨,n.,,,\r\n"));
        Check(lib.Words.Count == 2, "Expected duplicate English to be merged");
        Check(lib.Words[0].Chinese == "苹果" && lib.Words[0].Example == "An apple,\r\nplease.", "CSV quoting/UTF-8 damaged");
        Check(lib.Words[0].ExampleChinese == "一个\"苹果\"。", "Escaped quote damaged");
        LibraryStore reloaded = new LibraryStore(store.DataDirectory); reloaded.Load();
        Check(reloaded.Libraries.Count == store.Libraries.Count, "Import not persisted");
        Check(reloaded.Libraries[reloaded.Libraries.Count - 1].Words.Count == 2, "Persisted imported words missing");
        store.Import(Input("same.csv", "english,chinese\napple,苹果\npear,梨\n"));
        Check(store.Libraries.Count >= 2, "Import should remain usable");
    }

    private static void JsonIdentity()
    {
        LibraryStore store = Store();
        WordLibrary lib = store.Import(Input("one.json", "{\"id\":\"../escape\",\"name\":\"测试\",\"words\":[{\"id\":\"same\",\"english\":\"  alpha  \",\"chinese\":\"甲\"},{\"id\":\"same\",\"english\":\"beta\",\"chinese\":\"乙\"},{\"id\":\"different\",\"english\":\"ALPHA\",\"chinese\":\"重复\"}]}"));
        Check(lib.Words.Count == 2 && lib.Words[0].English == "alpha", "JSON normalizing/dedup failed");
        Check(lib.Words[0].Id != lib.Words[1].Id, "Duplicate incoming IDs aliased words");
        store.ToggleFavorite(lib.Words[0]);
        Check(!store.IsFavorite(lib.Words[1]), "Favorite keyed only by supplied ID");
        Check(store.IsFavorite(new Word { Id = "unrelated", English = "ALPHA", Chinese = "甲" }), "English identity not stable across libraries");
        WordLibrary array = store.Import(Input("array.json", "[{\"English\":\"gamma\",\"Chinese\":\"丙\"}]"));
        Check(array.Words.Count == 1, "JSON word arrays not supported");
    }

    private static void FavoriteSnapshot()
    {
        LibraryStore store = Store();
        Word word = store.Import(Input("favorite.csv", "english,chinese,example\nsnapshot,快照,Keep me.\n")).Words[0];
        Check(store.ToggleFavorite(word), "Add returned false");
        word.Chinese = "changed outside";
        Check(store.Favorites[0].Chinese == "快照", "Favorite did not retain an independent snapshot");
        List<Word> external = store.Favorites; external.Clear();
        Check(store.Favorites.Count == 1, "Caller can remove persisted favorite via snapshot");
        foreach (string file in Directory.GetFiles(Path.Combine(store.DataDirectory, "libraries"), "*.json")) File.Delete(file);
        LibraryStore reload = new LibraryStore(store.DataDirectory); reload.Load();
        Check(reload.Favorites.Count == 1 && reload.Favorites[0].Example == "Keep me.", "Favorite vanished with import");
        Check(!reload.ToggleFavorite(reload.Favorites[0]), "Remove returned true");
        LibraryStore final = new LibraryStore(store.DataDirectory); final.Load(); Check(final.Favorites.Count == 0, "Removal not persisted");
    }

    private static void InvalidImport()
    {
        LibraryStore store = Store(); int before = store.Libraries.Count;
        Reject(delegate { store.Import(Input("bad.json", "{\"Words\":[{\"English\":\"valid\",\"Chinese\":\"有效\"},{\"English\":\"invalid\"}]}")); }, "中文");
        Reject(delegate { store.Import(Input("bad.json", "null")); }, "JSON");
        Reject(delegate { store.Import(Input("bad.txt", "anything")); }, "JSON");
        Check(store.Libraries.Count == before, "Failed import changed memory");
        Check(Directory.GetFiles(Path.Combine(store.DataDirectory, "libraries"), "*.json").Length == 0, "Failed import changed disk");
    }

    private static void InvalidCsv()
    {
        LibraryStore store = Store();
        Reject(delegate { store.Import(Input("bad.csv", "english,chinese\n\"word,词\n")); }, "引号");
        Reject(delegate { store.Import(Input("bad.csv", "english,chinese\nword,词,extra\n")); }, "列");
        Reject(delegate { store.Import(Input("bad.csv", "english,chinese\nwo\"rd,词\n")); }, "引号");
        Reject(delegate { store.Import(Input("bad.csv", "english,english\nword,词\n")); }, "表头");
    }

    private static void Limits()
    {
        LibraryStore store = Store();
        Reject(delegate { store.Import(Input("huge.csv", "english,chinese\n" + new string('x', 300) + ",词\n")); }, "长度");
        string file = Path.Combine(Folder(), "huge.json"); using (FileStream s = File.Create(file)) s.SetLength(16 * 1024 * 1024 + 1);
        Reject(delegate { store.Import(file); }, "16 MB");
        string invalid = Path.Combine(Folder(), "invalid.csv"); File.WriteAllBytes(invalid, new byte[] { 0xff, 0xfe, 0xff });
        Reject(delegate { store.Import(invalid); }, "UTF-8");
    }

    private static void SettingsPersistence()
    {
        LibraryStore store = Store(); store.Settings.BackgroundOpacity = 0; store.Settings.TextOpacity = 0.31;
        store.Settings.Topmost = false; store.Settings.Width = 620; store.Settings.Left = 175; store.SaveSettings();
        LibraryStore reload = new LibraryStore(store.DataDirectory); reload.Load();
        Check(reload.Settings.BackgroundOpacity == 0 && Math.Abs(reload.Settings.TextOpacity - 0.31) < 0.0001, "Opacity lost");
        Check(!reload.Settings.Topmost && reload.Settings.Width == 620 && reload.Settings.Left == 175, "Geometry/pin lost");
        reload.Settings.BackgroundOpacity = double.NaN; reload.Settings.Width = -100; reload.Settings.Top = double.PositiveInfinity; reload.SaveSettings();
        LibraryStore bounded = new LibraryStore(store.DataDirectory); bounded.Load();
        Check(bounded.Settings.BackgroundOpacity >= 0 && bounded.Settings.BackgroundOpacity <= 1, "Invalid alpha persisted");
        Check(bounded.Settings.Width >= 280 && !double.IsInfinity(bounded.Settings.Top), "Invalid geometry persisted");
    }

    private static void CorruptRecovery()
    {
        LibraryStore store = Store(); store.Settings.Width = 601; store.SaveSettings(); store.Settings.Width = 602; store.SaveSettings();
        string settings = Path.Combine(store.DataDirectory, "settings.json"); File.WriteAllText(settings, "broken", Utf8);
        LibraryStore reload = new LibraryStore(store.DataDirectory); reload.Load();
        Check(reload.Settings.Width == 601, "Did not recover last good atomic backup");
        Check(File.ReadAllText(settings, Utf8) == "broken", "Load destroyed corrupt original");
    }

    private static void CorruptStartup()
    {
        LibraryStore store = Store();
        File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "bad", Utf8);
        File.WriteAllText(Path.Combine(store.DataDirectory, "favorites.json"), "bad", Utf8);
        File.WriteAllText(Path.Combine(store.DataDirectory, "libraries", "broken.json"), "bad", Utf8);
        LibraryStore reload = new LibraryStore(store.DataDirectory); reload.Load();
        Check(reload.Settings != null && reload.Favorites.Count == 0 && reload.Libraries.Count > 0, "Corrupt optional data blocks startup");
    }

    private static void BuiltIn()
    {
        LibraryStore store = Store();
        Check(store.Libraries[0].Words.Count == 4025, "Expected all 4025 TEM-4 entries");
        Check(store.Libraries[0].Name.Contains("专四"), "Wrong vocabulary exam");
        Check(store.Libraries[0].Source.Contains("kajweb/dict"), "Source missing");
        foreach (Word word in store.Libraries[0].Words) Check(!String.IsNullOrWhiteSpace(word.English) && !String.IsNullOrWhiteSpace(word.Chinese), "Unusable source word");
    }
}
