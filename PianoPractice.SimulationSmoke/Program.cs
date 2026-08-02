using PianoPractice.Desktop;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;
using System.Windows.Input;

if (args.Length != 1)
    throw new ArgumentException("Pass the MusicXML or MXL fixture path as the only argument.");
var path = Path.GetFullPath(args[0]);

var importer = new MusicXmlImporter();
var score = importer.Import(path);
if (score.PerformanceMeasures.Count != 77 || Math.Abs(score.TotalBeats - 306) > 0.001)
    throw new InvalidOperationException($"Repeat-aware timeline mismatch: occurrences={score.PerformanceMeasures.Count}, beats={score.TotalBeats:0.###}.");
if (!score.HasBlockingAssessmentWarning(16, 40) || score.HasBlockingAssessmentWarning(1, 15))
    throw new InvalidOperationException("Ambiguous ending validation did not block only the affected assessed range.");
if (!score.CutsRepeatRegion(49, 49) || score.CutsRepeatRegion(49, 50))
    throw new InvalidOperationException("Partial repeat-range validation did not distinguish a cut repeat from the complete repeated section.");
var transientProfileRoot = Path.Combine(Path.GetTempPath(), $"cadenza-simulation-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(transientProfileRoot);
using var midiService = new MidiDeviceService();
var midiSnapshot = midiService.DiscoverInputDevices();
if (midiSnapshot.Devices.Count > 0)
{
    var openResult = midiService.StartInput(midiSnapshot.Devices[0].Id);
    Console.WriteLine($"WinMM capture open: success={openResult.Success}, error={openResult.Error ?? "none"}");
    if (!openResult.Success) throw new InvalidOperationException(openResult.Error);
    midiService.StopInput();
}
using (var liveSynth = new MidiOutSynthService())
{
    var quietAtFull = MidiOutSynthService.MapMonitorVelocity(32, 100);
    var mediumAtFull = MidiOutSynthService.MapMonitorVelocity(64, 100);
    var maximumAtFull = MidiOutSynthService.MapMonitorVelocity(127, 100);
    var mediumAtHalf = MidiOutSynthService.MapMonitorVelocity(64, 50);
    if (quietAtFull <= 32 || mediumAtFull <= 64 || maximumAtFull != 127 ||
        mediumAtHalf >= mediumAtFull || mediumAtFull > 127)
    {
        throw new InvalidOperationException(
            $"Monitor gain calibration is invalid: quiet={quietAtFull}, medium={mediumAtFull}, " +
            $"maximum={maximumAtFull}, mediumHalf={mediumAtHalf}.");
    }
    var output = liveSynth.Open();
    Console.WriteLine($"WinMM piano output: success={output.Success}, status={output.Message}");
    if (!output.Success) throw new InvalidOperationException(output.Message);
    var noteOn = liveSynth.NoteOn(60, 72);
    Thread.Sleep(90);
    var noteOff = liveSynth.NoteOff(60);
    if (!noteOn.Success || !noteOff.Success) throw new InvalidOperationException($"Live monitor test failed: {noteOn.Message}; {noteOff.Message}");
}
if (score.GetPracticeGroups(PracticeMode.LeftHand).Count == 0 || score.GetPracticeGroups(PracticeMode.RightHand).Count == 0)
{
    throw new InvalidOperationException("The supplied score did not produce both staff-specific practice timelines.");
}

using var audioService = new PianoAudioService();
var preview = await audioService.BuildPreviewAsync(score, includeMetronome: true, 0, Math.Min(score.TotalBeats, 4), score.TempoBpm, CancellationToken.None);
if (preview.Length < 44 || preview[0] != (byte)'R' || preview[1] != (byte)'I' || preview[2] != (byte)'F' || preview[3] != (byte)'F')
{
    throw new InvalidOperationException("The synthesized preview did not produce a RIFF/WAV payload.");
}
audioService.PlayPreview(preview);
audioService.PlayMetronomeClick(accent: true);
Thread.Sleep(50);
audioService.StopPreview();
var oneBarEnd = Math.Min(score.TotalBeats, 4);
var fullTempoPreview = await audioService.BuildPreviewAsync(score, false, 0, oneBarEnd, score.TempoBpm, CancellationToken.None);
var halfTempoPreview = await audioService.BuildPreviewAsync(score, false, 0, oneBarEnd, score.TempoBpm * 0.5, CancellationToken.None);
if (halfTempoPreview.Length < fullTempoPreview.Length * 1.7)
{
    throw new InvalidOperationException("Listen audio generation did not honor the selected effective tempo.");
}

var expectedComputerKeys = new[]
{
    (Key.A, 60), (Key.W, 61), (Key.S, 62), (Key.E, 63), (Key.D, 64),
    (Key.F, 65), (Key.T, 66), (Key.G, 67), (Key.Y, 68), (Key.H, 69),
    (Key.U, 70), (Key.J, 71), (Key.K, 72), (Key.O, 73), (Key.L, 74),
    (Key.P, 75), (Key.OemSemicolon, 76)
};
foreach (var (key, midiNote) in expectedComputerKeys)
{
    if (!ComputerKeyboardPianoMap.MidiNotes.TryGetValue(key, out var mapped) || mapped != midiNote)
    {
        throw new InvalidOperationException($"Computer piano mapping mismatch for {key}: expected {midiNote}, got {mapped}.");
    }
}

using (var transportViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "transport.json")))
{
    transportViewModel.LoadScore(path);
    transportViewModel.SetLessonMode(LessonMode.Listen);
    transportViewModel.FocusStartMeasure = 20;
    transportViewModel.FocusEndMeasure = 22;
    var selectedStartBeat = score.Notes
        .Where(note => int.TryParse(note.MeasureNumber, out var measure) && measure >= 20)
        .Min(note => note.OnsetBeats);

    await transportViewModel.TogglePreviewAsync();
    if (!transportViewModel.IsScorePreviewPlaying ||
        Math.Abs(transportViewModel.CursorBeat - selectedStartBeat) > 0.01)
    {
        throw new InvalidOperationException("Listen transport did not start from the selected start measure.");
    }
    var initialGuide = transportViewModel.NextNotesLabel;

    Thread.Sleep(900);
    transportViewModel.UpdateVisualClock();
    if (transportViewModel.NextNotesLabel == initialGuide)
    {
        throw new InvalidOperationException(
            $"Listen Next Notes remained frozen while the authoritative cursor advanced: {initialGuide}.");
    }
    await transportViewModel.TogglePreviewAsync();
    var pausedBeat = transportViewModel.CursorBeat;
    if (!transportViewModel.IsPreviewPaused || transportViewModel.IsPreviewPlaying)
    {
        throw new InvalidOperationException("Listen transport did not enter a real paused state.");
    }

    await transportViewModel.TogglePreviewAsync();
    if (!transportViewModel.IsScorePreviewPlaying || transportViewModel.CursorBeat + 0.01 < pausedBeat)
    {
        throw new InvalidOperationException("Listen transport did not resume from its paused beat.");
    }

    transportViewModel.StopPreview();
    if (transportViewModel.IsPreviewPlaying || transportViewModel.IsPreviewPaused ||
        Math.Abs(transportViewModel.CursorBeat - selectedStartBeat) > 0.01)
    {
        throw new InvalidOperationException("Stopping Listen did not reset to the selected start measure.");
    }

    await transportViewModel.SeekPreviewMeasureAsync(1);
    if (!transportViewModel.IsPreviewPaused || transportViewModel.CursorBeat <= selectedStartBeat)
    {
        throw new InvalidOperationException("Next-measure transport did not cue a later measure.");
    }
    if (transportViewModel.NextNotesLabel == initialGuide)
    {
        throw new InvalidOperationException("Next Notes did not refresh after a transport seek.");
    }
    var guideAfterSeek = transportViewModel.NextNotesLabel;
    transportViewModel.SetReadingMode(ScoreReadingMode.Continuous);
    if (transportViewModel.NextNotesLabel != guideAfterSeek)
        throw new InvalidOperationException("Changing reading mode selected a different musical occurrence.");
    transportViewModel.SetReadingMode(ScoreReadingMode.Page);

    await transportViewModel.RestartPreviewAsync();
    if (!transportViewModel.IsScorePreviewPlaying ||
        Math.Abs(transportViewModel.CursorBeat - selectedStartBeat) > 0.01)
    {
        throw new InvalidOperationException("Restart transport did not return to the selected start measure.");
    }
    transportViewModel.StopPreview();

    transportViewModel.FocusStartMeasure = 16;
    transportViewModel.FocusEndMeasure = 32;
    var firstPassGuide = transportViewModel.NextNotesLabel;
    var firstPassBeat = transportViewModel.CursorBeat;
    for (var occurrenceStep = 0; occurrenceStep < 17; occurrenceStep++)
        await transportViewModel.SeekPreviewMeasureAsync(1);
    if (transportViewModel.CursorBeat <= firstPassBeat + 60 ||
        transportViewModel.NextNotesLabel != firstPassGuide)
    {
        throw new InvalidOperationException(
            "Repeat-aware Next Notes did not follow the second occurrence of bars 16–32.");
    }
    transportViewModel.StopPreview();

    transportViewModel.LessonTempoPercent = 50;
    await transportViewModel.TogglePreviewAsync();
    var slowStartBeat = transportViewModel.CursorBeat;
    Thread.Sleep(220);
    transportViewModel.UpdateVisualClock();
    var expectedSlowAdvance = 0.220 * transportViewModel.EffectiveLessonTempoBpm / 60d;
    var actualSlowAdvance = transportViewModel.CursorBeat - slowStartBeat;
    if (Math.Abs(actualSlowAdvance - expectedSlowAdvance) > 0.08)
    {
        throw new InvalidOperationException(
            $"Listen visual clock drifted from effective tempo: expected {expectedSlowAdvance:0.###} beats, got {actualSlowAdvance:0.###}.");
    }
    transportViewModel.StopPreview();
}

