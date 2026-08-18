using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class ImporterPropertySmoke
{
    private const int Seed = 0xCAD2026;

    internal static void Run()
    {
        var random = new Random(Seed);
        for (var index = 0; index < 160; index++)
        {
            var xml = GenerateScore(random, random.Next(1, 13), includeRepeat: index % 7 == 0);
            AssertDeterministicValidImport(xml, index);
        }

        var valid = GenerateScore(new Random(Seed), 4, includeRepeat: false);
        var mutations = new[]
        {
            valid.Replace("<duration>16</duration>", "<duration>0</duration>", StringComparison.Ordinal),
            valid.Replace("tempo=\"96\"", "tempo=\"NaN\"", StringComparison.Ordinal),
            valid.Replace("<divisions>4</divisions>", "<divisions>1000001</divisions>", StringComparison.Ordinal),
            Regex.Replace(valid, "<step>[A-G]</step>", "<step>H</step>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
        };
        foreach (var mutation in mutations)
            AssertRejects(mutation);

        var stress = GenerateScore(new Random(Seed + 1), 1_000, includeRepeat: true);
        var watch = Stopwatch.StartNew();
        var stressScore = Import(stress);
        watch.Stop();
        Assert(stressScore.MeasureCount == 1_000, "bounded stress score lost measures");
        Assert(stressScore.PerformanceMeasures.Count == 2_000, "bounded stress repeat expansion was incorrect");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(30),
            $"bounded 1,000-measure import exceeded 30 seconds ({watch.Elapsed.TotalSeconds:0.###}s)");
        Console.WriteLine(
            $"  seeded cases=160, mutations={mutations.Length}, stressMeasures=1000, " +
            $"stressOccurrences={stressScore.PerformanceMeasures.Count}, stressMs={watch.Elapsed.TotalMilliseconds:0.0}");
    }

    private static void AssertDeterministicValidImport(string xml, int caseIndex)
    {
        var first = Import(xml);
        var second = Import(xml);
        Assert(first.ContentSha256 == second.ContentSha256, $"case {caseIndex}: content identity changed");
        Assert(first.PerformanceMeasures.SequenceEqual(second.PerformanceMeasures),
            $"case {caseIndex}: performance expansion was nondeterministic");
        Assert(first.Notes.SequenceEqual(second.Notes), $"case {caseIndex}: note expansion was nondeterministic");
        Assert(first.ValidationWarnings.Select(WarningIdentity).SequenceEqual(second.ValidationWarnings.Select(WarningIdentity)),
            $"case {caseIndex}: diagnostics were nondeterministic");

        var expectedStart = 0d;
        foreach (var occurrence in first.PerformanceMeasures)
        {
            Assert(double.IsFinite(occurrence.PerformanceStartBeat) && double.IsFinite(occurrence.DurationBeats),
                $"case {caseIndex}: non-finite occurrence timing");
            Assert(Math.Abs(occurrence.PerformanceStartBeat - expectedStart) < 0.0001,
                $"case {caseIndex}: discontinuous performance plan");
            Assert(occurrence.DurationBeats > 0, $"case {caseIndex}: non-positive occurrence duration");
            expectedStart += occurrence.DurationBeats;
        }
        Assert(Math.Abs(expectedStart - first.TotalPerformanceBeats) < 0.0001,
            $"case {caseIndex}: total performed beats disagree with occurrences");

        foreach (var note in first.Notes)
        {
            Assert(double.IsFinite(note.OnsetBeats) && double.IsFinite(note.DurationBeats),
                $"case {caseIndex}: non-finite note timing");
            Assert(note.OnsetBeats >= -0.0001 && note.DurationBeats > 0,
                $"case {caseIndex}: invalid expanded note range");
            var occurrence = first.PerformanceMeasures[note.PerformanceOccurrence];
            Assert(note.OnsetBeats >= occurrence.PerformanceStartBeat - 0.0001 &&
                   note.OnsetBeats < occurrence.PerformanceStartBeat + occurrence.DurationBeats + 0.0001,
                $"case {caseIndex}: note escaped its occurrence");
        }

        for (var sample = 0; sample <= 8; sample++)
        {
            var beat = first.TotalPerformanceBeats * sample / 8d;
            var seconds = first.SecondsAtPerformanceBeat(beat);
            var roundTrip = first.PerformanceBeatAtSeconds(seconds);
            Assert(double.IsFinite(seconds) && Math.Abs(roundTrip - beat) < 0.001,
                $"case {caseIndex}: tempo-map round trip failed at beat {beat:0.###}");
        }
    }

    private static string WarningIdentity(ScoreValidationWarning warning) =>
        $"{warning.Code}:{warning.StartMeasure}:{warning.EndMeasure}:{warning.BlocksAssessment}:{warning.BlocksPlayback}:{warning.Capability}";

    private static void AssertRejects(string xml)
    {
        try
        {
            _ = Import(xml);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("a deterministic malformed-input mutation was accepted");
    }

    private static ScoreDocument Import(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadenza-property-{Guid.NewGuid():N}.musicxml");
        try
        {
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            return new MusicXmlImporter().Import(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string GenerateScore(Random random, int measureCount, bool includeRepeat)
    {
        var measures = new StringBuilder();
        for (var measure = 1; measure <= measureCount; measure++)
        {
            var (beats, beatType) = (measure % 9) switch
            {
                0 => (3, 8),
                4 => (6, 8),
                7 => (5, 4),
                _ => (4, 4)
            };
            const int divisions = 4;
            var duration = checked(divisions * beats * 4 / beatType);
            var upperStep = "CDEFGAB"[random.Next(7)];
            var lowerStep = "CDEFGAB"[random.Next(7)];
            var tempo = 72 + random.Next(97);
            var attributes = measure == 1 || measure % 9 is 0 or 4 or 7
                ? $"<attributes><divisions>{divisions}</divisions><key><fifths>{random.Next(-3, 4)}</fifths></key><time><beats>{beats}</beats><beat-type>{beatType}</beat-type></time><staves>2</staves></attributes>"
                : string.Empty;
            var tempoDirection = measure == 1 || measure % 5 == 0
                ? $"<direction><sound tempo=\"{(measure == 1 ? 96 : tempo).ToString(CultureInfo.InvariantCulture)}\"/></direction>"
                : string.Empty;
            var forwardRepeat = includeRepeat && measure == 1
                ? "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"
                : string.Empty;
            var backwardRepeat = includeRepeat && measure == measureCount
                ? "<barline location=\"right\"><repeat direction=\"backward\" times=\"2\"/></barline>"
                : string.Empty;
            measures.Append($"<measure number=\"{measure}\">{forwardRepeat}{attributes}{tempoDirection}");
            measures.Append($"<note><pitch><step>{upperStep}</step><octave>{4 + random.Next(2)}</octave></pitch><duration>{duration}</duration><voice>1</voice><staff>1</staff></note>");
            measures.Append($"<backup><duration>{duration}</duration></backup>");
            measures.Append($"<note><pitch><step>{lowerStep}</step><octave>{2 + random.Next(2)}</octave></pitch><duration>{duration}</duration><voice>2</voice><staff>2</staff></note>{backwardRepeat}</measure>");
        }

        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><score-partwise version=\"4.0\"><part-list><score-part id=\"P1\"><part-name>Generated Piano</part-name></score-part></part-list><part id=\"P1\">{measures}</part></score-partwise>";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
