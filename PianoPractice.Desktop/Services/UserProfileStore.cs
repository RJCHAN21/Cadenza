using System.IO;
using System.Text.Json;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class UserProfileStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public UserProfileStore(string? profilePath = null)
    {
        ProfilePath = profilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CadenzaPianoStudio",
            "profile.json");
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
            if (profile is null || profile.SchemaVersion > CurrentSchemaVersion || profile.SchemaVersion < 1)
                return CadenzaUserProfile.CreateDefault();
            profile.Settings ??= new CadenzaUserSettings();
            profile.Songs ??= new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase);
            return profile;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CadenzaUserProfile.CreateDefault();
        }
    }

    public void Save(CadenzaUserProfile profile)
    {
        var directory = Path.GetDirectoryName(ProfilePath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The user profile path has no directory.");
        Directory.CreateDirectory(directory);
        profile.SchemaVersion = CurrentSchemaVersion;
        var temporaryPath = ProfilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(temporaryPath, ProfilePath, overwrite: true);
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
    public bool AutoDismissResultsEnabled { get; set; } = true;
    public double AutoDismissResultsSeconds { get; set; } = 10.0;
    public string KeyShortcutListen { get; set; } = "F4";
    public string KeyShortcutStartPractice { get; set; } = "F5";
    public string KeyShortcutStartPerformance { get; set; } = "F6";
    public string KeyShortcutTogglePlay { get; set; } = "Space";
    public string KeyShortcutRestartSession { get; set; } = "R";
    public string KeyShortcutPreviousMeasure { get; set; } = "Left";
    public string KeyShortcutNextMeasure { get; set; } = "Right";
    public string KeyShortcutPreviousPage { get; set; } = "PageUp";
    public string KeyShortcutNextPage { get; set; } = "PageDown";
    public string KeyShortcutDismissResults { get; set; } = "Escape";
    public string KeyShortcutRepeatResults { get; set; } = "Enter";

    public int MidiShortcutListenNote { get; set; } = 48; // C3
    public int MidiShortcutPracticeNote { get; set; } = -1;
    public int MidiShortcutPerformanceNote { get; set; } = -2;
    public int MidiShortcutTogglePlayNote { get; set; } = 60; // C4
    public int MidiShortcutRestartNote { get; set; } = -1; // Unassigned to prevent C4 collision
    public int MidiShortcutPreviousMeasureNote { get; set; } = 57; // A3
    public int MidiShortcutNextMeasureNote { get; set; } = 59; // B3
    public int MidiShortcutPreviousPageNote { get; set; } = 53; // F3
    public int MidiShortcutNextPageNote { get; set; } = 55; // G3
    public int MidiShortcutDismissResultsNote { get; set; } = 62; // D4
    public int MidiShortcutRepeatResultsNote { get; set; } = 67; // G4
    public double MidiShortcutHoldSeconds { get; set; } = 3.0;
    public double HoldSecondsListen { get; set; } = 3.0;
    public double HoldSecondsPractice { get; set; } = 3.0;
    public double HoldSecondsPerformance { get; set; } = 3.0;
    public double HoldSecondsTogglePlay { get; set; } = 3.0;
    public double HoldSecondsRestart { get; set; } = 3.0;
    public double HoldSecondsPrevMeasure { get; set; } = 3.0;
    public double HoldSecondsNextMeasure { get; set; } = 3.0;
    public double HoldSecondsPrevPage { get; set; } = 3.0;
    public double HoldSecondsNextPage { get; set; } = 3.0;
    public double HoldSecondsDismiss { get; set; } = 3.0;
    public double HoldSecondsRepeat { get; set; } = 3.0;

    public int MultiTapCountListen { get; set; } = 4;
    public int MultiTapCountPractice { get; set; } = 4;
    public int MultiTapCountPerformance { get; set; } = 4;
    public int MultiTapCountTogglePlay { get; set; } = 4;
    public int MultiTapCountRestart { get; set; } = 4;
    public int MultiTapCountPrevMeasure { get; set; } = 4;
    public int MultiTapCountNextMeasure { get; set; } = 4;
    public int MultiTapCountPrevPage { get; set; } = 4;
    public int MultiTapCountNextPage { get; set; } = 4;
    public int MultiTapCountDismiss { get; set; } = 4;
    public int MultiTapCountRepeat { get; set; } = 4;

    public int BehaviorListenIndex { get; set; } = 1; // Single Tap
    public int BehaviorPracticeIndex { get; set; } = 0; // Hold Note
    public int BehaviorPerformanceIndex { get; set; } = 3; // 1-Bar Sequence
    public int BehaviorTogglePlayIndex { get; set; } = 0; // Hold Note
    public int BehaviorRestartIndex { get; set; } = 0; // Hold Note
    public int BehaviorPrevMeasureIndex { get; set; } = 1; // Single Tap
    public int BehaviorNextMeasureIndex { get; set; } = 1; // Single Tap
    public int BehaviorPrevPageIndex { get; set; } = 1; // Single Tap
    public int BehaviorNextPageIndex { get; set; } = 1; // Single Tap
    public int BehaviorDismissIndex { get; set; } = 1; // Single Tap
    public int BehaviorRepeatIndex { get; set; } = 0; // Hold Note
    public bool AlwaysShowLiveNoteFeedback { get; set; } = true;
}

public sealed class SongProgressRecord
{
    public string SongTitle { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
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