var profileTestRoot = Path.Combine(Path.GetTempPath(), $"cadenza-profile-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(profileTestRoot);
try
{
    var profilePath = Path.Combine(profileTestRoot, "profile.json");
    using (var settingsViewModel = new MainWindowViewModel(profilePath))
    {
        settingsViewModel.LoadScore(path);
        settingsViewModel.MonitorVolume = 61;
        settingsViewModel.MidiMonitorEnabled = false;
        settingsViewModel.OverallVolume = 84;
        settingsViewModel.InstrumentalVolume = 73;
        settingsViewModel.InstrumentalMuted = true;
        settingsViewModel.MetronomeVolume = 57;
        settingsViewModel.MetronomeMuted = true;
        settingsViewModel.PracticeFullAccompanimentEnabled = true;
        settingsViewModel.PerformanceFullAccompanimentEnabled = false;
        settingsViewModel.OtherHandAccompanimentEnabled = false;
        settingsViewModel.OtherHandAccompanimentVolume = 49;
        settingsViewModel.LessonTempoPercent = 85;
        settingsViewModel.SetPracticeMode(PracticeMode.LeftHand);
        settingsViewModel.SetLessonMode(LessonMode.WaitForYou);
        settingsViewModel.SetReadingMode(ScoreReadingMode.Continuous);
        settingsViewModel.HintModeEnabled = true;
        settingsViewModel.NotationZoomPercent = 125;
        settingsViewModel.FocusStartMeasure = 1;
        settingsViewModel.FocusEndMeasure = 1;
        settingsViewModel.PedalEnabled = true;
        settingsViewModel.LatencyMilliseconds = 37;
        settingsViewModel.MetronomeEnabled = false;
        settingsViewModel.UseKeyboardSimulation = true;
    }

    var store = new UserProfileStore(profilePath);
    var profile = store.Load();
    profile.Settings!.PreferredMidiDeviceId = "9";
    profile.Settings.PreferredMidiDeviceName = "Saved Piano";
    store.Save(profile);
    var renamedIndexMatch = UserProfileStore.MatchPreferredMidiDevice(
        profile.Settings,
        [new MidiDeviceInfo("2", "Saved Piano")]);
    if (renamedIndexMatch is null || renamedIndexMatch.Id != "2" ||
        UserProfileStore.MatchPreferredMidiDevice(profile.Settings, []) is not null)
    {
        throw new InvalidOperationException("Preferred MIDI matching did not reconnect by name or preserve the absent-device state.");
    }

    using (var reloadedSettings = new MainWindowViewModel(profilePath))
    {
        reloadedSettings.LoadScore(path);
        if (reloadedSettings.MonitorVolume != 61 ||
            reloadedSettings.MidiMonitorEnabled ||
            reloadedSettings.OverallVolume != 84 ||
            reloadedSettings.InstrumentalVolume != 73 ||
            !reloadedSettings.InstrumentalMuted ||
            reloadedSettings.MetronomeVolume != 57 ||
            !reloadedSettings.MetronomeMuted ||
            !reloadedSettings.PracticeFullAccompanimentEnabled ||
            reloadedSettings.PerformanceFullAccompanimentEnabled ||
            reloadedSettings.OtherHandAccompanimentEnabled ||
            reloadedSettings.OtherHandAccompanimentVolume != 49 ||
            reloadedSettings.LessonTempoPercent != 85 ||
            reloadedSettings.SelectedMode != PracticeMode.LeftHand ||
            reloadedSettings.ReadingMode != ScoreReadingMode.Continuous ||
            !reloadedSettings.HintModeEnabled ||
            reloadedSettings.NotationZoomPercent != 125 ||
            reloadedSettings.FocusStartMeasure != 1 ||
            reloadedSettings.FocusEndMeasure != 1 ||
            !reloadedSettings.PedalEnabled ||
            reloadedSettings.LatencyMilliseconds != 37 ||
            reloadedSettings.MetronomeEnabled ||
            !reloadedSettings.UseKeyboardSimulation ||
            !reloadedSettings.PreferredMidiDeviceLabel.Contains("Saved Piano", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted user settings did not survive a view-model restart.");
        }

        reloadedSettings.SetPracticeMode(PracticeMode.BothHands);
        var measureOneGroups = score.GetPracticeGroups(PracticeMode.BothHands)
            .Where(group => group.MeasureNumber == "1")
            .ToArray();
        if (measureOneGroups.Length == 0 || !reloadedSettings.StartLesson())
            throw new InvalidOperationException("Could not start the focused persistence lesson.");
        Thread.Sleep(25);
        foreach (var group in measureOneGroups)
        {
            foreach (var note in group.MidiNotes)
            {
                reloadedSettings.SimulateNoteOn(note);
                reloadedSettings.SimulateNoteOff(note);
            }
        }
        if (!reloadedSettings.ResultsVisible || reloadedSettings.CompletedAttemptCount != 1)
            throw new InvalidOperationException("A completed lesson was not finalized into progress history.");
    }

    using (var progressReload = new MainWindowViewModel(profilePath))
    {
        progressReload.LoadScore(path);
        if (progressReload.CompletedAttemptCount != 1 ||
            !progressReload.DashboardProgressSummary.Contains("1 completed", StringComparison.Ordinal) ||
            progressReload.RecentAttemptLabel.Contains("Complete a lesson", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Completed progress did not survive an app restart.");
        }

        progressReload.UseKeyboardSimulation = true;
        progressReload.FocusStartMeasure = 2;
        progressReload.FocusEndMeasure = 2;
        progressReload.SetPracticeMode(PracticeMode.BothHands);
        if (!progressReload.StartLesson()) throw new InvalidOperationException("Could not start partial-run persistence check.");
        var oneNote = score.GetPracticeGroups(PracticeMode.BothHands)
            .First(group => group.MeasureNumber == "2").MidiNotes[0];
        progressReload.SimulateNoteOn(oneNote);
        progressReload.StopLesson();
    }

    using (var partialReload = new MainWindowViewModel(profilePath))
    {
        partialReload.LoadScore(path);
        if (partialReload.CompletedAttemptCount != 1)
            throw new InvalidOperationException("An interrupted partial lesson was incorrectly persisted as a completed attempt.");
    }

    var malformedPath = Path.Combine(profileTestRoot, "malformed.json");
    File.WriteAllText(malformedPath, "{ definitely not valid json");
    using var malformedProfileViewModel = new MainWindowViewModel(malformedPath);
    malformedProfileViewModel.LoadScore(path);
    if (malformedProfileViewModel.CompletedAttemptCount != 0)
        throw new InvalidOperationException("Malformed profile fallback did not load clean defaults.");
}
finally
{
    Directory.Delete(profileTestRoot, recursive: true);
}

using (var waitViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "wait.json")))
{
    var feedback = new List<LessonNoteFeedbackEvent>();
    var runStates = new List<LessonRunStateEvent>();
    waitViewModel.NoteFeedback += (_, message) => feedback.Add(message);
    waitViewModel.LessonRunStateChanged += (_, message) => runStates.Add(message);
    waitViewModel.LoadScore(path);
    waitViewModel.RefreshMidiDevices();
    var parallelHardwareAvailable = waitViewModel.MidiDevices.Count > 0;
    if (parallelHardwareAvailable)
        waitViewModel.SelectedMidiDevice = waitViewModel.MidiDevices[0];
    waitViewModel.UseKeyboardSimulation = true;
    if (parallelHardwareAvailable &&
        (!waitViewModel.InputSourceLabel.Contains("MIDI:", StringComparison.Ordinal) ||
         !waitViewModel.InputSourceLabel.Contains("computer keys", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException($"Parallel MIDI/fallback status was not exposed: {waitViewModel.InputSourceLabel}");
    }
    waitViewModel.FocusStartMeasure = 1;
    waitViewModel.FocusEndMeasure = 2;
    waitViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!waitViewModel.LessonStatusLabel.Contains("Ready", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Selecting Practice did not expose a visible ready state.");
    if (!waitViewModel.StartLesson()) throw new InvalidOperationException(waitViewModel.StatusMessage);
    if (parallelHardwareAvailable)
    {
        waitViewModel.UseKeyboardSimulation = false;
        if (!waitViewModel.InputSourceLabel.Contains("MIDI:", StringComparison.Ordinal) ||
            waitViewModel.InputSourceLabel.Contains("computer keys", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Turning fallback off during Practice disrupted MIDI state: {waitViewModel.InputSourceLabel}");
        }
        waitViewModel.UseKeyboardSimulation = true;
        if (!waitViewModel.InputSourceLabel.Contains("MIDI:", StringComparison.Ordinal) ||
            !waitViewModel.InputSourceLabel.Contains("computer keys", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Turning fallback back on during Practice replaced MIDI state: {waitViewModel.InputSourceLabel}");
        }
    }
    if (!waitViewModel.ExpectedLabel.Contains("B♭", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected-note guidance ignored the score's flat key spelling: {waitViewModel.ExpectedLabel}");
    }

    var firstGroup = score.GetPracticeGroups(PracticeMode.BothHands).First();
    var firstBeat = waitViewModel.CursorBeat;
    var firstWaitGuide = waitViewModel.NextNotesLabel;
    Thread.Sleep(120);
    waitViewModel.UpdateVisualClock();
    if (Math.Abs(waitViewModel.CursorBeat - firstBeat) > 0.0001)
    {
        throw new InvalidOperationException("Practice/Wait autoplayed instead of remaining anchored for input.");
    }
    waitViewModel.SimulateNoteOn(0);
    if (waitViewModel.CursorBeat != firstBeat || feedback.LastOrDefault()?.Kind != "extra")
    {
        throw new InvalidOperationException("A Practice wrong note advanced the cursor or failed to emit transient extra feedback.");
    }
    if (waitViewModel.MissedLabel != "0")
        throw new InvalidOperationException("Practice incorrectly counted a pending expected tone as missed.");
    var firstChordTone = firstGroup.MidiNotes[0];
    waitViewModel.SimulateNoteOn(firstChordTone);
    waitViewModel.SimulateNoteOff(firstChordTone);
    if (firstGroup.NoteCount > 1)
    {
        waitViewModel.SimulateNoteOn(firstChordTone);
        waitViewModel.SimulateNoteOff(firstChordTone);
    }
    foreach (var note in firstGroup.MidiNotes.Skip(1))
    {
        waitViewModel.SimulateNoteOn(note);
        Thread.Sleep(35);
        waitViewModel.SimulateNoteOff(note);
    }
    var expectedWaitExtras = firstGroup.NoteCount > 1 ? "2" : "1";
    if (waitViewModel.CorrectLabel != firstGroup.NoteCount.ToString() ||
        waitViewModel.ExtraLabel != expectedWaitExtras ||
        waitViewModel.MissedLabel != "0")
    {
        throw new InvalidOperationException($"Wait-for-you simulation did not score as expected: correct={waitViewModel.CorrectLabel}, extra={waitViewModel.ExtraLabel}.");
    }
    if (waitViewModel.CursorBeat <= firstBeat || feedback.LastOrDefault(message => message.Kind == "correct") is null)
    {
        throw new InvalidOperationException("Practice did not advance after the full expected note/chord was accepted.");
    }
    if (feedback.Any(message => message.RunGeneration <= 0 || message.EventId <= 0) ||
        feedback.Select(message => message.EventId).Distinct().Count() != feedback.Count ||
        feedback.Select(message => message.RunGeneration).Distinct().Count() != 1)
    {
        throw new InvalidOperationException("Practice feedback was not tagged with one run generation and unique event identities.");
    }
    foreach (var accepted in feedback.Where(message => message.Kind == "correct"))
    {
        var expectedStaff = firstGroup.Notes
            .Where(note => note.MidiNoteNumber == accepted.MidiNoteNumber)
            .Select(note => note.StaffNumber)
            .FirstOrDefault();
        if (expectedStaff == 0 || accepted.StaffNumber != expectedStaff)
            throw new InvalidOperationException($"Accepted feedback lost staff identity for MIDI {accepted.MidiNoteNumber}.");
    }
    if (waitViewModel.NextNotesLabel == firstWaitGuide)
    {
        foreach (var followingGroup in score.GetPracticeGroups(PracticeMode.BothHands)
                     .Where(group => group.MeasureNumber == "1")
                     .Skip(1)
                     .Take(8))
        {
            foreach (var note in followingGroup.MidiNotes)
            {
                waitViewModel.SimulateNoteOn(note);
                waitViewModel.SimulateNoteOff(note);
            }
            if (waitViewModel.NextNotesLabel != firstWaitGuide) break;
        }
    }
    if (waitViewModel.NextNotesLabel == firstWaitGuide)
        throw new InvalidOperationException("Practice Next Notes did not refresh while accepted onsets advanced.");
    if (!waitViewModel.HoldCategoryLabel.StartsWith("Hold ", StringComparison.Ordinal) ||
        waitViewModel.HoldCategoryLabel.EndsWith("—", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Simulation note-off did not close a hold observation.");
    }
    waitViewModel.StopLesson();
    if (runStates.LastOrDefault()?.State != "stopped" || waitViewModel.ResultsVisible)
    {
        throw new InvalidOperationException("Stopping Practice did not produce a clean non-results stopped state.");
    }
    if (!waitViewModel.StartLesson() || waitViewModel.CorrectLabel != "0" || waitViewModel.ExtraLabel != "0" ||
        Math.Abs(waitViewModel.CursorBeat - firstBeat) > 0.0001)
    {
        throw new InvalidOperationException("Restarting Practice did not reset counters and cursor predictably.");
    }
    waitViewModel.StopLesson();
    waitViewModel.SetPracticeMode(PracticeMode.LeftHand);
    waitViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!waitViewModel.StartLesson()) throw new InvalidOperationException("Could not start Left-hand feedback regression.");
    waitViewModel.SimulateNoteOn(36);
    waitViewModel.SimulateNoteOn(60);
    if (feedback.TakeLast(2).Any(message => message.Kind != "extra" || message.StaffNumber != 2))
        throw new InvalidOperationException("Left-hand wrong-note feedback did not retain bass-staff identity.");
    waitViewModel.StopLesson();

    waitViewModel.SetPracticeMode(PracticeMode.RightHand);
    if (!waitViewModel.StartLesson()) throw new InvalidOperationException("Could not start Right-hand feedback regression.");
    waitViewModel.SimulateNoteOn(60);
    waitViewModel.SimulateNoteOn(84);
    if (feedback.TakeLast(2).Any(message => message.Kind != "extra" || message.StaffNumber != 1))
        throw new InvalidOperationException("Right-hand wrong-note feedback did not retain treble-staff identity.");
    waitViewModel.StopLesson();
    waitViewModel.SetPracticeMode(PracticeMode.BothHands);

    waitViewModel.SetLessonMode(LessonMode.TimedPlay);
    if (!waitViewModel.StartLesson() ||
        Math.Abs(waitViewModel.CursorBeat - firstBeat) > 0.0001 ||
        runStates.LastOrDefault() is not { State: "started", Mode: LessonMode.TimedPlay } timedStart ||
        Math.Abs(timedStart.StartBeat - firstBeat) > 0.0001)
    {
        throw new InvalidOperationException("Practice-to-Performance mode switch reused stale lesson position.");
    }
    waitViewModel.StopLesson();
    waitViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!waitViewModel.StartLesson() ||
        Math.Abs(waitViewModel.CursorBeat - firstBeat) > 0.0001 ||
        runStates.LastOrDefault() is not { State: "started", Mode: LessonMode.WaitForYou } waitRestart ||
        Math.Abs(waitRestart.StartBeat - firstBeat) > 0.0001)
    {
        throw new InvalidOperationException("Performance-to-Practice mode switch reused stale lesson position.");
    }
    waitViewModel.StopLesson();
}

using (var blockedModeViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "blocked-mode.json")))
{
    blockedModeViewModel.LoadScore(path);
    blockedModeViewModel.UseKeyboardSimulation = true;
    blockedModeViewModel.FocusStartMeasure = 16;
    blockedModeViewModel.FocusEndMeasure = 40;
    blockedModeViewModel.SetLessonMode(LessonMode.TimedPlay);
    if (blockedModeViewModel.CanStartLesson ||
        await blockedModeViewModel.StartSelectedModeAsync() ||
        !blockedModeViewModel.LessonStatusLabel.Contains("bars 16–32", StringComparison.OrdinalIgnoreCase) ||
        !blockedModeViewModel.LessonStatusLabel.Contains("import warning badge", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "A blocked ambiguous ending did not produce a visible, actionable Performance start state.");
    }
    blockedModeViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!blockedModeViewModel.CanStartLesson ||
        !await blockedModeViewModel.StartSelectedModeAsync() ||
        !blockedModeViewModel.StartLessonReason.Contains("guided Practice", StringComparison.OrdinalIgnoreCase) ||
        !blockedModeViewModel.StartLessonReason.Contains("explicit repeat", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Ambiguous volta warnings prevented unscored guided Practice from following explicit repeats.");
    }
    blockedModeViewModel.StopLesson();
}

var threeToneGroup = score.GetPracticeGroups(PracticeMode.BothHands)
    .FirstOrDefault(group => group.MidiNotes.Count >= 3 &&
        int.TryParse(group.MeasureNumber, out var measure) &&
        measure <= 15);
if (threeToneGroup is null || !int.TryParse(threeToneGroup.MeasureNumber, out var threeToneMeasure))
    throw new InvalidOperationException("The supplied score did not expose an assessable three-tone chord for feedback regression.");

using (var chordFeedbackViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "three-tone-feedback.json")))
{
    var chordFeedback = new List<LessonNoteFeedbackEvent>();
    chordFeedbackViewModel.NoteFeedback += (_, message) => chordFeedback.Add(message);
    chordFeedbackViewModel.LoadScore(path);
    chordFeedbackViewModel.UseKeyboardSimulation = true;
    chordFeedbackViewModel.FocusStartMeasure = threeToneMeasure;
    chordFeedbackViewModel.FocusEndMeasure = threeToneMeasure;
    chordFeedbackViewModel.SetPracticeMode(PracticeMode.BothHands);
    chordFeedbackViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!chordFeedbackViewModel.StartLesson())
        throw new InvalidOperationException(chordFeedbackViewModel.StatusMessage);

    var groupsInMeasure = score.GetPracticeGroups(PracticeMode.BothHands)
        .Where(group => group.MeasureNumber == threeToneGroup.MeasureNumber)
        .ToArray();
    foreach (var preceding in groupsInMeasure.TakeWhile(group => group.OnsetBeats < threeToneGroup.OnsetBeats))
    {
        foreach (var note in preceding.MidiNotes)
        {
            chordFeedbackViewModel.SimulateNoteOn(note);
            chordFeedbackViewModel.SimulateNoteOff(note);
        }
    }

    var correctBeforeChord = int.Parse(chordFeedbackViewModel.CorrectLabel);
    var extraBeforeChord = int.Parse(chordFeedbackViewModel.ExtraLabel);
    var first = threeToneGroup.MidiNotes[0];
    var second = threeToneGroup.MidiNotes[1];
    var final = threeToneGroup.MidiNotes[2];
    chordFeedbackViewModel.SimulateNoteOn(first);
    chordFeedbackViewModel.SimulateNoteOff(first);
    chordFeedbackViewModel.SimulateNoteOn(second);
    chordFeedbackViewModel.SimulateNoteOff(second);
    if (int.Parse(chordFeedbackViewModel.CorrectLabel) != correctBeforeChord + 2 ||
        Math.Abs(chordFeedbackViewModel.CursorBeat - threeToneGroup.OnsetBeats) > 0.0001)
    {
        throw new InvalidOperationException(
            "A three-tone Practice chord did not retain two accepted tones while leaving the final tone pending.");
    }

    chordFeedbackViewModel.SimulateNoteOn(first);
    chordFeedbackViewModel.SimulateNoteOff(first);
    if (int.Parse(chordFeedbackViewModel.CorrectLabel) != correctBeforeChord + 2 ||
        int.Parse(chordFeedbackViewModel.ExtraLabel) != extraBeforeChord + 1 ||
        Math.Abs(chordFeedbackViewModel.CursorBeat - threeToneGroup.OnsetBeats) > 0.0001)
    {
        throw new InvalidOperationException(
            "A duplicate accepted chord tone stacked correctness or advanced the pending Practice chord.");
    }

    chordFeedbackViewModel.SimulateNoteOn(final);
    chordFeedbackViewModel.SimulateNoteOff(final);
    var chordCorrectEvents = chordFeedback
        .Where(message => message.Kind == "correct" &&
                          Math.Abs(message.Beat - threeToneGroup.OnsetBeats) < 0.0001)
        .ToArray();
    if (int.Parse(chordFeedbackViewModel.CorrectLabel) != correctBeforeChord + 3 ||
        chordCorrectEvents.Length != 3 ||
        chordCorrectEvents.Select(message => message.MidiNoteNumber).Distinct().Count() != 3 ||
        chordFeedbackViewModel.MissedLabel != "0")
    {
        throw new InvalidOperationException(
            "Three-tone Practice completion did not emit exactly one accepted event per chord tone.");
    }
    chordFeedbackViewModel.StopLesson();
    Console.WriteLine(
        $"Three-tone chord regression passed: accepted=3, duplicateExtra=1, pendingMisses=0, measure={threeToneMeasure}.");
}

using (var fullRepeatWaitViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "full-repeat-wait.json")))
{
    var repeatStates = new List<LessonRunStateEvent>();
    var acceptedOccurrences = new HashSet<int>();
    fullRepeatWaitViewModel.LessonRunStateChanged += (_, message) => repeatStates.Add(message);
    fullRepeatWaitViewModel.NoteFeedback += (_, message) =>
    {
        if (message.Kind == "correct") acceptedOccurrences.Add(message.OccurrenceIndex);
    };
    fullRepeatWaitViewModel.LoadScore(path);
    fullRepeatWaitViewModel.UseKeyboardSimulation = true;
    fullRepeatWaitViewModel.FocusStartMeasure = 1;
    fullRepeatWaitViewModel.FocusEndMeasure = score.MeasureCount;
    fullRepeatWaitViewModel.SetPracticeMode(PracticeMode.BothHands);
    fullRepeatWaitViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!fullRepeatWaitViewModel.CanStartLesson || !fullRepeatWaitViewModel.StartLesson())
        throw new InvalidOperationException(
            $"Full repeat-aware guided Practice could not start: {fullRepeatWaitViewModel.StartLessonReason}");

    var fullGroups = score.GetPracticeGroups(PracticeMode.BothHands);
    var requiredBoundaries = new[] { 126d, 226d, 298d };
    foreach (var boundary in requiredBoundaries)
    {
        if (!fullGroups.Any(group => Math.Abs(group.OnsetBeats - boundary) < 0.001))
            throw new InvalidOperationException($"Expanded Practice groups do not contain repeat-pass boundary beat {boundary:0}.");
    }

    for (var index = 0; index < fullGroups.Count; index++)
    {
        var group = fullGroups[index];
        if (Math.Abs(group.OnsetBeats - 126d) < 0.001)
            fullRepeatWaitViewModel.SetReadingMode(ScoreReadingMode.Continuous);
        else if (Math.Abs(group.OnsetBeats - 226d) < 0.001)
            fullRepeatWaitViewModel.SetReadingMode(ScoreReadingMode.Page);
        else if (Math.Abs(group.OnsetBeats - 298d) < 0.001)
            fullRepeatWaitViewModel.SetReadingMode(ScoreReadingMode.Continuous);

        foreach (var note in group.MidiNotes)
        {
            fullRepeatWaitViewModel.SimulateNoteOn(note);
            fullRepeatWaitViewModel.SimulateNoteOff(note);
        }

        if (index < fullGroups.Count - 1)
        {
            if (!fullRepeatWaitViewModel.IsLessonActive ||
                fullRepeatWaitViewModel.CursorBeat <= group.OnsetBeats)
            {
                throw new InvalidOperationException(
                    $"Practice ended or moved backward after beat {group.OnsetBeats:0.###}, before the terminal occurrence.");
            }
        }
    }

    if (fullRepeatWaitViewModel.IsLessonActive ||
        repeatStates.Count(state => state.State == "completed") != 1 ||
        int.Parse(fullRepeatWaitViewModel.CorrectLabel) != fullGroups.Sum(group => group.NoteCount) ||
        fullRepeatWaitViewModel.MissedLabel != "0" ||
        fullRepeatWaitViewModel.CompletedAttemptCount != 0 ||
        !requiredBoundaries.All(boundary =>
            score.PerformanceMeasures.Any(occurrence => Math.Abs(occurrence.PerformanceStartBeat - boundary) < 0.001)) ||
        acceptedOccurrences.Count < 2)
    {
        throw new InvalidOperationException(
            "Full 306-beat guided Practice did not continue through every explicit repeat occurrence and terminate only once at the end.");
    }
    Console.WriteLine(
        $"Full repeat Practice regression passed: groups={fullGroups.Count}, beats={score.TotalBeats:0}, " +
        $"boundaries={string.Join(',', requiredBoundaries.Select(boundary => boundary.ToString("0")))}, " +
        $"completedEvents=1, savedAttempts=0.");
}

