using System.Runtime.CompilerServices;
using System.Text;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class AuthoritativeNavigationRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        StandardVoltaSkipsFirstEndingOnSecondPass();
        RightBarlineEndingStartAppliesToFollowingMeasure();
        EventsPastWrittenBarlineCannotLeakAcrossRepeat();
        ExpectedGroupsMatchExpandedPerformance();
        Console.WriteLine("PASS authoritative navigation regression fixtures");
    }

    private static void StandardVoltaSkipsFirstEndingOnSecondPass()
    {
        var score = Import(Score(
            Measure(1, Note("C"), "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"),
            Measure(2, Note("D")),
            Measure(3, Note("E"),
                "<barline location=\"left\"><ending number=\"1\" type=\"start\"/></barline>" +
                "<barline location=\"right\"><ending number=\"1\" type=\"stop\"/><repeat direction=\"backward\"/></barline>"),
            Measure(4, Note("F"),
                "<barline location=\"left\"><ending number=\"2\" type=\"start\"/></barline>" +
                "<barline location=\"right\"><ending number=\"2\" type=\"stop\"/></barline>"),
            Measure(5, Note("G"))));

        AssertSequence(score, 1, 2, 3, 1, 2, 4, 5);
    }

    private static void RightBarlineEndingStartAppliesToFollowingMeasure()
    {
        var score = Import(Score(
            Measure(1, Note("C"), "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"),
            Measure(2, Note("D"),
                "<barline location=\"right\"><ending number=\"1\" type=\"start\"/></barline>"),
            Measure(3, Note("E"),
                "<barline location=\"right\"><ending number=\"1\" type=\"stop\"/><repeat direction=\"backward\"/></barline>"),
            Measure(4, Note("F"),
                "<barline location=\"left\"><ending number=\"2\" type=\"start\"/></barline>" +
                "<barline location=\"right\"><ending number=\"2\" type=\"stop\"/></barline>"),
            Measure(5, Note("G"))));

        AssertSequence(score, 1, 2, 3, 1, 2, 4, 5);
    }

    private static void EventsPastWrittenBarlineCannotLeakAcrossRepeat()
    {
        var overflowingBody = string.Concat(
            Note("C"), Note("D"), Note("E"), Note("F"),
            Note("G"), Note("A"), Note("B"), Note("C", 5));
        var score = Import(Score(
            Measure(1, overflowingBody,
                "<barline location=\"left\"><repeat direction=\"forward\"/></barline>" +
                "<barline location=\"right\"><repeat direction=\"backward\"/></barline>"),
            Measure(2, Note("D", 5))));

        AssertSequence(score, 1, 1, 2);
        Assert(Math.Abs(score.PerformanceMeasures[0].DurationBeats - 4d) < 0.001,
            "An overflowing 4/4 measure was not bounded at four beats.");
        Assert(score.Notes.Count(note => note.SourceMeasureIndex == 0) == 8,
            "Notes serialized beyond the barline leaked into the expanded performance.");
        Assert(score.ValidationWarnings.Any(warning => warning.Code == "measure-overflow" && warning.BlocksAssessment),
            "The overflowing source measure did not produce a blocking warning.");
    }

    private static void ExpectedGroupsMatchExpandedPerformance()
    {
        var score = Import(Score(
            Measure(1, Note("C"), "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"),
            Measure(2, Note("D"), "<barline location=\"right\"><repeat direction=\"backward\"/></barline>"),
            Measure(3, Note("E"))));

        var expectedOnsets = score.Notes.Select(note => Math.Round(note.OnsetBeats, 5)).Distinct().Count();
        var groups = score.GetPracticeGroups(PracticeMode.BothHands);
        Assert(groups.Count == expectedOnsets,
            $"Expected-note groups ({groups.Count}) disagree with expanded note onsets ({expectedOnsets}).");
        Assert(groups.Count > 0 && groups[^1].OnsetBeats < score.TotalPerformanceBeats,
            "The final expected-note group lies outside the performance plan.");
    }

    private static ScoreDocument Import(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadenza-authoritative-{Guid.NewGuid():N}.musicxml");
        try
        {
            File.WriteAllText(path, xml, Encoding.UTF8);
            return new MusicXmlImporter().Import(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Score(params string[] measures) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN" "http://www.musicxml.org/dtds/partwise.dtd">
        <score-partwise version="4.0">
          <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
          <part id="P1">{string.Join(string.Empty, measures)}</part>
        </score-partwise>
        """;

    private static string Measure(int number, string body, string barlines = "") =>
        $"<measure number=\"{number}\"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves></attributes>{barlines}{body}</measure>";

    private static string Note(string step, int octave = 4) =>
        $"<note><pitch><step>{step}</step><octave>{octave}</octave></pitch><duration>1</duration><voice>1</voice><type>quarter</type><staff>1</staff></note>";

    private static void AssertSequence(ScoreDocument score, params int[] expected)
    {
        var actual = score.PerformanceMeasures
            .Select(occurrence => int.TryParse(occurrence.MeasureNumber, out var value) ? value : -1)
            .ToArray();
        Assert(actual.SequenceEqual(expected),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
