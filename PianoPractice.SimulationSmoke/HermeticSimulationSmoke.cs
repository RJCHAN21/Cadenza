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
}
