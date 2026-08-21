using System.IO;
using System.Text.Json;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class UserProfileStore
{
    public const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public UserProfileStore(string? profilePath = null)
    {
        ProfilePath = profilePath ?? Path.Combine(AppStoragePaths.ProductDirectory, "profile.json");
    }

    public string ProfilePath { get; }

    public static MidiDeviceInfo? MatchPreferredMidiDevice(
        CadenzaUserSettings settings,
        IEnumerable<MidiDeviceInfo> devices)
    {
        var available = devices.ToArray();
        if (!string.IsNullOrWhiteSpace(settings.PreferredMidiDeviceId))
        {
            var byId = available.FirstOrDefault(device =>
                string.Equals(device.Id, settings.PreferredMidiDeviceId, StringComparison.Ordinal));
            if (byId is not null) return byId;
        }

        return string.IsNullOrWhiteSpace(settings.PreferredMidiDeviceName)
            ? null
            : available.FirstOrDefault(device =>
                string.Equals(device.Name, settings.PreferredMidiDeviceName, StringComparison.OrdinalIgnoreCase));
    }

    public CadenzaUserProfile Load()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return CadenzaUserProfile.CreateDefault();
            var profile = JsonSerializer.Deserialize<CadenzaUserProfile>(File.ReadAllText(ProfilePath), JsonOptions);
            if (profile is null)
                throw new JsonException("The Cadenza profile did not contain a profile object.");
            if (profile.SchemaVersion > CurrentSchemaVersion || profile.SchemaVersion < 1)
            {
                PreserveUnreadableProfile($"unsupported-v{profile.SchemaVersion}");
                return LoadBackupOrDefault();
            }
            return Normalize(profile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            PreserveUnreadableProfile("corrupt");
            return LoadBackupOrDefault();
        }
    }

    public void Save(CadenzaUserProfile profile)
    {
        var directory = Path.GetDirectoryName(ProfilePath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The user profile path has no directory.");
        Directory.CreateDirectory(directory);
        profile.SchemaVersion = CurrentSchemaVersion;
        var temporaryPath = ProfilePath + ".tmp";
        WriteDurableText(temporaryPath, JsonSerializer.Serialize(profile, JsonOptions));
        var backupPath = ProfilePath + ".bak";
        if (File.Exists(ProfilePath))
            File.Replace(temporaryPath, ProfilePath, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, ProfilePath, overwrite: false);
    }

    private void PreserveUnreadableProfile(string reason)
    {
        try
        {
            if (!File.Exists(ProfilePath))
                return;
            var preservedPath = ProfilePath + $".{reason}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(ProfilePath, preservedPath, overwrite: false);
        }
        catch
        {
            // If preservation fails, the original remains in place and Save
            // will surface the filesystem failure instead of deleting it.
        }
    }

    private CadenzaUserProfile LoadBackupOrDefault()
    {
        try
        {
            var backupPath = ProfilePath + ".bak";
            if (!File.Exists(backupPath)) return CadenzaUserProfile.CreateDefault();
            var profile = JsonSerializer.Deserialize<CadenzaUserProfile>(File.ReadAllText(backupPath), JsonOptions);
            if (profile is null || profile.SchemaVersion > CurrentSchemaVersion || profile.SchemaVersion < 1)
                return CadenzaUserProfile.CreateDefault();
            return Normalize(profile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CadenzaUserProfile.CreateDefault();
        }
    }

    private static CadenzaUserProfile Normalize(CadenzaUserProfile profile)
    {
        var sourceSchemaVersion = profile.SchemaVersion;
        profile.Settings ??= new CadenzaUserSettings();
        if (sourceSchemaVersion < 3)
            profile.Settings.AutoDismissResultsEnabled = false;
        profile.Songs ??= new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase);
        profile.SchemaVersion = CurrentSchemaVersion;
        return profile;
    }

    private static void WriteDurableText(string path, string value)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(value);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}

public sealed class CadenzaUserProfile
{
    public int SchemaVersion { get; set; } = UserProfileStore.CurrentSchemaVersion;
    public CadenzaUserSettings Settings { get; set; } = new();
    public Dictionary<string, SongProgressRecord> Songs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CadenzaUserProfile CreateDefault() => new();
}

