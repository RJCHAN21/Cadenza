using System.IO.Compression;
using System.Text;
using System.Xml;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

var failures = new List<string>();
Run("simple repeat", TestSimpleRepeat);
Run("implicit-start repeat", TestImplicitStartRepeat);
Run("explicit repeat count", TestRepeatCount);
Run("first and second endings", TestAlternateEndings);
Run("multiple parts", TestMultipleParts);
Run("ties remain occurrence-aware", TestTies);
Run("tempo and meter map", TestTempoAndMeter);
Run("standard MusicXML DOCTYPE is accepted", TestStandardMusicXmlDoctype);
Run("DTD entity references remain blocked", TestDtdEntityIsRejected);
Run("unsafe MXL path is rejected", TestUnsafeMxlPath);

if (args.Length > 0)
{
    Run("user supplied score", () =>
    {
        var score = new MusicXmlImporter().Import(args[0]);
        Assert(score.MeasureCount > 0, "The imported score has no measures.");
        Assert(score.PerformanceMeasures.Count > 0, "The imported score has no performance plan.");
        AssertContinuousTimeline(score);
        Console.WriteLine(
            $"Imported {score.Title}: written measures={score.MeasureCount}, " +
            $"occurrences={score.PerformanceMeasures.Count}, notes={score.Notes.Count}, " +
            $"warnings={score.ValidationWarnings.Count}.");
    });
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} parser smoke test(s) failed:");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine();
    Console.WriteLine("All deterministic parser smoke tests passed.");
}

return;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

void TestSimpleRepeat()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 4)),
            Measure(2, Note("D", 4), forwardRepeat: true),
            Measure(3, Note("E", 4), backwardRepeat: true),
            Measure(4, Note("F", 4)))));
    AssertSequence(score, 1, 2, 3, 2, 3, 4);
    Assert(score.RepeatPairCount == 1, "Expected one repeat pair.");
}

void TestImplicitStartRepeat()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 4)),
            Measure(2, Note("D", 4), backwardRepeat: true),
            Measure(3, Note("E", 4)))));
    AssertSequence(score, 1, 2, 1, 2, 3);
}

void TestRepeatCount()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 4), forwardRepeat: true),
            Measure(2, Note("D", 4), backwardRepeat: true, repeatTimes: 3),
            Measure(3, Note("E", 4)))));
    AssertSequence(score, 1, 2, 1, 2, 1, 2, 3);
    Assert(score.PerformanceMeasures.Where(item => item.SourceMeasureIndex == 0)
        .Select(item => item.RepeatPass).SequenceEqual([1, 2, 3]),
        "Repeat pass numbers were not preserved.");
}

void TestAlternateEndings()
{
    var firstEnding =
        "<barline location=\"left\"><ending number=\"1\" type=\"start\"/></barline>" +
        "<barline location=\"right\"><ending number=\"1\" type=\"stop\"/><repeat direction=\"backward\"/></barline>";
    var secondEnding =
        "<barline location=\"left\"><ending number=\"2\" type=\"start\"/></barline>" +
        "<barline location=\"right\"><ending number=\"2\" type=\"stop\"/></barline>";

    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 4), forwardRepeat: true),
            Measure(2, Note("D", 4)),
            Measure(3, Note("E", 4), customBarlines: firstEnding),
            Measure(4, Note("F", 4), customBarlines: secondEnding),
            Measure(5, Note("G", 4)))));
    AssertSequence(score, 1, 2, 3, 1, 2, 4, 5);
}

void TestMultipleParts()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 5)),
            Measure(2, Note("D", 5))),
        Part("P2",
            Measure(1, Note("C", 3, staff: 2)),
            Measure(2, Note("D", 3, staff: 2)))));

    Assert(score.Parts.Count == 2, "Both MusicXML parts were not retained.");
    Assert(score.Notes.Count == 4, $"Expected four playable notes, got {score.Notes.Count}.");
    Assert(score.Notes.Select(note => note.PartId).Distinct().Count() == 2,
        "Playable notes lost their part identity.");
}

