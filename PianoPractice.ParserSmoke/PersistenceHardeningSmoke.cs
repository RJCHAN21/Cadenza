using System.Text;
using System.Text.Json;
using PianoPractice.Desktop;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class PersistenceHardeningSmoke
{
    internal static void Run()
    {
        LibraryIdentityUsesContentNotFilename();
        LibraryMetadataRoundTrips();
        LibraryWriteFailuresRollBackInMemoryAndFiles();
        ProfileCorruptionRemainsRecoverable();
        LegacyPathProgressMigratesToLibraryIdentity();
        ViewModelTestStorageIsHermetic();
    }

    private static void LibraryMetadataRoundTrips()
    {
        WithTemporaryDirectory(root =>
        {
            var source = Path.Combine(root, "score.musicxml");
            WriteFile(source, "score");
            var storeRoot = Path.Combine(root, "store");
            var store = new LibraryStore(storeRoot);
            store.LoadLibrary();
            var item = store.AddOrUpdateFile(source, "Original", "Original Composer", 12, new string('c', 64));

            Assert(store.UpdateMetadata(item.Id, "Edited", "New Composer", "New Arranger"),
                "score metadata update failed");

            var reloaded = new LibraryStore(storeRoot).LoadLibrary().Single();
            Assert(reloaded.DisplayName == "Edited", "edited score title did not persist");
            Assert(reloaded.Composer == "New Composer", "edited composer did not persist");
            Assert(reloaded.Arranger == "New Arranger", "edited arranger did not persist");

            var duplicate = new LibraryStore(storeRoot);
            duplicate.LoadLibrary();
            duplicate.AddOrUpdateFile(source, "Imported Again", "Imported Composer", 12, new string('c', 64));
            var preserved = new LibraryStore(storeRoot).LoadLibrary().Single();
            Assert(preserved.DisplayName == "Edited" &&
                   preserved.Composer == "New Composer" &&
                   preserved.Arranger == "New Arranger",
                "re-importing an existing score overwrote user-edited metadata");
        });
    }

    private static void LibraryWriteFailuresRollBackInMemoryAndFiles()
    {
        WithTemporaryDirectory(root =>
        {
            var sourceOne = Path.Combine(root, "source-one.musicxml");
            var sourceTwo = Path.Combine(root, "source-two.musicxml");
            WriteFile(sourceOne, "one");
            WriteFile(sourceTwo, "two");
            var store = new LibraryStore(Path.Combine(root, "store"));
            store.LoadLibrary();
            var first = store.AddOrUpdateFile(sourceOne, "First", "Unknown Composer", 0, new string('a', 64));
            var second = store.AddOrUpdateFile(sourceTwo, "Second", "Unknown Composer", 0, new string('b', 64));
            var originalOrder = store.Items.Select(item => item.Id).ToArray();
            Directory.CreateDirectory(store.ManifestPath + ".tmp");

            AssertThrows(() => store.RenameItem(first.Id, "Changed"), "rename write failure was hidden");
            Assert(first.DisplayName == "First", "failed rename leaked into in-memory library state");

            AssertThrows(() => store.UpdateMetadata(first.Id, "Changed", "Composer", "Arranger"),
                "metadata write failure was hidden");
            Assert(first.DisplayName == "First" && first.Composer == "Unknown Composer" && first.Arranger.Length == 0,
                "failed metadata update leaked into in-memory library state");

            AssertThrows(() => store.RecordPlayed(first.Id), "last-played write failure was hidden");
            Assert(first.LastPlayedUtc is null, "failed last-played update leaked into in-memory state");

            AssertThrows(() => store.DeleteItems([first.Id, second.Id]), "delete write failure was hidden");
            Assert(store.Items.Select(item => item.Id).SequenceEqual(originalOrder),
                "failed deletion did not restore exact library ordering");
            Assert(File.Exists(first.StoredFilePath) && File.Exists(second.StoredFilePath),
                "failed deletion did not restore managed score files");
        });
    }

    private static void LibraryIdentityUsesContentNotFilename()
    {
        WithTemporaryDirectory(root =>
        {
            var sourceOne = Path.Combine(root, "one", "score.musicxml");
            var sourceTwo = Path.Combine(root, "two", "score.musicxml");
            var sourceThree = Path.Combine(root, "three", "renamed.musicxml");
            WriteFile(sourceOne, "first");
            WriteFile(sourceTwo, "second");
            WriteFile(sourceThree, "first");

            var store = new LibraryStore(Path.Combine(root, "store"));
            store.LoadLibrary();
            var first = store.AddOrUpdateFile(sourceOne, "First", "Unknown Composer", 0, new string('a', 64));
            var second = store.AddOrUpdateFile(sourceTwo, "Second", "Unknown Composer", 0, new string('b', 64));
            var duplicate = store.AddOrUpdateFile(sourceThree, "First again", "Unknown Composer", 0, new string('a', 64));

            Assert(first.Id != second.Id, "same filename with different content collapsed into one library item");
            Assert(first.StoredFilePath != second.StoredFilePath, "different score content shared one managed file");
            Assert(duplicate.Id == first.Id, "same score content imported from another path was duplicated accidentally");
            Assert(store.Items.Count == 2, "library content identity produced an unexpected item count");

            Assert(store.DeleteItem(first.Id), "library item deletion failed");
            Assert(!File.Exists(first.StoredFilePath), "deleted managed score remained in the library");
            Assert(File.Exists(sourceOne) && File.Exists(sourceThree), "library deletion escaped the managed root");
            Assert(store.LoadLibrary().Single().Id == second.Id, "manifest did not preserve the surviving item");
        });
    }

    private static void ProfileCorruptionRemainsRecoverable()
    {
        WithTemporaryDirectory(root =>
        {
            var profilePath = Path.Combine(root, "profile.json");
            WriteFile(profilePath, "{not valid json");
            var store = new UserProfileStore(profilePath);
            var profile = store.Load();

            Assert(profile.SchemaVersion == UserProfileStore.CurrentSchemaVersion,
                "corrupt profile did not return a current default profile");
            Assert(!File.Exists(profilePath), "corrupt profile remained at the live profile path");
            Assert(Directory.GetFiles(root, "profile.json.corrupt-*").Length == 1,
                "corrupt profile was not preserved for recovery");

            store.Save(profile);
            profile.Settings.MonitorVolume = 42;
            store.Save(profile);
            Assert(File.Exists(profilePath + ".bak"), "atomic profile replacement did not retain a backup");
            var reloaded = store.Load();
            Assert(reloaded.Settings.MonitorVolume == 42, "durably saved profile did not round-trip");

            reloaded.Settings.MonitorVolume = 73;
            store.Save(reloaded);
            WriteFile(profilePath, "{interrupted write");
            var recovered = store.Load();
            Assert(recovered.Settings.MonitorVolume == 42,
                "a damaged live profile did not recover the last atomic backup");
            Assert(Directory.GetFiles(root, "profile.json.corrupt-*").Length == 2,
                "the damaged live profile was not preserved before backup recovery");

            var unsupportedPath = Path.Combine(root, "future.json");
            WriteFile(unsupportedPath, "{\"SchemaVersion\":999,\"Settings\":{},\"Songs\":{}}");
            var unsupported = new UserProfileStore(unsupportedPath).Load();
            Assert(unsupported.SchemaVersion == UserProfileStore.CurrentSchemaVersion,
                "unsupported profile schema did not degrade to a current empty profile");
            Assert(!File.Exists(unsupportedPath) && Directory.GetFiles(root, "future.json.unsupported-v999-*").Length == 1,
                "unsupported profile schema was not preserved for recovery");

            var priorSchemaPath = Path.Combine(root, "prior-schema.json");
            WriteFile(priorSchemaPath, "{\"SchemaVersion\":2,\"Settings\":{\"AutoDismissResultsEnabled\":true},\"Songs\":{}}");
            var migrated = new UserProfileStore(priorSchemaPath).Load();
            Assert(migrated.SchemaVersion == UserProfileStore.CurrentSchemaVersion &&
                   !migrated.Settings.AutoDismissResultsEnabled,
                "the prior always-on results auto-dismiss default was not disabled during migration");
            migrated.Settings.AutoDismissResultsEnabled = true;
            new UserProfileStore(priorSchemaPath).Save(migrated);
            Assert(new UserProfileStore(priorSchemaPath).Load().Settings.AutoDismissResultsEnabled,
                "an explicit post-migration results auto-dismiss choice did not persist");
        });
    }

    private static void LegacyPathProgressMigratesToLibraryIdentity()
    {
        WithTemporaryDirectory(root =>
        {
            var scorePath = Path.Combine(root, "source", "score.musicxml");
            WriteFile(scorePath, MinimalScore());
            var profilePath = Path.Combine(root, "state", "profile.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            var legacyKey = Path.GetFullPath(scorePath).ToUpperInvariant();
            var completedUtc = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            CompletedAttemptSummary Attempt() => new()
            {
                CompletedUtc = completedUtc,
                Mode = LessonMode.WaitForYou,
                HandMode = PracticeMode.BothHands,
                StartMeasure = 1,
                EndMeasure = 1,
                AccuracyPercent = 100,
                Correct = 1,
                PracticeSeconds = 30,
                BestStreak = 1
            };
            var legacyProfile = new CadenzaUserProfile
            {
                SchemaVersion = 1,
                Songs = new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase)
                {
                    [legacyKey] = new()
                    {
                        SongTitle = "Legacy score",
                        SourcePath = scorePath,
                        CumulativePracticeSeconds = 90,
                        BestStreak = 7,
                        Attempts = [Attempt()]
                    },
                    ["historical-copy"] = new()
                    {
                        SongTitle = "Legacy score copy",
                        SourcePath = scorePath,
                        CumulativePracticeSeconds = 30,
                        BestStreak = 4,
                        Attempts = [Attempt()]
                    }
                }
            };
            File.WriteAllText(profilePath, JsonSerializer.Serialize(legacyProfile), new UTF8Encoding(false));

            using (var viewModel = new MainWindowViewModel(profilePath, Path.Combine(root, "state")))
                viewModel.LoadScore(scorePath);

            var migrated = new UserProfileStore(profilePath).Load();
            var pair = migrated.Songs.Single();
            Assert(pair.Key.StartsWith("library:", StringComparison.OrdinalIgnoreCase),
                "legacy path-keyed progress did not migrate to stable library identity");
            Assert(pair.Value.CumulativePracticeSeconds == 120 && pair.Value.BestStreak == 7,
                "progress values were lost during identity migration");
            Assert(pair.Value.Attempts.Count == 1,
                "identical historical attempts were duplicated during migration");
            Assert(pair.Value.LegacySourcePaths.Any(path =>
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(scorePath), StringComparison.OrdinalIgnoreCase)),
                "legacy progress provenance was discarded during migration");
        });
    }

    private static void ViewModelTestStorageIsHermetic()
    {
        WithTemporaryDirectory(root =>
        {
            var scorePath = Path.Combine(root, "score.musicxml");
            var profilePath = Path.Combine(root, "profile.json");
            WriteFile(scorePath, MinimalScore());

            using (var viewModel = new MainWindowViewModel(profilePath))
                viewModel.LoadScore(scorePath);

            Assert(File.Exists(Path.Combine(root, "library_manifest.json")),
                "profile-path injection did not isolate the library manifest");
            Assert(Directory.GetFiles(Path.Combine(root, "Library"), "*.musicxml").Length == 1,
                "profile-path injection did not isolate managed score storage");

            using var reloaded = new MainWindowViewModel(profilePath);
            Assert(reloaded.TryLoadLastOpenedScore(),
                "startup policy did not restore the persisted managed-library item");
            Assert(reloaded.CurrentScore is not null,
                "managed last-opened policy reported success without loading a score");
            var managedPath = new LibraryStore(root).LoadLibrary().Single().StoredFilePath;
            WriteFile(managedPath, "<score-partwise>");
            using var invalidRestore = new MainWindowViewModel(profilePath);
            Assert(!invalidRestore.TryLoadLastOpenedScore() && invalidRestore.CurrentScore is null,
                "malformed managed startup score escaped the safe empty-state policy");
        });
    }

    private static string MinimalScore() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><score-partwise version=\"4.0\"><part-list><score-part id=\"P1\"><part-name>Piano</part-name></score-part></part-list><part id=\"P1\"><measure number=\"1\"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time></attributes><note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><type>quarter</type><staff>1</staff></note></measure></part></score-partwise>";

    private static void WriteFile(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value, new UTF8Encoding(false));
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cadenza-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