using (var defaultRangeViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "default-range.json")))
{
    defaultRangeViewModel.LoadScore(path);
    defaultRangeViewModel.UseKeyboardSimulation = true;
    defaultRangeViewModel.FocusStartMeasure = 1;
    defaultRangeViewModel.FocusEndMeasure = score.MeasureCount;
    defaultRangeViewModel.SetLessonMode(LessonMode.WaitForYou);
    if (!defaultRangeViewModel.PrepareDefaultAssessableRange() ||
        defaultRangeViewModel.FocusStartMeasure != 1 ||
        defaultRangeViewModel.FocusEndMeasure != score.MeasureCount ||
        !await defaultRangeViewModel.StartSelectedModeAsync() ||
        !defaultRangeViewModel.IsLessonActive)
    {
        throw new InvalidOperationException(
            $"Default guided Practice did not preserve the full explicit-repeat sequence: " +
            $"{defaultRangeViewModel.FocusStartMeasure}-{defaultRangeViewModel.FocusEndMeasure}.");
    }
    defaultRangeViewModel.StopLesson();
    defaultRangeViewModel.SetLessonMode(LessonMode.TimedPlay);
    defaultRangeViewModel.FocusStartMeasure = 1;
    defaultRangeViewModel.FocusEndMeasure = score.MeasureCount;
    if (!defaultRangeViewModel.PrepareDefaultAssessableRange() ||
        defaultRangeViewModel.FocusStartMeasure != 1 ||
        defaultRangeViewModel.FocusEndMeasure != 15)
    {
        throw new InvalidOperationException(
            $"Default Performance range was not repaired to the safe assessed section: " +
            $"{defaultRangeViewModel.FocusStartMeasure}-{defaultRangeViewModel.FocusEndMeasure}.");
    }
}