void TestTies()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(1, Note("C", 4, tieStart: true, id: "n1")),
            Measure(2, Note("C", 4, tieStop: true, id: "n2")))));

    Assert(score.Notes.Count == 1, $"Expected one merged tied note, got {score.Notes.Count}.");
    Assert(Math.Abs(score.Notes[0].DurationBeats - 2d) < 0.001,
        $"Expected a two-beat tied duration, got {score.Notes[0].DurationBeats}.");
    Assert((score.Notes[0].TiedSourceNoteIds ?? []).SequenceEqual(["n1", "n2"]),
        "The tied continuation identities were not retained.");
}

void TestTempoAndMeter()
{
    var score = ImportXml(Score(
        Part("P1",
            Measure(
                1,
                Note("C", 4, duration: 4),
                attributes: Attributes(divisions: 1, beats: 4, beatType: 4),
                direction: TempoDirection(120)),
            Measure(
                2,
                Note("D", 4, duration: 1) + Note("E", 4, duration: 1) + Note("F", 4, duration: 1),
                attributes: Attributes(divisions: 2, beats: 3, beatType: 8),
                direction: TempoDirection(60)))));

    Assert(score.TempoChanges.Any(change =>
        Math.Abs(change.PerformanceBeat - 4d) < 0.001 && Math.Abs(change.Bpm - 60d) < 0.001),
        "The second-measure tempo change is missing.");
    Assert(score.MeterChanges.Any(change =>
        Math.Abs(change.PerformanceBeat - 4d) < 0.001 && change.Beats == 3 && change.BeatType == 8),
        "The 3/8 meter change is missing.");
    Assert(Math.Abs(score.SecondsAtPerformanceBeat(score.TotalPerformanceBeats) - 3.5d) < 0.01,
        $"Expected a 3.5 second performance, got {score.SecondsAtPerformanceBeat(score.TotalPerformanceBeats):0.###}.");
}

void TestStandardMusicXmlDoctype()
{
    var scoreXml = Score(Part("P1", Measure(1, Note("C", 4))));
    var withDoctype = scoreXml.Replace(
        "<score-partwise version=\"4.0\">",
        "<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">\n<score-partwise version=\"4.0\">",
        StringComparison.Ordinal);

    var score = ImportXml(withDoctype);
    Assert(score.MeasureCount == 1, "A standard MusicXML DOCTYPE prevented score import.");
    Assert(score.Notes.Count == 1, "The DOCTYPE-compatible score lost its playable note.");
}

void TestDtdEntityIsRejected()
{
    var xml = """
              <?xml version="1.0" encoding="UTF-8"?>
              <!DOCTYPE score-partwise [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
              <score-partwise version="4.0">
                <work><work-title>&xxe;</work-title></work>
                <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
                <part id="P1"><measure number="1"><note><rest/><duration>1</duration></note></measure></part>
              </score-partwise>
              """;
    AssertThrows<XmlException>(
        () => ImportXml(xml),
        "XML that depends on a DTD entity was accepted.");
}

void TestUnsafeMxlPath()
{
    var path = Path.Combine(Path.GetTempPath(), $"cadenza-{Guid.NewGuid():N}.mxl");
    try
    {
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "META-INF/container.xml",
                "<container><rootfiles><rootfile full-path=\"../score.xml\"/></rootfiles></container>");
            WriteEntry(archive, "score.xml", Score(Part("P1", Measure(1, Note("C", 4)))));
        }

        AssertThrows<InvalidDataException>(
            () => new MusicXmlImporter().Import(path),
            "An MXL manifest containing path traversal was accepted.");
    }
    finally
    {
        File.Delete(path);
    }
}

