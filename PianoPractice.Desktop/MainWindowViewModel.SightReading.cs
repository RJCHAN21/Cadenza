using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

namespace PianoPractice.Desktop;

public sealed partial class MainWindowViewModel
{
    private bool _isSightReadingVisible;
    private bool _isSightReadingActive;
    private bool _isSightReadingAwaitingAdvance;
    private bool _isSightReadingComplete;
    private SightReadingTestKind _selectedSightReadingTest = SightReadingTestKind.GuidedNotes;
    private IReadOnlyList<SightReadingPrompt> _sightReadingPrompts = [];
    private int _sightReadingPromptIndex;
    private int _sightReadingNoteIndex;
    private int _sightReadingCorrect;
    private int _sightReadingMistakes;
    private int _sightReadingStreak;
    private int _sightReadingBestStreak;
    private bool _autoRestartSightReading;
    private DateTimeOffset _sightReadingStartedAt;
    private string _sightReadingStatus = "Choose a test, then start when your MIDI keyboard is ready.";
    private long _sightReadingSessionGeneration;
    private long _sightReadingEventSequence;
    private readonly Dictionary<SightReadingTestKind, string> _lastSightReadingSessionSignatures = [];
    private CancellationTokenSource? _sightReadingAdvanceCancellation;

    public event EventHandler<SightReadingPrompt>? SightReadingPromptChanged;
    public event EventHandler<SightReadingFeedbackEvent>? SightReadingFeedback;

    public bool IsSightReadingVisible
    {
        get => _isSightReadingVisible;
        private set
        {
            if (!SetField(ref _isSightReadingVisible, value)) return;
            ResetMidiShortcutState();
            OnPropertyChanged(nameof(IsDashboardVisible));
            OnPropertyChanged(nameof(IsMusicalWorkspaceVisible));
        }
    }