public sealed class CadenzaUserSettings
{
    public string? PreferredMidiDeviceId { get; set; }
    public string? PreferredMidiDeviceName { get; set; }
    public bool MidiMonitorEnabled { get; set; } = true;
    public int MonitorVolume { get; set; } = 85;
    public int OverallVolume { get; set; } = 88;
    public bool InstrumentalMuted { get; set; }
    public int InstrumentalVolume { get; set; } = 82;
    public bool MetronomeMuted { get; set; }
    public int MetronomeVolume { get; set; } = 65;
    public bool PracticeFullAccompanimentEnabled { get; set; }
    public bool PerformanceFullAccompanimentEnabled { get; set; }
    public bool OtherHandAccompanimentEnabled { get; set; } = true;
    public int OtherHandAccompanimentVolume { get; set; } = 58;
    public int LessonTempoPercent { get; set; } = 100;
    public PracticeMode HandMode { get; set; } = PracticeMode.BothHands;
    public LessonMode LessonMode { get; set; } = LessonMode.WaitForYou;
    public ScoreReadingMode ScoreReadingMode { get; set; } = ScoreReadingMode.Page;
    public bool HintModeEnabled { get; set; }
    public int NotationZoomPercent { get; set; } = 100;
    public int CustomScoreScale { get; set; } = 75;
    public int CustomScoreMargin { get; set; } = 80;
    public int CustomNoteSpacing { get; set; } = 100;
    public int CustomBarDensity { get; set; } = 4;
    public int FocusStartMeasure { get; set; } = 1;
    public int FocusEndMeasure { get; set; }
    public bool PedalEnabled { get; set; }
    public int LatencyMilliseconds { get; set; }
    public bool MetronomeEnabled { get; set; } = true;
    public bool LoopEnabled { get; set; }
    public bool ComputerKeyboardEnabled { get; set; }
    public string PlaybackSoundPresetId { get; set; } = "acoustic_grand";
    public string LiveSoundPresetId { get; set; } = "acoustic_grand";
    public bool MatchPlaybackSynthEnabled { get; set; }
    public bool OnlyShowFeedbackOnPerformanceEnd { get; set; }
    public string? LastOpenedScorePath { get; set; }
    public string? LastOpenedLibraryItemId { get; set; }
    public bool AutoDismissResultsEnabled { get; set; }
    public double AutoDismissResultsSeconds { get; set; } = 10.0;
    public bool AlwaysShowLiveNoteFeedback { get; set; } = true;
    public bool MidiShortcutsEnabled { get; set; } = true;
    public int MidiRemoteArmNote { get; set; } = -1; // Retained for profile compatibility; piano keys cannot arm shortcuts.
    public int MidiShortcutListenNote { get; set; } = 48; // C3
    public int MidiShortcutPracticeNote { get; set; } = 50; // D3
    public int MidiShortcutPerformanceNote { get; set; } = 52; // E3
    public int MidiShortcutTogglePlayNote { get; set; } = 64; // E4
    public int MidiShortcutPauseNote { get; set; } = -1;
    public int MidiShortcutRestartNote { get; set; } = 60; // C4
    public int MidiShortcutPreviousMeasureNote { get; set; } = 57; // A3
    public int MidiShortcutNextMeasureNote { get; set; } = 59; // B3
    public int MidiShortcutPreviousPageNote { get; set; } = 53; // F3
    public int MidiShortcutNextPageNote { get; set; } = 55; // G3
    public int MidiShortcutReturnToLivePageNote { get; set; } = -1;
    public int MidiShortcutDismissResultsNote { get; set; } = 62; // D4
    public int MidiShortcutRepeatResultsNote { get; set; } = 67; // G4
    public int MidiControllerMappingVersion { get; set; }
    public Dictionary<string, string> MidiControllerBindings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> KeyboardShortcutBindings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SongProgressRecord
{
    public string LibraryItemId { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public List<string> LegacySourcePaths { get; set; } = [];
    public DateTimeOffset? LastPracticedUtc { get; set; }
    public double LastPositionBeat { get; set; }
    public double CumulativePracticeSeconds { get; set; }
    public int BestStreak { get; set; }
    public List<CompletedAttemptSummary> Attempts { get; set; } = [];
}

public sealed class CompletedAttemptSummary
{
    public DateTimeOffset CompletedUtc { get; set; }
    public LessonMode Mode { get; set; }
    public PracticeMode HandMode { get; set; }
    public int StartMeasure { get; set; }
    public int EndMeasure { get; set; }
    public double AccuracyPercent { get; set; }
    public double TimingPercent { get; set; }
    public double HoldPercent { get; set; }
    public int Correct { get; set; }
    public int Missed { get; set; }
    public int Extra { get; set; }
    public double PracticeSeconds { get; set; }
    public int BestStreak { get; set; }
}