ScoreDocument ImportXml(string xml)
{
    var path = Path.Combine(Path.GetTempPath(), $"cadenza-{Guid.NewGuid():N}.musicxml");
    try
    {
        File.WriteAllText(path, xml, Encoding.UTF8);
        var score = new MusicXmlImporter().Import(path);
        AssertContinuousTimeline(score);
        return score;
    }
    finally
    {
        File.Delete(path);
    }
}

void AssertContinuousTimeline(ScoreDocument score)
{
    var expected = 0d;
    foreach (var occurrence in score.PerformanceMeasures)
    {
        Assert(Math.Abs(occurrence.PerformanceStartBeat - expected) < 0.001,
            $"Occurrence {occurrence.OccurrenceIndex} starts at {occurrence.PerformanceStartBeat}, expected {expected}.");
        expected += occurrence.DurationBeats;
    }

    Assert(Math.Abs(score.TotalPerformanceBeats - expected) < 0.001,
        "TotalPerformanceBeats disagrees with the occurrence timeline.");
}

void AssertSequence(ScoreDocument score, params int[] expected)
{
    var actual = score.PerformanceMeasures
        .Select(item => int.TryParse(item.MeasureNumber, out var number) ? number : -1)
        .ToArray();
    Assert(actual.SequenceEqual(expected),
        $"Expected performance sequence [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
}

void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

string Score(params string[] parts)
{
    var partList = string.Join("", parts.Select((_, index) =>
        $"<score-part id=\"P{index + 1}\"><part-name>Part {index + 1}</part-name></score-part>"));
    return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <score-partwise version="4.0">
              <work><work-title>Deterministic Fixture</work-title></work>
              <part-list>{partList}</part-list>
              {string.Join("", parts)}
            </score-partwise>
            """;
}

string Part(string id, params string[] measures) =>
    $"<part id=\"{id}\">{string.Join("", measures)}</part>";

string Measure(
    int number,
    string body,
    bool forwardRepeat = false,
    bool backwardRepeat = false,
    int repeatTimes = 2,
    string? customBarlines = null,
    string? attributes = null,
    string? direction = null)
{
    var left = forwardRepeat
        ? "<barline location=\"left\"><repeat direction=\"forward\"/></barline>"
        : "";
    var right = backwardRepeat
        ? $"<barline location=\"right\"><repeat direction=\"backward\" times=\"{repeatTimes}\"/></barline>"
        : "";
    return $"<measure number=\"{number}\">{attributes ?? Attributes()}{left}{direction ?? ""}{body}{customBarlines ?? right}</measure>";
}

string Attributes(int divisions = 1, int beats = 4, int beatType = 4) =>
    $"<attributes><divisions>{divisions}</divisions><time><beats>{beats}</beats><beat-type>{beatType}</beat-type></time><staves>2</staves></attributes>";

string TempoDirection(double bpm) =>
    $"<direction><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>{bpm}</per-minute></metronome></direction-type><sound tempo=\"{bpm}\"/></direction>";

string Note(
    string step,
    int octave,
    int duration = 1,
    int staff = 1,
    bool tieStart = false,
    bool tieStop = false,
    string? id = null)
{
    var ties = (tieStart ? "<tie type=\"start\"/>" : "") +
               (tieStop ? "<tie type=\"stop\"/>" : "");
    var tied = (tieStart ? "<tied type=\"start\"/>" : "") +
               (tieStop ? "<tied type=\"stop\"/>" : "");
    var notations = tied.Length > 0 ? $"<notations>{tied}</notations>" : "";
    var idAttribute = string.IsNullOrWhiteSpace(id) ? "" : $" id=\"{id}\"";
    return $"<note{idAttribute}><pitch><step>{step}</step><octave>{octave}</octave></pitch><duration>{duration}</duration><voice>1</voice><type>quarter</type><staff>{staff}</staff>{ties}{notations}</note>";
}

void WriteEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
    writer.Write(content);
}