    public bool IsMusicalWorkspaceVisible => IsPlayerVisible || IsSightReadingVisible;
    public bool IsSightReadingActive
    {
        get => _isSightReadingActive;
        private set => SetField(ref _isSightReadingActive, value);
    }
    public bool IsSightReadingComplete
    {
        get => _isSightReadingComplete;
        private set => SetField(ref _isSightReadingComplete, value);
    }
    public bool AutoRestartSightReading
    {
        get => _autoRestartSightReading;
        set => SetField(ref _autoRestartSightReading, value);
    }
    public SightReadingTestKind SelectedSightReadingTest
    {
        get => _selectedSightReadingTest;
        private set
        {
            if (!SetField(ref _selectedSightReadingTest, value)) return;
            OnPropertyChanged(nameof(SightReadingTestName));
            OnPropertyChanged(nameof(SightReadingTestDescription));
        }
    }
    public SightReadingPrompt? CurrentSightReadingPrompt =>
        _sightReadingPromptIndex >= 0 && _sightReadingPromptIndex < _sightReadingPrompts.Count
            ? _sightReadingPrompts[_sightReadingPromptIndex]
            : null;
    public long SightReadingSessionGeneration => _sightReadingSessionGeneration;
    public string SightReadingTestName => SelectedSightReadingTest switch
    {
        SightReadingTestKind.GuidedNotes => "Guided Note Discovery",
        SightReadingTestKind.NoteRecognition => "Unlabeled Note Test",
        SightReadingTestKind.IntervalReading => "Interval Patterns",
        SightReadingTestKind.LookAheadSequences => "Look-Ahead Sequences",
        SightReadingTestKind.Accidentals => "Accidentals",
        SightReadingTestKind.KeySignatures => "Key Signatures",
        SightReadingTestKind.LedgerLines => "Ledger Lines",
        SightReadingTestKind.MixedChallenge => "Mixed Reading Challenge",
        _ => "Sight Reading"
    };
    public string SightReadingTestDescription => SelectedSightReadingTest switch
    {
        SightReadingTestKind.GuidedNotes => "Each staff note is introduced with its printed name, then returns later without a label.",
        SightReadingTestKind.NoteRecognition => "Recognize randomized treble and bass notes without note-name labels.",
        SightReadingTestKind.IntervalReading => "Use the first note as a landmark and read the visual distance to the next notes.",
        SightReadingTestKind.LookAheadSequences => "Preview a four-note phrase and keep your eyes ahead while playing it in order.",
        SightReadingTestKind.Accidentals => "Read sharps, flats, and naturals across both clefs, including changing accidental signs.",
        SightReadingTestKind.KeySignatures => "Apply the displayed key signature while reading short treble and bass patterns.",
        SightReadingTestKind.LedgerLines => "Recognize notes above and below the normal five-line treble and bass staves.",
        SightReadingTestKind.MixedChallenge => "Switch between natural notes, intervals, sequences, accidentals, and key signatures.",
        _ => string.Empty
    };
    public string SightReadingPromptTitle => CurrentSightReadingPrompt?.Title ?? SightReadingTestName;
    public string SightReadingInstruction => CurrentSightReadingPrompt?.Instruction ?? SightReadingTestDescription;
    public string SightReadingStatus
    {
        get => _sightReadingStatus;
        private set => SetField(ref _sightReadingStatus, value);
    }
    public string SightReadingProgressLabel => _sightReadingPrompts.Count == 0
        ? "Not started"
        : $"Prompt {Math.Min(_sightReadingPromptIndex + 1, _sightReadingPrompts.Count)} of {_sightReadingPrompts.Count}";
    public string SightReadingScoreLabel => $"{_sightReadingCorrect} correct · {_sightReadingMistakes} mistakes";
    public string SightReadingCorrectValue => _sightReadingCorrect.ToString();
    public string SightReadingMistakesValue => _sightReadingMistakes.ToString();
    public string SightReadingBestStreakValue => _sightReadingBestStreak.ToString();
    public string SightReadingAccuracyValue
    {
        get
        {
            var attempts = _sightReadingCorrect + _sightReadingMistakes;
            return attempts == 0 ? "—" : $"{_sightReadingCorrect * 100d / attempts:0}%";
        }
    }
    public double SightReadingProgressPercent => _sightReadingPrompts.Count == 0
        ? 0
        : Math.Min(_sightReadingPromptIndex, _sightReadingPrompts.Count) * 100d / _sightReadingPrompts.Count;
    public string SightReadingStreakLabel => _sightReadingStreak == 0
        ? "Build a reading streak"
        : $"{_sightReadingStreak} note streak · best {_sightReadingBestStreak}";
    public string SightReadingAccuracyLabel
    {
        get
        {
            var attempts = _sightReadingCorrect + _sightReadingMistakes;
            return attempts == 0 ? "Accuracy —" : $"Accuracy {_sightReadingCorrect * 100d / attempts:0}%";
        }
    }
    public string SightReadingResultRating
    {
        get
        {
            var attempts = _sightReadingCorrect + _sightReadingMistakes;
            var accuracy = attempts == 0 ? 0 : _sightReadingCorrect * 100d / attempts;
            return accuracy switch
            {
                >= 95 => "Excellent reading",
                >= 85 => "Strong reading",
                >= 70 => "Good progress",
                _ => "Keep building"
            };
        }
    }
    public string SightReadingResultMessage =>
        $"You completed {SightReadingTestName}. Start another unseen set to reinforce the same reading skill.";
    public string SightReadingElapsedLabel => _sightReadingStartedAt == default
        ? "—"
        : FormatSightReadingElapsed(DateTimeOffset.UtcNow - _sightReadingStartedAt);

    public void OpenSightReading()
    {
        if (IsLessonActive) StopLesson();
        StopPreview();
        IsPlayerVisible = false;
        IsSightReadingVisible = true;
        SightReadingStatus = HasAcceptedInput
            ? "Choose a test and start. Your normal piano input is ready."
            : "Connect a MIDI keyboard or enable computer piano keys in Settings before starting.";
        if (HasAcceptedInput)
            StartSightReadingTest();
    }

