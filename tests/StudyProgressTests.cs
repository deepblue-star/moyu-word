using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoyuWord;

internal static class StudyProgressTests
{
    private static int passed;
    private static string root;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "MoyuWord-progress-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("Missing progress is optional and leaves no resume position", MissingProgress);
            Run("First, last and backward positions record the last displayed word", LatestPosition);
            Run("Built-in, imported and favorite positions persist independently", IndependentDecks);
            Run("Favorite resume follows English identity across reordering", ReorderedFavorites);
            Run("Removed words clamp the old index into the remaining deck", RemovedFavorites);
            Run("Duplicate presentation avoids rewriting progress", DuplicatePresentation);
            Run("Changed English at the same index is saved", ChangedWord);
            Run("Invalid save arguments leave memory and disk unchanged", InvalidArguments);
            Run("Corrupt progress recovers backup and preserves originals", CorruptRecovery);
            Run("Unusable progress falls back without blocking startup", CorruptFallback);
            Run("Stored out-of-range indices are bounded on resume", BoundedStoredIndex);
            Run("Failed atomic write leaves prior progress intact", FailedWrite);
            Console.WriteLine("PASS: " + passed + " study progress tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static LibraryStore Store()
    {
        LibraryStore store = new LibraryStore(Path.Combine(root, Guid.NewGuid().ToString("N")));
        store.Load(); return store;
    }
    private static LibraryStore Reload(LibraryStore store) { LibraryStore next = new LibraryStore(store.DataDirectory); next.Load(); return next; }
    private static string ProgressFile(LibraryStore store) { return Path.Combine(store.DataDirectory, "progress.json"); }
    private static List<Word> Words(params string[] words)
    {
        List<Word> result = new List<Word>();
        foreach (string word in words) result.Add(new Word { English = word, Chinese = "测试" });
        return result;
    }
    private static int? GetIndex(LibraryStore store, string deckId, IList<Word> words) { return store.GetStudyIndex(deckId, words); }
    private static void Save(LibraryStore store, string deckId, IList<Word> words, int index) { store.SaveStudyPosition(deckId, words, index); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + " for invalid progress operation");
    }

    private static void MissingProgress()
    {
        LibraryStore store = Store();
        Check(GetIndex(store, "builtin-tem4", store.Libraries[0].Words) == null, "A new deck must not offer resume");
        Check(GetIndex(store, "favorites", new List<Word>()) == null, "An empty deck must not offer resume");
        Check(!File.Exists(ProgressFile(store)), "Reading missing progress created data");
        Check(store.Warnings.Count == 0, "Missing optional progress produced a warning");
    }

    private static void LatestPosition()
    {
        LibraryStore store = Store(); WordLibrary deck = store.Libraries[0];
        Save(store, deck.Id, deck.Words, 0);
        Check(GetIndex(Reload(store), deck.Id, deck.Words) == 0, "First displayed word was treated as missing or next");
        Save(store, deck.Id, deck.Words, deck.Words.Count - 1);
        Check(GetIndex(Reload(store), deck.Id, deck.Words) == deck.Words.Count - 1, "Last word was not retained");
        Save(store, deck.Id, deck.Words, 1);
        Check(GetIndex(Reload(store), deck.Id, deck.Words) == 1, "Backward navigation retained a maximum instead of latest position");
    }

    private static void IndependentDecks()
    {
        LibraryStore store = Store(); WordLibrary builtin = store.Libraries[0];
        string source = Path.Combine(store.DataDirectory, "import.csv");
        File.WriteAllText(source, "english,chinese\nalpha,甲\nbeta,乙\ngamma,丙\n", Utf8);
        WordLibrary imported = store.Import(source);
        store.ToggleFavorite(imported.Words[0]); store.ToggleFavorite(imported.Words[2]);
        Save(store, builtin.Id, builtin.Words, 8);
        Save(store, imported.Id, imported.Words, 1);
        Save(store, "favorites", store.Favorites, 0);
        LibraryStore next = Reload(store);
        Check(GetIndex(next, builtin.Id, next.Libraries[0].Words) == 8, "Built-in progress was overwritten");
        Check(GetIndex(next, imported.Id, next.Libraries[1].Words) == 1, "Imported progress was overwritten");
        Check(GetIndex(next, "favorites", next.Favorites) == 0, "Favorite progress was overwritten");
        Check(GetIndex(next, "another-deck", imported.Words) == null, "An unseen deck inherited progress");
    }

