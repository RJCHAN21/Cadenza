using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

namespace PianoPractice.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MusicXmlImporter _importer = new();
    private readonly MidiDeviceService _midiDeviceService = new();
    private readonly PianoAudioService _audioService = new();
    private readonly MidiOutSynthService _liveSynth = new();
    private readonly MidiOutSynthService _accompanimentSynth = new();
    private readonly MidiFileImporter _midiFileImporter = new();
    private readonly UserProfileStore _profileStore;
    private readonly LibraryStore _libraryStore = new();
    private readonly CadenzaUserProfile _profile;
    private string _librarySearchQuery = string.Empty;
    private int _libraryCurrentPage = 1;
    private int _libraryPageSize = 6;
    private bool _isSelectAllLibraryItemsChecked;
    private bool _isRenameOverlayVisible;
    private string _renameItemTitleInput = string.Empty;
    private LibraryItemViewModel? _itemBeingRenamed;
    private readonly DispatcherTimer _lessonTimer;
    private readonly DispatcherTimer _midiRefreshTimer;
    private readonly Stopwatch _lessonClock = new();
    private readonly Stopwatch _previewClock = new();
    private readonly Stopwatch _practiceSessionClock = new();
    private readonly SemaphoreSlim _modeTransitionGate = new(1, 1);
    private readonly HashSet<int> _matchedNotes = [];
    private readonly Dictionary<int, ActiveHold> _activeHolds = [];
    private PracticeMode _selectedMode = PracticeMode.BothHands;
    private LessonMode _selectedLessonMode = LessonMode.WaitForYou;
    private ScoreReadingMode _readingMode = ScoreReadingMode.Page;
    private MidiDeviceInfo? _selectedMidiDevice;
    private ScoreDocument? _score;
    private MidiReference? _midiReference;
    private bool _useKeyboardSimulation;
    private bool _metronomeEnabled = true;
    private bool _isLoopEnabled;
    private bool _midiMonitorEnabled = true;
    private bool _pedalEnabled;
    private bool _pedalDown;
    private bool _resultsVisible;
    private bool _isPlayerVisible;
    private bool _isLessonActive;
    private bool _isPreviewPlaying;
    private bool _isPreviewBuilding;
    private bool _isPreviewPaused;
    private bool _isStartingLesson;
    private bool _previewUsesScore;
    private string _scoreTitle = "No score loaded";
    private string _scoreByline = "Import a MusicXML or MXL score to begin.";
    private string _sourceFileLabel = "Waiting for a score";
    private string _scoreStatusLabel = "MusicXML is the notation source of truth";
    private string _statusMessage = "Step 1: import a MusicXML score.";
    private string _formatLabel = "-";
    private string _keyLabel = "-";
    private string _timeLabel = "-";
    private string _tempoLabel = "-";
    private string _measureLabel = "-";
    private string _noteLabel = "-";
    private string _lyricLabel = "-";
    private string _partLabel = "-";
    private string _midiStatusLabel = "Checking WinMM MIDI inputs...";
    private string _midiApiDetail = "No WinMM query has run yet.";
    private string _inputActivityLabel = "No note input received yet.";
    private string _lessonStatusLabel = "No lesson running.";
    private string _progressLabel = "0 / 0 expected groups";
    private string _expectedLabel = "Import a score and start a lesson.";
    private string _correctLabel = "0";
    private string _missedLabel = "0";
    private string _extraLabel = "0";
    private string _accuracyLabel = "-";
    private string _timingLabel = "Timing: -";
    private string _previewStatusLabel = "Preview uses a local synthesized piano-like tone.";
    private string _keyboardSimulationHint = "Computer piano: A S D F G H J K L ; are white keys. W E T Y U O P are black keys.";
    private string _midiReferenceLabel = "No MIDI reference imported";
    private string _pdfReferenceLabel = "No PDF reference imported";
    private string _liveMonitorStatus = "MIDI monitor is on. Select a device to hear live piano.";
    private string _resultHeadline = "Lesson complete";
    private string _resultSummary = string.Empty;
    private string _rewardLabel = string.Empty;
    private string _midiLiveIndicator = "Not connected";
    private string _lastMidiKeyLabel = "No physical MIDI event received";
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _modeStartCancellation;
    private int _modeSwitchGeneration;
    private IReadOnlyList<ScoreNoteGroup> _lessonGroups = [];
    private int _lessonGroupIndex;
    private int _correctCount;
    private int _missedCount;
    private int _extraCount;
    private double _timingQualityTotal;
    private double _holdQualityTotal;
    private int _holdQualityCount;
    private int _pedalCorrect;
    private int _pedalAttempts;
    private double _nextMetronomeBeat;
    private bool _nativeInputActive;
    private long _lessonRunGeneration;
    private long _feedbackEventSequence;
    private DateTime _lastLiveUpdate = DateTime.MinValue;
    private string? _pdfReferencePath;
    private int _monitorVolume = 85;
    private int _overallVolume = 88;
    private int _instrumentalVolume = 82;
    private int _metronomeVolume = 65;
    private int _otherHandAccompanimentVolume = 58;
    private bool _instrumentalMuted;
    private bool _metronomeMuted;
    private bool _practiceFullAccompanimentEnabled;
    private bool _performanceFullAccompanimentEnabled;
    private bool _otherHandAccompanimentEnabled = true;
    private byte[]? _preparedPerformanceWave;
    private bool _onlyShowFeedbackOnPerformanceEnd;
    private bool _performanceAudioOwnsMetronome;
    private int _latencyMilliseconds;
    private int _currentStreak;
    private int _bestStreak;
    private int _feedbackPulse;
    private double _cursorBeat;
    private double _feedbackBeat = -1;
    private readonly List<double> _calibrationOffsets = [];
    private long _lastCalibrationClickTimestamp;
    private bool _calibrationActive;
    private int _calibrationCapturedForClick = -1;
    private int _calibrationClickIndex = -1;
    private IReadOnlySet<int> _heldNoteNumbers = new HashSet<int>();
    private DateTimeOffset? _lastMidiEventAt;
    private int _lastIndicatorSecond = -1;
    private int _focusStartMeasure = 1;
    private int _focusEndMeasure = 1;
    private int _lessonTempoPercent = 100;
    private double _lessonStartBeat;
    private double _previewStartBeat;
    private double _previewEndBeat;
    private bool _hintModeEnabled;
    private int _notationZoomPercent = 100;
    private SongProgressRecord? _currentSongProgress;
    private AudioSoundPreset _playbackSoundPreset = AudioSoundPreset.AcousticGrand;
    private AudioSoundPreset _liveSoundPreset = AudioSoundPreset.AcousticGrand;
    private bool _matchPlaybackSynthEnabled;
    private double _autoRepeatProgress = 1.0;
    private string _autoRepeatStatusText = string.Empty;
    private DispatcherTimer? _autoRepeatTimer;
    private DateTime _autoRepeatStartTime;
    private double _autoRepeatTotalSeconds = 5.0;
    private double _articulationQualityTotal;
    private int _articulationQualityCount;
    private double _voicingQualityTotal;
    private int _voicingQualityCount;
    private double _chordSyncTotal;
    private int _chordSyncCount;
    private long _chordFirstTimestamp;
    private long _chordLastTimestamp;
    private int _chordHitsInGroup;

    public MainWindowViewModel(string? profilePath = null)
    {
        _profileStore = new UserProfileStore(profilePath);
        _profile = _profileStore.Load();
        var settings = _profile.Settings ??= new CadenzaUserSettings();
        _selectedMode = settings.HandMode;
        _selectedLessonMode = settings.LessonMode;
        _readingMode = settings.ScoreReadingMode;
        _midiMonitorEnabled = settings.MidiMonitorEnabled;
        _monitorVolume = Math.Clamp(settings.MonitorVolume, 0, 100);
        _overallVolume = Math.Clamp(settings.OverallVolume, 0, 100);
        _instrumentalVolume = Math.Clamp(settings.InstrumentalVolume, 0, 100);
        _metronomeVolume = Math.Clamp(settings.MetronomeVolume, 0, 100);
        _otherHandAccompanimentVolume = Math.Clamp(settings.OtherHandAccompanimentVolume, 0, 100);
        _instrumentalMuted = settings.InstrumentalMuted;
        _metronomeMuted = settings.MetronomeMuted;
        _practiceFullAccompanimentEnabled = settings.PracticeFullAccompanimentEnabled;
        _performanceFullAccompanimentEnabled = settings.PerformanceFullAccompanimentEnabled;
        _otherHandAccompanimentEnabled = settings.OtherHandAccompanimentEnabled;
        _lessonTempoPercent = Math.Clamp(settings.LessonTempoPercent, 50, 120);
        _hintModeEnabled = settings.HintModeEnabled;
        _notationZoomPercent = Math.Clamp(settings.NotationZoomPercent, 80, 165);
        _focusStartMeasure = Math.Max(1, settings.FocusStartMeasure);
        _focusEndMeasure = settings.FocusEndMeasure;
        _pedalEnabled = settings.PedalEnabled;
        _latencyMilliseconds = Math.Clamp(settings.LatencyMilliseconds, -250, 500);
        _metronomeEnabled = settings.MetronomeEnabled;
        _isLoopEnabled = settings.LoopEnabled;
        _useKeyboardSimulation = settings.ComputerKeyboardEnabled;
        _playbackSoundPreset = AudioSoundPreset.FromId(settings.PlaybackSoundPresetId, AudioSoundPreset.AcousticGrand);
        _liveSoundPreset = AudioSoundPreset.FromId(settings.LiveSoundPresetId, AudioSoundPreset.AcousticGrand);
        _matchPlaybackSynthEnabled = settings.MatchPlaybackSynthEnabled;
        _onlyShowFeedbackOnPerformanceEnd = settings.OnlyShowFeedbackOnPerformanceEnd;
        _bestStreak = (_profile.Songs ??= new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase))
            .Values.Select(progress => progress.BestStreak).DefaultIfEmpty(0).Max();
        ApplyMixerVolumes();
        ApplySoundPresets();
        _lessonTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _lessonTimer.Tick += LessonTimer_Tick;
        _midiRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _midiRefreshTimer.Tick += MidiRefreshTimer_Tick;
        _midiRefreshTimer.Start();
        _midiDeviceService.NoteOn += MidiDeviceService_NoteOn;
        _midiDeviceService.NoteOff += MidiDeviceService_NoteOff;
        _midiDeviceService.ControlChange += MidiDeviceService_ControlChange;
        _midiDeviceService.RawMessage += MidiDeviceService_RawMessage;
        _midiDeviceService.InputError += MidiDeviceService_InputError;
        _midiDeviceService.InputDisconnected += MidiDeviceService_InputDisconnected;
        _midiDeviceService.Diagnostic += MidiDeviceService_Diagnostic;
        RefreshLibrary();
        SanitizeShortcutBindings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CorrectFeedback;
    public event EventHandler? ResultsPresented;
    public event EventHandler<LessonNoteFeedbackEvent>? NoteFeedback;
    public event EventHandler<LessonRunStateEvent>? LessonRunStateChanged;
    public event EventHandler? AutoRepeatUpdated;
    public event EventHandler? ResultsDismissed;

    public ObservableCollection<MeasureSummary> Measures { get; } = [];
    public ObservableCollection<MidiDeviceInfo> MidiDevices { get; } = [];
    public ObservableCollection<MidiListenTrackOption> MidiListenTracks { get; } = [];
    public ObservableCollection<string> MidiDiagnosticTrace { get; } = [];
    public ObservableCollection<int> MeasureNumbers { get; } = [];
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<LibraryItemViewModel> PagedLibraryItems { get; } = [];

    public string ScoreTitle { get => _scoreTitle; set => SetField(ref _scoreTitle, value); }
    public string ScoreByline { get => _scoreByline; private set => SetField(ref _scoreByline, value); }
    public string SourceFileLabel { get => _sourceFileLabel; private set => SetField(ref _sourceFileLabel, value); }
    public string ScoreStatusLabel { get => _scoreStatusLabel; private set => SetField(ref _scoreStatusLabel, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string FormatLabel { get => _formatLabel; private set => SetField(ref _formatLabel, value); }
    public string KeyLabel { get => _keyLabel; private set => SetField(ref _keyLabel, value); }
    public string TimeLabel { get => _timeLabel; private set => SetField(ref _timeLabel, value); }
    public string TempoLabel { get => _tempoLabel; private set => SetField(ref _tempoLabel, value); }
    public string MeasureLabel { get => _measureLabel; private set => SetField(ref _measureLabel, value); }
    public string NoteLabel { get => _noteLabel; private set => SetField(ref _noteLabel, value); }
    public string LyricLabel { get => _lyricLabel; private set => SetField(ref _lyricLabel, value); }
    public string PartLabel { get => _partLabel; private set => SetField(ref _partLabel, value); }
    public string MidiStatusLabel { get => _midiStatusLabel; private set => SetField(ref _midiStatusLabel, value); }
    public string MidiApiDetail { get => _midiApiDetail; private set => SetField(ref _midiApiDetail, value); }
    public string InputActivityLabel { get => _inputActivityLabel; private set => SetField(ref _inputActivityLabel, value); }
    public string LessonStatusLabel { get => _lessonStatusLabel; private set => SetField(ref _lessonStatusLabel, value); }
    public string ProgressLabel { get => _progressLabel; private set => SetField(ref _progressLabel, value); }
    public string ExpectedLabel
    {
        get => _expectedLabel;
        private set
        {
            if (!SetField(ref _expectedLabel, value)) return;
            OnPropertyChanged(nameof(NextNotesLabel));
            OnPropertyChanged(nameof(CurrentBarLabel));
            OnPropertyChanged(nameof(CurrentNoteToHitLabel));
        }
    }
    public string NextNotesLabel
    {
        get
        {
            var separator = ExpectedLabel.IndexOf('|');
            return separator >= 0 ? ExpectedLabel[(separator + 1)..].Trim() : ExpectedLabel;
        }
    }
    public string CurrentBarLabel
    {
        get
        {
            var separator = ExpectedLabel.IndexOf('|');
            return separator >= 0 ? ExpectedLabel[..separator].Trim() : (HasScore ? "Bar 1" : string.Empty);
        }
    }
    public string CurrentNoteToHitLabel => NextNotesLabel;
    public string ModeTitleLabel => SelectedLessonMode switch
    {
        LessonMode.Listen => "LISTEN MODE",
        LessonMode.WaitForYou => "PRACTICE MODE",
        _ => "PERFORMANCE MODE"
    };
    public string CorrectLabel { get => _correctLabel; private set => SetField(ref _correctLabel, value); }
    public string MissedLabel { get => _missedLabel; private set => SetField(ref _missedLabel, value); }
    public string ExtraLabel { get => _extraLabel; private set => SetField(ref _extraLabel, value); }
    public string AccuracyLabel { get => _accuracyLabel; private set => SetField(ref _accuracyLabel, value); }
    public string TimingLabel { get => _timingLabel; private set => SetField(ref _timingLabel, value); }
    public string PreviewStatusLabel { get => _previewStatusLabel; private set => SetField(ref _previewStatusLabel, value); }
    public string KeyboardSimulationHint { get => _keyboardSimulationHint; private set => SetField(ref _keyboardSimulationHint, value); }
    public string MidiReferenceLabel { get => _midiReferenceLabel; private set => SetField(ref _midiReferenceLabel, value); }
    public string PdfReferenceLabel { get => _pdfReferenceLabel; private set => SetField(ref _pdfReferenceLabel, value); }
    public string LiveMonitorStatus { get => _liveMonitorStatus; private set => SetField(ref _liveMonitorStatus, value); }
    public string ResultHeadline { get => _resultHeadline; private set => SetField(ref _resultHeadline, value); }
    public string ResultSummary { get => _resultSummary; private set => SetField(ref _resultSummary, value); }
    public string RewardLabel { get => _rewardLabel; private set => SetField(ref _rewardLabel, value); }
    public string MidiLiveIndicator { get => _midiLiveIndicator; private set => SetField(ref _midiLiveIndicator, value); }
    public string LastMidiKeyLabel { get => _lastMidiKeyLabel; private set => SetField(ref _lastMidiKeyLabel, value); }
    public string PitchCategoryLabel => $"Pitch {AccuracyLabel}";
    public string TimingCategoryLabel => TimingLabel.Replace("Timing: ", "Timing ");
    public string HoldCategoryLabel => _holdQualityCount == 0 ? "Hold —" : $"Hold {_holdQualityTotal / _holdQualityCount * 100:0}%";
    public string VoicingStatValue =>
        _voicingQualityCount == 0 ? "100%" : $"{_voicingQualityTotal / _voicingQualityCount * 100:0}%";
    public string ArticulationStatValue =>
        _articulationQualityCount == 0 ? "100%" : $"{_articulationQualityTotal / _articulationQualityCount * 100:0}%";
    public string ChordSyncStatValue =>
        _chordSyncCount == 0 ? "100%" : $"{_chordSyncTotal / _chordSyncCount * 100:0}%";
    public string PedalCategoryLabel
    {
        get
        {
            if (!PedalEnabled) return "Pedal not enabled";
            if (_score?.Marks.Any(mark => mark.Kind == ScoreMarkKind.Pedal) != true) return "Pedal monitored · not graded";
            return _pedalAttempts == 0 ? "Pedal —" : $"Pedal {_pedalCorrect * 100d / _pedalAttempts:0}%";
        }
    }
    public string TimingStatValue =>
        SelectedLessonMode == LessonMode.WaitForYou
            ? "n/a"
            : (_correctCount == 0 ? "0%" : $"{_timingQualityTotal / _correctCount * 100:0}%");
    public string HoldStatValue =>
        _holdQualityCount == 0 ? "—" : $"{_holdQualityTotal / _holdQualityCount * 100:0}%";
    public string? PedalStatValue =>
        (PedalEnabled && _score?.Marks.Any(mark => mark.Kind == ScoreMarkKind.Pedal) == true)
            ? (_pedalAttempts == 0 ? "—" : $"{_pedalCorrect * 100d / _pedalAttempts:0}%")
            : null;
    public int CorrectCount => _correctCount;
    public int MissedCount => _missedCount;
    public int ExtraCount => _extraCount;
    public IReadOnlySet<int> HeldNoteNumbers { get => _heldNoteNumbers; private set => SetField(ref _heldNoteNumbers, value); }
    public ScoreDocument? CurrentScore => _score;
    public bool HasMidiReference => _midiReference is not null;
    public bool HasPdfReference => !string.IsNullOrWhiteSpace(_pdfReferencePath);
    public double CursorBeat
    {
        get => _cursorBeat;
        set
        {
            if (!SetField(ref _cursorBeat, value)) return;
            _prePracticeMatchedNotes.Clear();
            UpdateExpectedGuideForCursor();
        }
    }
    public double FeedbackBeat { get => _feedbackBeat; private set => SetField(ref _feedbackBeat, value); }
    public int FeedbackPulse { get => _feedbackPulse; private set => SetField(ref _feedbackPulse, value); }
    public int CurrentStreak { get => _currentStreak; private set { if (SetField(ref _currentStreak, value)) OnPropertyChanged(nameof(StreakLabel)); } }
    public int BestStreak { get => _bestStreak; private set => SetField(ref _bestStreak, value); }
    public string StreakLabel => CurrentStreak == 0 ? "Build your streak" : $"{CurrentStreak} note streak";
    public double StreakProgress => Math.Min(100, CurrentStreak * 10);
    public bool ResultsVisible
    {
        get => _resultsVisible;
        private set
        {
            if (SetField(ref _resultsVisible, value))
            {
                OnPropertyChanged(nameof(PreviewButtonLabel));
            }
        }
    }
    public bool IsPlayerVisible
    {
        get => _isPlayerVisible;
        private set
        {
            if (SetField(ref _isPlayerVisible, value)) OnPropertyChanged(nameof(IsDashboardVisible));
        }
    }
    public bool IsDashboardVisible => !IsPlayerVisible;
    public string DashboardScoreSummary => _score is null
        ? "No songs imported yet"
        : $"{_score.MeasureCount} measures · {_score.TempoBpm:0} BPM · {_score.TimeSignature}";
    public string DashboardProgressSummary => _currentSongProgress is null || _currentSongProgress.Attempts.Count == 0
        ? "No completed attempts yet"
        : $"{_currentSongProgress.Attempts.Count} completed · best {_currentSongProgress.Attempts.Max(attempt => attempt.AccuracyPercent):0}%";
    public string RecentAttemptLabel => _currentSongProgress?.Attempts.LastOrDefault() is { } attempt
        ? $"{attempt.CompletedUtc.ToLocalTime():MMM d} · {attempt.Mode} · {attempt.AccuracyPercent:0}%"
        : "Complete a lesson to begin your history";
    public string CumulativePracticeLabel => _currentSongProgress is null
        ? "0 min practiced"
        : $"{Math.Round(_currentSongProgress.CumulativePracticeSeconds / 60d):0} min practiced";
    public int CompletedAttemptCount => _currentSongProgress?.Attempts.Count ?? 0;

    public bool HasScore => _score is not null;
    public bool HasImportValidationWarnings => _score?.ValidationWarnings.Count > 0;
    public IReadOnlyList<ScoreValidationWarning> ImportValidationWarnings =>
        _score?.ValidationWarnings ?? Array.Empty<ScoreValidationWarning>();
    public string ImportWarningBadgeLabel => _score?.ValidationWarnings.Count switch
    {
        null or 0 => "Import verified",
        1 => "1 import warning",
        var count => $"{count} import warnings"
    };
    public string ImportValidationSummary => _score?.ValidationWarnings.Count switch
    {
        null or 0 => "Score navigation validated",
        1 => _score.ValidationWarnings[0].Message,
        _ => $"{_score.ValidationWarnings.Count} import warnings · {_score.ValidationWarnings[0].Message}"
    };
    public bool HasMidiHardware => MidiDevices.Count > 0;
    public bool HasNoMidiDevices => !HasMidiHardware;
    public string PreferredMidiDeviceLabel => string.IsNullOrWhiteSpace(_profile.Settings?.PreferredMidiDeviceName)
        ? "No saved MIDI preference"
        : $"Saved keyboard: {_profile.Settings.PreferredMidiDeviceName}";
    public bool IsLessonActive { get => _isLessonActive; private set => SetField(ref _isLessonActive, value); }
    public bool IsPreviewPlaying { get => _isPreviewPlaying; private set => SetField(ref _isPreviewPlaying, value); }
    public bool IsPreviewBuilding { get => _isPreviewBuilding; private set => SetField(ref _isPreviewBuilding, value); }
    public bool IsPreviewPaused { get => _isPreviewPaused; private set => SetField(ref _isPreviewPaused, value); }
    public bool IsScorePreviewPlaying => IsPreviewPlaying && _previewUsesScore;
    public bool CanChooseInput => !IsLessonActive;
    public bool HasAcceptedInput => _nativeInputActive || UseKeyboardSimulation;
    public bool CanStartLesson => HasScore && !IsLessonActive && !_isStartingLesson &&
                                  (SelectedLessonMode == LessonMode.Listen ||
                                   ((SelectedLessonMode == LessonMode.WaitForYou ||
                                     !_score!.HasBlockingAssessmentWarning(FocusStartMeasure, FocusEndMeasure)) &&
                                    !_score!.CutsRepeatRegion(FocusStartMeasure, FocusEndMeasure) &&
                                    HasAcceptedInput &&
                                    _lessonGroups.Count > 0));
    public bool CanPlayMidiReference => _midiReference is { Notes.Count: > 0 } &&
                                        MidiListenTracks.Any(track => track.IsSelected) &&
                                        !IsPreviewBuilding;
    public bool IsCalibrationActive { get => _calibrationActive; private set => SetField(ref _calibrationActive, value); }
    public int FocusStartMeasure
    {
        get => _focusStartMeasure;
        set
        {
            var next = Math.Clamp(value, 1, Math.Max(1, _score?.MeasureCount ?? 1));
            if (!SetField(ref _focusStartMeasure, next)) return;
            StopActiveSessionForSelectionChange();
            if (_focusEndMeasure < next)
            {
                _focusEndMeasure = next;
                OnPropertyChanged(nameof(FocusEndMeasure));
            }
            RefreshLessonGroups();
            ResetPreviewPositionToRangeStart();
            OnPropertyChanged(nameof(FocusRangeLabel));
            SaveProfileSettings();
        }
    }
    public int FocusEndMeasure
    {
        get => _focusEndMeasure <= 0 || (_score is not null && _focusEndMeasure > _score.MeasureCount)
            ? (_score?.MeasureCount ?? 1)
            : _focusEndMeasure;
        set
        {
            var maxBar = Math.Max(FocusStartMeasure, _score?.MeasureCount ?? 1);
            var next = Math.Clamp(value, FocusStartMeasure, maxBar);
            var stored = (_score is not null && next == _score.MeasureCount) ? 0 : next;
            if (_focusEndMeasure == stored) return;
            _focusEndMeasure = stored;
            OnPropertyChanged(nameof(FocusEndMeasure));
            StopActiveSessionForSelectionChange();
            RefreshLessonGroups();
            ResetPreviewPositionToRangeStart();
            OnPropertyChanged(nameof(FocusRangeLabel));
            SaveProfileSettings();
        }
    }
    public string FocusRangeLabel => $"Bars {FocusStartMeasure}–{FocusEndMeasure}";
    public int LessonTempoPercent
    {
        get => _lessonTempoPercent;
        set
        {
            if (!SetField(ref _lessonTempoPercent, Math.Clamp(value, 50, 120))) return;
            OnPropertyChanged(nameof(EffectiveLessonTempoBpm));
            OnPropertyChanged(nameof(LessonTempoLabel));
            OnPropertyChanged(nameof(LessonTempoPercentText));
            OnPropertyChanged(nameof(EffectiveLessonTempoBpmText));
            OnPropertyChanged(nameof(MetronomeLabel));
            SaveProfileSettings();
        }
    }
    public double EffectiveLessonTempoBpm => (_score?.TempoBpm ?? 120) * LessonTempoPercent / 100d;
    public string LessonTempoLabel => $"{LessonTempoPercent}% · {EffectiveLessonTempoBpm:0} BPM";
    public string LessonTempoPercentText
    {
        get => LessonTempoPercent.ToString(CultureInfo.CurrentCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var percent))
            {
                LessonTempoPercent = Math.Clamp(percent, 50, 120);
            }

            // Restore canonical, clamped text after invalid input or an unchanged assignment.
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveLessonTempoBpmText));
        }
    }
    public string EffectiveLessonTempoBpmText
    {
        get => EffectiveLessonTempoBpm.ToString("0", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var bpm) && bpm > 0)
            {
                var baseTempo = Math.Max(1d, _score?.TempoBpm ?? 120d);
                var percent = (int)Math.Round(bpm / baseTempo * 100d, MidpointRounding.AwayFromZero);
                LessonTempoPercent = Math.Clamp(percent, 50, 120);
            }

            // The effective BPM is derived from the persisted percentage.
            OnPropertyChanged();
            OnPropertyChanged(nameof(LessonTempoPercentText));
        }
    }

    public string NoteLyricLabel => $"{NoteLabel} / {LyricLabel}";
    public string PreviewButtonLabel =>
        IsPreviewBuilding ? "Preparing..." :
        (ResultsVisible || (IsLessonActive && SelectedLessonMode != LessonMode.Listen)) ? "Restart" :
        IsPreviewPlaying ? "Pause" :
        IsPreviewPaused ? "Resume" : "Play";
    public bool CanUseTransport => HasScore;
    public bool HintModeEnabled
    {
        get => _hintModeEnabled;
        set
        {
            if (!SetField(ref _hintModeEnabled, value)) return;
            SaveProfileSettings();
        }
    }

    public int NotationZoomPercent
    {
        get => _notationZoomPercent;
        set
        {
            if (!SetField(ref _notationZoomPercent, Math.Clamp(value, 80, 165))) return;
            SaveProfileSettings();
        }
    }
    public string LessonButtonLabel => SelectedLessonMode switch
    {
        LessonMode.Listen when IsPreviewPlaying => "Pause listening",
        LessonMode.Listen when IsPreviewPaused => "Resume listening",
        LessonMode.Listen => "Start listening",
        _ when IsLessonActive => "Lesson running",
        _ when _isStartingLesson => "Preparing…",
        LessonMode.WaitForYou => "Start practice",
        _ => "Start performance"
    };
    public double AutoRepeatProgress
    {
        get => _autoRepeatProgress;
        set => SetField(ref _autoRepeatProgress, value);
    }

    public string AutoRepeatStatusText
    {
        get => _autoRepeatStatusText;
        set => SetField(ref _autoRepeatStatusText, value);
    }

    public bool AutoDismissResultsEnabled
    {
        get => _profile.Settings.AutoDismissResultsEnabled;
        set
        {
            if (_profile.Settings.AutoDismissResultsEnabled != value)
            {
                _profile.Settings.AutoDismissResultsEnabled = value;
                OnPropertyChanged();
                SaveProfileSettings();
                if (ResultsVisible)
                {
                    if (value) StartAutoRepeatCountdown();
                    else StopAutoRepeatCountdown();
                }
            }
        }
    }

    public int CustomScoreScale
    {
        get => _profile.Settings.CustomScoreScale <= 0 ? 75 : _profile.Settings.CustomScoreScale;
        set
        {
            var clamped = Math.Clamp(value, 50, 120);
            if (_profile.Settings.CustomScoreScale != clamped)
            {
                _profile.Settings.CustomScoreScale = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomScoreScaleLabel));
                SaveProfileSettings();
            }
        }
    }

    public string CustomScoreScaleLabel => CustomScoreScale switch
    {
        <= 60 => $"{CustomScoreScale}% (Compact)",
        <= 75 => $"{CustomScoreScale}% (Balanced)",
        <= 90 => $"{CustomScoreScale}% (Large)",
        _ => $"{CustomScoreScale}% (Extra Large)"
    };

    public int CustomScoreMargin
    {
        get => _profile.Settings.CustomScoreMargin <= 0 ? 80 : _profile.Settings.CustomScoreMargin;
        set
        {
            var clamped = Math.Clamp(value, 20, 120);
            if (_profile.Settings.CustomScoreMargin != clamped)
            {
                _profile.Settings.CustomScoreMargin = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomScoreMarginLabel));
                SaveProfileSettings();
            }
        }
    }

    public string CustomScoreMarginLabel => CustomScoreMargin switch
    {
        <= 35 => $"{CustomScoreMargin}px (Full Width)",
        <= 65 => $"{CustomScoreMargin}px (Balanced)",
        <= 95 => $"{CustomScoreMargin}px (Spacious)",
        _ => $"{CustomScoreMargin}px (Wide Margins)"
    };

    public int CustomNoteSpacing
    {
        get => _profile.Settings.CustomNoteSpacing <= 0 ? 100 : _profile.Settings.CustomNoteSpacing;
        set
        {
            var clamped = Math.Clamp(value, 50, 160);
            if (_profile.Settings.CustomNoteSpacing != clamped)
            {
                _profile.Settings.CustomNoteSpacing = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomNoteSpacingLabel));
                SaveProfileSettings();
            }
        }
    }

    public string CustomNoteSpacingLabel => CustomNoteSpacing switch
    {
        <= 70 => $"{CustomNoteSpacing}% (Tight Note Distance)",
        <= 110 => $"{CustomNoteSpacing}% (Balanced Note Distance)",
        <= 135 => $"{CustomNoteSpacing}% (Spacious Note Distance)",
        _ => $"{CustomNoteSpacing}% (Expanded Note Distance)"
    };

    public int CustomBarDensity
    {
        get => _profile.Settings.CustomBarDensity <= 0 ? 4 : _profile.Settings.CustomBarDensity;
        set
        {
            var clamped = Math.Clamp(value, 2, 8);
            if (_profile.Settings.CustomBarDensity != clamped)
            {
                _profile.Settings.CustomBarDensity = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomBarDensityLabel));
                SaveProfileSettings();
            }
        }
    }

    public string CustomBarDensityLabel => CustomBarDensity switch
    {
        <= 2 => $"{CustomBarDensity} Bars per Line (Spacious)",
        <= 4 => $"{CustomBarDensity} Bars per Line (Balanced)",
        <= 6 => $"{CustomBarDensity} Bars per Line (Dense)",
        _ => $"{CustomBarDensity} Bars per Line (Max Density)"
    };

    private bool _isKeyLearningActive;
    private bool _isMidiLearningActive;
    private string _learningActionName = string.Empty;
    private string _learningPromptText = string.Empty;

    public bool IsKeyLearningActive
    {
        get => _isKeyLearningActive;
        set => SetField(ref _isKeyLearningActive, value);
    }

    public bool IsMidiLearningActive
    {
        get => _isMidiLearningActive;
        set => SetField(ref _isMidiLearningActive, value);
    }

    public bool IsShortcutLearningActive => IsKeyLearningActive || IsMidiLearningActive;

    public string LearningActionName
    {
        get => _learningActionName;
        set => SetField(ref _learningActionName, value);
    }

    public string LearningPromptText
    {
        get => _learningPromptText;
        set => SetField(ref _learningPromptText, value);
    }

    public string KeyShortcutStartPractice
    {
        get => _profile.Settings.KeyShortcutStartPractice;
        set
        {
            _profile.Settings.KeyShortcutStartPractice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyShortcutStartPracticeText));
            SaveProfileSettings();
        }
    }

    public string KeyShortcutStartPerformance
    {
        get => _profile.Settings.KeyShortcutStartPerformance;
        set
        {
            _profile.Settings.KeyShortcutStartPerformance = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyShortcutStartPerformanceText));
            SaveProfileSettings();
        }
    }

    public string KeyShortcutRestartSession
    {
        get => _profile.Settings.KeyShortcutRestartSession;
        set
        {
            _profile.Settings.KeyShortcutRestartSession = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyShortcutRestartSessionText));
            SaveProfileSettings();
        }
    }

    public string KeyShortcutDismissResults
    {
        get => _profile.Settings.KeyShortcutDismissResults;
        set
        {
            _profile.Settings.KeyShortcutDismissResults = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyShortcutDismissResultsText));
            SaveProfileSettings();
        }
    }

    public string KeyShortcutRepeatResults
    {
        get => _profile.Settings.KeyShortcutRepeatResults;
        set
        {
            _profile.Settings.KeyShortcutRepeatResults = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyShortcutRepeatResultsText));
            SaveProfileSettings();
        }
    }

    public double HoldDurationSeconds
    {
        get => _profile.Settings.MidiShortcutHoldSeconds <= 0 ? 3.0 : _profile.Settings.MidiShortcutHoldSeconds;
        set
        {
            var clamped = Math.Clamp(Math.Round(value * 2.0) / 2.0, 1.0, 5.0);
            if (Math.Abs(_profile.Settings.MidiShortcutHoldSeconds - clamped) < 0.01) return;
            _profile.Settings.MidiShortcutHoldSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoldDurationSecondsText));
            SaveProfileSettings();
        }
    }

    public string HoldDurationSecondsText => $"{HoldDurationSeconds:0.0} seconds";

    public int MidiShortcutRestartNote
    {
        get => _profile.Settings.MidiShortcutRestartNote;
        set
        {
            _profile.Settings.MidiShortcutRestartNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutRestartNoteText));
            SaveProfileSettings();
        }
    }

    public string KeyShortcutListen
    {
        get => _profile.Settings.KeyShortcutListen;
        set { _profile.Settings.KeyShortcutListen = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutListenText)); SaveProfileSettings(); }
    }

    public string KeyShortcutTogglePlay
    {
        get => _profile.Settings.KeyShortcutTogglePlay;
        set { _profile.Settings.KeyShortcutTogglePlay = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutTogglePlayText)); SaveProfileSettings(); }
    }

    public string KeyShortcutPreviousMeasure
    {
        get => _profile.Settings.KeyShortcutPreviousMeasure;
        set { _profile.Settings.KeyShortcutPreviousMeasure = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutPreviousMeasureText)); SaveProfileSettings(); }
    }

    public string KeyShortcutNextMeasure
    {
        get => _profile.Settings.KeyShortcutNextMeasure;
        set { _profile.Settings.KeyShortcutNextMeasure = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutNextMeasureText)); SaveProfileSettings(); }
    }

    public string KeyShortcutPreviousPage
    {
        get => _profile.Settings.KeyShortcutPreviousPage;
        set { _profile.Settings.KeyShortcutPreviousPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutPreviousPageText)); SaveProfileSettings(); }
    }

    public string KeyShortcutNextPage
    {
        get => _profile.Settings.KeyShortcutNextPage;
        set { _profile.Settings.KeyShortcutNextPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyShortcutNextPageText)); SaveProfileSettings(); }
    }

    public int MidiShortcutListenNote
    {
        get => _profile.Settings.MidiShortcutListenNote;
        set { _profile.Settings.MidiShortcutListenNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutListenNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutTogglePlayNote
    {
        get => _profile.Settings.MidiShortcutTogglePlayNote;
        set { _profile.Settings.MidiShortcutTogglePlayNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutTogglePlayNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutPreviousMeasureNote
    {
        get => _profile.Settings.MidiShortcutPreviousMeasureNote;
        set { _profile.Settings.MidiShortcutPreviousMeasureNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutPreviousMeasureNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutNextMeasureNote
    {
        get => _profile.Settings.MidiShortcutNextMeasureNote;
        set { _profile.Settings.MidiShortcutNextMeasureNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutNextMeasureNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutPreviousPageNote
    {
        get => _profile.Settings.MidiShortcutPreviousPageNote;
        set { _profile.Settings.MidiShortcutPreviousPageNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutPreviousPageNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutNextPageNote
    {
        get => _profile.Settings.MidiShortcutNextPageNote;
        set { _profile.Settings.MidiShortcutNextPageNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutNextPageNoteText)); SaveProfileSettings(); }
    }

    public string KeyShortcutListenText => string.IsNullOrWhiteSpace(KeyShortcutListen) ? "F4" : KeyShortcutListen;
    public string KeyShortcutStartPracticeText => string.IsNullOrWhiteSpace(KeyShortcutStartPractice) ? "F5" : KeyShortcutStartPractice;
    public string KeyShortcutStartPerformanceText => string.IsNullOrWhiteSpace(KeyShortcutStartPerformance) ? "F6" : KeyShortcutStartPerformance;
    public string KeyShortcutTogglePlayText => string.IsNullOrWhiteSpace(KeyShortcutTogglePlay) ? "Space" : KeyShortcutTogglePlay;
    public int MidiShortcutDismissResultsNote
    {
        get => _profile.Settings.MidiShortcutDismissResultsNote;
        set { _profile.Settings.MidiShortcutDismissResultsNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutDismissResultsNoteText)); SaveProfileSettings(); }
    }

    public int MidiShortcutRepeatResultsNote
    {
        get => _profile.Settings.MidiShortcutRepeatResultsNote;
        set { _profile.Settings.MidiShortcutRepeatResultsNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(MidiShortcutRepeatResultsNoteText)); SaveProfileSettings(); }
    }

    public string KeyShortcutRestartSessionText => string.IsNullOrWhiteSpace(KeyShortcutRestartSession) ? "R" : KeyShortcutRestartSession;
    public string KeyShortcutPreviousMeasureText => string.IsNullOrWhiteSpace(KeyShortcutPreviousMeasure) ? "Left" : KeyShortcutPreviousMeasure;
    public string KeyShortcutNextMeasureText => string.IsNullOrWhiteSpace(KeyShortcutNextMeasure) ? "Right" : KeyShortcutNextMeasure;
    public string KeyShortcutPreviousPageText => string.IsNullOrWhiteSpace(KeyShortcutPreviousPage) ? "PageUp" : KeyShortcutPreviousPage;
    public string KeyShortcutNextPageText => string.IsNullOrWhiteSpace(KeyShortcutNextPage) ? "PageDown" : KeyShortcutNextPage;
    public string KeyShortcutDismissResultsText => string.IsNullOrWhiteSpace(KeyShortcutDismissResults) ? "Escape" : KeyShortcutDismissResults;
    public string KeyShortcutRepeatResultsText => string.IsNullOrWhiteSpace(KeyShortcutRepeatResults) ? "Enter" : KeyShortcutRepeatResults;

    public IReadOnlyList<string> ShortcutBehaviorOptions { get; } = new string[]
    {
        "Hold Note (1s-5s)",
        "Single Tap Note",
        "Double-Tap Note",
        "Triple-Tap Note",
        "Multi-Tap Note (Custom)",
        "1-Bar Sequence",
        "2-Bar Sequence",
        "Visible Page Sequence",
        "First & Last Note Sequence"
    };

    public int GetMultiTapCountForAction(string actionName) => actionName switch
    {
        "Listen" => _profile.Settings.MultiTapCountListen > 1 ? _profile.Settings.MultiTapCountListen : 4,
        "Practice" => _profile.Settings.MultiTapCountPractice > 1 ? _profile.Settings.MultiTapCountPractice : 4,
        "Performance" => _profile.Settings.MultiTapCountPerformance > 1 ? _profile.Settings.MultiTapCountPerformance : 4,
        "TogglePlay" => _profile.Settings.MultiTapCountTogglePlay > 1 ? _profile.Settings.MultiTapCountTogglePlay : 4,
        "Restart" => _profile.Settings.MultiTapCountRestart > 1 ? _profile.Settings.MultiTapCountRestart : 4,
        "PrevMeasure" => _profile.Settings.MultiTapCountPrevMeasure > 1 ? _profile.Settings.MultiTapCountPrevMeasure : 4,
        "NextMeasure" => _profile.Settings.MultiTapCountNextMeasure > 1 ? _profile.Settings.MultiTapCountNextMeasure : 4,
        "PrevPage" => _profile.Settings.MultiTapCountPrevPage > 1 ? _profile.Settings.MultiTapCountPrevPage : 4,
        "NextPage" => _profile.Settings.MultiTapCountNextPage > 1 ? _profile.Settings.MultiTapCountNextPage : 4,
        "Dismiss" => _profile.Settings.MultiTapCountDismiss > 1 ? _profile.Settings.MultiTapCountDismiss : 4,
        "Repeat" => _profile.Settings.MultiTapCountRepeat > 1 ? _profile.Settings.MultiTapCountRepeat : 4,
        _ => 4
    };

    public void SetMultiTapCountForAction(string actionName, int count)
    {
        var clamped = Math.Clamp(count, 2, 10);
        switch (actionName)
        {
            case "Listen": _profile.Settings.MultiTapCountListen = clamped; break;
            case "Practice": _profile.Settings.MultiTapCountPractice = clamped; break;
            case "Performance": _profile.Settings.MultiTapCountPerformance = clamped; break;
            case "TogglePlay": _profile.Settings.MultiTapCountTogglePlay = clamped; break;
            case "Restart": _profile.Settings.MultiTapCountRestart = clamped; break;
            case "PrevMeasure": _profile.Settings.MultiTapCountPrevMeasure = clamped; break;
            case "NextMeasure": _profile.Settings.MultiTapCountNextMeasure = clamped; break;
            case "PrevPage": _profile.Settings.MultiTapCountPrevPage = clamped; break;
            case "NextPage": _profile.Settings.MultiTapCountNextPage = clamped; break;
            case "Dismiss": _profile.Settings.MultiTapCountDismiss = clamped; break;
            case "Repeat": _profile.Settings.MultiTapCountRepeat = clamped; break;
        }
        SaveProfileSettings();
    }

    public string FormatMidiShortcutText(string actionName, int behaviorIndex, int midiNoteNumber) => behaviorIndex switch
    {
        3 => "3x Tap Note",
        4 => $"{GetMultiTapCountForAction(actionName)}x Tap Note",
        5 => "Bar 1 Notes",
        6 => "First 2 Bars",
        7 => "Page Notes",
        8 => "First & Last Note",
        _ => midiNoteNumber > 0 ? MidiNoteFormatter.Format(midiNoteNumber) : "Unassigned"
    };

    public string MidiShortcutListenNoteText => FormatMidiShortcutText("Listen", BehaviorListenIndex, MidiShortcutListenNote);
    public string MidiShortcutPracticeNoteText => FormatMidiShortcutText("Practice", BehaviorPracticeIndex, _profile.Settings.MidiShortcutPracticeNote);
    public string MidiShortcutPerformanceNoteText => FormatMidiShortcutText("Performance", BehaviorPerformanceIndex, _profile.Settings.MidiShortcutPerformanceNote);
    public string MidiShortcutTogglePlayNoteText => FormatMidiShortcutText("TogglePlay", BehaviorTogglePlayIndex, MidiShortcutTogglePlayNote);
    public string MidiShortcutRestartNoteText => FormatMidiShortcutText("Restart", BehaviorRestartIndex, MidiShortcutRestartNote);
    public string MidiShortcutPreviousMeasureNoteText => FormatMidiShortcutText("PrevMeasure", BehaviorPrevMeasureIndex, MidiShortcutPreviousMeasureNote);
    public string MidiShortcutNextMeasureNoteText => FormatMidiShortcutText("NextMeasure", BehaviorNextMeasureIndex, MidiShortcutNextMeasureNote);
    public string MidiShortcutPreviousPageNoteText => FormatMidiShortcutText("PrevPage", BehaviorPrevPageIndex, MidiShortcutPreviousPageNote);
    public string MidiShortcutNextPageNoteText => FormatMidiShortcutText("NextPage", BehaviorNextPageIndex, MidiShortcutNextPageNote);
    public string MidiShortcutDismissResultsNoteText => FormatMidiShortcutText("Dismiss", BehaviorDismissIndex, MidiShortcutDismissResultsNote);
    public string MidiShortcutRepeatResultsNoteText => FormatMidiShortcutText("Repeat", BehaviorRepeatIndex, MidiShortcutRepeatResultsNote);

    public bool IsHoldTimerVisibleListen => BehaviorListenIndex == 0;
    public bool IsHoldTimerVisiblePractice => BehaviorPracticeIndex == 0;
    public bool IsHoldTimerVisiblePerformance => BehaviorPerformanceIndex == 0;
    public bool IsHoldTimerVisibleTogglePlay => BehaviorTogglePlayIndex == 0;
    public bool IsHoldTimerVisibleRestart => BehaviorRestartIndex == 0;
    public bool IsHoldTimerVisiblePrevMeasure => BehaviorPrevMeasureIndex == 0;
    public bool IsHoldTimerVisibleNextMeasure => BehaviorNextMeasureIndex == 0;
    public bool IsHoldTimerVisiblePrevPage => BehaviorPrevPageIndex == 0;
    public bool IsHoldTimerVisibleNextPage => BehaviorNextPageIndex == 0;
    public bool IsHoldTimerVisibleDismiss => BehaviorDismissIndex == 0;
    public bool IsHoldTimerVisibleRepeat => BehaviorRepeatIndex == 0;

    public int BehaviorListenIndex
    {
        get => _profile.Settings.BehaviorListenIndex;
        set
        {
            _profile.Settings.BehaviorListenIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutListenNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleListen));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Listen");
        }
    }

    public int BehaviorPracticeIndex
    {
        get => _profile.Settings.BehaviorPracticeIndex;
        set
        {
            _profile.Settings.BehaviorPracticeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutPracticeNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisiblePractice));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Practice");
        }
    }

    public int BehaviorPerformanceIndex
    {
        get => _profile.Settings.BehaviorPerformanceIndex;
        set
        {
            _profile.Settings.BehaviorPerformanceIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutPerformanceNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisiblePerformance));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Performance");
        }
    }

    public int BehaviorTogglePlayIndex
    {
        get => _profile.Settings.BehaviorTogglePlayIndex;
        set
        {
            _profile.Settings.BehaviorTogglePlayIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutTogglePlayNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleTogglePlay));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("TogglePlay");
        }
    }

    public int BehaviorRestartIndex
    {
        get => _profile.Settings.BehaviorRestartIndex;
        set
        {
            _profile.Settings.BehaviorRestartIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutRestartNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleRestart));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Restart");
        }
    }

    public int BehaviorPrevMeasureIndex
    {
        get => _profile.Settings.BehaviorPrevMeasureIndex;
        set
        {
            _profile.Settings.BehaviorPrevMeasureIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutPreviousMeasureNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisiblePrevMeasure));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("PrevMeasure");
        }
    }

    public int BehaviorNextMeasureIndex
    {
        get => _profile.Settings.BehaviorNextMeasureIndex;
        set
        {
            _profile.Settings.BehaviorNextMeasureIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutNextMeasureNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleNextMeasure));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("NextMeasure");
        }
    }

    public int BehaviorPrevPageIndex
    {
        get => _profile.Settings.BehaviorPrevPageIndex;
        set
        {
            _profile.Settings.BehaviorPrevPageIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutPreviousPageNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisiblePrevPage));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("PrevPage");
        }
    }

    public int BehaviorNextPageIndex
    {
        get => _profile.Settings.BehaviorNextPageIndex;
        set
        {
            _profile.Settings.BehaviorNextPageIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutNextPageNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleNextPage));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("NextPage");
        }
    }

    public int BehaviorDismissIndex
    {
        get => _profile.Settings.BehaviorDismissIndex;
        set
        {
            _profile.Settings.BehaviorDismissIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutDismissResultsNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleDismiss));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Dismiss");
        }
    }

    public int BehaviorRepeatIndex
    {
        get => _profile.Settings.BehaviorRepeatIndex;
        set
        {
            _profile.Settings.BehaviorRepeatIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiShortcutRepeatResultsNoteText));
            OnPropertyChanged(nameof(IsHoldTimerVisibleRepeat));
            SaveProfileSettings();
            if (value == 4) OpenMultiTapPrompt("Repeat");
        }
    }

    public double HoldSecondsListen
    {
        get => _profile.Settings.HoldSecondsListen > 0 ? _profile.Settings.HoldSecondsListen : 3.0;
        set { _profile.Settings.HoldSecondsListen = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsPractice
    {
        get => _profile.Settings.HoldSecondsPractice > 0 ? _profile.Settings.HoldSecondsPractice : 3.0;
        set { _profile.Settings.HoldSecondsPractice = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsPerformance
    {
        get => _profile.Settings.HoldSecondsPerformance > 0 ? _profile.Settings.HoldSecondsPerformance : 3.0;
        set { _profile.Settings.HoldSecondsPerformance = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsTogglePlay
    {
        get => _profile.Settings.HoldSecondsTogglePlay > 0 ? _profile.Settings.HoldSecondsTogglePlay : 3.0;
        set { _profile.Settings.HoldSecondsTogglePlay = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsRestart
    {
        get => _profile.Settings.HoldSecondsRestart > 0 ? _profile.Settings.HoldSecondsRestart : 3.0;
        set { _profile.Settings.HoldSecondsRestart = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsPrevMeasure
    {
        get => _profile.Settings.HoldSecondsPrevMeasure > 0 ? _profile.Settings.HoldSecondsPrevMeasure : 3.0;
        set { _profile.Settings.HoldSecondsPrevMeasure = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsNextMeasure
    {
        get => _profile.Settings.HoldSecondsNextMeasure > 0 ? _profile.Settings.HoldSecondsNextMeasure : 3.0;
        set { _profile.Settings.HoldSecondsNextMeasure = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsPrevPage
    {
        get => _profile.Settings.HoldSecondsPrevPage > 0 ? _profile.Settings.HoldSecondsPrevPage : 3.0;
        set { _profile.Settings.HoldSecondsPrevPage = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsNextPage
    {
        get => _profile.Settings.HoldSecondsNextPage > 0 ? _profile.Settings.HoldSecondsNextPage : 3.0;
        set { _profile.Settings.HoldSecondsNextPage = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsDismiss
    {
        get => _profile.Settings.HoldSecondsDismiss > 0 ? _profile.Settings.HoldSecondsDismiss : 3.0;
        set { _profile.Settings.HoldSecondsDismiss = value; OnPropertyChanged(); SaveProfileSettings(); }
    }
    public double HoldSecondsRepeat
    {
        get => _profile.Settings.HoldSecondsRepeat > 0 ? _profile.Settings.HoldSecondsRepeat : 3.0;
        set { _profile.Settings.HoldSecondsRepeat = value; OnPropertyChanged(); SaveProfileSettings(); }
    }

    public double GetHoldSecondsForAction(string actionName) => actionName switch
    {
        "Listen" => HoldSecondsListen,
        "Practice" => HoldSecondsPractice,
        "Performance" => HoldSecondsPerformance,
        "TogglePlay" => HoldSecondsTogglePlay,
        "Restart" => HoldSecondsRestart,
        "PrevMeasure" => HoldSecondsPrevMeasure,
        "NextMeasure" => HoldSecondsNextMeasure,
        "PrevPage" => HoldSecondsPrevPage,
        "NextPage" => HoldSecondsNextPage,
        "Dismiss" => HoldSecondsDismiss,
        "Repeat" => HoldSecondsRepeat,
        _ => 3.0
    };

    private int _selectedSettingsTabIndex = 0;
    public int SelectedSettingsTabIndex
    {
        get => _selectedSettingsTabIndex;
        set
        {
            if (SetField(ref _selectedSettingsTabIndex, Math.Clamp(value, 0, 3)))
            {
                OnPropertyChanged(nameof(IsSettingsTabShortcutsVisible));
                OnPropertyChanged(nameof(IsSettingsTabMidiVisible));
                OnPropertyChanged(nameof(IsSettingsTabLatencyVisible));
                OnPropertyChanged(nameof(IsSettingsTabDisplayVisible));
            }
        }
    }

    public bool IsSettingsTabShortcutsVisible => SelectedSettingsTabIndex == 0;
    public bool IsSettingsTabMidiVisible => SelectedSettingsTabIndex == 1;
    public bool IsSettingsTabLatencyVisible => SelectedSettingsTabIndex == 2;
    public bool IsSettingsTabDisplayVisible => SelectedSettingsTabIndex == 3;

    private bool _isMultiTapPromptActive;
    private string _multiTapPromptActionName = string.Empty;
    private int _pendingMultiTapCount = 4;

    public bool IsMultiTapPromptActive
    {
        get => _isMultiTapPromptActive;
        set => SetField(ref _isMultiTapPromptActive, value);
    }

    public string MultiTapPromptActionName
    {
        get => _multiTapPromptActionName;
        set => SetField(ref _multiTapPromptActionName, value);
    }

    public int PendingMultiTapCount
    {
        get => _pendingMultiTapCount;
        set { SetField(ref _pendingMultiTapCount, Math.Clamp(value, 2, 10)); OnPropertyChanged(nameof(MultiTapPromptHeadlineText)); }
    }

    public string MultiTapPromptHeadlineText => $"Configure Multi-Tap Count for {GetActionDisplayName(MultiTapPromptActionName)}";

    public void OpenMultiTapPrompt(string actionName)
    {
        MultiTapPromptActionName = actionName;
        PendingMultiTapCount = GetMultiTapCountForAction(actionName);
        OnPropertyChanged(nameof(MultiTapPromptHeadlineText));
        IsMultiTapPromptActive = true;
    }

    public void AcceptMultiTapPrompt()
    {
        SetMultiTapCountForAction(MultiTapPromptActionName, PendingMultiTapCount);
        IsMultiTapPromptActive = false;
        NotifyMidiNoteTextChanged(MultiTapPromptActionName);
    }

    public void CancelMultiTapPrompt()
    {
        IsMultiTapPromptActive = false;
    }

    public void IncrementMultiTapCount() => PendingMultiTapCount++;
    public void DecrementMultiTapCount() => PendingMultiTapCount--;

    public void NotifyMidiNoteTextChanged(string actionName)
    {
        switch (actionName)
        {
            case "Listen": OnPropertyChanged(nameof(MidiShortcutListenNoteText)); break;
            case "Practice": OnPropertyChanged(nameof(MidiShortcutPracticeNoteText)); break;
            case "Performance": OnPropertyChanged(nameof(MidiShortcutPerformanceNoteText)); break;
            case "TogglePlay": OnPropertyChanged(nameof(MidiShortcutTogglePlayNoteText)); break;
            case "Restart": OnPropertyChanged(nameof(MidiShortcutRestartNoteText)); break;
            case "PrevMeasure": OnPropertyChanged(nameof(MidiShortcutPreviousMeasureNoteText)); break;
            case "NextMeasure": OnPropertyChanged(nameof(MidiShortcutNextMeasureNoteText)); break;
            case "PrevPage": OnPropertyChanged(nameof(MidiShortcutPreviousPageNoteText)); break;
            case "NextPage": OnPropertyChanged(nameof(MidiShortcutNextPageNoteText)); break;
            case "Dismiss": OnPropertyChanged(nameof(MidiShortcutDismissResultsNoteText)); break;
            case "Repeat": OnPropertyChanged(nameof(MidiShortcutRepeatResultsNoteText)); break;
        }
    }

    public bool AlwaysShowLiveNoteFeedback
    {
        get => _profile.Settings.AlwaysShowLiveNoteFeedback;
        set { _profile.Settings.AlwaysShowLiveNoteFeedback = value; OnPropertyChanged(); SaveProfileSettings(); }
    }

    public event EventHandler<(string kind, double beat, int midiNote)>? OnLiveNoteFeedbackTriggered;

    public void StartKeyLearning(string actionName)
    {
        CancelLearning();
        LearningActionName = actionName;
        LearningPromptText = $"Press any key on your keyboard to set shortcut for {actionName}...";
        IsKeyLearningActive = true;
        OnPropertyChanged(nameof(IsShortcutLearningActive));
    }

    public void StartMidiLearning(string actionName)
    {
        CancelLearning();
        LearningActionName = actionName;
        LearningPromptText = $"Press any key or pedal on your MIDI controller to bind {actionName}...";
        IsMidiLearningActive = true;
        OnPropertyChanged(nameof(IsShortcutLearningActive));
    }

    private bool _isConflictOverlayVisible;
    private string _conflictMessageText = string.Empty;
    public string PendingConflictActionName { get; set; } = string.Empty;
    public int? PendingConflictMidiNote { get; set; }
    public string? PendingConflictKeyString { get; set; }

    public bool IsConflictOverlayVisible
    {
        get => _isConflictOverlayVisible;
        set => SetField(ref _isConflictOverlayVisible, value);
    }

    public string ConflictMessageText
    {
        get => _conflictMessageText;
        set => SetField(ref _conflictMessageText, value);
    }

    public void CancelLearning()
    {
        IsKeyLearningActive = false;
        IsMidiLearningActive = false;
        LearningActionName = string.Empty;
        LearningPromptText = string.Empty;
        OnPropertyChanged(nameof(IsShortcutLearningActive));
    }

    public int GetActionBehaviorIndex(string actionName)
    {
        return actionName switch
        {
            "Listen" => BehaviorListenIndex,
            "Practice" => BehaviorPracticeIndex,
            "Performance" => BehaviorPerformanceIndex,
            "TogglePlay" => BehaviorTogglePlayIndex,
            "Restart" => BehaviorRestartIndex,
            "PrevMeasure" => BehaviorPrevMeasureIndex,
            "NextMeasure" => BehaviorNextMeasureIndex,
            "PrevPage" => BehaviorPrevPageIndex,
            "NextPage" => BehaviorNextPageIndex,
            "Dismiss" => BehaviorDismissIndex,
            "Repeat" => BehaviorRepeatIndex,
            _ => 1
        };
    }

    public bool IsActiveSessionRunning => IsLessonActive || IsPreviewPlaying;

    public string GetActionDisplayName(string actionName)
    {
        var isRunning = IsActiveSessionRunning;
        return actionName switch
        {
            "Listen" => "Listen to Score",
            "Practice" => "Begin Practice",
            "Performance" => "Begin Performance",
            "TogglePlay" => isRunning ? "Pause Session" : (SelectedLessonMode switch
            {
                LessonMode.Listen => "Listen to Score",
                LessonMode.TimedPlay => "Begin Performance",
                _ => "Begin Practice"
            }),
            "Restart" => isRunning ? "Restart Session" : (SelectedLessonMode switch
            {
                LessonMode.Listen => "Listen to Score",
                LessonMode.TimedPlay => "Begin Performance",
                _ => "Begin Practice"
            }),
            "PrevMeasure" => "Previous Measure",
            "NextMeasure" => "Next Measure",
            "PrevPage" => "Previous Page",
            "NextPage" => "Next Page",
            "Dismiss" => "Dismiss Results",
            "Repeat" => "Repeat Session",
            _ => actionName
        };
    }

    public string FindConflictingMidiAction(string targetAction, int midiNoteNumber)
    {
        if (midiNoteNumber <= 0) return string.Empty;

        if (targetAction != "Listen" && MidiShortcutListenNote == midiNoteNumber) return "Listen Mode";
        if (targetAction != "TogglePlay" && MidiShortcutTogglePlayNote == midiNoteNumber) return "Play / Pause";
        if (targetAction != "Restart" && MidiShortcutRestartNote == midiNoteNumber) return "Restart Session";
        if (targetAction != "PrevMeasure" && MidiShortcutPreviousMeasureNote == midiNoteNumber) return "Previous Measure";
        if (targetAction != "NextMeasure" && MidiShortcutNextMeasureNote == midiNoteNumber) return "Next Measure";
        if (targetAction != "PrevPage" && MidiShortcutPreviousPageNote == midiNoteNumber) return "Previous Page";
        if (targetAction != "NextPage" && MidiShortcutNextPageNote == midiNoteNumber) return "Next Page";
        if (targetAction != "Dismiss" && MidiShortcutDismissResultsNote == midiNoteNumber) return "Dismiss Results";
        if (targetAction != "Repeat" && MidiShortcutRepeatResultsNote == midiNoteNumber) return "Repeat Session";

        return string.Empty;
    }

    public string FindConflictingKeyAction(string targetAction, string keyString)
    {
        if (string.IsNullOrWhiteSpace(keyString)) return string.Empty;

        if (targetAction != "Listen" && string.Equals(KeyShortcutListen, keyString, StringComparison.OrdinalIgnoreCase)) return "Listen Mode";
        if (targetAction != "Practice" && string.Equals(KeyShortcutStartPractice, keyString, StringComparison.OrdinalIgnoreCase)) return "Practice Mode";
        if (targetAction != "Performance" && string.Equals(KeyShortcutStartPerformance, keyString, StringComparison.OrdinalIgnoreCase)) return "Performance Mode";
        if (targetAction != "TogglePlay" && string.Equals(KeyShortcutTogglePlay, keyString, StringComparison.OrdinalIgnoreCase)) return "Play / Pause";
        if (targetAction != "Restart" && string.Equals(KeyShortcutRestartSession, keyString, StringComparison.OrdinalIgnoreCase)) return "Restart Session";
        if (targetAction != "PrevMeasure" && string.Equals(KeyShortcutPreviousMeasure, keyString, StringComparison.OrdinalIgnoreCase)) return "Previous Measure";
        if (targetAction != "NextMeasure" && string.Equals(KeyShortcutNextMeasure, keyString, StringComparison.OrdinalIgnoreCase)) return "Next Measure";
        if (targetAction != "PrevPage" && string.Equals(KeyShortcutPreviousPage, keyString, StringComparison.OrdinalIgnoreCase)) return "Previous Page";
        if (targetAction != "NextPage" && string.Equals(KeyShortcutNextPage, keyString, StringComparison.OrdinalIgnoreCase)) return "Next Page";
        if (targetAction != "Dismiss" && string.Equals(KeyShortcutDismissResults, keyString, StringComparison.OrdinalIgnoreCase)) return "Dismiss Results";
        if (targetAction != "Repeat" && string.Equals(KeyShortcutRepeatResults, keyString, StringComparison.OrdinalIgnoreCase)) return "Repeat Session";

        return string.Empty;
    }

    public void UnbindMidiNoteFromAll(int midiNoteNumber, string exceptAction = "")
    {
        if (midiNoteNumber <= 0) return;

        if (exceptAction != "Listen" && MidiShortcutListenNote == midiNoteNumber) MidiShortcutListenNote = -1;
        if (exceptAction != "TogglePlay" && MidiShortcutTogglePlayNote == midiNoteNumber) MidiShortcutTogglePlayNote = -1;
        if (exceptAction != "Restart" && MidiShortcutRestartNote == midiNoteNumber) MidiShortcutRestartNote = -1;
        if (exceptAction != "PrevMeasure" && MidiShortcutPreviousMeasureNote == midiNoteNumber) MidiShortcutPreviousMeasureNote = -1;
        if (exceptAction != "NextMeasure" && MidiShortcutNextMeasureNote == midiNoteNumber) MidiShortcutNextMeasureNote = -1;
        if (exceptAction != "PrevPage" && MidiShortcutPreviousPageNote == midiNoteNumber) MidiShortcutPreviousPageNote = -1;
        if (exceptAction != "NextPage" && MidiShortcutNextPageNote == midiNoteNumber) MidiShortcutNextPageNote = -1;
        if (exceptAction != "Dismiss" && MidiShortcutDismissResultsNote == midiNoteNumber) MidiShortcutDismissResultsNote = -1;
        if (exceptAction != "Repeat" && MidiShortcutRepeatResultsNote == midiNoteNumber) MidiShortcutRepeatResultsNote = -1;
    }

    public void UnbindKeyStringFromAll(string keyString, string exceptAction = "")
    {
        if (string.IsNullOrWhiteSpace(keyString)) return;

        if (exceptAction != "Listen" && string.Equals(KeyShortcutListen, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutListen = string.Empty;
        if (exceptAction != "Practice" && string.Equals(KeyShortcutStartPractice, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutStartPractice = string.Empty;
        if (exceptAction != "Performance" && string.Equals(KeyShortcutStartPerformance, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutStartPerformance = string.Empty;
        if (exceptAction != "TogglePlay" && string.Equals(KeyShortcutTogglePlay, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutTogglePlay = string.Empty;
        if (exceptAction != "Restart" && string.Equals(KeyShortcutRestartSession, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutRestartSession = string.Empty;
        if (exceptAction != "PrevMeasure" && string.Equals(KeyShortcutPreviousMeasure, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutPreviousMeasure = string.Empty;
        if (exceptAction != "NextMeasure" && string.Equals(KeyShortcutNextMeasure, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutNextMeasure = string.Empty;
        if (exceptAction != "PrevPage" && string.Equals(KeyShortcutPreviousPage, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutPreviousPage = string.Empty;
        if (exceptAction != "NextPage" && string.Equals(KeyShortcutNextPage, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutNextPage = string.Empty;
        if (exceptAction != "Dismiss" && string.Equals(KeyShortcutDismissResults, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutDismissResults = string.Empty;
        if (exceptAction != "Repeat" && string.Equals(KeyShortcutRepeatResults, keyString, StringComparison.OrdinalIgnoreCase)) KeyShortcutRepeatResults = string.Empty;
    }

    public void UnbindAction(string action)
    {
        if (string.IsNullOrEmpty(action)) action = LearningActionName;
        if (string.IsNullOrEmpty(action)) return;

        if (IsMidiLearningActive)
        {
            ExecuteApplyMidiBinding(action, -1);
        }
        else if (IsKeyLearningActive)
        {
            ExecuteApplyKeyBinding(action, string.Empty);
        }
        else
        {
            ExecuteApplyMidiBinding(action, -1);
            ExecuteApplyKeyBinding(action, string.Empty);
        }

        CancelLearning();
    }

    public void ApplyLearnedKey(string keyString)
    {
        if (!IsKeyLearningActive || string.IsNullOrWhiteSpace(keyString)) return;
        var action = LearningActionName;

        if (string.Equals(keyString, "Escape", StringComparison.OrdinalIgnoreCase) || string.Equals(keyString, "Esc", StringComparison.OrdinalIgnoreCase))
        {
            UnbindAction(action);
            return;
        }

        CancelLearning();

        var conflictingAction = FindConflictingKeyAction(action, keyString);
        if (!string.IsNullOrEmpty(conflictingAction))
        {
            var msg = $"Note/Key '{keyString}' is also bound to '{conflictingAction}'.\n\nConfirming will assign it to '{GetActionDisplayName(action)}' while keeping its existing binding.\n\nDo you want to proceed?";
            PendingConflictActionName = action;
            PendingConflictMidiNote = null;
            PendingConflictKeyString = keyString;
            ConflictMessageText = msg;
            IsConflictOverlayVisible = true;
            return;
        }

        ExecuteApplyKeyBinding(action, keyString);
    }

    public void ExecuteApplyKeyBinding(string action, string keyString)
    {
        if (action == "Listen") KeyShortcutListen = keyString;
        else if (action == "Practice") KeyShortcutStartPractice = keyString;
        else if (action == "Performance") KeyShortcutStartPerformance = keyString;
        else if (action == "TogglePlay") KeyShortcutTogglePlay = keyString;
        else if (action == "Restart") KeyShortcutRestartSession = keyString;
        else if (action == "PrevMeasure") KeyShortcutPreviousMeasure = keyString;
        else if (action == "NextMeasure") KeyShortcutNextMeasure = keyString;
        else if (action == "PrevPage") KeyShortcutPreviousPage = keyString;
        else if (action == "NextPage") KeyShortcutNextPage = keyString;
        else if (action == "Dismiss") KeyShortcutDismissResults = keyString;
        else if (action == "Repeat") KeyShortcutRepeatResults = keyString;

        SaveProfileSettings();
    }

    public void ApplyLearnedMidiNote(int midiNoteNumber)
    {
        if (!IsMidiLearningActive) return;
        var action = LearningActionName;
        CancelLearning();

        var noteName = MidiNoteFormatter.Format(midiNoteNumber);
        var conflictingAction = FindConflictingMidiAction(action, midiNoteNumber);
        var isSingleTapPianoNote = (midiNoteNumber >= 21 && midiNoteNumber <= 108) && GetActionBehaviorIndex(action) == 1;

        if (!string.IsNullOrEmpty(conflictingAction) || isSingleTapPianoNote)
        {
            var msg = new StringBuilder();
            if (!string.IsNullOrEmpty(conflictingAction))
            {
                msg.AppendLine($"Note {noteName} (MIDI {midiNoteNumber}) is currently bound to '{conflictingAction}'.");
                msg.AppendLine("Choose 'Bind Anyway' to share this note, or 'Unbind Other' to replace the existing binding.");
            }
            if (isSingleTapPianoNote)
            {
                if (msg.Length > 0) msg.AppendLine();
                msg.AppendLine($"Warning: Note {noteName} is a single-press note in the standard piano playing range. Playing pieces containing {noteName} will trigger '{GetActionDisplayName(action)}'.");
            }
            msg.AppendLine();
            msg.AppendLine("How would you like to proceed?");

            PendingConflictActionName = action;
            PendingConflictMidiNote = midiNoteNumber;
            PendingConflictKeyString = null;
            ConflictMessageText = msg.ToString().TrimEnd();
            IsConflictOverlayVisible = true;
            return;
        }

        ExecuteApplyMidiBinding(action, midiNoteNumber);
    }

    public void ExecuteApplyMidiBinding(string action, int midiNoteNumber)
    {
        if (action == "Listen") MidiShortcutListenNote = midiNoteNumber;
        else if (action == "TogglePlay") MidiShortcutTogglePlayNote = midiNoteNumber;
        else if (action == "Restart") MidiShortcutRestartNote = midiNoteNumber;
        else if (action == "PrevMeasure") MidiShortcutPreviousMeasureNote = midiNoteNumber;
        else if (action == "NextMeasure") MidiShortcutNextMeasureNote = midiNoteNumber;
        else if (action == "PrevPage") MidiShortcutPreviousPageNote = midiNoteNumber;
        else if (action == "NextPage") MidiShortcutNextPageNote = midiNoteNumber;
        else if (action == "Dismiss") MidiShortcutDismissResultsNote = midiNoteNumber;
        else if (action == "Repeat") MidiShortcutRepeatResultsNote = midiNoteNumber;

        SaveProfileSettings();
    }

    public void ConfirmConflict(bool unbindExisting = false)
    {
        IsConflictOverlayVisible = false;
        var action = PendingConflictActionName;
        if (string.IsNullOrEmpty(action)) return;

        if (PendingConflictMidiNote.HasValue)
        {
            if (unbindExisting) UnbindMidiNoteFromAll(PendingConflictMidiNote.Value, action);
            ExecuteApplyMidiBinding(action, PendingConflictMidiNote.Value);
        }
        else if (!string.IsNullOrEmpty(PendingConflictKeyString))
        {
            if (unbindExisting) UnbindKeyStringFromAll(PendingConflictKeyString, action);
            ExecuteApplyKeyBinding(action, PendingConflictKeyString);
        }

        PendingConflictActionName = string.Empty;
        PendingConflictMidiNote = null;
        PendingConflictKeyString = null;
    }

    public void CancelConflict()
    {
        IsConflictOverlayVisible = false;
        PendingConflictActionName = string.Empty;
        PendingConflictMidiNote = null;
        PendingConflictKeyString = null;
    }

    public void SanitizeShortcutBindings()
    {
        var settings = _profile.Settings;
        if (settings is null) return;
        if (settings.MidiShortcutListenNote <= 0) settings.MidiShortcutListenNote = 48; // C3
        if (settings.MidiShortcutTogglePlayNote <= 0) settings.MidiShortcutTogglePlayNote = 60; // C4
        if (settings.MidiShortcutRestartNote <= 0) settings.MidiShortcutRestartNote = 60; // C4
        if (settings.MidiShortcutHoldSeconds <= 0) settings.MidiShortcutHoldSeconds = 3.0;
        if (settings.MidiShortcutPreviousMeasureNote <= 0) settings.MidiShortcutPreviousMeasureNote = 57; // A3
        if (settings.MidiShortcutNextMeasureNote <= 0) settings.MidiShortcutNextMeasureNote = 59; // B3
        if (settings.MidiShortcutPreviousPageNote <= 0) settings.MidiShortcutPreviousPageNote = 53; // F3
        if (settings.MidiShortcutNextPageNote <= 0) settings.MidiShortcutNextPageNote = 55; // G3
        if (settings.MidiShortcutDismissResultsNote <= 0) settings.MidiShortcutDismissResultsNote = 62; // D4
        if (settings.MidiShortcutRepeatResultsNote <= 0) settings.MidiShortcutRepeatResultsNote = 67; // G4

        var midiBinds = new List<(string action, int note)>
        {
            ("Listen", MidiShortcutListenNote),
            ("TogglePlay", MidiShortcutTogglePlayNote),
            ("Restart", MidiShortcutRestartNote),
            ("PrevMeasure", MidiShortcutPreviousMeasureNote),
            ("NextMeasure", MidiShortcutNextMeasureNote),
            ("PrevPage", MidiShortcutPreviousPageNote),
            ("NextPage", MidiShortcutNextPageNote),
            ("Dismiss", MidiShortcutDismissResultsNote),
            ("Repeat", MidiShortcutRepeatResultsNote)
        };

        var seenMidi = new HashSet<int>();
        foreach (var (action, note) in midiBinds)
        {
            if (note <= 0) continue;
            if (seenMidi.Contains(note))
            {
                UnbindMidiNoteFromAll(note, action);
            }
            else
            {
                seenMidi.Add(note);
            }
        }

        SaveProfileSettings();
    }

    public void StartAutoRepeatCountdown()
    {
        StopAutoRepeatCountdown();
        if (!AutoDismissResultsEnabled)
        {
            AutoRepeatProgress = 1.0;
            AutoRepeatStatusText = "Press Space, Esc, or MIDI key to continue.";
            AutoRepeatUpdated?.Invoke(this, EventArgs.Empty);
            return;
        }

        _autoRepeatTotalSeconds = IsLoopEnabled ? 5.0 : 10.0;
        _autoRepeatStartTime = DateTime.UtcNow;
        AutoRepeatProgress = 1.0;
        UpdateAutoRepeatLabel(_autoRepeatTotalSeconds);

        _autoRepeatTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _autoRepeatTimer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.UtcNow - _autoRepeatStartTime).TotalSeconds;
            var remaining = Math.Max(0, _autoRepeatTotalSeconds - elapsed);
            AutoRepeatProgress = Math.Clamp(remaining / _autoRepeatTotalSeconds, 0, 1);
            UpdateAutoRepeatLabel(remaining);
            AutoRepeatUpdated?.Invoke(this, EventArgs.Empty);

            if (remaining <= 0)
            {
                StopAutoRepeatCountdown();
                _ = TriggerAutoRepeatAsync();
            }
        };
        _autoRepeatTimer.Start();
    }

    private void UpdateAutoRepeatLabel(double remainingSeconds)
    {
        AutoRepeatStatusText = IsLoopEnabled
            ? $"Loop active · Auto-restarting in {remainingSeconds:0.0}s..."
            : $"Auto-restarting in {remainingSeconds:0.0}s · Press C4 or Space to restart now";
    }

    public void StopAutoRepeatCountdown()
    {
        _autoRepeatTimer?.Stop();
        _autoRepeatTimer = null;
    }

    public async Task TriggerAutoRepeatAsync()
    {
        DismissResults();
        await RestartPreviewAsync();
    }

    public string MetronomeLabel => _metronomeEnabled
        ? $"Metronome on · {EffectiveLessonTempoBpm:0} BPM"
        : "Metronome off";
    public string InputSourceLabel => (_nativeInputActive, UseKeyboardSimulation) switch
    {
        (true, true) => $"MIDI: {SelectedMidiDevice?.Name ?? "connected keyboard"} + computer keys",
        (true, false) => $"MIDI: {SelectedMidiDevice?.Name ?? "connected keyboard"}",
        (false, true) => "Computer keys active · no MIDI capture",
        _ => "No active lesson input"
    };
    public string StartLessonReason
    {
        get
        {
            if (!HasScore) return "Import a MusicXML score first.";
            if (SelectedLessonMode == LessonMode.Listen) return "Ready to play the selected score automatically without assessment.";
            var blockingWarning = _score?.ValidationWarnings.FirstOrDefault(warning =>
                warning.BlocksAssessment &&
                warning.StartMeasure <= FocusEndMeasure &&
                warning.EndMeasure >= FocusStartMeasure);
            if (blockingWarning is not null && SelectedLessonMode == LessonMode.TimedPlay)
            {
                return $"Assessment is unavailable for bars {blockingWarning.StartMeasure}–{blockingWarning.EndMeasure}. " +
                       "Choose a range outside this warning; full details are in the import warning badge.";
            }
            if (_score?.PartialRepeatReason(FocusStartMeasure, FocusEndMeasure) is { } partialRepeat) return partialRepeat;
            if (_lessonGroups.Count == 0) return $"No playable notes were found for {SelectedModeLabel}.";
            if (!HasAcceptedInput) return "Open Settings and connect a MIDI keyboard or enable computer piano keys.";
            if (blockingWarning is not null && SelectedLessonMode == LessonMode.WaitForYou)
            {
                return "Ready for guided Practice. Explicit repeat barlines are followed; ambiguous volta endings remain unscored and are not saved as an assessed result.";
            }
            return (_nativeInputActive, UseKeyboardSimulation) switch
            {
                (true, true) => "Ready. Physical MIDI and computer piano keys both feed lesson scoring.",
                (true, false) => "Ready. The selected MIDI input is connected for live capture.",
                _ => "Ready with computer piano keys. No MIDI hardware is currently capturing."
            };
        }
    }

    public PracticeMode SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (SetField(ref _selectedMode, value))
            {
                RefreshLessonGroups();
                OnPropertyChanged(nameof(SelectedModeLabel));
                OnPropertyChanged(nameof(SelectedModeDescription));
                OnPropertyChanged(nameof(StartLessonReason));
                SaveProfileSettings();
            }
        }
    }

    public LessonMode SelectedLessonMode
    {
        get => _selectedLessonMode;
        private set
        {
            if (SetField(ref _selectedLessonMode, value))
            {
                OnPropertyChanged(nameof(SelectedLessonModeLabel));
                OnPropertyChanged(nameof(ModeTitleLabel));
                ResetLessonStats();
                SaveProfileSettings();
            }
        }
    }

    public ScoreReadingMode ReadingMode
    {
        get => _readingMode;
        private set
        {
            if (SetField(ref _readingMode, value)) SaveProfileSettings();
        }
    }

    public MidiDeviceInfo? SelectedMidiDevice
    {
        get => _selectedMidiDevice;
        set
        {
            if (SetField(ref _selectedMidiDevice, value))
            {
                ConnectSelectedMidiDevice();
                if (value is not null)
                {
                    var settings = _profile.Settings ??= new CadenzaUserSettings();
                    settings.PreferredMidiDeviceId = value.Id;
                    settings.PreferredMidiDeviceName = value.Name;
                    SaveProfileSettings();
                    OnPropertyChanged(nameof(PreferredMidiDeviceLabel));
                }
                OnPropertyChanged(nameof(InputSourceLabel));
                OnPropertyChanged(nameof(StartLessonReason));
                OnPropertyChanged(nameof(CanStartLesson));
            }
        }
    }

    public bool MidiMonitorEnabled
    {
        get => _midiMonitorEnabled;
        set
        {
            if (!SetField(ref _midiMonitorEnabled, value)) return;
            if (!value) _liveSynth.AllNotesOff();
            LiveMonitorStatus = value
                ? "MIDI Monitor / Thru is on. Incoming notes sound through the Windows piano output."
                : "MIDI Monitor / Thru is off. Input is still captured and scored.";
            SaveProfileSettings();
        }
    }

    public int MonitorVolume
    {
        get => _monitorVolume;
        set
        {
            if (!SetField(ref _monitorVolume, Math.Clamp(value, 0, 100))) return;
            ApplyMixerVolumes();
            OnPropertyChanged(nameof(MonitorVolumeLabel));
            SaveProfileSettings();
        }
    }

    public string MonitorVolumeLabel => $"{MonitorVolume}%";

    public IReadOnlyList<AudioSoundPreset> SoundPresets => AudioSoundPreset.AllPresets;

    public AudioSoundPreset PlaybackSoundPreset
    {
        get => _playbackSoundPreset;
        set
        {
            if (!SetField(ref _playbackSoundPreset, value ?? AudioSoundPreset.AcousticGrand)) return;
            ApplySoundPresets();
            SaveProfileSettings();
            if (IsPreviewPlaying && _previewUsesScore)
            {
                var currentBeat = CursorBeat;
                _ = StartScorePreviewAsync(currentBeat);
            }
        }
    }

    public AudioSoundPreset LiveSoundPreset
    {
        get => _liveSoundPreset;
        set
        {
            if (!SetField(ref _liveSoundPreset, value ?? AudioSoundPreset.AcousticGrand)) return;
            ApplySoundPresets();
            SaveProfileSettings();
        }
    }

    public bool MatchPlaybackSynthEnabled
    {
        get => _matchPlaybackSynthEnabled;
        set
        {
            if (!SetField(ref _matchPlaybackSynthEnabled, value)) return;
            SaveProfileSettings();
        }
    }

    public int OverallVolume
    {
        get => _overallVolume;
        set
        {
            if (!SetField(ref _overallVolume, Math.Clamp(value, 0, 100))) return;
            ApplyMixerVolumes();
            OnPropertyChanged(nameof(OverallVolumeLabel));
            OnPropertyChanged(nameof(IsMetronomeAudible));
            SaveProfileSettings();
        }
    }

    public string OverallVolumeLabel => $"{OverallVolume}%";

    public int InstrumentalVolume
    {
        get => _instrumentalVolume;
        set
        {
            if (!SetField(ref _instrumentalVolume, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(InstrumentalVolumeLabel));
            SaveProfileSettings();
        }
    }

    public string InstrumentalVolumeLabel => $"{InstrumentalVolume}%";

    public bool InstrumentalMuted
    {
        get => _instrumentalMuted;
        set
        {
            if (!SetField(ref _instrumentalMuted, value)) return;
            OnPropertyChanged(nameof(SongAccompanimentEnabled));
            SaveProfileSettings();
        }
    }

    public bool SongAccompanimentEnabled
    {
        get => !InstrumentalMuted;
        set => InstrumentalMuted = !value;
    }

    public int MetronomeVolume
    {
        get => _metronomeVolume;
        set
        {
            if (!SetField(ref _metronomeVolume, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(MetronomeVolumeLabel));
            OnMetronomeAudibilityChanged();
            SaveProfileSettings();
        }
    }

    public string MetronomeVolumeLabel => $"{MetronomeVolume}%";

    public bool MetronomeMuted
    {
        get => _metronomeMuted;
        set
        {
            if (!SetField(ref _metronomeMuted, value)) return;
            OnPropertyChanged(nameof(MetronomeSoundEnabled));
            OnMetronomeAudibilityChanged();
            SaveProfileSettings();
        }
    }

    public bool MetronomeSoundEnabled
    {
        get => !MetronomeMuted;
        set => MetronomeMuted = !value;
    }

    public bool IsMetronomeAudible => MetronomeEnabled && !MetronomeMuted && MetronomeVolume > 0 && OverallVolume > 0;

    private void OnMetronomeAudibilityChanged()
    {
        OnPropertyChanged(nameof(IsMetronomeAudible));
        OnPropertyChanged(nameof(MetronomeLabel));
    }

    public bool PracticeFullAccompanimentEnabled
    {
        get => _practiceFullAccompanimentEnabled;
        set
        {
            if (!SetField(ref _practiceFullAccompanimentEnabled, value)) return;
            SaveProfileSettings();
        }
    }

    public bool PerformanceFullAccompanimentEnabled
    {
        get => _performanceFullAccompanimentEnabled;
        set
        {
            if (!SetField(ref _performanceFullAccompanimentEnabled, value)) return;
            SaveProfileSettings();
        }
    }

    public bool OtherHandAccompanimentEnabled
    {
        get => _otherHandAccompanimentEnabled;
        set
        {
            if (!SetField(ref _otherHandAccompanimentEnabled, value)) return;
            if (!value) _accompanimentSynth.AllNotesOff();
            SaveProfileSettings();
        }
    }

    public int OtherHandAccompanimentVolume
    {
        get => _otherHandAccompanimentVolume;
        set
        {
            if (!SetField(ref _otherHandAccompanimentVolume, Math.Clamp(value, 0, 100))) return;
            ApplyMixerVolumes();
            OnPropertyChanged(nameof(OtherHandAccompanimentVolumeLabel));
            SaveProfileSettings();
        }
    }

    public string OtherHandAccompanimentVolumeLabel => $"{OtherHandAccompanimentVolume}%";
    public bool HasVocalTrack => false;
    public string VocalAvailabilityLabel => "No vocal audio is included with this lesson.";

    public bool PedalEnabled
    {
        get => _pedalEnabled;
        set
        {
            if (!SetField(ref _pedalEnabled, value)) return;
            OnPropertyChanged(nameof(PedalStatusLabel));
            OnPropertyChanged(nameof(PedalGradingLabel));
            OnPropertyChanged(nameof(PedalCategoryLabel));
            OnPropertyChanged(nameof(DashboardScoreSummary));
            OnPropertyChanged(nameof(DashboardScoreSummary));
            StatusMessage = value
                ? "Sustain-pedal cues are enabled. CC64 is monitored and reported; pitch scoring remains note-based."
                : "Sustain-pedal setup is optional and currently off.";
            SaveProfileSettings();
        }
    }

    public string PedalStatusLabel => !PedalEnabled ? "Optional pedal disabled" : _pedalDown ? "Sustain pedal down (CC64)" : "Sustain pedal ready (CC64)";

    public string PedalGradingLabel => _score?.Marks.Any(mark => mark.Kind == ScoreMarkKind.Pedal) == true
        ? "This score contains pedal cues; CC64 timing is graded."
        : "This score has no pedal marks. CC64 is monitored and passed through, but not graded.";

    public int LatencyMilliseconds
    {
        get => _latencyMilliseconds;
        set
        {
            if (!SetField(ref _latencyMilliseconds, Math.Clamp(value, -250, 500))) return;
            OnPropertyChanged(nameof(LatencyLabel));
            SaveProfileSettings();
        }
    }

    public string LatencyLabel => LatencyMilliseconds == 0 ? "0 ms correction" : $"{LatencyMilliseconds:+0;-0} ms correction";

    #region Music Library Management

    public string LibrarySearchQuery
    {
        get => _librarySearchQuery;
        set
        {
            if (SetField(ref _librarySearchQuery, value))
            {
                _libraryCurrentPage = 1;
                OnPropertyChanged(nameof(IsLibrarySearchQueryEmpty));
                ApplyLibraryFilterAndPagination();
            }
        }
    }

    public bool IsLibrarySearchQueryEmpty => string.IsNullOrEmpty(LibrarySearchQuery);

    public int LibraryCurrentPage
    {
        get => _libraryCurrentPage;
        set
        {
            if (SetField(ref _libraryCurrentPage, Math.Clamp(value, 1, LibraryTotalPages)))
            {
                ApplyLibraryFilterAndPagination();
            }
        }
    }

    public int LibraryPageSize
    {
        get => _libraryPageSize;
        set
        {
            if (SetField(ref _libraryPageSize, Math.Max(1, value)))
            {
                _libraryCurrentPage = 1;
                ApplyLibraryFilterAndPagination();
            }
        }
    }

    public int LibraryTotalPages
    {
        get
        {
            var totalFiltered = FilteredLibraryItems.Count;
            return Math.Max(1, (int)Math.Ceiling(totalFiltered / (double)_libraryPageSize));
        }
    }

    public string LibraryPaginationLabel => $"Page {LibraryCurrentPage} of {LibraryTotalPages} · {FilteredLibraryItems.Count} songs";
    public bool CanGoToPreviousLibraryPage => LibraryCurrentPage > 1;
    public bool CanGoToNextLibraryPage => LibraryCurrentPage < LibraryTotalPages;

    public bool IsSelectAllLibraryItemsChecked
    {
        get => _isSelectAllLibraryItemsChecked;
        set
        {
            if (SetField(ref _isSelectAllLibraryItemsChecked, value))
            {
                foreach (var item in PagedLibraryItems)
                {
                    item.IsSelected = value;
                }
                OnPropertyChanged(nameof(SelectedLibraryItemCount));
                OnPropertyChanged(nameof(HasSelectedLibraryItems));
                OnPropertyChanged(nameof(SelectedLibraryCountLabel));
            }
        }
    }

    public int SelectedLibraryItemCount => LibraryItems.Count(item => item.IsSelected);
    public bool HasSelectedLibraryItems => SelectedLibraryItemCount > 0;
    public string SelectedLibraryCountLabel => $"{SelectedLibraryItemCount} selected";

    public bool IsRenameOverlayVisible
    {
        get => _isRenameOverlayVisible;
        set => SetField(ref _isRenameOverlayVisible, value);
    }

    public string RenameItemTitleInput
    {
        get => _renameItemTitleInput;
        set => SetField(ref _renameItemTitleInput, value);
    }

    public LibraryItemViewModel? ItemBeingRenamed
    {
        get => _itemBeingRenamed;
        set => SetField(ref _itemBeingRenamed, value);
    }

    private List<LibraryItemViewModel> FilteredLibraryItems
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LibrarySearchQuery))
                return LibraryItems.ToList();

            var query = LibrarySearchQuery.Trim();
            return LibraryItems.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.OriginalFileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Composer.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
    }

    public void RefreshLibrary()
    {
        var rawItems = _libraryStore.LoadLibrary();

        foreach (var existing in LibraryItems)
        {
            existing.PropertyChanged -= LibraryItem_PropertyChanged;
        }

        LibraryItems.Clear();
        var currentPath = _score?.SourcePath;

        foreach (var item in rawItems)
        {
            var vm = new LibraryItemViewModel(item)
            {
                IsActiveScore = !string.IsNullOrWhiteSpace(currentPath) &&
                                string.Equals(item.StoredFilePath, currentPath, StringComparison.OrdinalIgnoreCase)
            };
            vm.PropertyChanged += LibraryItem_PropertyChanged;
            LibraryItems.Add(vm);
        }

        ApplyLibraryFilterAndPagination();
    }

    private void LibraryItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedLibraryItemCount));
            OnPropertyChanged(nameof(HasSelectedLibraryItems));
            OnPropertyChanged(nameof(SelectedLibraryCountLabel));
        }
    }

    public void ApplyLibraryFilterAndPagination()
    {
        var filtered = FilteredLibraryItems;
        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)_libraryPageSize));
        _libraryCurrentPage = Math.Clamp(_libraryCurrentPage, 1, totalPages);

        PagedLibraryItems.Clear();
        var pageItems = filtered
            .Skip((_libraryCurrentPage - 1) * _libraryPageSize)
            .Take(_libraryPageSize);

        foreach (var item in pageItems)
        {
            PagedLibraryItems.Add(item);
        }

        OnPropertyChanged(nameof(LibraryTotalPages));
        OnPropertyChanged(nameof(LibraryPaginationLabel));
        OnPropertyChanged(nameof(CanGoToPreviousLibraryPage));
        OnPropertyChanged(nameof(CanGoToNextLibraryPage));
        OnPropertyChanged(nameof(SelectedLibraryItemCount));
        OnPropertyChanged(nameof(HasSelectedLibraryItems));
        OnPropertyChanged(nameof(SelectedLibraryCountLabel));
    }

    public void LibraryNextPage()
    {
        if (CanGoToNextLibraryPage)
        {
            LibraryCurrentPage++;
        }
    }

    public void LibraryPreviousPage()
    {
        if (CanGoToPreviousLibraryPage)
        {
            LibraryCurrentPage--;
        }
    }

    public void ClearLibrarySelection()
    {
        foreach (var item in LibraryItems)
        {
            item.IsSelected = false;
        }
        _isSelectAllLibraryItemsChecked = false;
        OnPropertyChanged(nameof(IsSelectAllLibraryItemsChecked));
        OnPropertyChanged(nameof(SelectedLibraryItemCount));
        OnPropertyChanged(nameof(HasSelectedLibraryItems));
        OnPropertyChanged(nameof(SelectedLibraryCountLabel));
    }

    public void DeleteSelectedLibraryItems()
    {
        var selectedIds = LibraryItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        if (selectedIds.Count == 0) return;

        _libraryStore.DeleteItems(selectedIds);
        ClearLibrarySelection();
        RefreshLibrary();
    }

    public void DeleteLibraryItem(LibraryItemViewModel? item)
    {
        if (item is null) return;
        _libraryStore.DeleteItem(item.Id);
        RefreshLibrary();
    }

    public void OpenRenameOverlay(LibraryItemViewModel? item)
    {
        if (item is null) return;
        ItemBeingRenamed = item;
        RenameItemTitleInput = item.DisplayName;
        IsRenameOverlayVisible = true;
    }

    public void SaveRenameItem()
    {
        if (ItemBeingRenamed is null || string.IsNullOrWhiteSpace(RenameItemTitleInput)) return;
        var trimmed = RenameItemTitleInput.Trim();
        _libraryStore.RenameItem(ItemBeingRenamed.Id, trimmed);
        ItemBeingRenamed.DisplayName = trimmed;
        if (_score is not null && string.Equals(ItemBeingRenamed.StoredFilePath, _score.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            ScoreTitle = trimmed;
        }
        IsRenameOverlayVisible = false;
        RefreshLibrary();
    }

    public void CloseRenameOverlay()
    {
        IsRenameOverlayVisible = false;
        ItemBeingRenamed = null;
    }

    public void LoadLibraryItem(LibraryItemViewModel? item)
    {
        if (item is null || !File.Exists(item.StoredFilePath)) return;
        LoadScore(item.StoredFilePath);
        _libraryStore.RecordPlayed(item.Id);
        RefreshLibrary();
        IsPlayerVisible = true;
    }

    #endregion

    public async Task CalibrateLatencyAsync()
    {
        if (IsCalibrationActive) return;
        if (SelectedMidiDevice is null && !UseKeyboardSimulation)
        {
            StatusMessage = "Select a MIDI device or enable computer keyboard input before calibration.";
            LiveMonitorStatus = StatusMessage;
            return;
        }

        _calibrationOffsets.Clear();
        IsCalibrationActive = true;
        StatusMessage = "Latency calibration: play any key immediately after each of four clicks.";
        LiveMonitorStatus = StatusMessage;
        try
        {
            await Task.Delay(700);
            for (var index = 0; index < 4; index++)
            {
                _calibrationClickIndex = index;
                _calibrationCapturedForClick = -1;
                _lastCalibrationClickTimestamp = Stopwatch.GetTimestamp();
                _audioService.PlayMetronomeClick(index == 0, EffectiveMixerVolume(MetronomeVolume));
                StatusMessage = $"Latency calibration: click {index + 1}/4 — play now.";
                await Task.Delay(850);
            }

            if (_calibrationOffsets.Count == 0)
            {
                StatusMessage = "Calibration received no notes. The existing latency correction was kept.";
                LiveMonitorStatus = StatusMessage;
            }
            else
            {
                LatencyMilliseconds = (int)Math.Round(_calibrationOffsets.Average());
                StatusMessage = $"Latency calibrated from {_calibrationOffsets.Count} response(s): {LatencyLabel}.";
                LiveMonitorStatus = StatusMessage;
            }
        }
        finally
        {
            IsCalibrationActive = false;
            _calibrationClickIndex = -1;
        }
    }

    public bool UseKeyboardSimulation
    {
        get => _useKeyboardSimulation;
        set
        {
            if (SetField(ref _useKeyboardSimulation, value))
            {
                // The computer keyboard is an additional source. It must never stop
                // or replace an active physical MIDI capture.
                if (SelectedMidiDevice is not null && !_midiDeviceService.IsCapturing)
                    ConnectSelectedMidiDevice();
                MidiApiDetail = value
                    ? "Computer piano keys are enabled alongside any connected WinMM MIDI input."
                    : "Computer piano keys are off. Connected WinMM MIDI input remains active.";
                LiveMonitorStatus = value
                    ? "Computer keys enabled. Physical MIDI remains accepted whenever connected."
                    : MidiMonitorEnabled
                        ? "Computer keys off. Physical MIDI input and keyboard sound remain active."
                        : "Computer keys off. Physical MIDI input remains captured for scoring.";
                OnPropertyChanged(nameof(InputSourceLabel));
                OnPropertyChanged(nameof(HasAcceptedInput));
                OnPropertyChanged(nameof(StartLessonReason));
                OnPropertyChanged(nameof(CanStartLesson));
                KeyboardSimulationHint = value
                    ? "A S D F G H J K L ; = white keys · W E T Y U O P = black keys · not MIDI hardware."
                    : "Computer keyboard input is off.";
                SaveProfileSettings();
            }
        }
    }

    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set
        {
            if (SetField(ref _metronomeEnabled, value))
            {
                PreviewStatusLabel = value
                    ? "Preview and lessons will use the parsed score tempo."
                    : "Metronome is off; lesson timing still uses the parsed score tempo.";
                OnMetronomeAudibilityChanged();
                SaveProfileSettings();
            }
        }
    }

    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set
        {
            if (!SetField(ref _isLoopEnabled, value)) return;
            OnPropertyChanged(nameof(LoopButtonToolTip));
            SaveProfileSettings();
        }
    }

    public bool OnlyShowFeedbackOnPerformanceEnd
    {
        get => _onlyShowFeedbackOnPerformanceEnd;
        set
        {
            if (SetField(ref _onlyShowFeedbackOnPerformanceEnd, value))
            {
                SaveProfileSettings();
            }
        }
    }

    public string LoopButtonToolTip => IsLoopEnabled
        ? "Loop playback: ON (Click to disable)"
        : "Loop playback: OFF (Click to enable)";

    public string SelectedModeLabel => SelectedMode switch
    {
        PracticeMode.LeftHand => "Left hand",
        PracticeMode.RightHand => "Right hand",
        _ => "Both hands"
    };

    public string SelectedModeDescription => SelectedMode switch
    {
        PracticeMode.LeftHand => "Practice staff 2 while staff 1 remains visible for context.",
        PracticeMode.RightHand => "Practice staff 1 while staff 2 remains visible for context.",
        _ => "Practice both piano staves together."
    };

    public string SelectedLessonModeLabel => SelectedLessonMode switch
    {
        LessonMode.Listen => "Listen · automatic score playback",
        LessonMode.WaitForYou => "Practice · waits for every note",
        _ => "Performance · timed and assessed"
    };

    public void LoadScore(string path)
    {
        try
        {
            _score = _importer.Import(path);
            var initialTitle = NormalizeSampleTitle(_score.Title, path);
            ScoreByline = _score.ComposerOrCreator;
            SourceFileLabel = Path.GetFileName(_score.SourcePath);
            var libItem = _libraryStore.AddOrUpdateFile(path, initialTitle, ScoreByline, _score.MeasureCount);
            ScoreTitle = libItem.DisplayName;
            _libraryStore.RecordPlayed(libItem.Id);
            RefreshLibrary();
            ScoreStatusLabel = $"{_score.FormatVersion} | {_score.SourceContainer} | authoritative notation source";
            FormatLabel = _score.FormatVersion;
            KeyLabel = _score.KeySignature;
            TimeLabel = _score.TimeSignature;
            TempoLabel = _score.Tempo;
            MeasureLabel = _score.MeasureCount.ToString("N0");
            NoteLabel = _score.TotalNoteCount.ToString("N0");
            LyricLabel = _score.TotalLyricCount.ToString("N0");
            PartLabel = _score.Parts.Count == 0 ? "None" : string.Join(" / ", _score.Parts.Select(part => part.Name));
            Measures.Clear();
            foreach (var measure in _score.Measures) Measures.Add(measure);
            MeasureNumbers.Clear();
            for (var measure = 1; measure <= _score.MeasureCount; measure++) MeasureNumbers.Add(measure);
            _focusStartMeasure = 1;
            _focusEndMeasure = 0;
            var songKey = GetSongProgressKey(_score);
            (_profile.Songs ??= new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase))
                .TryGetValue(songKey, out _currentSongProgress);
            OnPropertyChanged(nameof(FocusStartMeasure));
            OnPropertyChanged(nameof(FocusEndMeasure));
            OnPropertyChanged(nameof(FocusRangeLabel));
            OnPropertyChanged(nameof(EffectiveLessonTempoBpm));
            OnPropertyChanged(nameof(LessonTempoLabel));
            OnPropertyChanged(nameof(LessonTempoPercentText));
            OnPropertyChanged(nameof(EffectiveLessonTempoBpmText));
            OnPropertyChanged(nameof(HasScore));
            OnPropertyChanged(nameof(CurrentScore));
            OnPropertyChanged(nameof(HasImportValidationWarnings));
            OnPropertyChanged(nameof(ImportValidationWarnings));
            OnPropertyChanged(nameof(ImportWarningBadgeLabel));
            OnPropertyChanged(nameof(ImportValidationSummary));
            OnPropertyChanged(nameof(NoteLyricLabel));
            OnPropertyChanged(nameof(MetronomeLabel));
            OnPropertyChanged(nameof(PedalGradingLabel));
            OnPropertyChanged(nameof(PedalCategoryLabel));
            OnPropertyChanged(nameof(DashboardScoreSummary));
            OnPropertyChanged(nameof(DashboardProgressSummary));
            OnPropertyChanged(nameof(RecentAttemptLabel));
            OnPropertyChanged(nameof(CumulativePracticeLabel));
            OnPropertyChanged(nameof(CompletedAttemptCount));
            OnPropertyChanged(nameof(CanUseTransport));
            RefreshLessonGroups();
            CursorBeat = SelectedPreviewStartBeat;
            StatusMessage = _score.ValidationWarnings.Count == 0
                ? $"Loaded {ScoreTitle} | {_score.MeasureCount:N0} written measures | {_score.PerformanceMeasures.Count:N0} performed measures | {_score.TempoBpm:0} BPM."
                : $"Loaded with {_score.ValidationWarnings.Count} validation warning(s). Listen is available; affected assessed ranges are disabled.";
            PreviewStatusLabel = "Preview is ready. It includes a simple synthesized piano-like sound, not an audio recording.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or XmlException or ArgumentException)
        {
            StatusMessage = $"Import failed: {exception.Message}";
            throw;
        }
    }

    private static string NormalizeSampleTitle(string importedTitle, string sourcePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (fileName.Contains("drivers-license", StringComparison.OrdinalIgnoreCase) ||
            importedTitle.Replace("'", string.Empty, StringComparison.Ordinal).Contains("drivers licence", StringComparison.OrdinalIgnoreCase) ||
            importedTitle.Replace("'", string.Empty, StringComparison.Ordinal).Contains("drivers license", StringComparison.OrdinalIgnoreCase))
        {
            return "drivers license";
        }
        return importedTitle;
    }

    public void RefreshMidiDevices() => RefreshMidiDevices(userInitiated: true);

    private void RefreshMidiDevices(bool userInitiated)
    {
        var previousId = SelectedMidiDevice?.Id;
        var previousName = SelectedMidiDevice?.Name;
        var snapshot = _midiDeviceService.DiscoverInputDevices();
        MidiDevices.Clear();
        foreach (var device in snapshot.Devices) MidiDevices.Add(device);
        var settings = _profile.Settings ??= new CadenzaUserSettings();
        var match = MidiDevices.FirstOrDefault(device =>
                string.Equals(device.Id, previousId, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(previousName) &&
                 string.Equals(device.Name, previousName, StringComparison.OrdinalIgnoreCase))) ??
            UserProfileStore.MatchPreferredMidiDevice(settings, MidiDevices);
        if (match is not null)
        {
            SelectedMidiDevice = match;
        }
        else
        {
            _midiDeviceService.StopInput();
            SetNativeInputActive(false);
            _selectedMidiDevice = null;
            OnPropertyChanged(nameof(SelectedMidiDevice));
            MidiLiveIndicator = "MIDI disconnected";
            LastMidiKeyLabel = "No live MIDI input";
        }

        MidiStatusLabel = snapshot.IsDiscoverySupported
            ? snapshot.Devices.Count == 0 ? "No MIDI devices available" :
              match is not null ? $"{match.Name} connected" : $"{snapshot.Devices.Count} MIDI device(s) available"
            : "Windows MIDI discovery is unavailable";
        MidiApiDetail = snapshot.ApiMessage ?? $"WinMM midiInGetNumDevs returned {snapshot.ApiDeviceCount ?? 0}; device capabilities loaded.";
        OnPropertyChanged(nameof(HasMidiHardware));
        OnPropertyChanged(nameof(HasNoMidiDevices));
        OnPropertyChanged(nameof(PreferredMidiDeviceLabel));
        OnPropertyChanged(nameof(InputSourceLabel));
        OnPropertyChanged(nameof(StartLessonReason));
        OnPropertyChanged(nameof(CanStartLesson));
        if (userInitiated)
        {
            StatusMessage = snapshot.Devices.Count == 0
                ? string.IsNullOrWhiteSpace(settings.PreferredMidiDeviceName)
                    ? "No MIDI devices available. Connect a keyboard, then select Refresh."
                    : $"{settings.PreferredMidiDeviceName} is saved but not connected. Cadenza will reconnect it when it returns."
                : match is not null
                    ? $"{match.Name} was found and reconnected."
                    : "Choose a MIDI keyboard to begin live monitoring.";
        }
    }

    private void MidiRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (IsLessonActive) return;

        if (_midiDeviceService.IsCapturing && SelectedMidiDevice is not null)
        {
            var snapshot = _midiDeviceService.DiscoverInputDevices();
            var activeStillPresent = snapshot.Devices.Any(device =>
                string.Equals(device.Id, SelectedMidiDevice.Id, StringComparison.Ordinal) ||
                string.Equals(device.Name, SelectedMidiDevice.Name, StringComparison.OrdinalIgnoreCase));
            if (activeStillPresent) return;
        }

        RefreshMidiDevices(userInitiated: false);
    }

    private void ConnectSelectedMidiDevice()
    {
        if (SelectedMidiDevice is null)
        {
            _midiDeviceService.StopInput();
            SetNativeInputActive(false);
            MidiLiveIndicator = "Not connected";
            MidiStatusLabel = MidiDevices.Count == 0 ? "No MIDI keyboard found" : "Choose a MIDI keyboard to connect";
            return;
        }

        if (_midiDeviceService.IsCapturing &&
            string.Equals(_midiDeviceService.ActiveDeviceId, SelectedMidiDevice.Id, StringComparison.Ordinal))
        {
            SetNativeInputActive(true);
            MidiStatusLabel = $"{SelectedMidiDevice.Name} connected";
            MidiLiveIndicator = _lastMidiEventAt is null
                ? $"{SelectedMidiDevice.Name} connected · waiting for first event"
                : $"{SelectedMidiDevice.Name} connected · receiving";
            return;
        }

        _lastMidiEventAt = null;
        _lastIndicatorSecond = -1;
        LastMidiKeyLabel = "No physical MIDI callback received on this selection";
        MidiDiagnosticTrace.Clear();
        AddMidiDiagnostic($"Selected {SelectedMidiDevice.Name} (WinMM id {SelectedMidiDevice.Id}).");
        var result = _midiDeviceService.StartInput(SelectedMidiDevice.Id);
        if (!result.Success)
        {
            SetNativeInputActive(false);
            MidiStatusLabel = $"{SelectedMidiDevice.Name} could not be opened";
            MidiApiDetail = result.Error ?? "WinMM returned no error description.";
            LiveMonitorStatus = $"Input unavailable: {MidiApiDetail}";
            MidiLiveIndicator = "Connection failed";
            StatusMessage = LiveMonitorStatus;
            return;
        }

        SetNativeInputActive(true);
        _lastMidiEventAt = null;
        _lastIndicatorSecond = -1;
        MidiLiveIndicator = $"{SelectedMidiDevice.Name} connected · waiting for first event";
        LastMidiKeyLabel = "Press any key on the connected keyboard";
        MidiStatusLabel = $"{SelectedMidiDevice.Name} connected — waiting for MIDI";
        MidiApiDetail = $"WinMM input {SelectedMidiDevice.Id} is open and callbacks are active.";
        var synthResult = _liveSynth.Open();
        LiveMonitorStatus = synthResult.Success
            ? "MIDI Monitor / Thru is on. Play a key to hear the piano and see the received event."
            : $"MIDI input is connected, but audio monitor failed: {synthResult.Message}";
        StatusMessage = LiveMonitorStatus;
    }

    public async Task TogglePreviewAsync()
    {
        if (IsPreviewBuilding) return;

        if (ResultsVisible)
        {
            await TriggerAutoRepeatAsync();
            return;
        }

        if (SelectedLessonMode == LessonMode.Listen)
        {
            if (IsPreviewPlaying)
            {
                PausePreview();
                return;
            }

            if (_score is null)
            {
                StatusMessage = "Import a MusicXML score before starting playback.";
                return;
            }

            var startBeat = IsPreviewPaused
                ? CursorBeat
                : (CursorBeat >= SelectedPreviewEndBeat - 0.01 ? SelectedPreviewStartBeat : CursorBeat);
            await StartScorePreviewAsync(startBeat);
        }
        else
        {
            if (IsLessonActive)
            {
                await RestartPreviewAsync();
                return;
            }

            if (_score is null)
            {
                StatusMessage = "Import a MusicXML score before starting practice or performance.";
                return;
            }

            var generation = Interlocked.Increment(ref _modeSwitchGeneration);
            _modeStartCancellation?.Cancel();
            _previewCancellation?.Cancel();

            await _modeTransitionGate.WaitAsync();
            try
            {
                if (generation != _modeSwitchGeneration) return;
                using var cancellation = new CancellationTokenSource();
                _modeStartCancellation = cancellation;
                await StartSelectedModeCoreAsync(cancellation.Token, generation);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                if (generation == _modeSwitchGeneration)
                    _modeStartCancellation = null;
                _modeTransitionGate.Release();
            }
        }
    }

    public async Task RestartPreviewAsync()
    {
        if (_score is null) return;
        StopTransport();
        await TogglePreviewAsync();
    }

    public async Task SeekPreviewMeasureAsync(int delta)
    {
        if (_score is null || delta == 0) return;
        var wasLessonActive = IsLessonActive;
        if (IsLessonActive) EndLesson(false);

        var occurrences = SelectedPerformanceOccurrences;
        if (occurrences.Count == 0) return;
        var currentIndex = occurrences
            .Select((occurrence, index) => (occurrence, index))
            .Where(item => item.occurrence.PerformanceStartBeat <= CursorBeat + 0.001)
            .Select(item => item.index)
            .DefaultIfEmpty(0)
            .Max();
        var targetIndex = Math.Clamp(currentIndex + Math.Sign(delta), 0, occurrences.Count - 1);
        var targetOccurrence = occurrences[targetIndex];
        var targetBeat = targetOccurrence.PerformanceStartBeat;
        var wasPlaying = IsScorePreviewPlaying || IsPreviewBuilding;
        CancelPreviewPlayback();
        CursorBeat = targetBeat;
        IsPreviewPaused = !wasPlaying;
        _previewUsesScore = true;
        RaisePreviewStateProperties();
        PreviewStatusLabel = $"Cued bar {targetOccurrence.MeasureNumber} · occurrence {targetOccurrence.RepeatPass}.";
        if (wasPlaying)
        {
            await StartScorePreviewAsync(targetBeat);
        }
        else if (wasLessonActive)
        {
            await TogglePreviewAsync();
        }
    }

    public void StopTransport()
    {
        if (IsLessonActive) EndLesson(false);
        if (IsPreviewPlaying || IsPreviewBuilding || IsPreviewPaused) StopPreview();
        CursorBeat = SelectedPreviewStartBeat;
        StatusMessage = $"{SelectedLessonModeLabel} stopped.";
    }

    public async Task SeekDisplayMeasureAsync(int delta)
    {
        if (_score is null || delta == 0) return;
        if (IsLessonActive) EndLesson(false);

        if (SelectedLessonMode == LessonMode.Listen)
        {
            await SeekPreviewMeasureAsync(delta);
            return;
        }

        var occurrences = SelectedPerformanceOccurrences;
        if (occurrences.Count == 0) return;
        var currentIndex = occurrences
            .Select((occurrence, index) => (occurrence, index))
            .Where(item => item.occurrence.PerformanceStartBeat <= CursorBeat + 0.001)
            .Select(item => item.index)
            .DefaultIfEmpty(0)
            .Max();
        var target = occurrences[Math.Clamp(currentIndex + Math.Sign(delta), 0, occurrences.Count - 1)];
        CursorBeat = target.PerformanceStartBeat;
        UpdateExpectedGuideForCursor();
        LessonStatusLabel = $"Cued bar {target.MeasureNumber} · occurrence {target.RepeatPass}.";
        StatusMessage = "Score position changed. Start the selected lesson mode when ready.";
    }

    public async Task SeekDisplayPageAsync(int pageDelta)
    {
        if (_score is null || pageDelta == 0) return;
        if (IsLessonActive) EndLesson(false);

        var occurrences = SelectedPerformanceOccurrences;
        if (occurrences.Count == 0) return;

        var currentOcc = _score.OccurrenceAtBeat(CursorBeat) ?? occurrences[0];
        var currentMeasure = int.TryParse(currentOcc.MeasureNumber, out var m) ? m : 1;
        var targetMeasure = Math.Clamp(currentMeasure + (pageDelta * 4), 1, _score.MeasureCount);
        var targetOcc = occurrences.FirstOrDefault(occ =>
            int.TryParse(occ.MeasureNumber, out var om) && om >= targetMeasure) ?? occurrences.LastOrDefault();

        if (targetOcc is not null)
        {
            var targetBeat = targetOcc.PerformanceStartBeat;
            if (IsScorePreviewPlaying || IsPreviewBuilding)
            {
                await StartScorePreviewAsync(targetBeat);
            }
            else
            {
                CursorBeat = targetBeat;
                IsPreviewPaused = true;
                _previewUsesScore = true;
                RaisePreviewStateProperties();
                PreviewStatusLabel = $"Cued bar {targetOcc.MeasureNumber} · occurrence {targetOcc.RepeatPass}.";
            }
        }
    }

    private async Task StartScorePreviewAsync(double startBeat)
    {
        if (_score is null) return;
        CancelPreviewPlayback();
        var endBeat = SelectedPreviewEndBeat;
        startBeat = Math.Clamp(startBeat, SelectedPreviewStartBeat, Math.Max(SelectedPreviewStartBeat, endBeat - 0.01));
        _previewCancellation = new CancellationTokenSource();
        IsPreviewBuilding = true;
        RaisePreviewStateProperties();
        PreviewStatusLabel = "Rendering the local preview...";
        try
        {
            var token = _previewCancellation.Token;
            var waveData = await _audioService.BuildPreviewAsync(
                _score,
                IsMetronomeAudible,
                startBeat,
                endBeat,
                EffectiveLessonTempoBpm,
                InstrumentalMuted ? 0 : EffectiveMixerVolume(InstrumentalVolume),
                EffectiveMixerVolume(MetronomeVolume),
                null,
                PlaybackSoundPreset.Id,
                token);
            _audioService.PlayPreview(waveData);

            _previewUsesScore = true;
            _previewStartBeat = startBeat;
            _previewEndBeat = endBeat;
            CursorBeat = startBeat;
            _previewClock.Restart();
            _lessonTimer.Start();
            IsPreviewBuilding = false;
            IsPreviewPaused = false;
            IsPreviewPlaying = true;
            RaisePreviewStateProperties();
            PreviewStatusLabel = $"Playing from bar {MeasureAtBeat(startBeat)}.";
            _ = FinishPreviewWhenDoneAsync(token, TimeSpan.FromSeconds((endBeat - startBeat + 1.5) * 60d / EffectiveLessonTempoBpm));
        }
        catch (OperationCanceledException)
        {
            IsPreviewBuilding = false;
            RaisePreviewStateProperties();
        }
        catch (Exception exception)
        {
            IsPreviewBuilding = false;
            PreviewStatusLabel = $"Preview unavailable: {exception.Message}";
            StatusMessage = PreviewStatusLabel;
            RaisePreviewStateProperties();
        }
    }

    private async Task PlayScoreMidiPreviewAsync(ScoreDocument score, double startBeat, double endBeat, CancellationToken token)
    {
        _accompanimentSynth.Open();
        _accompanimentSynth.SetProgram(PlaybackSoundPreset.PatchNumber);
        var vol = EffectiveMixerVolume(InstrumentalVolume);
        _accompanimentSynth.VolumePercent = InstrumentalMuted ? 0 : (vol > 0 ? vol : 85);

        var notes = score.Notes
            .Where(note => note.OnsetBeats >= startBeat && note.OnsetBeats < endBeat)
            .OrderBy(note => note.OnsetBeats)
            .ToList();

        var secondsPerBeat = 60d / EffectiveLessonTempoBpm;
        var sw = Stopwatch.StartNew();
        var activeNotes = new List<(ScoreNote note, double endSec)>();

        foreach (var note in notes)
        {
            if (token.IsCancellationRequested) break;
            var targetSec = (note.OnsetBeats - startBeat) * secondsPerBeat;

            while (!token.IsCancellationRequested)
            {
                var currentSec = sw.Elapsed.TotalSeconds;
                for (int i = activeNotes.Count - 1; i >= 0; i--)
                {
                    if (currentSec >= activeNotes[i].endSec)
                    {
                        _accompanimentSynth.NoteOff(activeNotes[i].note.MidiNoteNumber);
                        activeNotes.RemoveAt(i);
                    }
                }
                if (currentSec >= targetSec) break;
                var remainingMs = Math.Min(15, (targetSec - currentSec) * 1000d);
                if (remainingMs > 1) await Task.Delay((int)remainingMs, token);
                else await Task.Yield();
            }

            if (token.IsCancellationRequested) break;
            _accompanimentSynth.NoteOn(note.MidiNoteNumber, 92);
            activeNotes.Add((note, targetSec + note.DurationBeats * secondsPerBeat));
        }

        while (activeNotes.Count > 0 && !token.IsCancellationRequested)
        {
            await Task.Delay(20, token);
            var currentSec = sw.Elapsed.TotalSeconds;
            for (int i = activeNotes.Count - 1; i >= 0; i--)
            {
                if (currentSec >= activeNotes[i].endSec)
                {
                    _accompanimentSynth.NoteOff(activeNotes[i].note.MidiNoteNumber);
                    activeNotes.RemoveAt(i);
                }
            }
        }
    }

    public void LoadMidiReference(string path)
    {
        try
        {
            _midiReference = _midiFileImporter.Import(path);
            MidiListenTracks.Clear();
            foreach (var track in _midiReference.Tracks.Where(track => track.NoteCount > 0))
            {
                MidiListenTracks.Add(new MidiListenTrackOption(
                    track.Index,
                    $"{track.Name} · {track.NoteCount:N0} notes{(track.IsPercussion ? " · percussion" : string.Empty)}",
                    !track.IsPercussion));
                MidiListenTracks[^1].PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MidiListenTrackOption.IsSelected))
                        OnPropertyChanged(nameof(CanPlayMidiReference));
                };
            }
            var melodicTracks = _midiReference.Tracks.Count(track => !track.IsPercussion && track.NoteCount > 0);
            MidiReferenceLabel = $"{Path.GetFileName(path)} · {_midiReference.Notes.Count:N0} notes · {melodicTracks} melodic track(s) · {_midiReference.TempoBpm:0} BPM";
            var scoreComparison = _score is null
                ? "Load MusicXML separately for authoritative notation and assessment."
                : Math.Abs(_midiReference.TotalBeats - _score.TotalBeats) > Math.Max(4, _score.TotalBeats * 0.08)
                    ? "This MIDI duration does not closely match the loaded MusicXML; it is kept as a reference/listen source and is not used for scoring."
                    : "MIDI duration is close to the loaded score, but MusicXML remains authoritative for notation and scoring.";
            StatusMessage = $"MIDI imported. {scoreComparison}";
            OnPropertyChanged(nameof(HasMidiReference));
            OnPropertyChanged(nameof(CanPlayMidiReference));
        }
        catch (Exception exception)
        {
            StatusMessage = $"MIDI import failed: {exception.Message}";
        }
    }

    public async Task PlayMidiReferenceAsync()
    {
        if (_midiReference is null)
        {
            StatusMessage = "Import a MIDI file before playing the MIDI reference.";
            return;
        }

        StopPreview();
        _previewCancellation = new CancellationTokenSource();
        IsPreviewBuilding = true;
        OnPropertyChanged(nameof(CanPlayMidiReference));
        try
        {
            var selectedTracks = MidiListenTracks.Where(track => track.IsSelected).Select(track => track.TrackIndex).ToHashSet();
            if (selectedTracks.Count == 0)
            {
                IsPreviewBuilding = false;
                StatusMessage = "Select at least one imported MIDI track to hear.";
                OnPropertyChanged(nameof(CanPlayMidiReference));
                return;
            }
            var wave = await _audioService.BuildMidiPreviewAsync(
                _midiReference,
                selectedTracks,
                IsMetronomeAudible,
                InstrumentalMuted ? 0 : EffectiveMixerVolume(InstrumentalVolume),
                EffectiveMixerVolume(MetronomeVolume),
                PlaybackSoundPreset.Id,
                _previewCancellation.Token);
            _audioService.PlayPreview(wave);
            _previewUsesScore = false;
            _previewClock.Restart();
            IsPreviewBuilding = false;
            IsPreviewPlaying = true;
            PreviewStatusLabel = "Imported MIDI reference is playing through the local piano renderer.";
            OnPropertyChanged(nameof(PreviewButtonLabel));
            OnPropertyChanged(nameof(CanPlayMidiReference));
            _ = FinishPreviewWhenDoneAsync(_previewCancellation.Token,
                TimeSpan.FromSeconds((_midiReference.TotalBeats + 1.5) * 60d / _midiReference.TempoBpm));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsPreviewBuilding = false;
            StatusMessage = $"MIDI reference playback failed: {exception.Message}";
            PreviewStatusLabel = StatusMessage;
            OnPropertyChanged(nameof(CanPlayMidiReference));
        }
    }

    public void LoadPdfReference(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The PDF reference could not be found.", path);
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a PDF file.");
        _pdfReferencePath = path;
        PdfReferenceLabel = $"{Path.GetFileName(path)} · review reference";
        OnPropertyChanged(nameof(HasPdfReference));
        StatusMessage = "PDF attached for visual review. It is not treated as notation data; import reviewed MusicXML after OMR/manual correction.";
    }

    public void OpenPdfReference()
    {
        if (string.IsNullOrWhiteSpace(_pdfReferencePath) || !File.Exists(_pdfReferencePath))
        {
            StatusMessage = "Attach a PDF reference before opening it.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_pdfReferencePath) { UseShellExecute = true });
            StatusMessage = "Opened the PDF in the Windows default viewer for side-by-side score review.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Windows could not open the PDF viewer: {exception.Message}";
            throw;
        }
    }

    public void StopPreview(bool resetToStart = true)
    {
        CancelPreviewPlayback();
        _previewUsesScore = false;
        IsPreviewBuilding = false;
        IsPreviewPaused = false;
        IsPreviewPlaying = false;
        if (_score is not null && resetToStart) CursorBeat = SelectedPreviewStartBeat;
        if (!IsLessonActive) _lessonTimer.Stop();
        RaisePreviewStateProperties();
        if (_score is not null) PreviewStatusLabel = resetToStart ? "Preview stopped." : "Preview complete.";
    }

    private void PausePreview()
    {
        if (!IsScorePreviewPlaying) return;
        CursorBeat = Math.Min(_previewEndBeat, _previewStartBeat + _previewClock.Elapsed.TotalSeconds * EffectiveLessonTempoBpm / 60d);
        CancelPreviewPlayback();
        IsPreviewPlaying = false;
        IsPreviewPaused = true;
        _previewUsesScore = true;
        if (!IsLessonActive) _lessonTimer.Stop();
        RaisePreviewStateProperties();
        PreviewStatusLabel = $"Paused at bar {MeasureAtBeat(CursorBeat)}.";
    }

    private void CancelPreviewPlayback()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _audioService.StopPreview();
        _previewClock.Reset();
        IsPreviewPlaying = false;
        IsPreviewBuilding = false;
        RaisePreviewStateProperties();
    }

    public void SetPracticeMode(PracticeMode mode)
    {
        if (IsLessonActive) EndLesson(false);
        _preparedPerformanceWave = null;
        _performanceAudioOwnsMetronome = false;
        SelectedMode = mode;
        if (_score is not null)
        {
            _lessonGroups = _score.GetPracticeGroups(SelectedMode);
            _lessonGroupIndex = 0;
            _matchedNotes.Clear();
            CursorBeat = SelectedPreviewStartBeat;
            UpdateExpectedGuideForCursor();
        }
        StatusMessage = $"{SelectedModeLabel} selected. Press Play (or Space) to start.";
    }

    public void SetLessonMode(LessonMode mode)
    {
        if (IsLessonActive) return;
        if (IsPreviewPlaying || IsPreviewBuilding || IsPreviewPaused) StopPreview();
        ApplyLessonModeSelection(mode);
    }

    private void ApplyLessonModeSelection(LessonMode mode)
    {
        _preparedPerformanceWave = null;
        _performanceAudioOwnsMetronome = false;
        SelectedLessonMode = mode;
        StatusMessage = $"{SelectedLessonModeLabel} selected. {StartLessonReason}";
        LessonStatusLabel = StartLessonReason;
        ExpectedLabel = mode == LessonMode.Listen
            ? "Automatic score playback"
            : FormatExpectedGroup();
        RaiseLessonStateProperties();
        RaisePreviewStateProperties();
    }

    public async Task<bool> SwitchLessonModeAsync(LessonMode mode)
    {
        if (mode == SelectedLessonMode && !IsLessonActive && !IsPreviewPlaying)
        {
            return true;
        }

        var generation = Interlocked.Increment(ref _modeSwitchGeneration);
        _modeStartCancellation?.Cancel();
        _previewCancellation?.Cancel();

        await _modeTransitionGate.WaitAsync();
        try
        {
            if (generation != _modeSwitchGeneration) return false;

            StopActiveModeForSwitch();
            ApplyLessonModeSelection(mode);
            PrepareDefaultAssessableRange();
            CursorBeat = SelectedPreviewStartBeat;
            UpdateExpectedGuideForCursor();

            StatusMessage = $"{SelectedLessonModeLabel} selected. Press Play (or Space) to start.";
            LessonStatusLabel = StartLessonReason;
            return true;
        }
        finally
        {
            if (generation == _modeSwitchGeneration)
                _modeStartCancellation = null;
            _modeTransitionGate.Release();
        }
    }

    private void StopActiveModeForSwitch()
    {
        if (IsLessonActive) EndLesson(false);
        if (IsPreviewPlaying || IsPreviewBuilding || IsPreviewPaused || _previewCancellation is not null)
            StopPreview();

        _lessonTimer.Stop();
        _lessonClock.Reset();
        _practiceSessionClock.Reset();
        _preparedPerformanceWave = null;
        _performanceAudioOwnsMetronome = false;
        _audioService.StopPreview();
        _accompanimentSynth.AllNotesOff();
        _liveSynth.AllNotesOff();
        _matchedNotes.Clear();
        _activeHolds.Clear();
        HeldNoteNumbers = new HashSet<int>();
        ResultsVisible = false;
        _lessonGroupIndex = 0;
        _correctCount = 0;
        _missedCount = 0;
        _extraCount = 0;
        CorrectLabel = "0";
        MissedLabel = "0";
        ExtraLabel = "0";
        AccuracyLabel = "-";
        ProgressLabel = $"0 / {_lessonGroups.Count} expected groups";
    }

    private void StopActiveSessionForSelectionChange()
    {
        _modeStartCancellation?.Cancel();
        Interlocked.Increment(ref _modeSwitchGeneration);
        if (IsLessonActive) EndLesson(false);
        if (IsPreviewPlaying || IsPreviewBuilding || IsPreviewPaused || _previewCancellation is not null)
            StopPreview();
    }

    public bool PrepareDefaultAssessableRange()
    {
        if (_score is null || SelectedLessonMode == LessonMode.Listen) return true;
        if (SelectedLessonMode == LessonMode.WaitForYou &&
            !_score.CutsRepeatRegion(FocusStartMeasure, FocusEndMeasure))
        {
            return true;
        }
        if (!_score.HasBlockingAssessmentWarning(FocusStartMeasure, FocusEndMeasure) &&
            !_score.CutsRepeatRegion(FocusStartMeasure, FocusEndMeasure)) return true;

        // Preserve an intentional focused selection. Only repair the default
        // whole-score range that would otherwise make Practice/Performance
        // look inert on an imported score with a bounded warning.
        var isWholeScore = FocusStartMeasure == 1 && (FocusEndMeasure <= 0 || FocusEndMeasure >= _score.MeasureCount);
        if (isWholeScore) return true;

        (int Start, int End, int Groups)? best = null;
        for (var start = 1; start <= _score.MeasureCount; start++)
        {
            for (var end = start; end <= _score.MeasureCount; end++)
            {
                if (_score.HasBlockingAssessmentWarning(start, end) || _score.CutsRepeatRegion(start, end)) continue;
                var groups = _score.GetPracticeGroups(SelectedMode).Count(group =>
                    int.TryParse(group.MeasureNumber, out var measure) && measure >= start && measure <= end);
                if (groups == 0) continue;
                if (best is null || groups > best.Value.Groups || (groups == best.Value.Groups && start < best.Value.Start))
                {
                    best = (start, end, groups);
                }
            }
        }

        if (best is null) return false;
        FocusStartMeasure = best.Value.Start;
        FocusEndMeasure = best.Value.End;
        LessonStatusLabel =
            $"Ready on bars {best.Value.Start}–{best.Value.End}. Other bars remain available in Listen; warning details are in the badge.";
        return true;
    }

    public void SetReadingMode(ScoreReadingMode mode)
    {
        ReadingMode = mode;
        UpdateExpectedGuideForCursor();
        StatusMessage = mode == ScoreReadingMode.Page
            ? "Page reading: the playhead advances across each system, then follows the next system."
            : "Continuous reading: the grand staff advances horizontally and keeps the next phrase visible.";
    }

    public void OpenCurrentLesson()
    {
        if (_score is null)
        {
            StatusMessage = "Import a MusicXML song before opening the lesson player.";
            return;
        }
        IsPlayerVisible = true;
        StatusMessage = $"Ready to practice {ScoreTitle}.";
    }

    public void ShowDashboard()
    {
        // A hold gesture started inside the player must not complete after
        // navigation back to the dashboard.
        CancelRemoteHold();
        _prePracticeMatchedNotes.Clear();
        if (IsLessonActive) StopLesson();
        StopPreview();
        IsPlayerVisible = false;
        StatusMessage = "Dashboard ready.";
    }

    public void SetPedalEnabled(bool enabled) => PedalEnabled = enabled;

    public bool StartLesson()
    {
        if (SelectedLessonMode == LessonMode.Listen)
        {
            StatusMessage = "Use Start listening or the Listen transport to begin automatic playback.";
            return false;
        }
        if (!CanStartLesson)
        {
            StatusMessage = StartLessonReason;
            LessonStatusLabel = StartLessonReason;
            return false;
        }
        if (IsPreviewPlaying || IsPreviewBuilding) StopPreview();

        if (SelectedMidiDevice is not null &&
            (!_midiDeviceService.IsCapturing || _midiDeviceService.ActiveDeviceId != SelectedMidiDevice.Id))
        {
            var startResult = _midiDeviceService.StartInput(SelectedMidiDevice.Id);
            if (!startResult.Success)
            {
                MidiApiDetail = startResult.Error ?? "WinMM did not provide an error description.";
                StatusMessage = $"MIDI capture could not start: {MidiApiDetail}";
                return false;
            }

            SetNativeInputActive(true);
        }

        _lessonGroups = _score!.GetPracticeGroups(SelectedMode);
        _lessonGroups = _lessonGroups
            .Where(IsGroupInFocus)
            .ToArray();
        ResetLessonStats();
        _lessonGroupIndex = 0;
        _lessonStartBeat = SelectedPreviewStartBeat;
        _nextMetronomeBeat = _lessonStartBeat;
        CursorBeat = _lessonStartBeat;
        if (SelectedLessonMode == LessonMode.TimedPlay)
        {
            if (_preparedPerformanceWave is not null)
                _audioService.PlayPreview(_preparedPerformanceWave);
            _lessonClock.Restart();
            _preparedPerformanceWave = null;
        }
        else _lessonClock.Reset();
        _practiceSessionClock.Restart();
        IsLessonActive = true;
        _lessonRunGeneration++;
        _feedbackEventSequence = 0;
        LessonRunStateChanged?.Invoke(this, new LessonRunStateEvent("started", SelectedLessonMode, _lessonStartBeat, _lessonRunGeneration));
        _lessonTimer.Start();
        LessonStatusLabel = $"{SelectedLessonModeLabel} · {FocusRangeLabel} · {EffectiveLessonTempoBpm:0} BPM.";
        ExpectedLabel = FormatExpectedGroup();
        StatusMessage = (_nativeInputActive, UseKeyboardSimulation) switch
        {
            (true, true) => $"Lesson started. {SelectedMidiDevice!.Name} and computer piano keys are both active.",
            (true, false) => $"Lesson started from {SelectedMidiDevice!.Name}. MIDI note-on capture is active.",
            _ => "Lesson started with computer piano keys."
        };
        RaiseLessonStateProperties();
        return true;
    }

    public async Task<bool> StartSelectedModeAsync()
    {
        return await StartSelectedModeCoreAsync(CancellationToken.None, null);
    }

    public event EventHandler<string>? CountdownStepRequested;
    public event EventHandler? HideCountdownRequested;

    private async Task<bool> StartSelectedModeCoreAsync(CancellationToken cancellationToken, int? requiredGeneration)
    {
        if (_isStartingLesson) return false;
        if (!CanStartLesson)
        {
            StatusMessage = StartLessonReason;
            LessonStatusLabel = StartLessonReason;
            return false;
        }

        _isStartingLesson = true;
        RaiseLessonStateProperties();
        try
        {
            if (SelectedLessonMode == LessonMode.TimedPlay)
            {
                await PreparePerformanceAudioAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (requiredGeneration is { } generation && generation != _modeSwitchGeneration) return false;

                // Run 4-beat countdown with audible metronome clicks and visual overlay
                await RunPerformanceCountdownAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _isStartingLesson = false;
                RaiseLessonStateProperties();
                return StartLesson();
            }
            if (SelectedLessonMode != LessonMode.Listen)
            {
                _isStartingLesson = false;
                RaiseLessonStateProperties();
                return StartLesson();
            }

            _isStartingLesson = false;
            RaiseLessonStateProperties();
            cancellationToken.ThrowIfCancellationRequested();
            if (requiredGeneration is { } listenGeneration && listenGeneration != _modeSwitchGeneration) return false;
            await TogglePreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return IsPreviewPlaying || IsPreviewPaused;
        }
        finally
        {
            _isStartingLesson = false;
            RaiseLessonStateProperties();
        }
    }

    private async Task RunPerformanceCountdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            var beatMs = Math.Clamp((int)Math.Round(60_000d / EffectiveLessonTempoBpm), 150, 2000);
            var steps = new[] { "4", "3", "2", "1" };

            for (int i = 0; i < steps.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stepText = steps[i];

                // Metronome clicks: accent on 4, regular on 3, 2, 1
                var isAccent = i == 0;
                _audioService.PlayMetronomeClick(isAccent, 85);
                CountdownStepRequested?.Invoke(this, stepText);

                await Task.Delay(beatMs, cancellationToken);
            }
        }
        finally
        {
            HideCountdownRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PreparePerformanceAudioAsync(CancellationToken cancellationToken)
    {
        _preparedPerformanceWave = null;
        _performanceAudioOwnsMetronome = false;
        if (_score is null) return;

        int pianoVolume;
        int? includedStaff;
        if (PerformanceFullAccompanimentEnabled && !InstrumentalMuted)
        {
            pianoVolume = EffectiveMixerVolume(InstrumentalVolume);
            includedStaff = null;
        }
        else if (OtherHandAccompanimentEnabled && SelectedMode != PracticeMode.BothHands)
        {
            pianoVolume = EffectiveMixerVolume(OtherHandAccompanimentVolume);
            includedStaff = SelectedMode == PracticeMode.LeftHand ? 1 : 2;
        }
        else
        {
            pianoVolume = 0;
            includedStaff = null;
        }

        if (pianoVolume <= 0 && !IsMetronomeAudible) return;
        StatusMessage = "Preparing synchronized lesson audio...";
        var startBeat = SelectedPreviewStartBeat;
        _preparedPerformanceWave = await _audioService.BuildPreviewAsync(
            _score,
            IsMetronomeAudible,
            startBeat,
            SelectedPreviewEndBeat,
            EffectiveLessonTempoBpm,
            pianoVolume,
            EffectiveMixerVolume(MetronomeVolume),
            includedStaff,
            cancellationToken);
        _performanceAudioOwnsMetronome = IsMetronomeAudible;
    }

    public void ArmLesson() => StartLesson();

    public void StopLesson()
    {
        if (!IsLessonActive)
        {
            StatusMessage = "No lesson is currently running.";
            return;
        }

        EndLesson(false);
        StatusMessage = "Lesson stopped. The score and selected input remain loaded.";
    }

    public void SimulateNoteOn(int midiNoteNumber)
    {
        if (!UseKeyboardSimulation)
        {
            return;
        }

        HandleNoteOn(midiNoteNumber, 100, true);
    }

    public void SimulateNoteOff(int midiNoteNumber)
    {
        if (!UseKeyboardSimulation) return;
        if (midiNoteNumber == _remoteHoldMidiNote) CancelRemoteHold();
        CompleteHold(midiNoteNumber);
        if (MidiMonitorEnabled) _liveSynth.NoteOff(midiNoteNumber);
        InputActivityLabel = $"Simulation note-off: {MidiNoteFormatter.Format(midiNoteNumber)}";
    }

    public void SetStatusMessage(string message) => StatusMessage = message;

    public void UpdateVisualClock()
    {
        if (_score is not null && IsPreviewPlaying && _previewUsesScore && !IsLessonActive)
        {
            CursorBeat = Math.Min(_previewEndBeat, _previewStartBeat + _previewClock.Elapsed.TotalSeconds * EffectiveLessonTempoBpm / 60d);
        }
        else if (_score is not null && IsLessonActive && SelectedLessonMode == LessonMode.TimedPlay)
        {
            CursorBeat = Math.Min(SelectedPreviewEndBeat, _lessonStartBeat + _lessonClock.Elapsed.TotalSeconds * EffectiveLessonTempoBpm / 60d);
        }

        if (_lastMidiEventAt is not { } last) return;
        var seconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - last).TotalSeconds);
        if (seconds == _lastIndicatorSecond) return;
        _lastIndicatorSecond = seconds;
        MidiLiveIndicator = $"{SelectedMidiDevice?.Name ?? "MIDI input"} connected · last event {seconds}s ago";
    }

    public async Task TestMonitorToneAsync()
    {
        var on = _liveSynth.NoteOn(60, 92);
        LiveMonitorStatus = on.Success ? "Output test: acoustic piano C4 sounded." : $"Output test failed: {on.Message}";
        if (!on.Success)
        {
            StatusMessage = LiveMonitorStatus;
            return;
        }
        await Task.Delay(420);
        var off = _liveSynth.NoteOff(60);
        if (!off.Success)
        {
            LiveMonitorStatus = $"Output release failed: {off.Message}";
            StatusMessage = LiveMonitorStatus;
        }
    }

    public void Dispose()
    {
        StopPreview();
        if (IsLessonActive) EndLesson(false);
        _lessonTimer.Stop();
        _midiRefreshTimer.Stop();
        _midiRefreshTimer.Tick -= MidiRefreshTimer_Tick;
        _midiDeviceService.NoteOn -= MidiDeviceService_NoteOn;
        _midiDeviceService.NoteOff -= MidiDeviceService_NoteOff;
        _midiDeviceService.ControlChange -= MidiDeviceService_ControlChange;
        _midiDeviceService.RawMessage -= MidiDeviceService_RawMessage;
        _midiDeviceService.InputError -= MidiDeviceService_InputError;
        _midiDeviceService.InputDisconnected -= MidiDeviceService_InputDisconnected;
        _midiDeviceService.Diagnostic -= MidiDeviceService_Diagnostic;
        _midiDeviceService.Dispose();
        _liveSynth.Dispose();
        _accompanimentSynth.Dispose();
        _audioService.Dispose();
    }

    private async Task FinishPreviewWhenDoneAsync(CancellationToken token, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration + TimeSpan.FromMilliseconds(250), token);
            if (Application.Current is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsLoopEnabled && _score is not null && !IsLessonActive)
                        {
                            CursorBeat = SelectedPreviewStartBeat;
                            await StartScorePreviewAsync(SelectedPreviewStartBeat);
                        }
                        else
                        {
                            CursorBeat = SelectedPreviewEndBeat;
                            StopPreview(false);
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshLessonGroups()
    {
        _lessonGroups = (_score?.GetPracticeGroups(SelectedMode) ?? [])
            .Where(IsGroupInFocus)
            .ToArray();
        _lessonGroupIndex = 0;
        OnPropertyChanged(nameof(CanStartLesson));
        OnPropertyChanged(nameof(StartLessonReason));
        ProgressLabel = $"0 / {_lessonGroups.Count} expected groups";
        UpdateExpectedGuideForCursor();
    }

    private bool IsGroupInFocus(ScoreNoteGroup group) =>
        int.TryParse(group.MeasureNumber, out var measure) &&
        measure >= FocusStartMeasure &&
        measure <= FocusEndMeasure;

    private IReadOnlyList<ScoreMeasureOccurrence> SelectedPerformanceOccurrences => _score?.PerformanceMeasures
        .Where(occurrence =>
            (FocusStartMeasure <= 1 && occurrence.PerformanceStartBeat <= 0.001) ||
            (int.TryParse(occurrence.MeasureNumber, out var measure) &&
             measure >= FocusStartMeasure &&
             (FocusEndMeasure <= 0 || measure <= FocusEndMeasure)))
        .OrderBy(occurrence => occurrence.PerformanceStartBeat)
        .ToArray() ?? [];

    public double SelectedPreviewStartBeat => SelectedPerformanceOccurrences
        .Select(occurrence => occurrence.PerformanceStartBeat)
        .DefaultIfEmpty(0)
        .Min();

    private double SelectedPreviewEndBeat => SelectedPerformanceOccurrences
        .Select(occurrence => occurrence.PerformanceStartBeat + occurrence.DurationBeats)
        .DefaultIfEmpty(_score?.TotalBeats ?? 1)
        .Max();

    private int MeasureAtBeat(double beat) =>
        int.TryParse(_score?.OccurrenceAtBeat(beat)?.MeasureNumber, out var measure)
            ? measure
            : FocusStartMeasure;

    private static int MeasureNumberOf(ScoreNote note) =>
        int.TryParse(note.MeasureNumber, out var measure) ? measure : 0;

    private void ResetPreviewPositionToRangeStart()
    {
        if (_score is null || IsLessonActive) return;
        if (IsPreviewPlaying || IsPreviewBuilding) CancelPreviewPlayback();
        IsPreviewPaused = false;
        _previewUsesScore = false;
        CursorBeat = SelectedPreviewStartBeat;
        RaisePreviewStateProperties();
    }

    private void ResetLessonStats()
    {
        _correctCount = 0;
        _missedCount = 0;
        _extraCount = 0;
        _timingQualityTotal = 0;
        _holdQualityTotal = 0;
        _holdQualityCount = 0;
        _articulationQualityTotal = 0;
        _articulationQualityCount = 0;
        _voicingQualityTotal = 0;
        _voicingQualityCount = 0;
        _chordSyncTotal = 0;
        _chordSyncCount = 0;
        _chordHitsInGroup = 0;
        _pedalCorrect = 0;
        _pedalAttempts = 0;
        _matchedNotes.Clear();
        _activeHolds.Clear();
        HeldNoteNumbers = new HashSet<int>();
        CurrentStreak = 0;
        BestStreak = 0;
        ResultsVisible = false;
        CursorBeat = SelectedPreviewStartBeat;
        CorrectLabel = "0";
        MissedLabel = "0";
        ExtraLabel = "0";
        AccuracyLabel = "-";
        TimingLabel = SelectedLessonMode == LessonMode.WaitForYou ? "Timing: n/a in Wait-for-you" : "Timing: 0%";
        OnPropertyChanged(nameof(PitchCategoryLabel));
        OnPropertyChanged(nameof(TimingCategoryLabel));
        OnPropertyChanged(nameof(HoldCategoryLabel));
        OnPropertyChanged(nameof(PedalCategoryLabel));
        OnPropertyChanged(nameof(CanStartLesson));
        OnPropertyChanged(nameof(StartLessonReason));
    }

    private void MidiDeviceService_NoteOn(object? sender, MidiNoteOnEvent note)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() => HandleNoteOn(note.NoteNumber, note.Velocity, false));
    }

    private void MidiDeviceService_RawMessage(object? sender, MidiRawEvent message)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _lastMidiEventAt = message.Timestamp;
            _lastIndicatorSecond = -1;
            var command = message.Status & 0xF0;
            var channel = (message.Status & 0x0F) + 1;
            var description = command switch
            {
                0x90 when message.Data2 > 0 => $"Key {MidiNoteFormatter.Format(message.Data1)} · velocity {message.Data2}",
                0x80 or 0x90 => $"Released {MidiNoteFormatter.Format(message.Data1)}",
                0xB0 => $"Controller CC{message.Data1} · value {message.Data2}",
                _ => $"MIDI 0x{message.Status:X2} · {message.Data1} · {message.Data2}"
            };
            LastMidiKeyLabel = $"{description} · channel {channel}";
            MidiLiveIndicator = $"{SelectedMidiDevice?.Name ?? "MIDI input"} connected · receiving now";
        });
    }

    private void MidiDeviceService_InputError(object? sender, string error)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            HandleMidiDeviceLoss(error);
        });
    }

    private void MidiDeviceService_InputDisconnected(object? sender, EventArgs e)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
            HandleMidiDeviceLoss("The selected MIDI device disconnected."));
    }

    private void HandleMidiDeviceLoss(string reason)
    {
        _midiDeviceService.StopInput();
        SetNativeInputActive(false);
        _selectedMidiDevice = null;
        _liveSynth.AllNotesOff();
        OnPropertyChanged(nameof(SelectedMidiDevice));
        OnPropertyChanged(nameof(InputSourceLabel));
        OnPropertyChanged(nameof(CanStartLesson));
        MidiLiveIndicator = "MIDI disconnected";
        MidiStatusLabel = "Preferred MIDI device is offline";
        LastMidiKeyLabel = "No live MIDI input";
        StatusMessage = $"{reason} Reconnect it, then select Refresh; Cadenza will restore the saved preference.";
        if (IsLessonActive) EndLesson(false);
    }

    private void MidiDeviceService_Diagnostic(object? sender, string message)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() => AddMidiDiagnostic(message));
    }

    private void AddMidiDiagnostic(string message)
    {
        MidiDiagnosticTrace.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (MidiDiagnosticTrace.Count > 24)
        {
            MidiDiagnosticTrace.RemoveAt(MidiDiagnosticTrace.Count - 1);
        }
    }

    private bool _isRemoteHoldVisible;
    private string _remoteHoldActionText = string.Empty;
    private string _remoteHoldTimeText = string.Empty;
    private double _remoteHoldProgress;
    private DispatcherTimer? _remoteHoldTimer;
    private DateTime _remoteHoldStartTime;
    private double _remoteHoldTargetSeconds = 3.0;
    private int _remoteHoldMidiNote = -1;
    private string _remoteHoldAction = string.Empty;
    private int _activeMeasureCountInIndex;

    public List<int> GetNotesAtBeat(double beat)
    {
        if (_score is null) return new List<int>();
        var groups = _score.GetPracticeGroups(SelectedMode);
        if (groups.Count > 0)
        {
            var matchingGroup = groups.FirstOrDefault(g => Math.Abs(g.OnsetBeats - beat) <= 0.25);
            if (matchingGroup is not null)
            {
                return matchingGroup.Notes.Select(n => n.MidiNoteNumber).Distinct().ToList();
            }

            var nearestGroup = groups.OrderBy(g => Math.Abs(g.OnsetBeats - beat)).FirstOrDefault();
            if (nearestGroup is not null && Math.Abs(nearestGroup.OnsetBeats - beat) <= 0.5)
            {
                return nearestGroup.Notes.Select(n => n.MidiNoteNumber).Distinct().ToList();
            }
        }

        var notesAtBeat = _score.Notes
            .Where(n => Math.Abs(n.OnsetBeats - beat) <= 0.25)
            .Select(n => n.MidiNoteNumber)
            .Distinct()
            .ToList();

        if (notesAtBeat.Count > 0) return notesAtBeat;

        var nearestNote = _score.Notes
            .OrderBy(n => Math.Abs(n.OnsetBeats - beat))
            .FirstOrDefault();
        if (nearestNote is null) return new List<int>();
        return _score.Notes
            .Where(n => Math.Abs(n.OnsetBeats - nearestNote.OnsetBeats) <= 0.05)
            .Select(n => n.MidiNoteNumber)
            .Distinct()
            .ToList();
    }

    public double? GetNextNoteBeat(double currentBeat)
    {
        if (_score is null) return null;
        var groups = _score.GetPracticeGroups(SelectedMode);
        if (groups.Count > 0)
        {
            var nextGroup = groups
                .Where(g => g.OnsetBeats > currentBeat + 0.05)
                .OrderBy(g => g.OnsetBeats)
                .FirstOrDefault();
            if (nextGroup is not null) return nextGroup.OnsetBeats;
        }

        var nextNote = _score.Notes
            .Where(n => n.OnsetBeats > currentBeat + 0.05)
            .OrderBy(n => n.OnsetBeats)
            .FirstOrDefault();
        return nextNote?.OnsetBeats;
    }

    public List<int> GetActiveMeasureNoteSequence()
    {
        if (_score is null) return new List<int>();
        var occ = _score.OccurrenceAtBeat(CursorBeat) ?? _score.PerformanceMeasures.FirstOrDefault();
        if (occ is null) return new List<int>();
        var startBeat = occ.SourceStartBeat;
        var endBeat = occ.SourceStartBeat + occ.DurationBeats;
        return _score.Notes
            .Where(n => n.OnsetBeats >= startBeat - 0.001 && n.OnsetBeats < endBeat + 0.001)
            .OrderBy(n => n.OnsetBeats)
            .Select(n => n.MidiNoteNumber)
            .ToList();
    }

    public List<(int midiNote, double beat)> GetPerformanceCountInNotes()
    {
        if (_score is null) return new List<(int, double)>();
        var occ = _score.OccurrenceAtBeat(CursorBeat) ?? _score.PerformanceMeasures.FirstOrDefault();
        if (occ is null) return new List<(int, double)>();
        var startBeat = occ.SourceStartBeat;
        var sequence = _score.Notes
            .Where(n => n.OnsetBeats >= startBeat - 0.001)
            .OrderBy(n => n.OnsetBeats)
            .Select(n => (n.MidiNoteNumber, n.OnsetBeats))
            .Take(6)
            .ToList();
        return sequence.Count >= 2 ? sequence : _score.Notes.Take(4).Select(n => (n.MidiNoteNumber, n.OnsetBeats)).ToList();
    }

    public List<int> GetPerformanceCountInSequence()
    {
        return GetPerformanceCountInNotes().Select(item => item.midiNote).ToList();
    }

    public bool IsRemoteHoldVisible
    {
        get => _isRemoteHoldVisible;
        set => SetField(ref _isRemoteHoldVisible, value);
    }

    public string RemoteHoldActionText
    {
        get => _remoteHoldActionText;
        set => SetField(ref _remoteHoldActionText, value);
    }

    public string RemoteHoldTimeText
    {
        get => _remoteHoldTimeText;
        set => SetField(ref _remoteHoldTimeText, value);
    }

    public double RemoteHoldProgress
    {
        get => _remoteHoldProgress;
        set => SetField(ref _remoteHoldProgress, value);
    }

    public int ActiveMeasureFirstMidiNote
    {
        get
        {
            if (_score is null) return 60;
            var occ = _score.OccurrenceAtBeat(CursorBeat) ?? _score.PerformanceMeasures.FirstOrDefault();
            if (occ is null) return 60;
            var note = _score.Notes.FirstOrDefault(n => n.OnsetBeats >= occ.SourceStartBeat - 0.001);
            return note?.MidiNoteNumber ?? 60;
        }
    }

    public void ProcessActionShortcutTrigger(string actionName, int midiNoteNumber, int behaviorIndex, int staticMidiNote, int scoreDefaultNote = -1)
    {
        var targetNote = staticMidiNote > 0 ? staticMidiNote : scoreDefaultNote;
        if (targetNote <= 0 || midiNoteNumber != targetNote) return;

        var displayName = GetActionDisplayName(actionName);
        var noteName = MidiNoteFormatter.Format(midiNoteNumber);
        var holdSeconds = GetHoldSecondsForAction(actionName);

        if (behaviorIndex == 0) // Hold Note
        {
            StartRemoteHold(midiNoteNumber, actionName, $"Keep holding {noteName} ({holdSeconds:0.0}s) to {displayName}...", holdSeconds);
        }
        else if (behaviorIndex == 1) // Single Tap
        {
            ExecuteRemoteHoldAction(actionName);
        }
        else if (behaviorIndex == 2) // Double Tap
        {
            if (CheckIsTapCount(midiNoteNumber, 2))
            {
                ExecuteRemoteHoldAction(actionName);
            }
        }
        else if (behaviorIndex == 3) // Triple Tap
        {
            if (CheckIsTapCount(midiNoteNumber, 3))
            {
                ExecuteRemoteHoldAction(actionName);
            }
        }
        else if (behaviorIndex == 4) // Multi Tap (Custom)
        {
            var targetCount = GetMultiTapCountForAction(actionName);
            if (CheckIsTapCount(midiNoteNumber, targetCount))
            {
                ExecuteRemoteHoldAction(actionName);
            }
        }
    }

    public event EventHandler<(string actionText, string timeText, double progress)>? OnHoldProgressUpdated;
    public event EventHandler? OnHoldProgressCancelled;

    public void StartRemoteHold(int midiNote, string action, string labelText, double durationSeconds)
    {
        CancelRemoteHold();
        _remoteHoldMidiNote = midiNote;
        _remoteHoldAction = action;
        _remoteHoldTargetSeconds = durationSeconds > 0 ? durationSeconds : 3.0;
        _remoteHoldStartTime = DateTime.UtcNow;
        RemoteHoldActionText = labelText;
        RemoteHoldTimeText = $"{_remoteHoldTargetSeconds:0.0}s";
        RemoteHoldProgress = 0.0;
        IsRemoteHoldVisible = true;

        OnHoldProgressUpdated?.Invoke(this, (RemoteHoldActionText, RemoteHoldTimeText, 0.0));

        _remoteHoldTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _remoteHoldTimer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.UtcNow - _remoteHoldStartTime).TotalSeconds;
            var remaining = Math.Max(0, _remoteHoldTargetSeconds - elapsed);
            RemoteHoldProgress = Math.Clamp(elapsed / _remoteHoldTargetSeconds, 0, 1);
            RemoteHoldTimeText = $"{remaining:0.0}s";

            OnHoldProgressUpdated?.Invoke(this, (RemoteHoldActionText, RemoteHoldTimeText, RemoteHoldProgress));

            if (elapsed >= _remoteHoldTargetSeconds)
            {
                var actionToRun = _remoteHoldAction;
                CancelRemoteHold();
                ExecuteRemoteHoldAction(actionToRun);
            }
        };
        _remoteHoldTimer.Start();
    }

    public void CancelRemoteHold()
    {
        _remoteHoldTimer?.Stop();
        _remoteHoldTimer = null;
        _remoteHoldMidiNote = -1;
        _remoteHoldAction = string.Empty;
        IsRemoteHoldVisible = false;
        RemoteHoldProgress = 0.0;

        OnHoldProgressCancelled?.Invoke(this, EventArgs.Empty);
    }

    private async void ExecuteRemoteHoldAction(string action)
    {
        if (action == "Listen")
        {
            SelectedLessonMode = LessonMode.Listen;
            await SwitchLessonModeAsync(LessonMode.Listen);
            await StartSelectedModeAsync();
        }
        else if (action == "Practice")
        {
            SelectedLessonMode = LessonMode.WaitForYou;
            await SwitchLessonModeAsync(LessonMode.WaitForYou);
            await StartSelectedModeAsync();
        }
        else if (action == "Performance")
        {
            SelectedLessonMode = LessonMode.TimedPlay;
            await SwitchLessonModeAsync(LessonMode.TimedPlay);
            await StartSelectedModeAsync();
        }
        else if (action == "TogglePlay")
        {
            if (IsLessonActive) StopLesson();
            else await StartSelectedModeAsync();
        }
        else if (action == "Restart")
        {
            _isStartingLesson = false;
            _prePracticeMatchedNotes.Clear();
            if (IsLessonActive) EndLesson(false);
            if (IsPreviewPlaying || IsPreviewBuilding) StopPreview();
            CursorBeat = SelectedPreviewStartBeat;
            await StartSelectedModeAsync();
        }
        else if (action == "PrevMeasure") await SeekDisplayMeasureAsync(-1);
        else if (action == "NextMeasure") await SeekDisplayMeasureAsync(1);
        else if (action == "PrevPage") await SeekDisplayPageAsync(-1);
        else if (action == "NextPage") await SeekDisplayPageAsync(1);
    }

    private void MidiDeviceService_NoteOff(object? sender, MidiNoteOffEvent note)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (note.NoteNumber == _remoteHoldMidiNote)
            {
                CancelRemoteHold();
            }
            CompleteHold(note.NoteNumber);
            InputActivityLabel = $"MIDI note-off from {SelectedMidiDevice?.Name ?? "selected input"}: {MidiNoteFormatter.Format(note.NoteNumber)}";
            if (!MidiMonitorEnabled) return;
            var result = _liveSynth.NoteOff(note.NoteNumber, note.Velocity, note.Channel);
            if (!result.Success) LiveMonitorStatus = $"Audio monitor error: {result.Message}";
        });
    }

    private void MidiDeviceService_ControlChange(object? sender, MidiControlChangeEvent message)
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (message.Controller == 64)
            {
                _pedalDown = message.Value >= 64;
                ScorePedalEvent(_pedalDown);
                OnPropertyChanged(nameof(PedalStatusLabel));
                OnPropertyChanged(nameof(PedalCategoryLabel));
                InputActivityLabel = $"MIDI CC64 from {SelectedMidiDevice?.Name ?? "selected input"}: sustain {(_pedalDown ? "down" : "up")} ({message.Value})";
            }
            else
            {
                InputActivityLabel = $"MIDI CC{message.Controller} from {SelectedMidiDevice?.Name ?? "selected input"}: {message.Value}";
            }

            if (MidiMonitorEnabled)
            {
                var result = _liveSynth.ControlChange(message.Controller, message.Value, message.Channel);
                if (!result.Success) LiveMonitorStatus = $"Audio monitor error: {result.Message}";
            }
        });
    }

    private int _lastMidiTapNote = -1;
    private int _lastMidiTapCount = 0;
    private DateTime _lastMidiTapTime = DateTime.MinValue;
    private readonly HashSet<int> _prePracticeMatchedNotes = new();

    private bool CheckIsTapCount(int midiNote, int requiredCount)
    {
        var now = DateTime.UtcNow;
        if (_lastMidiTapNote == midiNote && (now - _lastMidiTapTime).TotalMilliseconds <= 600)
        {
            _lastMidiTapCount++;
        }
        else
        {
            _lastMidiTapNote = midiNote;
            _lastMidiTapCount = 1;
        }

        _lastMidiTapTime = now;

        if (_lastMidiTapCount >= requiredCount)
        {
            _lastMidiTapNote = -1;
            _lastMidiTapCount = 0;
            _lastMidiTapTime = DateTime.MinValue;
            return true;
        }

        return false;
    }

    private void HandleNoteOn(int midiNoteNumber, int velocity, bool simulation)
    {
        if (IsMidiLearningActive)
        {
            ApplyLearnedMidiNote(midiNoteNumber);
            return;
        }

        // Dashboard MIDI input may still update connection diagnostics through
        // RawMessage, but it must never dispatch score commands or sound lessons.
        if (!IsPlayerVisible)
        {
            CancelRemoteHold();
            _prePracticeMatchedNotes.Clear();
            InputActivityLabel =
                $"MIDI note ignored while dashboard is active: {MidiNoteFormatter.Format(midiNoteNumber)}";
            return;
        }

        if (ResultsVisible)
        {
            if (BehaviorDismissIndex == 1 || (BehaviorDismissIndex == 2 && CheckIsTapCount(midiNoteNumber, 2)) || (BehaviorDismissIndex == 3 && CheckIsTapCount(midiNoteNumber, 3)))
            {
                _ = TriggerAutoRepeatAsync();
                return;
            }
        }

        // Temporary Live Key Feedback during active Listen Mode playback
        if (IsLessonActive && SelectedLessonMode == LessonMode.Listen)
        {
            if (AlwaysShowLiveNoteFeedback)
            {
                var currentBeat = CursorBeat;
                var notesAtCurrentBeat = _score is not null ? GetNotesAtBeat(currentBeat) : new List<int>();
                var isCorrect = notesAtCurrentBeat.Contains(midiNoteNumber);

                OnLiveNoteFeedbackTriggered?.Invoke(this, (isCorrect ? "correct" : "wrong", currentBeat, midiNoteNumber));
            }
        }

        // Global Action Shortcuts (Contextual dispatching)
        if (!IsActiveSessionRunning)
        {
            // Before starting a lesson/playback, TogglePlay (Begin Practice / Listen / Performance) takes priority
            ProcessActionShortcutTrigger("TogglePlay", midiNoteNumber, BehaviorTogglePlayIndex, _profile.Settings.MidiShortcutTogglePlayNote);
            if (_profile.Settings.MidiShortcutRestartNote != _profile.Settings.MidiShortcutTogglePlayNote)
            {
                ProcessActionShortcutTrigger("Restart", midiNoteNumber, BehaviorRestartIndex, _profile.Settings.MidiShortcutRestartNote);
            }
        }
        else
        {
            // While a lesson or playback is running, Restart (Restart Session) takes priority
            ProcessActionShortcutTrigger("Restart", midiNoteNumber, BehaviorRestartIndex, _profile.Settings.MidiShortcutRestartNote);
            if (_profile.Settings.MidiShortcutTogglePlayNote != _profile.Settings.MidiShortcutRestartNote)
            {
                ProcessActionShortcutTrigger("TogglePlay", midiNoteNumber, BehaviorTogglePlayIndex, _profile.Settings.MidiShortcutTogglePlayNote);
            }
        }
        ProcessActionShortcutTrigger("Listen", midiNoteNumber, BehaviorListenIndex, _profile.Settings.MidiShortcutListenNote);
        ProcessActionShortcutTrigger("PrevMeasure", midiNoteNumber, BehaviorPrevMeasureIndex, _profile.Settings.MidiShortcutPreviousMeasureNote);
        ProcessActionShortcutTrigger("NextMeasure", midiNoteNumber, BehaviorNextMeasureIndex, _profile.Settings.MidiShortcutNextMeasureNote);
        ProcessActionShortcutTrigger("PrevPage", midiNoteNumber, BehaviorPrevPageIndex, _profile.Settings.MidiShortcutPreviousPageNote);
        ProcessActionShortcutTrigger("NextPage", midiNoteNumber, BehaviorNextPageIndex, _profile.Settings.MidiShortcutNextPageNote);
        ProcessActionShortcutTrigger("Dismiss", midiNoteNumber, BehaviorDismissIndex, _profile.Settings.MidiShortcutDismissResultsNote);
        ProcessActionShortcutTrigger("Repeat", midiNoteNumber, BehaviorRepeatIndex, _profile.Settings.MidiShortcutRepeatResultsNote);

        if (!IsLessonActive)
        {
            var currentBeat = CursorBeat;
            var sectionStartBeat = SelectedPreviewStartBeat;
            var occ = _score?.OccurrenceAtBeat(currentBeat) ?? _score?.PerformanceMeasures.FirstOrDefault();
            var measureStartBeat = occ?.SourceStartBeat ?? sectionStartBeat;
            
            double targetEndBeat = measureStartBeat + 4.0;
            if (occ != null)
            {
                if (BehaviorPerformanceIndex == 6) // 2-Bar Sequence
                {
                    targetEndBeat = occ.SourceStartBeat + (occ.DurationBeats * 2);
                }
                else if (BehaviorPerformanceIndex == 7) // Visible Page / System Sequence
                {
                    targetEndBeat = SelectedPreviewEndBeat;
                }
                else // 1-Bar Sequence (5) or First & Last Note Sequence (8)
                {
                    targetEndBeat = occ.SourceStartBeat + occ.DurationBeats;
                }
            }

            var notesAtCurrentBeat = _score is not null ? GetNotesAtBeat(currentBeat) : new List<int>();
            var isCorrectNote = notesAtCurrentBeat.Count == 0 || notesAtCurrentBeat.Contains(midiNoteNumber);

            if (AlwaysShowLiveNoteFeedback)
            {
                OnLiveNoteFeedbackTriggered?.Invoke(this, (isCorrectNote ? "correct" : "wrong", currentBeat, midiNoteNumber));
            }

            if (isCorrectNote)
            {
                if (notesAtCurrentBeat.Contains(midiNoteNumber))
                {
                    _prePracticeMatchedNotes.Add(midiNoteNumber);
                }

                // Complete beat/chord when all expected notes at currentBeat are matched
                if (notesAtCurrentBeat.Count == 0 || _prePracticeMatchedNotes.Count >= notesAtCurrentBeat.Count)
                {
                    _prePracticeMatchedNotes.Clear();
                    _activeMeasureCountInIndex++;

                    var nextBeat = GetNextNoteBeat(currentBeat);
                    var isSequenceCompleted = !nextBeat.HasValue || nextBeat.Value >= targetEndBeat - 0.05;

                    // If a Sequence Count-In mode (Behavior >= 5) is active AND sequence target is reached:
                    if (BehaviorPerformanceIndex >= 5 && isSequenceCompleted)
                    {
                        _activeMeasureCountInIndex = 0;
                        CursorBeat = sectionStartBeat;
                        ExecuteRemoteHoldAction("Performance");
                        return;
                    }

                    if (nextBeat.HasValue && !isSequenceCompleted)
                    {
                        CursorBeat = nextBeat.Value;
                    }
                    else
                    {
                        // End of measure / section reached: reset playhead back to start
                        _activeMeasureCountInIndex = 0;
                        CursorBeat = sectionStartBeat;
                    }
                }
            }
            else
            {
                // Wrong note/chord tone: reset matched notes & count-in index, and reset playhead immediately back to beginning
                _prePracticeMatchedNotes.Clear();
                _activeMeasureCountInIndex = 0;
                CursorBeat = sectionStartBeat;
            }

            var activeFirstNote = ActiveMeasureFirstMidiNote;
            ProcessActionShortcutTrigger("Practice", midiNoteNumber, BehaviorPracticeIndex, 0, activeFirstNote);
            if (BehaviorPerformanceIndex < 5)
            {
                ProcessActionShortcutTrigger("Performance", midiNoteNumber, BehaviorPerformanceIndex, _profile.Settings.MidiShortcutPerformanceNote, activeFirstNote);
            }
        }

        var source = simulation ? "Simulation" : SelectedMidiDevice?.Name ?? "MIDI";
        InputActivityLabel = $"{source} note-on: {MidiNoteFormatter.Format(midiNoteNumber)} · velocity {velocity} · {DateTime.Now:HH:mm:ss.fff}";
        if (MidiMonitorEnabled)
        {
            if (MatchPlaybackSynthEnabled)
            {
                var presetId = LiveSoundPreset?.Id ?? PlaybackSoundPreset.Id;
                _audioService.PlayLiveNote(midiNoteNumber, velocity, EffectiveMixerVolume(MonitorVolume), presetId);
                LiveMonitorStatus = $"Monitor sounded {MidiNoteFormatter.Format(midiNoteNumber)} from {source} (software synth).";
            }
            else
            {
                var monitorResult = _liveSynth.NoteOn(midiNoteNumber, velocity);
                LiveMonitorStatus = monitorResult.Success
                    ? $"Monitor sounded {MidiNoteFormatter.Format(midiNoteNumber)} from {source}."
                    : $"Audio monitor error: {monitorResult.Message}";
            }
        }
        if (IsCalibrationActive && _calibrationClickIndex >= 0 && _calibrationCapturedForClick != _calibrationClickIndex)
        {
            var milliseconds = Stopwatch.GetElapsedTime(_lastCalibrationClickTimestamp).TotalMilliseconds;
            if (milliseconds <= 700)
            {
                _calibrationOffsets.Add(milliseconds);
                _calibrationCapturedForClick = _calibrationClickIndex;
                StatusMessage = $"Calibration captured {milliseconds:0} ms after click {_calibrationClickIndex + 1}.";
            }
        }
        if (!IsLessonActive || _lessonGroups.Count == 0) return;

        if (SelectedLessonMode == LessonMode.WaitForYou)
        {
            HandleWaitForYouNote(midiNoteNumber);
        }
        else
        {
            var beat = Math.Max(_lessonStartBeat, _lessonStartBeat +
                (_lessonClock.Elapsed.TotalMilliseconds - LatencyMilliseconds) / 1000d * EffectiveLessonTempoBpm / 60d);
            AdvanceTimedMisses(beat);
            if (IsLessonActive) HandleTimedNote(midiNoteNumber, beat);
        }

        UpdateLessonMetrics();
    }

    private void HandleWaitForYouNote(int midiNoteNumber)
    {
        if (_lessonGroupIndex >= _lessonGroups.Count)
        {
            EndLesson(true);
            return;
        }

        var expected = _lessonGroups[_lessonGroupIndex];
        if (expected.MidiNotes.Contains(midiNoteNumber))
        {
            if (_matchedNotes.Contains(midiNoteNumber))
            {
                // Already accepted as correct in the current chord; ignore duplicate hit cleanly.
                return;
            }

            _matchedNotes.Add(midiNoteNumber);
            RegisterCorrect(expected.OnsetBeats, midiNoteNumber);
            BeginHold(midiNoteNumber, expected);
            if (_matchedNotes.Count >= expected.NoteCount)
            {
                PlayPracticeGuidance(expected);
                _lessonGroupIndex++;
                _matchedNotes.Clear();
                if (_lessonGroupIndex >= _lessonGroups.Count)
                {
                    var generation = _modeSwitchGeneration;
                    _ = CompletePracticeLessonAsync(generation);
                }
                else
                {
                    CursorBeat = _lessonGroups[_lessonGroupIndex].OnsetBeats;
                    LessonStatusLabel = $"Correct. Now bar {_lessonGroups[_lessonGroupIndex].MeasureNumber}.";
                }
            }
            else
            {
                LessonStatusLabel = $"Chord tone accepted. {_matchedNotes.Count}/{expected.NoteCount} tones played.";
            }
        }
        else
        {
            if (_matchedNotes.Count > 0 && expected.NoteCount > 1)
            {
                ResetPartialChord(expected);
            }

            _missedCount++;
            CurrentStreak = 0;
            OnPropertyChanged(nameof(StreakProgress));
            EmitNoteFeedback("wrong", CursorBeat, midiNoteNumber);
            var preferFlats = _score?.KeySignature.Contains("b", StringComparison.OrdinalIgnoreCase) == true;
            var playedName = MidiNoteFormatter.Format(midiNoteNumber, preferFlats);
            var expectedNames = string.Join(" + ", expected.MidiNotes.Select(note => MidiNoteFormatter.Format(note, preferFlats)));
            LessonStatusLabel = expected.NoteCount > 1
                ? $"Wrong note ({playedName}, expected {expectedNames}). Resetting chord — strike expected notes together."
                : $"Wrong note ({playedName}, expected {expectedNames}). Strike the correct key to proceed.";
        }
    }

    private async Task CompletePracticeLessonAsync(int generation)
    {
        LessonStatusLabel = "Piece complete! Ringing out final chord...";
        await Task.Delay(1800);
        if (generation == _modeSwitchGeneration && IsLessonActive)
        {
            EndLesson(true);
        }
    }

    public event EventHandler<int>? PartialChordReset;

    private void ResetPartialChord(ScoreNoteGroup expected)
    {
        _matchedNotes.Clear();
        PartialChordReset?.Invoke(this, expected.PerformanceOccurrence);
    }

    private void PlayPracticeGuidance(ScoreNoteGroup acceptedGroup)
    {
        if (_score is null) return;
        if (IsMetronomeAudible)
        {
            _audioService.PlayMetronomeClick(
                Math.Abs(acceptedGroup.OnsetBeats % 4) < 0.01,
                EffectiveMixerVolume(MetronomeVolume));
        }

        int volume;
        IEnumerable<ScoreNote> notes;
        if (PracticeFullAccompanimentEnabled && !InstrumentalMuted)
        {
            volume = EffectiveMixerVolume(InstrumentalVolume);
            notes = _score.Notes.Where(note => Math.Abs(note.OnsetBeats - acceptedGroup.OnsetBeats) < 0.001);
        }
        else if (OtherHandAccompanimentEnabled && SelectedMode != PracticeMode.BothHands)
        {
            volume = EffectiveMixerVolume(OtherHandAccompanimentVolume);
            var oppositeStaff = SelectedMode == PracticeMode.LeftHand ? 1 : 2;
            notes = _score.Notes.Where(note =>
                note.StaffNumber == oppositeStaff &&
                Math.Abs(note.OnsetBeats - acceptedGroup.OnsetBeats) < 0.001);
        }
        else
        {
            return;
        }

        var sounding = notes.ToArray();
        if (volume <= 0 || sounding.Length == 0) return;
        _accompanimentSynth.VolumePercent = volume;
        foreach (var note in sounding) _accompanimentSynth.NoteOn(note.MidiNoteNumber, 92);
        _ = ReleasePracticeGuidanceAsync(sounding);
    }

    private async Task ReleasePracticeGuidanceAsync(IReadOnlyList<ScoreNote> notes)
    {
        var durationBeats = notes.Select(note => note.DurationBeats).DefaultIfEmpty(.5).Max();
        var milliseconds = Math.Clamp((int)Math.Round(durationBeats * 60_000d / EffectiveLessonTempoBpm), 90, 2500);
        await Task.Delay(milliseconds);
        foreach (var note in notes) _accompanimentSynth.NoteOff(note.MidiNoteNumber);
    }

    private void HandleTimedNote(int midiNoteNumber, double beat)
    {
        if (_lessonGroupIndex >= _lessonGroups.Count)
        {
            _extraCount++;
            EmitNoteFeedback("extra", beat, midiNoteNumber);
            return;
        }

        var expected = _lessonGroups[_lessonGroupIndex];
        var timingWindow = 0.55d;
        var error = beat - expected.OnsetBeats;
        if (error >= -timingWindow && error <= timingWindow)
        {
            if (expected.MidiNotes.Contains(midiNoteNumber))
            {
                if (_matchedNotes.Contains(midiNoteNumber))
                {
                    // Note already matched in the active chord timing window; ignore duplicate hit.
                    return;
                }

                _matchedNotes.Add(midiNoteNumber);
                RegisterCorrect(expected.OnsetBeats, midiNoteNumber);
                BeginHold(midiNoteNumber, expected);
                _timingQualityTotal += Math.Max(0, 1d - Math.Abs(error) / timingWindow);
                if (_matchedNotes.Count >= expected.NoteCount)
                {
                    _lessonGroupIndex++;
                    _matchedNotes.Clear();
                    UpdateExpectedGuideForCursor();
                }
            }
            else
            {
                // Played during an active note window, but wrong pitch!
                _missedCount++;
                CurrentStreak = 0;
                OnPropertyChanged(nameof(StreakProgress));
                EmitNoteFeedback("wrong", beat, midiNoteNumber);
            }
        }
        else
        {
            // Played outside any active note window (e.g. rest or off-beat)
            _extraCount++;
            EmitNoteFeedback("extra", beat, midiNoteNumber);
        }
    }

    private void LessonTimer_Tick(object? sender, EventArgs e)
    {
        if (_score is null) return;
        if (!IsLessonActive) return;
        var beat = _lessonStartBeat + _lessonClock.Elapsed.TotalSeconds * EffectiveLessonTempoBpm / 60d;
        while (_nextMetronomeBeat <= beat)
        {
            if (SelectedLessonMode == LessonMode.TimedPlay && IsMetronomeAudible && !_performanceAudioOwnsMetronome)
                _audioService.PlayMetronomeClick(
                    Math.Abs(_nextMetronomeBeat % 4) < 0.01,
                    EffectiveMixerVolume(MetronomeVolume));
            _nextMetronomeBeat += 1;
        }

        if (SelectedLessonMode == LessonMode.TimedPlay)
        {
            AdvanceTimedMisses(beat);
            if (_lessonGroupIndex >= _lessonGroups.Count && beat >= SelectedPreviewEndBeat)
            {
                EndLesson(true);
            }
        }

        if ((DateTime.UtcNow - _lastLiveUpdate).TotalMilliseconds >= 80)
        {
            _lastLiveUpdate = DateTime.UtcNow;
            UpdateLessonMetrics();
        }
    }

    private void AdvanceTimedMisses(double beat)
    {
        const double lateWindow = 0.55d;
        var advanced = false;
        while (_lessonGroupIndex < _lessonGroups.Count && beat > _lessonGroups[_lessonGroupIndex].OnsetBeats + lateWindow)
        {
            var missed = Math.Max(0, _lessonGroups[_lessonGroupIndex].NoteCount - _matchedNotes.Count);
            _missedCount += missed;
            if (missed > 0)
            {
                CurrentStreak = 0;
                foreach (var missedNote in _lessonGroups[_lessonGroupIndex].MidiNotes.Except(_matchedNotes))
                EmitNoteFeedback("missed", _lessonGroups[_lessonGroupIndex].OnsetBeats, missedNote);
            }
            _lessonGroupIndex++;
            _matchedNotes.Clear();
            advanced = true;
        }
        if (advanced) UpdateExpectedGuideForCursor();
    }

    public void EndLesson(bool completed)
    {
        if (!IsLessonActive) return;
        foreach (var note in _activeHolds.Keys.ToArray()) CompleteHold(note);
        if (completed && SelectedLessonMode == LessonMode.TimedPlay)
        {
            while (_lessonGroupIndex < _lessonGroups.Count)
            {
                var missed = Math.Max(0, _lessonGroups[_lessonGroupIndex].NoteCount - _matchedNotes.Count);
                _missedCount += missed;
                if (missed > 0)
                    foreach (var missedNote in _lessonGroups[_lessonGroupIndex].MidiNotes.Except(_matchedNotes))
                        EmitNoteFeedback("missed", _lessonGroups[_lessonGroupIndex].OnsetBeats, missedNote);
                _lessonGroupIndex++;
                _matchedNotes.Clear();
            }
        }

        _lessonTimer.Stop();
        _lessonClock.Stop();
        _practiceSessionClock.Stop();
        if (SelectedLessonMode == LessonMode.TimedPlay)
            _audioService.StopPreview();
        _preparedPerformanceWave = null;
        _performanceAudioOwnsMetronome = false;
        _accompanimentSynth.AllNotesOff();
        SetNativeInputActive(_midiDeviceService.IsCapturing);
        IsLessonActive = false;
        LessonRunStateChanged?.Invoke(this, new LessonRunStateEvent(completed ? "completed" : "stopped", SelectedLessonMode, _lessonStartBeat, _lessonRunGeneration));
        _liveSynth.AllNotesOff();
        LessonStatusLabel = completed ? "Selected performance sequence complete." : "Lesson stopped.";
        ExpectedLabel = completed ? "Selected range complete." : FormatExpectedGroup();
        StatusMessage = completed
            ? $"Lesson complete. Correct {_correctCount}, missed {_missedCount}, extra {_extraCount}."
            : "Lesson stopped. The selected score and input remain ready.";
        RaiseLessonStateProperties();
        UpdateLessonMetrics();
        var assessmentBlocked = _score?.HasBlockingAssessmentWarning(FocusStartMeasure, FocusEndMeasure) == true;
        if (completed && _correctCount > 0 && !assessmentBlocked)
            PersistCompletedAttempt(_practiceSessionClock.Elapsed);
        if (completed)
        {
            var denominator = _correctCount + _missedCount + _extraCount;
            var accuracy = denominator == 0 ? 0 : _correctCount * 100d / denominator;
            ResultHeadline = accuracy >= 90 ? "Brilliant run" :
                accuracy >= 75 ? "Strong progress" :
                SelectedLessonMode == LessonMode.TimedPlay ? "Run complete" : "Practice complete";
            var timingResult = SelectedLessonMode == LessonMode.WaitForYou
                ? "timing not graded"
                : $"timing {(_correctCount == 0 ? 0 : _timingQualityTotal / _correctCount * 100):0}%";
            var holdResult = _holdQualityCount == 0 ? "hold not observed" : $"hold {_holdQualityTotal / _holdQualityCount * 100:0}%";
            ResultSummary = assessmentBlocked
                ? "Guided repeat practice complete · ambiguous volta range was not graded or saved"
                : $"Pitch {accuracy:0}% · {timingResult} · {holdResult} · {PedalCategoryLabel}";
            RewardLabel = BestStreak >= 20 ? $"Gold cadence · best streak {BestStreak}" :
                BestStreak >= 8 ? $"Silver cadence · best streak {BestStreak}" :
                $"First phrase · best streak {BestStreak}";
            ResultsVisible = true;
            CursorBeat = SelectedPreviewStartBeat;
            StartAutoRepeatCountdown();
            ResultsPresented?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RegisterCorrect(double beat, int midiNoteNumber)
    {
        _correctCount++;
        CurrentStreak++;
        BestStreak = Math.Max(BestStreak, CurrentStreak);
        OnPropertyChanged(nameof(StreakProgress));
        FeedbackBeat = beat;
        FeedbackPulse++;
        CorrectFeedback?.Invoke(this, EventArgs.Empty);
        EmitNoteFeedback("correct", beat, midiNoteNumber);
    }

    private void BeginHold(int midiNoteNumber, ScoreNoteGroup group, int velocity = 90)
    {
        var expectedNote = group.Notes
            .Where(note => note.MidiNoteNumber == midiNoteNumber)
            .OrderByDescending(note => note.DurationBeats)
            .FirstOrDefault();
        if (expectedNote is null) return;

        if (_activeHolds.ContainsKey(midiNoteNumber))
        {
            CompleteHold(midiNoteNumber);
        }
        _activeHolds[midiNoteNumber] = new ActiveHold(
            Stopwatch.GetTimestamp(),
            Math.Max(0.05, expectedNote.DurationBeats),
            expectedNote.OnsetBeats,
            expectedNote.IsStaccato,
            expectedNote.IsAccent,
            expectedNote.IsTenuto,
            expectedNote.IsSlurred);
        HeldNoteNumbers = _activeHolds.Keys.ToHashSet();
        EmitNoteFeedback("hold", expectedNote.OnsetBeats, midiNoteNumber);

        EvaluateVoiceVoicing(expectedNote, velocity);
        EvaluateChordSync(group);
    }

    private void EvaluateVoiceVoicing(ScoreNote note, int velocity)
    {
        if (_score is null) return;
        if (note.StaffNumber == 1)
        {
            _voicingQualityTotal += 1.0;
            _voicingQualityCount++;
        }
        else if (note.StaffNumber == 2)
        {
            var quality = velocity > 102 ? 0.65 : 1.0;
            _voicingQualityTotal += quality;
            _voicingQualityCount++;
        }
    }

    private void EvaluateChordSync(ScoreNoteGroup group)
    {
        if (group.NoteCount <= 1) return;
        var now = Stopwatch.GetTimestamp();
        if (_chordHitsInGroup == 0)
        {
            _chordFirstTimestamp = now;
            _chordLastTimestamp = now;
            _chordHitsInGroup = 1;
        }
        else
        {
            _chordLastTimestamp = now;
            _chordHitsInGroup++;
            if (_chordHitsInGroup >= group.NoteCount)
            {
                var spreadMs = Stopwatch.GetElapsedTime(_chordFirstTimestamp, _chordLastTimestamp).TotalMilliseconds;
                var quality = spreadMs <= 28 ? 1.0 : Math.Clamp(1.0 - (spreadMs - 28) / 45.0, 0.25, 1.0);
                _chordSyncTotal += quality;
                _chordSyncCount++;
                _chordHitsInGroup = 0;
            }
        }
    }

    private void CompleteHold(int midiNoteNumber)
    {
        if (!_activeHolds.Remove(midiNoteNumber, out var hold)) return;
        var actualSeconds = Stopwatch.GetElapsedTime(hold.StartTimestamp).TotalSeconds;
        var expectedSeconds = hold.ExpectedBeats * 60d / Math.Max(1d, EffectiveLessonTempoBpm);

        double quality;
        if (hold.IsStaccato)
        {
            quality = actualSeconds > expectedSeconds * 0.65
                ? Math.Clamp(1.0 - (actualSeconds - expectedSeconds * 0.65) / expectedSeconds, 0.2, 1.0)
                : 1.0;
        }
        else
        {
            var tolerantTarget = Math.Max(0.08, expectedSeconds * 0.78);
            quality = expectedSeconds <= 0.12 ? 1d : Math.Clamp(actualSeconds / tolerantTarget, 0d, 1d);
        }

        _holdQualityTotal += quality;
        _holdQualityCount++;
        _articulationQualityTotal += quality;
        _articulationQualityCount++;

        HeldNoteNumbers = _activeHolds.Keys.ToHashSet();
        OnPropertyChanged(nameof(HoldCategoryLabel));
        EmitNoteFeedback(quality < .72 ? "early" : "release", hold.OnsetBeat, midiNoteNumber);
    }

    private void ScorePedalEvent(bool isDown)
    {
        if (!PedalEnabled || !IsLessonActive || _score is null) return;
        var pedalMarks = _score.Marks.Where(mark => mark.Kind == ScoreMarkKind.Pedal).ToArray();
        if (pedalMarks.Length == 0) return;
        var expectedText = isDown ? "start" : "stop";
        var currentBeat = SelectedLessonMode == LessonMode.TimedPlay
            ? _lessonStartBeat + _lessonClock.Elapsed.TotalSeconds * EffectiveLessonTempoBpm / 60d
            : CursorBeat;
        var nearest = pedalMarks
            .Where(mark => mark.Text.Equals(expectedText, StringComparison.OrdinalIgnoreCase) ||
                           (!isDown && mark.Text.Equals("change", StringComparison.OrdinalIgnoreCase)))
            .Select(mark => Math.Abs(mark.OnsetBeats - currentBeat))
            .DefaultIfEmpty(double.MaxValue)
            .Min();
        _pedalAttempts++;
        if (nearest <= 0.8) _pedalCorrect++;
    }

    public void DismissResults()
    {
        StopAutoRepeatCountdown();
        ResultsVisible = false;
        ResultsDismissed?.Invoke(this, EventArgs.Empty);
    }

    private string FormatExpectedGroup()
    {
        if (_lessonGroups.Count == 0) return "No playable notes for this hand choice.";
        if (_lessonGroupIndex >= _lessonGroups.Count) return "No expected notes remain.";
        return FormatExpectedGroup(_lessonGroups[_lessonGroupIndex]);
    }

    private string FormatExpectedGroup(ScoreNoteGroup group)
    {
        var preferFlats = _score?.KeySignature.Contains("b", StringComparison.OrdinalIgnoreCase) == true;
        var names = string.Join(" + ", group.MidiNotes.Select(note => MidiNoteFormatter.Format(note, preferFlats)));
        return $"Bar {group.MeasureNumber} | {names}";
    }

    private void UpdateExpectedGuideForCursor()
    {
        if (_score is null)
        {
            ExpectedLabel = "Import a score and start a lesson.";
            return;
        }
        if (_lessonGroups.Count == 0)
        {
            ExpectedLabel = "No playable notes for this hand choice.";
            return;
        }

        if (IsLessonActive && SelectedLessonMode is LessonMode.WaitForYou or LessonMode.TimedPlay)
        {
            ExpectedLabel = _lessonGroupIndex < _lessonGroups.Count
                ? FormatExpectedGroup(_lessonGroups[_lessonGroupIndex])
                : "No expected notes remain.";
            return;
        }

        var low = 0;
        var high = _lessonGroups.Count;
        var targetBeat = CursorBeat - 0.001;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_lessonGroups[middle].OnsetBeats < targetBeat) low = middle + 1;
            else high = middle;
        }
        ExpectedLabel = low < _lessonGroups.Count
            ? FormatExpectedGroup(_lessonGroups[low])
            : "No expected notes remain.";
    }

    private void UpdateLessonMetrics()
    {
        var denominator = _correctCount + _missedCount + _extraCount;
        var accuracy = denominator == 0 ? 0 : _correctCount * 100d / denominator;
        CorrectLabel = _correctCount.ToString();
        MissedLabel = _missedCount.ToString();
        ExtraLabel = _extraCount.ToString();
        AccuracyLabel = denominator == 0 ? "-" : $"{accuracy:0}%";
        TimingLabel = SelectedLessonMode == LessonMode.WaitForYou
            ? "Timing: n/a in Wait-for-you"
            : _correctCount == 0 ? "Timing: 0%" : $"Timing: {_timingQualityTotal / _correctCount * 100:0}%";
        ProgressLabel = $"{Math.Min(_lessonGroupIndex, _lessonGroups.Count)} / {_lessonGroups.Count} expected groups";
        ExpectedLabel = FormatExpectedGroup();
        OnPropertyChanged(nameof(PitchCategoryLabel));
        OnPropertyChanged(nameof(TimingCategoryLabel));
        OnPropertyChanged(nameof(HoldCategoryLabel));
        OnPropertyChanged(nameof(PedalCategoryLabel));
        OnPropertyChanged(nameof(StartLessonReason));
        OnPropertyChanged(nameof(CanStartLesson));
    }

    private void RaiseLessonStateProperties()
    {
        OnPropertyChanged(nameof(CanChooseInput));
        OnPropertyChanged(nameof(CanStartLesson));
        OnPropertyChanged(nameof(CanUseTransport));
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(LessonButtonLabel));
        OnPropertyChanged(nameof(InputSourceLabel));
        OnPropertyChanged(nameof(StartLessonReason));
    }

    private void RaisePreviewStateProperties()
    {
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(LessonButtonLabel));
        OnPropertyChanged(nameof(IsScorePreviewPlaying));
        OnPropertyChanged(nameof(CanUseTransport));
        OnPropertyChanged(nameof(CanPlayMidiReference));
    }

    private void SaveProfileSettings()
    {
        var settings = _profile.Settings ??= new CadenzaUserSettings();
        settings.MidiMonitorEnabled = MidiMonitorEnabled;
        settings.MonitorVolume = MonitorVolume;
        settings.OverallVolume = OverallVolume;
        settings.InstrumentalMuted = InstrumentalMuted;
        settings.InstrumentalVolume = InstrumentalVolume;
        settings.MetronomeMuted = MetronomeMuted;
        settings.MetronomeVolume = MetronomeVolume;
        settings.PracticeFullAccompanimentEnabled = PracticeFullAccompanimentEnabled;
        settings.PerformanceFullAccompanimentEnabled = PerformanceFullAccompanimentEnabled;
        settings.OtherHandAccompanimentEnabled = OtherHandAccompanimentEnabled;
        settings.OtherHandAccompanimentVolume = OtherHandAccompanimentVolume;
        settings.LessonTempoPercent = LessonTempoPercent;
        settings.HandMode = SelectedMode;
        settings.LessonMode = SelectedLessonMode;
        settings.ScoreReadingMode = ReadingMode;
        settings.HintModeEnabled = HintModeEnabled;
        settings.NotationZoomPercent = NotationZoomPercent;
        settings.FocusStartMeasure = FocusStartMeasure;
        settings.FocusEndMeasure = (_score is null || _focusEndMeasure <= 0 || FocusEndMeasure == _score.MeasureCount) ? 0 : _focusEndMeasure;
        settings.PedalEnabled = PedalEnabled;
        settings.LatencyMilliseconds = LatencyMilliseconds;
        settings.MetronomeEnabled = MetronomeEnabled;
        settings.LoopEnabled = IsLoopEnabled;
        settings.ComputerKeyboardEnabled = UseKeyboardSimulation;
        settings.PlaybackSoundPresetId = PlaybackSoundPreset.Id;
        settings.LiveSoundPresetId = LiveSoundPreset.Id;
        settings.MatchPlaybackSynthEnabled = MatchPlaybackSynthEnabled;
        settings.OnlyShowFeedbackOnPerformanceEnd = OnlyShowFeedbackOnPerformanceEnd;
        if (_score?.SourcePath is not null) settings.LastOpenedScorePath = _score.SourcePath;
        TrySaveProfile();
    }

    private int EffectiveMixerVolume(int channelVolume) =>
        Math.Clamp((int)Math.Round(Math.Clamp(channelVolume, 0, 100) * OverallVolume / 100d), 0, 100);

    private void ApplyMixerVolumes()
    {
        _liveSynth.VolumePercent = EffectiveMixerVolume(MonitorVolume);
        _accompanimentSynth.VolumePercent = EffectiveMixerVolume(OtherHandAccompanimentVolume);
    }

    private void ApplySoundPresets()
    {
        if (_liveSoundPreset.IsSoftSynth)
        {
            _liveSynth.SetProgram(0);
        }
        else
        {
            _liveSynth.SetProgram(_liveSoundPreset.PatchNumber);
        }

        if (_playbackSoundPreset.IsSoftSynth)
        {
            _accompanimentSynth.SetProgram(0);
        }
        else
        {
            _accompanimentSynth.SetProgram(_playbackSoundPreset.PatchNumber);
        }
    }

    private void PersistCompletedAttempt(TimeSpan elapsed)
    {
        if (_score is null) return;
        var songs = _profile.Songs ??= new Dictionary<string, SongProgressRecord>(StringComparer.OrdinalIgnoreCase);
        var key = GetSongProgressKey(_score);
        if (!songs.TryGetValue(key, out var progress))
        {
            progress = new SongProgressRecord
            {
                SongTitle = ScoreTitle,
                SourcePath = _score.SourcePath
            };
            songs[key] = progress;
        }

        var denominator = _correctCount + _missedCount + _extraCount;
        var accuracy = denominator == 0 ? 0 : _correctCount * 100d / denominator;
        var timing = SelectedLessonMode == LessonMode.WaitForYou || _correctCount == 0
            ? 0
            : _timingQualityTotal / _correctCount * 100d;
        var hold = _holdQualityCount == 0 ? 0 : _holdQualityTotal / _holdQualityCount * 100d;
        var practiceSeconds = Math.Max(0, elapsed.TotalSeconds);
        progress.SongTitle = ScoreTitle;
        progress.SourcePath = _score.SourcePath;
        progress.LastPracticedUtc = DateTimeOffset.UtcNow;
        progress.LastPositionBeat = Math.Clamp(CursorBeat, SelectedPreviewStartBeat, SelectedPreviewEndBeat);
        progress.CumulativePracticeSeconds += practiceSeconds;
        progress.BestStreak = Math.Max(progress.BestStreak, BestStreak);
        progress.Attempts.Add(new CompletedAttemptSummary
        {
            CompletedUtc = DateTimeOffset.UtcNow,
            Mode = SelectedLessonMode,
            HandMode = SelectedMode,
            StartMeasure = FocusStartMeasure,
            EndMeasure = FocusEndMeasure,
            AccuracyPercent = accuracy,
            TimingPercent = timing,
            HoldPercent = hold,
            Correct = _correctCount,
            Missed = _missedCount,
            Extra = _extraCount,
            PracticeSeconds = practiceSeconds,
            BestStreak = BestStreak
        });
        if (progress.Attempts.Count > 200)
            progress.Attempts.RemoveRange(0, progress.Attempts.Count - 200);

        _currentSongProgress = progress;
        TrySaveProfile();
        OnPropertyChanged(nameof(DashboardProgressSummary));
        OnPropertyChanged(nameof(RecentAttemptLabel));
        OnPropertyChanged(nameof(CumulativePracticeLabel));
        OnPropertyChanged(nameof(CompletedAttemptCount));
        OnPropertyChanged(nameof(DashboardScoreSummary));
    }

    private void TrySaveProfile()
    {
        try
        {
            _profileStore.Save(_profile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Settings could not be saved: {exception.Message}";
        }
    }

    private static string GetSongProgressKey(ScoreDocument score) =>
        Path.GetFullPath(score.SourcePath).Trim().ToUpperInvariant();

    private void EmitNoteFeedback(string kind, double beat, int? midiNoteNumber)
    {
        var expectedGroup = _lessonGroups
            .OrderBy(group => Math.Abs(group.OnsetBeats - beat))
            .FirstOrDefault();
        var occurrenceIndex = expectedGroup?.PerformanceOccurrence ??
            SelectedPerformanceOccurrences
                .Where(occurrence => occurrence.PerformanceStartBeat <= beat + 0.001)
                .Select(occurrence => occurrence.OccurrenceIndex)
                .DefaultIfEmpty(0)
                .Max();
        var staffNumber = SelectedMode switch
        {
            PracticeMode.RightHand => 1,
            PracticeMode.LeftHand => 2,
            _ when midiNoteNumber is not null && expectedGroup is not null => expectedGroup.Notes
                .OrderBy(note => Math.Abs(note.MidiNoteNumber - midiNoteNumber.Value))
                .ThenBy(note => note.StaffNumber)
                .Select(note => note.StaffNumber)
                .FirstOrDefault(),
            _ => 0
        };
        NoteFeedback?.Invoke(this, new LessonNoteFeedbackEvent(
            kind,
            beat,
            midiNoteNumber,
            _lessonRunGeneration,
            ++_feedbackEventSequence,
            occurrenceIndex,
            staffNumber));
    }

    private void SetNativeInputActive(bool active)
    {
        if (_nativeInputActive == active) return;
        _nativeInputActive = active;
        OnPropertyChanged(nameof(HasAcceptedInput));
        OnPropertyChanged(nameof(InputSourceLabel));
        OnPropertyChanged(nameof(StartLessonReason));
        OnPropertyChanged(nameof(CanStartLesson));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record ActiveHold(
        long StartTimestamp,
        double ExpectedBeats,
        double OnsetBeat,
        bool IsStaccato = false,
        bool IsAccent = false,
        bool IsTenuto = false,
        bool IsSlurred = false);
}

public sealed record LessonNoteFeedbackEvent(
    string Kind,
    double Beat,
    int? MidiNoteNumber = null,
    long RunGeneration = 0,
    long EventId = 0,
    int OccurrenceIndex = 0,
    int StaffNumber = 0);
public sealed record LessonRunStateEvent(string State, LessonMode Mode, double StartBeat, long RunGeneration = 0);