foreach (var from in Enum.GetValues<LessonMode>())
{
    foreach (var to in Enum.GetValues<LessonMode>())
    {
        if (from == to) continue;
        var profile = Path.Combine(transientProfileRoot, $"switch-{from}-{to}.json");
        using var switchViewModel = new MainWindowViewModel(profile);
        var states = new List<LessonRunStateEvent>();
        switchViewModel.LessonRunStateChanged += (_, state) => states.Add(state);
        switchViewModel.LoadScore(path);
        switchViewModel.UseKeyboardSimulation = true;
        switchViewModel.FocusStartMeasure = 1;
        switchViewModel.FocusEndMeasure = 1;

        if (!await switchViewModel.SwitchLessonModeAsync(from))
            throw new InvalidOperationException($"Could not start {from} before switching to {to}: {switchViewModel.StatusMessage}");
        if (!await switchViewModel.SwitchLessonModeAsync(to))
            throw new InvalidOperationException($"Could not atomically switch {from} to {to}: {switchViewModel.StatusMessage}");
        if (switchViewModel.SelectedLessonMode != to ||
            switchViewModel.ResultsVisible ||
            states.Any(state => state.State == "completed"))
        {
            throw new InvalidOperationException($"Mode switch {from} to {to} retained stale/completed lesson state.");
        }
        if (to == LessonMode.Listen)
        {
            if (!switchViewModel.IsScorePreviewPlaying || switchViewModel.IsLessonActive)
                throw new InvalidOperationException($"Mode switch {from} to Listen did not leave only preview playback active.");
        }
        else if (!switchViewModel.IsLessonActive ||
                 switchViewModel.IsPreviewPlaying ||
                 switchViewModel.IsPreviewPaused ||
                 switchViewModel.CorrectLabel != "0" ||
                 switchViewModel.ExtraLabel != "0")
        {
            throw new InvalidOperationException($"Mode switch {from} to {to} retained stale audio, transport, or score state.");
        }

        switchViewModel.StopTransport();
        using var reloaded = new MainWindowViewModel(profile);
        reloaded.LoadScore(path);
        if (reloaded.CompletedAttemptCount != 0)
            throw new InvalidOperationException($"Partial {from} attempt was persisted while switching to {to}.");
    }
}

