using System.Runtime.CompilerServices;
using System.Text;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class MuseScoreDiscontinuedVoltaRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        MultiMeasureFirstEndingAndSharedSecondEndingBoundary();
        Console.WriteLine("PASS MuseScore multi-measure volta boundary regression");
    }

    private static void MultiMeasureFirstEndingAndSharedSecondEndingBoundary()
    {
        var xml = Score(
            Measure(1, Note("C"),
                "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"),
            Measure(2, Note("D")),
            Measure(3, Note("E"),
                "<barline location=\"left\"><ending number=\"1\" type=\"start\"/></barline>" +
                "<barline location=\"right\"><ending number=\"1\" type=\"discontinue\"/></barline>"),
            Measure(4, Note("F")),
            Measure(5, Note("G"),
                "<barline location=\"right\"><repeat direction=\"backward\"/></barline>"),
            Measure(6, Note("A"),
                "<barline location=\"left\"><ending number=\"2\" type=\"start\"/><repeat direction=\"forward\"/></barline>" +
                "<barline location=\"right\"><ending number=\"2\" type=\"discontinue\"/></barline>"),
            Measure(7, Note("B"),
                "<barline location=\"right\"><repeat direction=\"backward\"/></barline>"),
            Measure(8, Note("C", 5)));

        var score = Import(xml);
        var actual = score.PerformanceMeasures
            .Select(occurrence => int.Parse(occurrence.MeasureNumber))
            .ToArray();
        var expected = new[] { 1, 2, 3, 4, 5, 1, 2, 6, 7, 6, 7, 8 };

        Assert(actual.SequenceEqual(expected),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        Assert(actual.Count(measure => measure is 3 or 4 or 5) == 3,
            "The second pass entered part of the first-ending region.");
        Assert(actual.Count(measure => measure == 6) == 2,
            "The measure shared by ending 2 and the next repeat start was not reused correctly.");
        Assert(!score.ValidationWarnings.Any(warning => warning.Code == "volta-owner-state"),
            "The shared second-ending boundary lost its owning repeat state.");

        var groups = score.GetPracticeGroups(PracticeMode.BothHands);
        Assert(groups.Count == expected.Length,
            $"Expected {expected.Length} expected-note groups, got {groups.Count}.");
    }

    private static ScoreDocument Import(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadenza-musescore-volta-{Guid.NewGuid():N}.musicxml");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
