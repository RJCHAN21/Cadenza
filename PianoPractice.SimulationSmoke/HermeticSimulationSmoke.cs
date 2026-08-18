using PianoPractice.Desktop;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class HermeticSimulationSmoke
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cadenza-hermetic-simulation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scorePath = Path.Combine(root, "deterministic.musicxml");
            await File.WriteAllTextAsync(scorePath, Fixture);
            var score = new MusicXmlImporter().Import(scorePath);

            Assert(score.MeasureCount == 3, "The deterministic fixture did not import all measures.");
            Assert(score.GetPracticeGroups(PracticeMode.LeftHand).Count > 0, "Left-hand timeline is empty.");
            Assert(score.GetPracticeGroups(PracticeMode.RightHand).Count > 0, "Right-hand timeline is empty.");
            Assert(score.TempoChanges.Count >= 2, "Tempo-map changes were not retained.");
            Assert(score.MeterChanges.Count >= 2, "Meter changes were not retained.");
            Assert(score.HasBlockingPlaybackWarning(1, score.MeasureCount),
                "The deterministic fixture no longer exercises best-effort Listen playback.");

            using (var audio = new PianoAudioService())
            {
                var preview = await audio.BuildPreviewAsync(
                    score,
                    includeMetronome: true,
                    startBeat: 0,
                    endBeat: score.TotalPerformanceBeats,
                    tempoBpm: score.TempoBpm,
                    CancellationToken.None);
                Assert(preview.Length >= 44 && preview.AsSpan(0, 4).SequenceEqual("RIFF"u8),
                    "The hardware-independent audio plan did not produce a WAV payload.");

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await AssertCancelledAsync(() => audio.BuildPreviewAsync(
                    score,
                    includeMetronome: false,
                    startBeat: 0,
                    endBeat: score.TotalPerformanceBeats,
                    tempoBpm: score.TempoBpm,
                    cancelled.Token));
            }

            var profilePath = Path.Combine(root, "profile", "profile.json");
            using (var viewModel = new MainWindowViewModel(profilePath))
            {
                viewModel.LoadScore(scorePath);
                viewModel.OpenCurrentLesson();
                viewModel.MidiMonitorEnabled = false;
                viewModel.UseKeyboardSimulation = true;
                viewModel.FocusStartMeasure = 1;
                viewModel.FocusEndMeasure = 1;
                viewModel.SetPracticeMode(PracticeMode.BothHands);
                viewModel.SetLessonMode(LessonMode.WaitForYou);
                Assert(viewModel.StartLesson(), viewModel.StatusMessage);

                var firstGroup = score.GetPracticeGroups(PracticeMode.BothHands).First();
                foreach (var note in firstGroup.MidiNotes)
                {
                    viewModel.SimulateNoteOn(note);
                    viewModel.SimulateNoteOff(note);
                }

                Assert(viewModel.CorrectLabel == firstGroup.NoteCount.ToString(),
                    "Deterministic keyboard input did not score the first chord exactly once.");
                viewModel.StopLesson();

                viewModel.SetLessonMode(LessonMode.TimedPlay);
                Assert(viewModel.StartLesson(), viewModel.StatusMessage);
                Assert(viewModel.IsLessonActive, "Performance mode did not enter its running state.");
                viewModel.StopLesson();
                Assert(!viewModel.IsLessonActive, "Performance mode did not leave its running state.");

                viewModel.SetLessonMode(LessonMode.Listen);
                Assert(viewModel.CanStartLesson,
                    "A successfully imported score with playback limitations disabled Listen mode.");

                var originalHash = viewModel.CurrentScore!.ContentSha256;
                var malformedPath = Path.Combine(root, "malformed.musicxml");
                await File.WriteAllTextAsync(malformedPath, "<score-partwise>");
                try
                {
                    viewModel.LoadScore(malformedPath);
                    throw new InvalidOperationException("Malformed score replacement succeeded.");
                }
                catch (InvalidDataException)
                {
                }
                Assert(viewModel.CurrentScore?.ContentSha256 == originalHash,
                    "Failed score replacement discarded the prior valid session.");
            }

            using (var restored = new MainWindowViewModel(profilePath))
            {
                Assert(restored.TryLoadLastOpenedScore(), "The last-opened library item was not restored by stable ID.");
                Assert(restored.CurrentScore?.ContentSha256 == score.ContentSha256,
                    "Restored score identity does not match the validated imported document.");
            }

            var delayedHandPath = Path.Combine(root, "delayed-right-hand.musicxml");
            await File.WriteAllTextAsync(delayedHandPath, DelayedRightHandFixture);
            var delayedHandScore = new MusicXmlImporter().Import(delayedHandPath);
            var firstRightHandGroup = delayedHandScore.GetPracticeGroups(PracticeMode.RightHand).First();
            Assert(firstRightHandGroup.OnsetBeats > 0,
                "The delayed-hand fixture does not contain an initial selected-hand rest.");
            using (var delayedHand = new MainWindowViewModel(Path.Combine(root, "delayed-hand-profile.json")))
            {
                LessonRunStateEvent? started = null;
                delayedHand.LessonRunStateChanged += (_, state) =>
                {
                    if (state.State == "started") started = state;
                };
                delayedHand.LoadScore(delayedHandPath);
                delayedHand.OpenCurrentLesson();
                delayedHand.UseKeyboardSimulation = true;
                delayedHand.SetPracticeMode(PracticeMode.RightHand);
                delayedHand.SetLessonMode(LessonMode.WaitForYou);
                Assert(delayedHand.StartLesson(), delayedHand.StatusMessage);
                var practiceClock = (System.Diagnostics.Stopwatch?)typeof(MainWindowViewModel)
                    .GetField("_practiceSessionClock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(delayedHand);
                Assert(practiceClock is { IsRunning: false },
                    "Practice timing started before the first played note.");
                Assert(Math.Abs(delayedHand.CursorBeat - firstRightHandGroup.OnsetBeats) < 0.001,
                    "Practice waited at an onset where the selected hand has no note.");
                Assert(started is not null && Math.Abs(started.StartBeat - firstRightHandGroup.OnsetBeats) < 0.001,
                    "The renderer lesson start was not aligned with the selected hand's first playable note.");
                var firstPlayedNote = Enumerable.Range(0, 128)
                    .First(note => !firstRightHandGroup.MidiNotes.Contains(note));
                delayedHand.SimulateNoteOn(firstPlayedNote);
                Assert(practiceClock is { IsRunning: true },
                    "Practice timing did not start on the first played note.");
                delayedHand.SimulateNoteOff(firstPlayedNote);
                foreach (var group in delayedHandScore.GetPracticeGroups(PracticeMode.RightHand))
                {
                    foreach (var note in group.MidiNotes)
                    {
                        delayedHand.SimulateNoteOn(note);
                        delayedHand.SimulateNoteOff(note);
                    }
                }
                Assert(practiceClock is { IsRunning: false },
                    "Practice timing continued after the final required note during result ring-out.");
                delayedHand.StopLesson();
            }

            Console.WriteLine(
                $"PASS hermetic simulation: measures={score.MeasureCount}, notes={score.Notes.Count}, " +
                $"beats={score.TotalPerformanceBeats:0.###}, hash={score.ContentSha256[..12]}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertCancelledAsync(Func<Task<byte[]>> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Cancelled audio preparation completed successfully.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private const string Fixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <score-partwise version="4.0">
          <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
          <part id="P1">
            <measure number="1">
              <attributes>
                <divisions>2</divisions><key><fifths>-1</fifths></key>
                <time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves>
                <clef number="1"><sign>G</sign><line>2</line></clef>
                <clef number="2"><sign>F</sign><line>4</line></clef>
              </attributes>
              <direction><sound tempo="120"/></direction>
              <direction><direction-type><pedal type="start"/></direction-type></direction>
              <note><pitch><step>C</step><octave>4</octave></pitch><duration>8</duration><voice>1</voice><staff>1</staff></note>
              <backup><duration>8</duration></backup>
              <note><pitch><step>C</step><octave>3</octave></pitch><duration>8</duration><voice>2</voice><staff>2</staff></note>
            </measure>
            <measure number="2">
              <attributes><time><beats>3</beats><beat-type>8</beat-type></time></attributes>
              <direction><sound tempo="90"/></direction>
              <note><pitch><step>D</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><staff>1</staff></note>
              <note><pitch><step>E</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><staff>1</staff></note>
              <note><pitch><step>F</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><staff>1</staff></note>
              <backup><duration>3</duration></backup>
              <note><pitch><step>G</step><octave>2</octave></pitch><duration>3</duration><voice>2</voice><staff>2</staff></note>
            </measure>
            <measure number="3">
              <attributes><time><beats>4</beats><beat-type>4</beat-type></time></attributes>
              <direction><sound tempo="132"/></direction>
              <note><pitch><step>G</step><octave>4</octave></pitch><duration>8</duration><voice>1</voice><staff>1</staff></note>
              <backup><duration>8</duration></backup>
              <note><pitch><step>F</step><octave>2</octave></pitch><duration>8</duration><voice>2</voice><staff>2</staff></note>
            </measure>
          </part>
        </score-partwise>
        """;

    private const string DelayedRightHandFixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <score-partwise version="4.0">
          <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
          <part id="P1">
            <measure number="1">
              <attributes>
                <divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves>
                <clef number="1"><sign>G</sign><line>2</line></clef>
                <clef number="2"><sign>F</sign><line>4</line></clef>
              </attributes>
              <direction><sound tempo="120"/></direction>
              <note><rest/><duration>2</duration><voice>1</voice><staff>1</staff></note>
              <note><pitch><step>E</step><octave>4</octave></pitch><duration>2</duration><voice>1</voice><staff>1</staff></note>
              <backup><duration>4</duration></backup>
              <note><pitch><step>C</step><octave>3</octave></pitch><duration>4</duration><voice>2</voice><staff>2</staff></note>
            </measure>
          </part>
        </score-partwise>
        """;
}