    private static void ReorderedFavorites()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta", "gamma");
        foreach (Word word in words) store.ToggleFavorite(word);
        Save(store, "favorites", store.Favorites, 1);
        store.ToggleFavorite(words[0]); store.ToggleFavorite(words[0]);
        LibraryStore next = Reload(store);
        Check(GetIndex(next, "favorites", next.Favorites) == 0, "Reordered favorites resumed the old index rather than beta");
        Check(GetIndex(next, "favorites", Words("GAMMA", "ALPHA", "BETA")) == 2, "English identity was case-sensitive");
    }

    private static void RemovedFavorites()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta", "gamma");
        Save(store, "favorites", words, 2);
        Check(GetIndex(Reload(store), "favorites", Words("alpha", "beta")) == 1, "Removed final word did not clamp to last remaining word");
        Save(store, "favorites", words, 1);
        Check(GetIndex(Reload(store), "favorites", Words("alpha", "gamma")) == 1, "Removed middle word did not retain its usable index");
        Check(GetIndex(store, "favorites", new List<Word>()) == null, "Empty favorites returned an invalid index");
    }

    private static void DuplicatePresentation()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta");
        Save(store, "deck", words, 1);
        string path = ProgressFile(store), before = File.ReadAllText(path, Utf8);
        DateTime timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        Save(store, "deck", words, 1);
        Check(File.GetLastWriteTimeUtc(path) == timestamp && File.ReadAllText(path, Utf8) == before, "Repeat rendering rewrote progress");
        Check(!File.Exists(path + ".bak"), "Duplicate position created an unnecessary backup");
    }

    private static void ChangedWord()
    {
        LibraryStore store = Store(); Save(store, "deck", Words("alpha", "beta"), 1);
        Save(store, "deck", Words("alpha", "gamma"), 1);
        Check(GetIndex(Reload(store), "deck", Words("gamma", "alpha")) == 0, "Same index prevented new word identity from being saved");
    }

    private static void InvalidArguments()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta");
        Save(store, "deck", words, 1); string before = File.ReadAllText(ProgressFile(store), Utf8);
        Reject<ArgumentException>(delegate { Save(store, null, words, 0); });
        Reject<ArgumentException>(delegate { Save(store, " \t", words, 0); });
        Reject<ArgumentNullException>(delegate { Save(store, "deck", null, 0); });
        Reject<ArgumentOutOfRangeException>(delegate { Save(store, "deck", words, -1); });
        Reject<ArgumentOutOfRangeException>(delegate { Save(store, "deck", words, words.Count); });
        Reject<ArgumentOutOfRangeException>(delegate { Save(store, "deck", new List<Word>(), 0); });
        Reject<InvalidDataException>(delegate { Save(store, "deck", new List<Word> { null }, 0); });
        Reject<InvalidDataException>(delegate { Save(store, "deck", Words(" "), 0); });
        Check(GetIndex(store, "deck", words) == 1, "Rejected save changed memory");
        Check(File.ReadAllText(ProgressFile(store), Utf8) == before, "Rejected save changed persisted progress");
    }

    private static void CorruptRecovery()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta", "gamma");
        Save(store, "deck", words, 0); Save(store, "deck", words, 2);
        string path = ProgressFile(store); string backup = File.ReadAllText(path + ".bak", Utf8);
        File.WriteAllText(path, "{broken", Utf8);
        LibraryStore next = Reload(store);
        Check(GetIndex(next, "deck", words) == 0, "Progress did not recover the atomic backup");
        Check(File.ReadAllText(path, Utf8) == "{broken" && File.ReadAllText(path + ".bak", Utf8) == backup, "Recovery modified original progress files");
        Check(next.Warnings.Exists(delegate(string warning) { return warning.Contains("progress.json") && warning.Contains("备份"); }), "Progress recovery was silent");
    }

    private static void CorruptFallback()
    {
        foreach (string content in new string[] { "broken", "null", "[]", "{\"deck\":null}", "{\" \":{\"Index\":1,\"English\":\"beta\"}}" })
        {
            LibraryStore store = Store(); string path = ProgressFile(store);
            File.WriteAllText(path, content, Utf8); File.WriteAllText(path + ".bak", "broken backup", Utf8);
            LibraryStore next = Reload(store);
            Check(GetIndex(next, "deck", Words("alpha", "beta")) == null, "Unusable progress supplied a position");
            Check(next.Libraries.Count > 0 && next.Warnings.Count >= 2, "Unusable progress blocked startup or lacked warnings");
            Check(File.ReadAllText(path, Utf8) == content, "Fallback destroyed unusable progress");
        }
    }

    private static void BoundedStoredIndex()
    {
        LibraryStore store = Store(); string path = ProgressFile(store);
        File.WriteAllText(path, "{\"low\":{\"Index\":-4,\"English\":\"gone\"},\"high\":{\"Index\":2147483647,\"English\":\"gone\"}}", Utf8);
        LibraryStore next = Reload(store); List<Word> words = Words("alpha", "beta");
        Check(GetIndex(next, "low", words) == 0 && GetIndex(next, "high", words) == 1, "Saved bounds did not clamp safely");
    }

    private static void FailedWrite()
    {
        LibraryStore store = Store(); List<Word> words = Words("alpha", "beta", "gamma");
        Save(store, "deck", words, 1); string path = ProgressFile(store), before = File.ReadAllText(path, Utf8);
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Save(store, "deck", words, 1);
            Reject<IOException>(delegate { Save(store, "deck", words, 2); });
            Reject<IOException>(delegate { Save(store, "new-deck", words, 0); });
            Check(GetIndex(store, "deck", words) == 1 && GetIndex(store, "new-deck", words) == null, "Failed write changed in-memory progress");
        }
        Check(File.ReadAllText(path, Utf8) == before, "Failed write changed the original file");
        Check(GetIndex(Reload(store), "deck", words) == 1, "Failed write damaged persisted progress");
        Check(Directory.GetFiles(store.DataDirectory, "progress.json.tmp-*").Length == 0, "Failed write left temporary progress files");
        Save(store, "deck", words, 2);
        Check(GetIndex(Reload(store), "deck", words) == 2, "Store did not recover after the write lock was released");
    }
}
