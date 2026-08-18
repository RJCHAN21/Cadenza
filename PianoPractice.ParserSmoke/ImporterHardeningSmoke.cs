using System.IO.Compression;
using System.Text;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class ImporterHardeningSmoke
{
    internal static void Run()
    {
        RejectsInvalidTiming();
        RejectsInvalidPitch();
        ClassifiesUnsupportedSemantics();
        RejectsAmbiguousArchives();
        PreservesValidatedDocumentIdentity();
    }

    private static void RejectsInvalidTiming()
    {
        AssertRejects("<score-partwise>", "truncated XML was accepted");
        AssertRejects(
            "<?xml version=\"1.0\"?><score-timewise version=\"4.0\"/>",
            "unsupported score-timewise root was accepted");
        AssertRejects(
            Score(Measure(Attributes("0") + Note())),
            "zero divisions were accepted");
        AssertRejects(
            Score(Measure(Attributes("1") + Note(duration: "0"))),
            "zero note duration was accepted");
        AssertRejects(
            Score(Measure(Attributes("1") + "<backup><duration>1</duration></backup>" + Note())),
            "backup before measure start was accepted");
        AssertRejects(
            Score(Measure(Attributes("1") + "<forward><duration>2147483647</duration></forward>")),
            "unbounded forward duration was accepted");
        AssertRejects(
            Score(Measure(Attributes("1") + Note().Replace("<voice>1</voice>", "<voice></voice>", StringComparison.Ordinal))),
            "blank voice identifier was accepted");
        AssertRejects(
            Score(Measure(Attributes("1") + Note().Replace("<voice>1</voice>", $"<voice>{new string('v', 129)}</voice>", StringComparison.Ordinal))),
            "unbounded voice identifier was accepted");
        AssertRejects(
            "<?xml version=\"1.0\"?><!DOCTYPE score-partwise SYSTEM \"https://example.invalid/score.dtd\"><score-partwise>&external;</score-partwise>",
            "external entity attempt was accepted");
    }

    private static void RejectsInvalidPitch()
    {
        AssertRejects(
            Score(Measure(Attributes("1") + Note(step: "H"))),
            "invalid pitch step was silently discarded");
        AssertRejects(
            Score(Measure(Attributes("1") + Note(octave: "999999999"))),
            "extreme octave was silently clamped");
    }

    private static void ClassifiesUnsupportedSemantics()
    {
        var microtonal = ImportXml(Score(Measure(Attributes("1") + Note(alter: "0.5"))));
        AssertDiagnostic(microtonal, "unsupported-microtonal-pitch", blocksPlayback: true);
        Assert(microtonal.Notes.Count == 0, "microtonal pitch was collapsed to an ordinary MIDI note");

        var grace = ImportXml(Score(Measure(Attributes("1") + Note(grace: true))));
        AssertDiagnostic(grace, "unsupported-grace-note-semantics", blocksPlayback: true);
        Assert(grace.Notes.Count == 0, "grace note was assigned invented playback timing");

        var additive = ImportXml(Score(Measure(
            "<attributes><divisions>1</divisions><time><beats>3+2</beats><beat-type>8</beat-type></time></attributes>" +
            Note())));
        AssertDiagnostic(additive, "unsupported-additive-meter", blocksPlayback: true);

        var navigation = ImportXml(Score(Measure(
            Attributes("1") +
            "<direction><direction-type><words>D.C. al Fine</words></direction-type></direction>" +
            Note())));
        AssertDiagnostic(navigation, "navigation-directive", blocksPlayback: true);

        var ornament = ImportXml(Score(Measure(
            Attributes("1") + Note(notations: "<ornaments><trill-mark/></ornaments>"))));
        AssertDiagnostic(ornament, "unsupported-ornament-semantics", blocksPlayback: false);

        AssertBlockingCapability(
            Attributes("1") +
            "<direction><direction-type><octave-shift type=\"down\" size=\"8\"/></direction-type></direction>" +
            Note(),
            "unsupported-octave-shift");
        AssertBlockingCapability(
            "<attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><transpose><chromatic>2</chromatic></transpose></attributes>" + Note(),
            "unsupported-written-pitch-transposition");
        AssertBlockingCapability(
            "<attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><measure-style><multiple-rest>4</multiple-rest></measure-style></attributes>" + Note(),
            "unsupported-multiple-measure-rest");
        AssertBlockingCapability(
            Attributes("1") + Note(notations: "<glissando type=\"start\"/>") ,
            "unsupported-continuous-pitch");
        AssertBlockingCapability(
            Attributes("1") + Note(notations: "<technical><bend><bend-alter>1</bend-alter></bend></technical>"),
            "unsupported-pitch-bend");
        AssertBlockingCapability(
            Attributes("1") + Note().Replace("<voice>1</voice>", "<accidental>quarter-sharp</accidental><voice>1</voice>", StringComparison.Ordinal),
            "unsupported-microtonal-accidental");
        AssertBlockingCapability(
            Attributes("1") + Note(notations: "<arpeggiate/>") ,
            "unsupported-arpeggiation");
        AssertBlockingCapability(
            Attributes("1") + Note(notations: "<ornaments><tremolo type=\"single\">3</tremolo></ornaments>") ,
            "unsupported-tremolo");
        AssertBlockingCapability(
            Attributes("1") + Note(notations: "<fermata/>") ,
            "unsupported-expressive-timing");
        AssertBlockingCapability(
            Attributes("1") +
            "<direction><direction-type><words>ritardando</words></direction-type></direction>" + Note(),
            "unsupported-textual-tempo");
        AssertBlockingCapability(
            Attributes("1") +
            "<direction><direction-type><pedal type=\"start\" pedal-type=\"sostenuto\"/></direction-type></direction>" + Note(),
            "unsupported-pedal-type");

        var expressive = ImportXml(Score(Measure(
            Attributes("1") +
            "<direction><direction-type><wedge type=\"crescendo\"/></direction-type></direction>" +
            Note(notations: "<articulations><staccato/></articulations><technical><fingering>1</fingering></technical>"))));
        AssertAdvisoryCapability(expressive, "limited-dynamic-expression");
        AssertAdvisoryCapability(expressive, "limited-articulation-expression");
        AssertAdvisoryCapability(expressive, "fingering-advisory-only");

        var sustain = ImportXml(Score(Measure(
            Attributes("1") +
            "<direction><direction-type><pedal type=\"start\"/></direction-type></direction>" + Note())));
        var sustainDiagnostic = sustain.ValidationWarnings.Single(warning => warning.Code == "limited-pedal-playback");
        Assert(!sustainDiagnostic.BlocksAssessment && sustainDiagnostic.BlocksPlayback,
            "sustain pedal policy did not isolate automatic playback from practice assessment");

        var numericTempo = ImportXml(Score(Measure(
            Attributes("1") +
            "<direction><direction-type><words>Allegro</words></direction-type><sound tempo=\"120\"/></direction>" + Note())));
        Assert(numericTempo.ValidationWarnings.All(warning => warning.Code != "unsupported-textual-tempo"),
            "text paired with an authoritative numeric tempo was blocked");

        var tempoBeforeMeasure = ImportXml(Score(Measure(
            Attributes("1") +
            "<direction><direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>120</per-minute></metronome></direction-type><offset>-1</offset></direction>" + Note())));
        AssertDiagnostic(tempoBeforeMeasure, "direction-before-measure", blocksPlayback: true);

        var cue = ImportXml(Score(Measure(
            Attributes("1") + Note().Replace("<note>", "<note><cue/>", StringComparison.Ordinal))));
        AssertAdvisoryCapability(cue, "cue-note-advisory-only");
        Assert(cue.Notes.Count == 0, "cue note was treated as a performed or assessed note");

        var extendedStaff = ImportXml(Score(Measure(
            "<attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><staves>3</staves></attributes>" + Note())));
        var extendedStaffDiagnostic = extendedStaff.ValidationWarnings.Single(warning => warning.Code == "extended-staff-assignment");
        Assert(extendedStaffDiagnostic.BlocksAssessment && !extendedStaffDiagnostic.BlocksPlayback,
            "extended-staff policy did not preserve playback while blocking two-hand assessment");

        var crossStaff = ImportXml(Score(Measure(
            Attributes("1") + Note() + "<backup><duration>1</duration></backup>" +
            Note(step: "E").Replace("<staff>1</staff>", "<staff>2</staff>", StringComparison.Ordinal))));
        Assert(!crossStaff.HasBlockingAssessmentWarning(1, 1) &&
               crossStaff.Notes.Select(note => note.StaffNumber).Order().SequenceEqual([1, 2]),
            "explicit cross-staff assignment was not preserved for hand-specific assessment");

        var tuplet = ImportXml(Score(Measure(
            Attributes("3") +
            Note(duration: "1").Replace(
                "<voice>1</voice>",
                "<voice>1</voice><time-modification><actual-notes>3</actual-notes><normal-notes>2</normal-notes></time-modification>",
                StringComparison.Ordinal))));
        Assert(!tuplet.HasBlockingAssessmentWarning(1, 1) && !tuplet.HasBlockingPlaybackWarning(1, 1),
            "duration-authoritative tuplet notation was incorrectly blocked");
    }

    private static void RejectsAmbiguousArchives()
    {
        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml",
                "<container><rootfiles><rootfile full-path=\"score.xml\"/></rootfiles></container>");
            WriteEntry(archive, "score.xml", Score(Measure(Attributes("1") + Note())));
            WriteEntry(archive, "SCORE.XML", Score(Measure(Attributes("1") + Note(step: "D"))));
        }, "case-insensitive duplicate archive paths were accepted");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml",
                "<container><rootfiles><rootfile full-path=\"scores/main.xml\"/></rootfiles></container>");
            WriteEntry(archive, "scores/main.xml", Score(Measure(Attributes("1") + Note())));
            WriteEntry(archive, "scores//main.xml", Score(Measure(Attributes("1") + Note(step: "D"))));
        }, "separator-normalized duplicate archive paths were accepted");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml", "<container><rootfiles/></container>");
            WriteEntry(archive, "score.xml", Score(Measure(Attributes("1") + Note())));
        }, "an invalid manifest silently fell back to another score entry");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "one.musicxml", Score(Measure(Attributes("1") + Note())));
            WriteEntry(archive, "two.musicxml", Score(Measure(Attributes("1") + Note(step: "D"))));
        }, "multiple score entries without a manifest were accepted ambiguously");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml",
                "<container><rootfiles><rootfile full-path=\"one.xml\"/><rootfile full-path=\"two.xml\"/></rootfiles></container>");
            WriteEntry(archive, "one.xml", Score(Measure(Attributes("1") + Note())));
            WriteEntry(archive, "two.xml", Score(Measure(Attributes("1") + Note(step: "D"))));
        }, "multiple manifest rootfiles were accepted");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml",
                "<container><rootfiles><rootfile full-path=\"missing.xml\"/></rootfiles></container>");
            WriteEntry(archive, "score.xml", Score(Measure(Attributes("1") + Note())));
        }, "missing manifest rootfile silently fell back to another entry");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "META-INF/container.xml", "<container><rootfiles>");
            WriteEntry(archive, "score.xml", Score(Measure(Attributes("1") + Note())));
        }, "corrupt container manifest was accepted");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "C:/absolute/score.xml", Score(Measure(Attributes("1") + Note())));
        }, "absolute Windows archive path was accepted");

        AssertArchiveRejects(archive =>
        {
            for (var index = 0; index < 257; index++)
                WriteEntry(archive, $"entries/{index}.txt", string.Empty);
        }, "archive entry-count limit was not enforced");

        AssertArchiveRejects(archive =>
        {
            WriteEntry(archive, "bomb.musicxml", new string(' ', 2 * 1024 * 1024));
        }, "suspicious archive compression ratio was accepted");
    }

    private static void PreservesValidatedDocumentIdentity()
    {
        var xml = Score(Measure(Attributes("1") + Note(step: "E")));
        var path = TemporaryPath(".mxl");
        try
        {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "META-INF/container.xml",
                    "<container><rootfiles><rootfile full-path=\"score.xml\"/></rootfiles></container>");
                WriteEntry(archive, "score.xml", xml);
                WriteEntry(archive, "README.txt", "not a score");
            }

            var score = new MusicXmlImporter().Import(path);
            var validated = Encoding.UTF8.GetString(score.ValidatedMusicXml);
            Assert(validated.Contains("<step>E</step>", StringComparison.Ordinal),
                "the exact validated score document was not retained for rendering");
            Assert(score.ContentSha256.Length == 64, "validated document identity hash is missing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertDiagnostic(ScoreDocument score, string code, bool blocksPlayback)
    {
        var diagnostic = score.ValidationWarnings.SingleOrDefault(warning => warning.Code == code);
        if (diagnostic is null)
            throw new InvalidOperationException($"expected diagnostic {code} was not emitted");
        Assert(diagnostic.BlocksAssessment, $"diagnostic {code} did not block assessment");
        Assert(diagnostic.BlocksPlayback == blocksPlayback,
            $"diagnostic {code} playback policy did not match the capability boundary");
    }

    private static void AssertBlockingCapability(string measureBody, string code)
    {
        var score = ImportXml(Score(Measure(measureBody)));
        var diagnostic = score.ValidationWarnings.Single(warning => warning.Code == code);
        Assert(diagnostic.BlocksAssessment && diagnostic.BlocksPlayback,
            $"capability {code} did not block unsafe playback and assessment");
        Assert(diagnostic.Capability == ScoreCapabilityDisposition.BlocksPlaybackAndAssessment,
            $"capability {code} did not expose its stable disposition");
    }

    private static void AssertAdvisoryCapability(ScoreDocument score, string code)
    {
        var diagnostic = score.ValidationWarnings.Single(warning => warning.Code == code);
        Assert(!diagnostic.BlocksAssessment && !diagnostic.BlocksPlayback,
            $"advisory capability {code} unexpectedly blocked a safe continuation");
        Assert(diagnostic.Capability == ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation,
            $"advisory capability {code} did not expose its stable disposition");
    }

    private static void AssertRejects(string xml, string message)
    {
        try
        {
            _ = ImportXml(xml);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertArchiveRejects(Action<ZipArchive> create, string message)
    {
        var path = TemporaryPath(".mxl");
        try
        {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                create(archive);

            try
            {
                _ = new MusicXmlImporter().Import(path);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ScoreDocument ImportXml(string xml)
    {
        var path = TemporaryPath(".musicxml");
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

    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"cadenza-hardening-{Guid.NewGuid():N}{extension}");

    private static string Score(string measure) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><score-partwise version=\"4.0\"><part-list><score-part id=\"P1\"><part-name>Piano</part-name></score-part></part-list><part id=\"P1\">{measure}</part></score-partwise>";

    private static string Measure(string body) => $"<measure number=\"1\">{body}</measure>";

    private static string Attributes(string divisions) =>
        $"<attributes><divisions>{divisions}</divisions><time><beats>4</beats><beat-type>4</beat-type></time></attributes>";

    private static string Note(
        string step = "C",
        string octave = "4",
        string duration = "1",
        string? alter = null,
        bool grace = false,
        string? notations = null)
    {
        var alterXml = alter is null ? string.Empty : $"<alter>{alter}</alter>";
        var graceXml = grace ? "<grace/>" : string.Empty;
        var durationXml = grace ? string.Empty : $"<duration>{duration}</duration>";
        var notationsXml = notations is null ? string.Empty : $"<notations>{notations}</notations>";
        return $"<note>{graceXml}<pitch><step>{step}</step>{alterXml}<octave>{octave}</octave></pitch>{durationXml}<voice>1</voice><type>quarter</type><staff>1</staff>{notationsXml}</note>";
    }

    private static void WriteEntry(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