    public void SelectSightReadingTest(SightReadingTestKind kind)
    {
        if (IsSightReadingActive) StopSightReadingTest();
        SelectedSightReadingTest = kind;
        SightReadingStatus = SightReadingTestDescription;
    }

    public bool StartSightReadingTest(int? seed = null)
    {
        if (!HasAcceptedInput)
        {
            SightReadingStatus = "Connect a MIDI keyboard or enable computer piano keys before starting.";
            return false;
        }

        CancelSightReadingAdvance();
        var generationSeed = seed ?? Random.Shared.Next();
        IReadOnlyList<SightReadingPrompt> generatedPrompts;
        string signature;
        do
        {
            generatedPrompts = SightReadingExerciseGenerator.CreateSession(SelectedSightReadingTest, generationSeed);
            signature = string.Join('|', generatedPrompts.Select(prompt =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prompt.MusicXml))));
            generationSeed = Random.Shared.Next();
        }
        while (seed is null &&
               _lastSightReadingSessionSignatures.TryGetValue(SelectedSightReadingTest, out var previousSignature) &&
               signature == previousSignature);
        _lastSightReadingSessionSignatures[SelectedSightReadingTest] = signature;
        _sightReadingPrompts = generatedPrompts;
        _sightReadingPromptIndex = 0;
        _sightReadingNoteIndex = 0;
        _sightReadingCorrect = 0;
        _sightReadingMistakes = 0;
        _sightReadingStreak = 0;
        _sightReadingBestStreak = 0;
        _sightReadingStartedAt = DateTimeOffset.UtcNow;
        _sightReadingSessionGeneration++;
        IsSightReadingComplete = false;
        IsSightReadingActive = _sightReadingPrompts.Count > 0;
        SightReadingStatus = CurrentSightReadingPrompt?.Instruction ?? "No exercise was generated.";
        NotifySightReadingState();
        if (CurrentSightReadingPrompt is { } prompt)
            SightReadingPromptChanged?.Invoke(this, prompt);
        return IsSightReadingActive;
    }

    public void StopSightReadingTest()
    {
        CancelSightReadingAdvance();
        IsSightReadingActive = false;
        _sightReadingNoteIndex = 0;
        SightReadingStatus = "Test stopped. Choose a test or start again.";
        NotifySightReadingState();
    }

    public void SetSightReadingRendererError(string message)
    {
        IsSightReadingActive = false;
        SightReadingStatus = $"Sight-reading notation could not be displayed: {message}";
    }

    private bool TryHandleSightReadingNote(int midiNoteNumber)
    {
        if (!IsSightReadingVisible) return false;
        if (!IsSightReadingActive || _isSightReadingAwaitingAdvance || CurrentSightReadingPrompt is not { } prompt)
            return true;

        var expectedNote = prompt.MidiNotes[_sightReadingNoteIndex];
        var beat = prompt.Beats[_sightReadingNoteIndex];
        var kind = midiNoteNumber == expectedNote ? "correct" : "wrong";
        SightReadingFeedback?.Invoke(this, new SightReadingFeedbackEvent(
            kind,
            beat,
            midiNoteNumber,
            _sightReadingSessionGeneration,
            ++_sightReadingEventSequence,
            prompt.StaffNumber));

        if (midiNoteNumber != expectedNote)
        {
            _sightReadingMistakes++;
            _sightReadingStreak = 0;
            SightReadingStatus = "That was not the displayed note. Keep reading the notation and try again.";
            NotifySightReadingScore();
            return true;
        }

        _sightReadingCorrect++;
        _sightReadingStreak++;
        _sightReadingBestStreak = Math.Max(_sightReadingBestStreak, _sightReadingStreak);
        _sightReadingNoteIndex++;
        NotifySightReadingScore();
        if (_sightReadingNoteIndex < prompt.MidiNotes.Count)
        {
            SightReadingStatus = $"Correct. Keep reading ahead — {_sightReadingNoteIndex + 1} of {prompt.MidiNotes.Count} notes next.";
            return true;
        }

        _isSightReadingAwaitingAdvance = true;
        SightReadingStatus = "Prompt complete.";
        _ = AdvanceSightReadingAfterFeedbackAsync(_sightReadingSessionGeneration);
        return true;
    }

    private async Task AdvanceSightReadingAfterFeedbackAsync(long sessionGeneration)
    {
        CancelSightReadingAdvance();
        _isSightReadingAwaitingAdvance = true;
        _sightReadingAdvanceCancellation = new CancellationTokenSource();
        var cancellationToken = _sightReadingAdvanceCancellation.Token;
        try
        {
            await Task.Delay(650, cancellationToken);
            if (sessionGeneration != _sightReadingSessionGeneration || !IsSightReadingActive) return;

            _sightReadingPromptIndex++;
            _sightReadingNoteIndex = 0;
            _isSightReadingAwaitingAdvance = false;
            if (_sightReadingPromptIndex >= _sightReadingPrompts.Count)
            {
                IsSightReadingActive = false;
                IsSightReadingComplete = true;
                SightReadingStatus = AutoRestartSightReading
                    ? $"Test complete — {SightReadingAccuracyValue} accuracy. A new unseen set starts in 3 seconds."
                    : $"Test complete — {SightReadingAccuracyValue} accuracy. Start again for a new unseen set.";
                NotifySightReadingState();
                if (AutoRestartSightReading)
                {
                    await Task.Delay(3000, cancellationToken);
                    if (sessionGeneration == _sightReadingSessionGeneration &&
                        IsSightReadingVisible &&
                        AutoRestartSightReading)
                        StartSightReadingTest();
                }
                return;
            }

            SightReadingStatus = CurrentSightReadingPrompt!.Instruction;
            NotifySightReadingState();
            SightReadingPromptChanged?.Invoke(this, CurrentSightReadingPrompt);
        }
        catch (OperationCanceledException)
        {
            // A stopped, replaced, or closed session owns no delayed transition.
        }
    }

    private void NotifySightReadingScore()
    {
        OnPropertyChanged(nameof(SightReadingScoreLabel));
        OnPropertyChanged(nameof(SightReadingCorrectValue));
        OnPropertyChanged(nameof(SightReadingMistakesValue));
        OnPropertyChanged(nameof(SightReadingBestStreakValue));
        OnPropertyChanged(nameof(SightReadingAccuracyValue));
        OnPropertyChanged(nameof(SightReadingStreakLabel));
        OnPropertyChanged(nameof(SightReadingAccuracyLabel));
        OnPropertyChanged(nameof(SightReadingResultRating));
        OnPropertyChanged(nameof(SightReadingElapsedLabel));
    }

    private void NotifySightReadingState()
    {
        OnPropertyChanged(nameof(CurrentSightReadingPrompt));
        OnPropertyChanged(nameof(SightReadingPromptTitle));
        OnPropertyChanged(nameof(SightReadingInstruction));
        OnPropertyChanged(nameof(SightReadingProgressLabel));
        OnPropertyChanged(nameof(SightReadingProgressPercent));
        OnPropertyChanged(nameof(SightReadingResultMessage));
        NotifySightReadingScore();
    }

    private static string FormatSightReadingElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
            : $"{Math.Max(1, (int)Math.Ceiling(elapsed.TotalSeconds))} sec";

    private void CancelSightReadingAdvance()
    {
        _sightReadingAdvanceCancellation?.Cancel();
        _sightReadingAdvanceCancellation?.Dispose();
        _sightReadingAdvanceCancellation = null;
        _isSightReadingAwaitingAdvance = false;
    }
}
