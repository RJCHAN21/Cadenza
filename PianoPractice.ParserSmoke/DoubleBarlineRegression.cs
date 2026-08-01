using System.Runtime.CompilerServices;
using System.Text;
using PianoPractice.Desktop.Services;

internal static class DoubleBarlineRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <score-partwise version="4.0">
              <work><work-title>Double Barline Fixture</work-title></work>
              <part-list>
                <score-part id="P1"><part-name>Piano</part-name></score-part>
              </part-list>
              <part id="P1">
                <measure number="1">
                  <attributes>
                    <divisions>1</divisions>
                    <time><beats>4</beats><beat-type>4</beat-type></time>
                    <staves>2</staves>
                  </attributes>
                  <note>
                    <rest measure="yes"/>
                    <duration>4</duration>
                    <voice>1</voice>
                    <type>whole</type>
                    <staff>1</staff>
                  </note>
                  <barline location="right"><bar-style>light-light</bar-style></barline>
                </measure>
                <measure number="2">
                  <attributes>
                    <divisions>1</divisions>
                    <time><beats>4</beats><beat-type>4</beat-type></time>
                    <staves>2</staves>
                  </attributes>
                  <note>
                    <pitch><step>C</step><octave>4</octave></pitch>
                    <duration>4</duration>
                    <voice>1</voice>
                    <type>whole</type>
                    <staff>1</staff>
                  </note>
                </measure>
              </part>
            </score-partwise>
            """;

        var path = Path.Combine(
            Path.GetTempPath(),
            $"cadenza-double-barline-{Guid.NewGuid():N}.musicxml");
        try
        {
            File.WriteAllText(path, xml, Encoding.UTF8);
            var score = new MusicXmlImporter().Import(path);
            var sequence = score.PerformanceMeasures
                .Select(item => item.MeasureNumber)
                .ToArray();

            Assert(sequence.SequenceEqual(["1", "2"]),
                $"Double barline changed the playback sequence to [{string.Join(", ", sequence)}].");
            Assert(score.RepeatPairCount == 0,
                $"Double barline created {score.RepeatPairCount} repeat pair(s).");
            Assert(Math.Abs(score.TotalPerformanceBeats - 8d) < 0.001,
                $"Double barline changed performed duration to {score.TotalPerformanceBeats:0.###} beats.");
            Assert(score.Rests.Any(rest => Math.Abs(rest.OnsetBeats) < 0.001),
                "The leading whole-measure rest was not retained at beat zero.");
            Assert(!score.ValidationWarnings.Any(warning => warning.BlocksAssessment),
                "An ordinary double barline produced a blocking validation warning.");

            Console.WriteLine("PASS ordinary double barline remains structural");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