using (var pausedRepeatSwitch = new MainWindowViewModel(Path.Combine(transientProfileRoot, "paused-repeat-switch.json")))
{
    pausedRepeatSwitch.LoadScore(path);
    pausedRepeatSwitch.UseKeyboardSimulation = true;
    pausedRepeatSwitch.FocusStartMeasure = 49;
    pausedRepeatSwitch.FocusEndMeasure = 50;
    if (!await pausedRepeatSwitch.SwitchLessonModeAsync(LessonMode.Listen))
        throw new InvalidOperationException(pausedRepeatSwitch.StatusMessage);
    await pausedRepeatSwitch.SeekDisplayMeasureAsync(1);
    await pausedRepeatSwitch.TogglePreviewAsync();
    if (!pausedRepeatSwitch.IsPreviewPaused)
        throw new InvalidOperationException("Repeat-position Listen setup did not enter paused state.");
    if (!await pausedRepeatSwitch.SwitchLessonModeAsync(LessonMode.WaitForYou) ||
        !pausedRepeatSwitch.IsLessonActive ||
        pausedRepeatSwitch.IsPreviewPaused ||
        pausedRepeatSwitch.IsPreviewPlaying)
    {
        throw new InvalidOperationException("Paused repeat-position Listen did not switch cleanly to Practice.");
    }
    pausedRepeatSwitch.StopLesson();
}

