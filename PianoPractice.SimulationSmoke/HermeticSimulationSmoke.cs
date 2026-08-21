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
            Assert(!new CadenzaUserSettings().AutoDismissResultsEnabled,
                "Results auto-dismiss must be disabled for new profiles.");
            Assert(!new CadenzaUserSettings().SkipResultsWhenLooping,
                "Looping must show Results by default for new profiles.");

            var guidedSightReading = SightReadingExerciseGenerator.CreateSession(
                SightReadingTestKind.GuidedNotes,
                seed: 17);
            Assert(guidedSightReading.Count == 16 &&
                   guidedSightReading.Take(8).All(prompt => prompt.ShowsNoteLabels) &&
                   guidedSightReading.Skip(8).All(prompt => !prompt.ShowsNoteLabels),
                "Guided sight reading did not introduce labeled notes before label-free review.");
            Assert(guidedSightReading.Take(8).Select(prompt => prompt.MidiNotes[0]).Order().SequenceEqual(
                       guidedSightReading.Skip(8).Select(prompt => prompt.MidiNotes[0]).Order()),
                "Guided sight reading did not review the same introduced notes without labels.");
            Assert(guidedSightReading.Take(8).All(prompt =>
                       System.Text.Encoding.UTF8.GetString(prompt.MusicXml).Contains(
                           "<lyric relative-x=\"45\" placement=\"below\">",
                           StringComparison.Ordinal)) &&
                   guidedSightReading.Skip(8).All(prompt =>
                       !System.Text.Encoding.UTF8.GetString(prompt.MusicXml).Contains("<lyric", StringComparison.Ordinal)),
                "Sight-reading labels were not offset from the playhead or isolated to the introduction stage.");
            var nextGuidedSightReading = SightReadingExerciseGenerator.CreateSession(
                SightReadingTestKind.GuidedNotes,
                seed: 18);
            Assert(!guidedSightReading.Select(prompt => Convert.ToHexString(prompt.MusicXml)).SequenceEqual(
                       nextGuidedSightReading.Select(prompt => Convert.ToHexString(prompt.MusicXml))),
                "Guided sight-reading sessions still generate the same notes every time.");

            var accidentalXml = string.Join(string.Empty,
                SightReadingExerciseGenerator.CreateSession(SightReadingTestKind.Accidentals, seed: 19)
                    .Select(prompt => System.Text.Encoding.UTF8.GetString(prompt.MusicXml)));
            Assert(accidentalXml.Contains("<accidental>sharp</accidental>", StringComparison.Ordinal) &&
                   accidentalXml.Contains("<accidental>flat</accidental>", StringComparison.Ordinal) &&
                   accidentalXml.Contains("<accidental>natural</accidental>", StringComparison.Ordinal),
                "Accidental sight reading does not include sharps, flats, and naturals.");
            var keySignatureXml = string.Join(string.Empty,
                SightReadingExerciseGenerator.CreateSession(SightReadingTestKind.KeySignatures, seed: 20)
                    .Select(prompt => System.Text.Encoding.UTF8.GetString(prompt.MusicXml)));
            Assert(keySignatureXml.Contains("<fifths>3</fifths>", StringComparison.Ordinal) &&
                   keySignatureXml.Contains("<fifths>-3</fifths>", StringComparison.Ordinal),
                "Key-signature sight reading does not cover both sharp and flat keys.");
            var mixedReading = SightReadingExerciseGenerator.CreateSession(
                SightReadingTestKind.MixedChallenge,
                seed: 21);
            Assert(mixedReading.Count == 12 &&
                   mixedReading.Any(prompt => prompt.MidiNotes.Count > 1) &&
                   mixedReading.Any(prompt => System.Text.Encoding.UTF8.GetString(prompt.MusicXml)
                       .Contains("<accidental>", StringComparison.Ordinal)) &&
                   mixedReading.Any(prompt => !System.Text.Encoding.UTF8.GetString(prompt.MusicXml)
                       .Contains("<fifths>0</fifths>", StringComparison.Ordinal)),
                "Mixed Reading Challenge does not combine patterns, accidentals, and key signatures.");

            foreach (var kind in Enum.GetValues<SightReadingTestKind>())
            {
                var prompts = SightReadingExerciseGenerator.CreateSession(kind, seed: 23);
                Assert(prompts.Count > 0 && prompts.All(prompt => prompt.MidiNotes.Count == prompt.Beats.Count),
                    $"{kind} did not produce a complete sight-reading response plan.");
                var generatedPath = Path.Combine(root, $"sight-reading-{kind}.musicxml");
                await File.WriteAllBytesAsync(generatedPath, prompts[0].MusicXml);
                var generatedScore = new MusicXmlImporter().Import(generatedPath);
                Assert(generatedScore.TotalNoteCount == prompts[0].MidiNotes.Count,
                    $"{kind} generated notation and expected MIDI notes disagree.");
            }

            var sightReadingProfilePath = Path.Combine(root, "sight-reading-profile", "profile.json");
            using (var sightReading = new MainWindowViewModel(sightReadingProfilePath))
            {
                Assert(!sightReading.AutoRestartSightReading,
                    "Sight-reading auto-restart must remain opt-in so completed results stay visible.");
                sightReading.MidiMonitorEnabled = false;
                sightReading.UseKeyboardSimulation = true;
                sightReading.OpenSightReading();
                Assert(sightReading.IsSightReadingActive &&
                       sightReading.CurrentSightReadingPrompt is { ShowsNoteLabels: true },
                    "Opening Sight Reading did not start the visibly selected guided test.");
                sightReading.SelectSightReadingTest(SightReadingTestKind.NoteRecognition);
                var sightFeedback = new List<SightReadingFeedbackEvent>();
                var sightPrompts = new List<SightReadingPrompt>();
                sightReading.SightReadingFeedback += (_, message) => sightFeedback.Add(message);
                sightReading.SightReadingPromptChanged += (_, prompt) => sightPrompts.Add(prompt);
                Assert(sightReading.StartSightReadingTest(seed: 31), sightReading.SightReadingStatus);
                var recognitionPrompt = sightReading.CurrentSightReadingPrompt!;
                var expectedNote = recognitionPrompt.MidiNotes[0];
                sightReading.SimulateNoteOn(expectedNote == 127 ? 126 : expectedNote + 1);
                Assert(ReferenceEquals(recognitionPrompt, sightReading.CurrentSightReadingPrompt) &&
                       sightReading.SightReadingScoreLabel.Contains("1 mistakes", StringComparison.Ordinal),
                    "An incorrect sight-reading note advanced the prompt or escaped scoring.");
                sightReading.SimulateNoteOn(expectedNote);
                Assert(sightFeedback.Select(message => message.Kind).SequenceEqual(["wrong", "correct"]),
                    "Sight-reading input did not emit ordered red and green renderer feedback.");
                await Task.Delay(750);
                Assert(!ReferenceEquals(recognitionPrompt, sightReading.CurrentSightReadingPrompt) && sightPrompts.Count >= 2,
                    "A completed single-note sight-reading prompt did not advance to new notation.");

                sightReading.StopSightReadingTest();
                sightReading.SelectSightReadingTest(SightReadingTestKind.LookAheadSequences);
                Assert(sightReading.StartSightReadingTest(seed: 37), sightReading.SightReadingStatus);
                var sequencePrompt = sightReading.CurrentSightReadingPrompt!;
                Assert(sequencePrompt.MidiNotes.Count == 4 && !sequencePrompt.ShowsNoteLabels,
                    "Look-ahead sight reading did not present an unlabeled four-note sequence.");
                foreach (var note in sequencePrompt.MidiNotes)
                {
                    sightReading.SimulateNoteOn(note);
                    sightReading.SimulateNoteOff(note);
                }
                Assert(sightReading.SightReadingScoreLabel.StartsWith("4 correct", StringComparison.Ordinal),
                    "The sight-reading sequence did not require and score every displayed note in order.");
            }

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
                Assert(viewModel.PreviewButtonLabel == "Pause", "Running Practice transport did not show Pause.");
                await viewModel.TogglePreviewAsync();
                Assert(viewModel.IsPracticePaused && viewModel.PreviewButtonLabel == "Resume",
                    "The Practice transport did not pause and change to Resume.");
                await viewModel.TogglePreviewAsync();
                Assert(!viewModel.IsPracticePaused && viewModel.PreviewButtonLabel == "Pause",
                    "The Practice transport did not resume at the same expected note.");

                var firstGroup = score.GetPracticeGroups(PracticeMode.BothHands).First();
                var feedback = new List<LessonNoteFeedbackEvent>();
                var partialChordResets = 0;
                viewModel.NoteFeedback += (_, message) => feedback.Add(message);
                viewModel.PartialChordReset += (_, _) => partialChordResets++;
                var acceptedTone = firstGroup.MidiNotes[0];
                viewModel.SimulateNoteOn(acceptedTone);
                viewModel.SimulateNoteOff(acceptedTone);

                Assert(viewModel.CorrectLabel == "1" &&
                       viewModel.MissedLabel == (firstGroup.NoteCount - 1).ToString(),
                    "An incomplete Practice chord did not score its unplayed tones as misses.");
                Assert(partialChordResets == 1 &&
                       feedback.Count(message => message.Kind == "correct") == 1 &&
                       feedback.Count(message => message.Kind == "missed") == firstGroup.NoteCount - 1,
                    "An incomplete Practice chord did not reset its accepted visual tones and emit missing-note feedback.");
                Assert(Math.Abs(viewModel.CursorBeat - firstGroup.OnsetBeats) < 0.001,
                    "An incomplete Practice chord advanced instead of waiting for a complete retry.");

                foreach (var note in firstGroup.MidiNotes.SkipLast(1))
                    viewModel.SimulateNoteOn(note);
                Assert(viewModel.CorrectLabel == firstGroup.NoteCount.ToString() &&
                       Math.Abs(viewModel.CursorBeat - firstGroup.OnsetBeats) < 0.001,
                    "Practice progressed before every tone of the retried chord was played.");

                viewModel.SimulateNoteOn(firstGroup.MidiNotes[^1]);
                Assert(viewModel.CorrectLabel == (firstGroup.NoteCount + 1).ToString(),
                    "Practice did not accept the complete chord retry after the failed attempt.");
                foreach (var note in firstGroup.MidiNotes)
                    viewModel.SimulateNoteOff(note);

                Assert(partialChordResets == 1,
                    "Completing the full chord retry unexpectedly reset it again.");
                viewModel.StopLesson();

                viewModel.SetLessonMode(LessonMode.TimedPlay);
                Assert(viewModel.StartLesson(), viewModel.StatusMessage);
                Assert(viewModel.IsLessonActive, "Performance mode did not enter its running state.");
                Assert(viewModel.PreviewButtonLabel == "Pause",
                    $"Running Performance transport showed {viewModel.PreviewButtonLabel} instead of Pause.");
                await Task.Delay(80);
                viewModel.UpdateVisualClock();
                var pausedBeat = viewModel.CursorBeat;
                await viewModel.TogglePreviewAsync();
                Assert(viewModel.IsPerformancePaused && viewModel.PreviewButtonLabel == "Resume",
                    "The Performance transport did not pause and change to Resume.");
                await Task.Delay(80);
                viewModel.UpdateVisualClock();
                Assert(Math.Abs(viewModel.CursorBeat - pausedBeat) < 0.001,
                    "The assessed performance position moved while page browsing was paused.");
                await viewModel.TogglePreviewAsync();
                Assert(!viewModel.IsPerformancePaused,
                    $"A paused performance did not resume after its count-in: active={viewModel.IsLessonActive}, " +
                    $"paused={viewModel.IsPerformancePaused}, status={viewModel.StatusMessage}");
                Assert(viewModel.IsLessonActive && !viewModel.IsPerformancePaused,
                    "Performance resume did not restore the running state.");
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

            var loopProfilePath = Path.Combine(root, "loop-profile.json");
            using (var looping = new MainWindowViewModel(loopProfilePath))
            {
                var startedRuns = 0;
                var presentedResults = 0;
                looping.LessonRunStateChanged += (_, state) =>
                {
                    if (state.State == "started") startedRuns++;
                };
                looping.ResultsPresented += (_, _) => presentedResults++;
                looping.LoadScore(scorePath);
                looping.OpenCurrentLesson();
                looping.MidiMonitorEnabled = false;
                looping.UseKeyboardSimulation = true;
                looping.FocusStartMeasure = 1;
                looping.FocusEndMeasure = 1;
                looping.SetPracticeMode(PracticeMode.BothHands);
                looping.SetLessonMode(LessonMode.WaitForYou);
                looping.IsLoopEnabled = true;
                var loopStartBeat = looping.CursorBeat;

                void CompleteSelectedMeasure()
                {
                    foreach (var group in score.GetPracticeGroups(PracticeMode.BothHands)
                                 .Where(group => group.MeasureNumber == "1"))
                    {
                        foreach (var note in group.MidiNotes)
                            looping.SimulateNoteOn(note);
                        foreach (var note in group.MidiNotes)
                            looping.SimulateNoteOff(note);
                    }
                }

                Assert(looping.StartLesson(), looping.StatusMessage);
                CompleteSelectedMeasure();
                Assert(startedRuns == 1 && !looping.IsLessonActive && looping.ResultsVisible &&
                       presentedResults == 1,
                    "Loop mode skipped Results even though Skip Results while looping was disabled.");
                await Task.Delay(900);
                Assert(startedRuns == 1 && !looping.IsLessonActive,
                    "Loop mode restarted automatically while its Results screen was waiting for review.");
                looping.DismissResults();

                looping.SkipResultsWhenLooping = true;
                Assert(looping.StartLesson(), looping.StatusMessage);
                CompleteSelectedMeasure();
                Assert(startedRuns == 2 && !looping.IsLessonActive &&
                       Math.Abs(looping.CursorBeat - loopStartBeat) < 0.001,
                    $"Loop mode did not reset the visual position while preserving the final audio tail: " +
                    $"started={startedRuns}, active={looping.IsLessonActive}, status={looping.StatusMessage}");
                Assert(!looping.ResultsVisible && presentedResults == 1,
                    "Loop mode presented a results summary between runs.");
                Assert(looping.IsLoopEnabled,
                    "Completing a looped run unexpectedly disabled loop mode.");
                await Task.Delay(900);
                Assert(startedRuns == 3 && looping.IsLessonActive,
                    $"Loop mode did not restart Practice after the final audio release tail: " +
                    $"started={startedRuns}, active={looping.IsLessonActive}, status={looping.StatusMessage}");
                looping.StopLesson();
            }
            Assert(new UserProfileStore(loopProfilePath).Load().Settings.SkipResultsWhenLooping,
                "Skip Results while looping did not persist after restart.");

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
