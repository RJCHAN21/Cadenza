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
    public CadenzaUserSettings? Settings { get; set; } = new();
    public Dictionary<string, SongProgressRecord>? Songs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