using (var continuousNavigation = new MainWindowViewModel(Path.Combine(transientProfileRoot, "continuous-navigation.json")))
{
    continuousNavigation.LoadScore(path);
    continuousNavigation.SetReadingMode(ScoreReadingMode.Continuous);
    continuousNavigation.FocusStartMeasure = 20;
    continuousNavigation.FocusEndMeasure = 22;
    if (!await continuousNavigation.SwitchLessonModeAsync(LessonMode.Listen))
        throw new InvalidOperationException(continuousNavigation.StatusMessage);
    var firstContinuousBeat = continuousNavigation.CursorBeat;
    await continuousNavigation.SeekDisplayMeasureAsync(1);
    if (continuousNavigation.CursorBeat <= firstContinuousBeat || !continuousNavigation.IsScorePreviewPlaying)
        throw new InvalidOperationException("Continuous next-measure navigation did not cue and synchronize playback.");
    continuousNavigation.FocusStartMeasure = 21;
    if (continuousNavigation.IsPreviewPlaying || continuousNavigation.IsLessonActive ||
        continuousNavigation.CursorBeat < firstContinuousBeat)
    {
        throw new InvalidOperationException("Continuous range selection did not stop stale playback and reset coherently.");
    }
}

var midiPath = Path.ChangeExtension(path, ".mid");
if (File.Exists(midiPath))
{
    var midiReference = new MidiFileImporter().Import(midiPath);
    if (midiReference.Notes.Count == 0) throw new InvalidOperationException("The supplied MIDI reference contained no parsed note events.");
    var selectedTracks = midiReference.Tracks.Where(track => !track.IsPercussion && track.NoteCount > 0).Select(track => track.Index).ToHashSet();
    var midiPreview = await audioService.BuildMidiPreviewAsync(midiReference, selectedTracks, includeMetronome: false, CancellationToken.None);
    if (midiPreview.Length < 44) throw new InvalidOperationException("MIDI reference preview did not produce WAV audio.");
    Console.WriteLine($"MIDI reference: tracks={midiReference.Tracks.Count}, notes={midiReference.Notes.Count}, bpm={midiReference.TempoBpm:0.##}, beats={midiReference.TotalBeats:0.##}");

    using var referenceViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "reference.json"));
    referenceViewModel.LoadScore(path);
    referenceViewModel.LoadMidiReference(midiPath);
    if (!referenceViewModel.HasMidiReference || !referenceViewModel.CanPlayMidiReference ||
        referenceViewModel.MidiListenTracks.Count == 0)
    {
        throw new InvalidOperationException("Imported MIDI reference did not expose selectable, playable track state.");
    }
    foreach (var track in referenceViewModel.MidiListenTracks) track.IsSelected = false;
    if (referenceViewModel.CanPlayMidiReference)
    {
        throw new InvalidOperationException("MIDI reference playback remained enabled with no selected tracks.");
    }
    referenceViewModel.MidiListenTracks[0].IsSelected = true;
    if (!referenceViewModel.CanPlayMidiReference)
    {
        throw new InvalidOperationException("MIDI reference playback did not re-enable after selecting a track.");
    }

    var pdfPath = Path.ChangeExtension(path, ".pdf");
    if (File.Exists(pdfPath))
    {
        referenceViewModel.LoadPdfReference(pdfPath);
        if (!referenceViewModel.HasPdfReference ||
            !referenceViewModel.PdfReferenceLabel.Contains(Path.GetFileName(pdfPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PDF review import did not expose an openable reference state.");
        }
    }
}

using (var performanceTaxonomy = new MainWindowViewModel(Path.Combine(transientProfileRoot, "performance-taxonomy.json")))
{
    performanceTaxonomy.LoadScore(path);
    performanceTaxonomy.UseKeyboardSimulation = true;
    performanceTaxonomy.FocusStartMeasure = 1;
    performanceTaxonomy.FocusEndMeasure = 1;
    performanceTaxonomy.SetPracticeMode(PracticeMode.BothHands);
    performanceTaxonomy.SetLessonMode(LessonMode.TimedPlay);
    if (!performanceTaxonomy.StartLesson())
        throw new InvalidOperationException(performanceTaxonomy.StatusMessage);

    var firstPerformanceGroup = score.GetPracticeGroups(PracticeMode.BothHands).First();
    var acceptedTone = firstPerformanceGroup.MidiNotes[0];
    performanceTaxonomy.SimulateNoteOn(acceptedTone);
    performanceTaxonomy.SimulateNoteOff(acceptedTone);
    performanceTaxonomy.SimulateNoteOn(acceptedTone);
    performanceTaxonomy.SimulateNoteOff(acceptedTone);
    if (performanceTaxonomy.CorrectLabel != "1" ||
        performanceTaxonomy.ExtraLabel != "1" ||
        performanceTaxonomy.MissedLabel != "0")
    {
        throw new InvalidOperationException(
            $"Performance duplicate taxonomy failed: correct={performanceTaxonomy.CorrectLabel}, " +
            $"missed={performanceTaxonomy.MissedLabel}, extra={performanceTaxonomy.ExtraLabel}.");
    }
    performanceTaxonomy.StopLesson();
}

using (var performanceDeadline = new MainWindowViewModel(Path.Combine(transientProfileRoot, "performance-deadline.json")))
{
    performanceDeadline.LoadScore(path);
    performanceDeadline.UseKeyboardSimulation = true;
    performanceDeadline.FocusStartMeasure = 1;
    performanceDeadline.FocusEndMeasure = 1;
    performanceDeadline.SetPracticeMode(PracticeMode.RightHand);
    performanceDeadline.SetLessonMode(LessonMode.TimedPlay);
    if (!performanceDeadline.StartLesson())
        throw new InvalidOperationException(performanceDeadline.StatusMessage);

    var firstPerformanceGroup = score.GetPracticeGroups(PracticeMode.RightHand).First();
    Thread.Sleep(560);
    performanceDeadline.SimulateNoteOn(0);
    performanceDeadline.SimulateNoteOff(0);
    if (performanceDeadline.CorrectLabel != "0" ||
        performanceDeadline.MissedLabel != firstPerformanceGroup.NoteCount.ToString() ||
        performanceDeadline.ExtraLabel != "1")
    {
        throw new InvalidOperationException(
            $"Performance deadline taxonomy failed: expected misses={firstPerformanceGroup.NoteCount}, " +
            $"actual correct={performanceDeadline.CorrectLabel}, missed={performanceDeadline.MissedLabel}, " +
            $"extra={performanceDeadline.ExtraLabel}.");
    }
    performanceDeadline.StopLesson();
}

using (var timedViewModel = new MainWindowViewModel(Path.Combine(transientProfileRoot, "timed.json")))
{
    var timedStates = new List<LessonRunStateEvent>();
    timedViewModel.LessonRunStateChanged += (_, message) => timedStates.Add(message);
    timedViewModel.LoadScore(path);
    timedViewModel.RefreshMidiDevices();
    timedViewModel.UseKeyboardSimulation = true;
    timedViewModel.FocusStartMeasure = 1;
    timedViewModel.FocusEndMeasure = 1;
    timedViewModel.SetPracticeMode(PracticeMode.RightHand);
    timedViewModel.SetLessonMode(LessonMode.TimedPlay);
    if (!await timedViewModel.StartSelectedModeAsync()) throw new InvalidOperationException(timedViewModel.StatusMessage);
    var timedStartBeat = timedViewModel.CursorBeat;
    Thread.Sleep(120);
    timedViewModel.UpdateVisualClock();
    if (timedViewModel.CursorBeat <= timedStartBeat)
    {
        throw new InvalidOperationException("Performance did not advance its automatic timed timeline.");
    }

    var cursorBeforeTimedInput = timedViewModel.CursorBeat;
    foreach (var note in score.GetPracticeGroups(PracticeMode.RightHand).First().MidiNotes) timedViewModel.SimulateNoteOn(note);
    if (timedViewModel.CorrectLabel == "0")
    {
        throw new InvalidOperationException("Timed-play simulation did not accept the on-time first group.");
    }
    if (timedViewModel.CursorBeat - cursorBeforeTimedInput > 0.25)
    {
        throw new InvalidOperationException(
            "A correct Performance note jumped the authoritative playhead to the next expected onset.");
    }
    timedViewModel.StopLesson();
    if (timedStates.FirstOrDefault()?.State != "started" || timedStates.LastOrDefault()?.State != "stopped")
    {
        throw new InvalidOperationException("Timed lesson start/stop lifecycle events were not coherent.");
    }
}

Console.WriteLine($"WinMM discovery: supported={midiSnapshot.IsDiscoverySupported}, apiCount={midiSnapshot.ApiDeviceCount?.ToString() ?? "n/a"}, devices={midiSnapshot.Devices.Count}, apiMessage={midiSnapshot.ApiMessage ?? "none"}");
Console.WriteLine($"WinMM devices: {string.Join(" | ", midiSnapshot.Devices.Select(device => $"{device.Id}:{device.Name}"))}");
Console.WriteLine(
    $"Simulation smoke passed: previewBytes={preview.Length}, leftGroups={score.GetPracticeGroups(PracticeMode.LeftHand).Count}, " +
    $"rightGroups={score.GetPracticeGroups(PracticeMode.RightHand).Count}, monitorVelocity64At100={MidiOutSynthService.MapMonitorVelocity(64, 100)}.");
Directory.Delete(transientProfileRoot, recursive: true);
