using System.Windows.Input;
using PianoPractice.Desktop;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

if (args.Length > 1)
    throw new ArgumentException("Pass at most one MusicXML or MXL fixture path.");

var fixtureRoot = FindFixtureRoot();
var scorePath = Path.GetFullPath(args.Length == 1
    ? args[0]
    : Path.Combine(fixtureRoot, "cadenza-timeline.musicxml"));
var midiPath = Path.Combine(fixtureRoot, "cadenza-reference.mid");
var transientProfileRoot = Path.Combine(
    Path.GetTempPath(),
    $"cadenza-simulation-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(transientProfileRoot);

try
{
    var score = new MusicXmlImporter().Import(scorePath);
    AssertTimelineContract(score);
    AssertTimelineMappings(score);
    AssertTempoAndMeterClock(score);
    AssertKeyboardMapping();

    using var audioService = new PianoAudioService();
    await AssertDeterministicAudioAsync(audioService, score);
    await AssertMidiReferenceAsync(audioService, midiPath);
    AssertViewModelImport(scorePath, midiPath, transientProfileRoot);
    await AssertFullGuidedPerformanceAsync(score, scorePath, transientProfileRoot);

    Console.WriteLine(
        $"Simulation regression passed: writtenBeats={score.TotalBeats:0.###}, " +
        $"performanceBeats={score.TotalPerformanceBeats:0.###}, " +
        $"occurrences={score.PerformanceMeasures.Count}, " +
        $"groups={score.GetPracticeGroups(PracticeMode.BothHands).Count}, hardwareRequired=false.");
}
finally
{
    var fullTempRoot = Path.GetFullPath(Path.GetTempPath());
    var fullProfileRoot = Path.GetFullPath(transientProfileRoot);
    if (fullProfileRoot.StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(fullProfileRoot).StartsWith("cadenza-simulation-smoke-", StringComparison.Ordinal))
    {
        try { Directory.Delete(fullProfileRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

return;

static void AssertTimelineContract(ScoreDocument score)
{
    Require(score.MeasureCount == 5, $"Expected 5 written measures, got {score.MeasureCount}.");
    Require(Math.Abs(score.TotalBeats - 18d) < 0.001,
        $"Expected 18 written beats, got {score.TotalBeats:0.###}.");
    Require(Math.Abs(score.TotalPerformanceBeats - 26d) < 0.001,
        $"Expected 26 performance beats, got {score.TotalPerformanceBeats:0.###}.");
    Require(score.PerformanceMeasures.Select(item => item.MeasureNumber)
        .SequenceEqual(["1", "2", "3", "1", "2", "4", "5"]),
        "The repeat/ending performance order differs from 1,2,3,1,2,4,5.");
    Require(score.PerformanceMeasures.Select(item => item.PerformanceStartBeat)
        .SequenceEqual([0d, 4d, 8d, 12d, 16d, 20d, 23d]),
        "Performance occurrence boundaries differ from the fixture contract.");
    Require(score.GetPracticeGroups(PracticeMode.BothHands).Count == 15,
        "The expanded fixture should contain 15 chronological practice groups.");
    Require(score.GetPracticeGroups(PracticeMode.LeftHand).Count > 0 &&
            score.GetPracticeGroups(PracticeMode.RightHand).Count > 0,
        "The fixture must exercise both staff-specific practice timelines.");
    Require(score.ValidationWarnings.Count == 0,
        $"The fixture produced {score.ValidationWarnings.Count} validation warning(s).");
}

static void AssertTimelineMappings(ScoreDocument score)
{
    foreach (var occurrence in score.PerformanceMeasures)
    {
        var probe = occurrence.PerformanceStartBeat + occurrence.DurationBeats / 2d;
        var resolved = score.OccurrenceAtBeat(probe);
        Require(resolved?.OccurrenceIndex == occurrence.OccurrenceIndex,
            $"Performance beat {probe:0.###} resolved to the wrong occurrence.");
        var expectedSourceBeat = occurrence.SourceStartBeat + occurrence.DurationBeats / 2d;
        Require(Math.Abs(score.PerformanceToSourceBeat(probe) - expectedSourceBeat) < 0.001,
            $"Performance beat {probe:0.###} did not map to written beat {expectedSourceBeat:0.###}.");
    }

    Require(Math.Abs(score.SourceToPerformanceBeat(0, preferredOccurrence: 0) - 0d) < 0.001,
        "Written beat zero did not map to the first repeat pass.");
    Require(Math.Abs(score.SourceToPerformanceBeat(0, preferredOccurrence: 3) - 12d) < 0.001,
        "Written beat zero did not map to the second repeat pass.");
    Require(Math.Abs(score.PerformanceToSourceBeat(20) - 12d) < 0.001,
        "The second-ending jump did not map performance beat 20 to written beat 12.");
}

static void AssertTempoAndMeterClock(ScoreDocument score)
{
    var initialMeter = score.MeterAtBeat(0);
    var changedMeter = score.MeterAtBeat(20);
    Require(initialMeter.Beats == 4 && initialMeter.BeatType == 4,
        "The initial performance meter is not 4/4.");
    Require(changedMeter.Beats == 3 && changedMeter.BeatType == 4,
        "The second ending did not apply the 3/4 meter at performance beat 20.");

    var totalSeconds = score.SecondsAtPerformanceBeat(score.TotalPerformanceBeats);
    Require(Math.Abs(totalSeconds - 14d) < 0.01,
        $"Expected a 14-second tempo map, got {totalSeconds:0.###} seconds.");
    Require(Math.Abs(score.PerformanceBeatAtSeconds(totalSeconds) - score.TotalPerformanceBeats) < 0.001,
        "The performance beat/seconds conversion did not round-trip at score end.");
}

static async Task AssertDeterministicAudioAsync(PianoAudioService audioService, ScoreDocument score)
{
    var metronomePreview = await audioService.BuildPreviewAsync(
        score, includeMetronome: true, 0, 4, score.TempoBpm, CancellationToken.None);
    var fullTempo = await audioService.BuildPreviewAsync(
        score, includeMetronome: false, 0, 4, score.TempoBpm, CancellationToken.None);
    var halfTempo = await audioService.BuildPreviewAsync(
        score, includeMetronome: false, 0, 4, score.TempoBpm * 0.5, CancellationToken.None);
    Require(IsWave(metronomePreview), "The metronome preview did not produce a RIFF/WAV payload.");
    Require(IsWave(fullTempo), "The score preview did not produce a RIFF/WAV payload.");
    Require(IsWave(halfTempo), "The half-tempo preview did not produce a RIFF/WAV payload.");
    Require(halfTempo.Length > fullTempo.Length * 1.5,
        "The synthesized preview did not honor the selected effective tempo.");
}

static async Task AssertMidiReferenceAsync(PianoAudioService audioService, string midiPath)
{
    var midi = new MidiFileImporter().Import(midiPath);
    Require(midi.Format == 0 && midi.TicksPerQuarter == 480,
        "The valid MIDI fixture header was not preserved.");
    Require(midi.Tracks.Count == 1 && midi.Notes.Count == 1,
        "The valid MIDI fixture did not produce one track and one note.");
    var selectedTracks = midi.Tracks.Select(track => track.Index).ToHashSet();
    var preview = await audioService.BuildMidiPreviewAsync(
        midi, selectedTracks, includeMetronome: false, CancellationToken.None);
    Require(IsWave(preview), "The MIDI reference did not synthesize a RIFF/WAV payload.");
}

static void AssertViewModelImport(string scorePath, string midiPath, string profileRoot)
{
    using var viewModel = new MainWindowViewModel(Path.Combine(profileRoot, "import.json"));
    viewModel.LoadScore(scorePath);
    Require(viewModel.CurrentScore is not null && viewModel.CurrentScore.PerformanceMeasures.Count == 7,
        "The application view model did not retain the authoritative score timeline.");
    viewModel.LoadMidiReference(midiPath);
    Require(viewModel.HasMidiReference && viewModel.MidiListenTracks.Count == 1,
        "The application view model did not expose the valid MIDI reference.");
    Require(viewModel.CanPlayMidiReference,
        "The melodic MIDI fixture was not selected for reference preview by default.");
    viewModel.MidiListenTracks[0].IsSelected = false;
    Require(!viewModel.CanPlayMidiReference,
        "MIDI reference preview remained enabled with no selected track.");
}

static async Task AssertFullGuidedPerformanceAsync(ScoreDocument score, string scorePath, string profileRoot)
{
    using var viewModel = new MainWindowViewModel(Path.Combine(profileRoot, "guided.json"));
    var states = new List<LessonRunStateEvent>();
    var acceptedOccurrences = new HashSet<int>();
    viewModel.LessonRunStateChanged += (_, state) => states.Add(state);
    viewModel.NoteFeedback += (_, feedback) =>
    {
        if (feedback.Kind == "correct") acceptedOccurrences.Add(feedback.OccurrenceIndex);
    };

    viewModel.LoadScore(scorePath);
    viewModel.UseKeyboardSimulation = true;
    DisableTransportShortcutsForSimulation(viewModel);
    viewModel.FocusStartMeasure = 1;
    viewModel.FocusEndMeasure = score.MeasureCount;
    viewModel.SetPracticeMode(PracticeMode.BothHands);
    viewModel.SetLessonMode(LessonMode.WaitForYou);
    Require(viewModel.CanStartLesson && viewModel.StartLesson(),
        $"Guided Practice could not start: {viewModel.StartLessonReason}");

    var groups = score.GetPracticeGroups(PracticeMode.BothHands);
    foreach (var group in groups)
    {
        foreach (var note in group.MidiNotes)
        {
            viewModel.SimulateNoteOn(note);
            viewModel.SimulateNoteOff(note);
        }
    }

    for (var attempt = 0; attempt < 30 && viewModel.IsLessonActive; attempt++)
        await Task.Delay(100);

    Require(!viewModel.IsLessonActive, "Guided Practice did not stop at the performance end.");
    Require(states.Count(state => state.State == "completed") == 1,
        $"Guided Practice did not emit exactly one completion event: {string.Join(',', states.Select(state => state.State))}.");
    Require(int.Parse(viewModel.CorrectLabel) == groups.Sum(group => group.NoteCount),
        "Guided Practice did not accept every expected note exactly once.");
    Require(viewModel.MissedLabel == "0" && viewModel.ExtraLabel == "0",
        "Guided Practice recorded a missed or extra note for the exact fixture performance.");
    Require(acceptedOccurrences.Contains(0) && acceptedOccurrences.Contains(3),
        "Guided Practice did not preserve distinct first- and second-pass occurrence identities.");
}

static void DisableTransportShortcutsForSimulation(MainWindowViewModel viewModel)
{
    // The fixture deliberately uses common keyboard pitches such as C4. Keep
    // global MIDI transport bindings from interpreting those musical notes as
    // UI commands during this deterministic performance simulation.
    viewModel.MidiShortcutListenNote = 1;
    viewModel.MidiShortcutTogglePlayNote = 2;
    viewModel.MidiShortcutRestartNote = 3;
    viewModel.MidiShortcutPreviousMeasureNote = 4;
    viewModel.MidiShortcutNextMeasureNote = 5;
    viewModel.MidiShortcutPreviousPageNote = 6;
    viewModel.MidiShortcutNextPageNote = 7;
    viewModel.MidiShortcutDismissResultsNote = 8;
    viewModel.MidiShortcutRepeatResultsNote = 9;
}

static void AssertKeyboardMapping()
{
    var expected = new[]
    {
        (Key.A, 60), (Key.W, 61), (Key.S, 62), (Key.E, 63), (Key.D, 64),
        (Key.F, 65), (Key.T, 66), (Key.G, 67), (Key.Y, 68), (Key.H, 69),
        (Key.U, 70), (Key.J, 71), (Key.K, 72), (Key.O, 73), (Key.L, 74),
        (Key.P, 75), (Key.OemSemicolon, 76)
    };
    foreach (var (key, midiNote) in expected)
    {
        Require(ComputerKeyboardPianoMap.MidiNotes.TryGetValue(key, out var actual) && actual == midiNote,
            $"Computer-piano mapping mismatch for {key}: expected {midiNote}, got {actual}.");
    }
}

static bool IsWave(byte[] data) =>
    data.Length >= 44 && data[0] == (byte)'R' && data[1] == (byte)'I' &&
    data[2] == (byte)'F' && data[3] == (byte)'F';

static string FindFixtureRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "TestData", "Fixtures");
            if (File.Exists(Path.Combine(candidate, "cadenza-timeline.musicxml"))) return candidate;
            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException("Could not locate TestData/Fixtures from the current repository.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
