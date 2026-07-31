using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static void ValidateNavigationDirectives(
        XElement root,
        ICollection<ScoreValidationWarning> warnings)
    {
        var navigationAttributes = new[] { "dacapo", "dalsegno", "segno", "coda", "tocoda", "fine" };
        var unsupported = Descendants(root, "sound")
            .SelectMany(sound => navigationAttributes.Where(attribute => sound.Attribute(attribute) is not null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length == 0)
            return;

        var measureCount = Children(Children(root, "part").FirstOrDefault(), "measure").Count();
        warnings.Add(new ScoreValidationWarning(
            "navigation-directive",
            $"Unsupported score navigation was found ({string.Join(", ", unsupported)}). Playback and assessment are disabled until reviewed.",
            1,
            Math.Max(1, measureCount),
            true));
    }

    private static void ValidateSlurs(
        XElement part,
        ICollection<ScoreValidationWarning> warnings,
        ref int slurPairCount)
    {
        var active = new Dictionary<string, int>();
        foreach (var note in Descendants(part, "note"))
        {
            var measureNumber = MeasureNumberOf((string?)note.Parent?.Attribute("number") ?? "1", 1);
            foreach (var slur in Descendants(Descendant(note, "notations"), "slur"))
            {
                var number = (string?)slur.Attribute("number") ?? "1";
                var type = (string?)slur.Attribute("type") ?? string.Empty;
                if (string.Equals(type, "start", StringComparison.OrdinalIgnoreCase))
                    active[number] = measureNumber;
                else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase) && active.Remove(number))
                    slurPairCount++;
                else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
                    warnings.Add(new ScoreValidationWarning(
                        "unmatched-slur-stop",
                        $"An unmatched slur stop was found at measure {measureNumber}.",
                        measureNumber,
                        measureNumber,
                        true));
            }
        }

        foreach (var measureNumber in active.Values)
        {
            warnings.Add(new ScoreValidationWarning(
                "unmatched-slur-start",
                $"An unmatched slur start was found at measure {measureNumber}.",
                measureNumber,
                measureNumber,
                true));
        }
    }

    private static ParsedPitch? ParsePitch(XElement note)
    {
        var pitch = Descendant(note, "pitch");
        var step = Value(Descendant(pitch, "step"));
        var octave = ParseInt(Value(Descendant(pitch, "octave")));
        if (step is null || octave is null)
            return null;

        var pitchClass = step.ToUpperInvariant() switch
        {
            "C" => 0,
            "D" => 2,
            "E" => 4,
            "F" => 5,
            "G" => 7,
            "A" => 9,
            "B" => 11,
            _ => -1
        };
        if (pitchClass < 0)
            return null;

        var alter = double.TryParse(
            Value(Descendant(pitch, "alter")),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedAlter)
            ? (int)Math.Round(parsedAlter)
            : 0;
        return new ParsedPitch(
            Math.Clamp((octave.Value + 1) * 12 + pitchClass + alter, 0, 127),
            step.ToUpperInvariant(),
            octave.Value,
            alter);
    }

    private static string InferNoteType(int durationDivisions, int divisions)
    {
        var beats = durationDivisions / (double)Math.Max(1, divisions);
        if (beats >= 4) return "whole";
        if (beats >= 2) return "half";
        if (beats >= 1) return "quarter";
        if (beats >= 0.5) return "eighth";
        if (beats >= 0.25) return "16th";
        return "32nd";
    }

    private static string FormatKeySignature(int fifths, string? mode)
    {
        var majorKeys = new[] { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
        var majorKey = majorKeys[Math.Clamp(fifths + 7, 0, majorKeys.Length - 1)];
        return string.Equals(mode, "minor", StringComparison.OrdinalIgnoreCase)
            ? $"{majorKey} minor"
            : $"{majorKey} major";
    }

    private static int MeasureNumberOf(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static string? Value(XElement? element) => element?.Value.Trim();

    private static XElement? Descendant(XElement? root, string localName) =>
        root?.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
        root is null
            ? []
            : root.Descendants().Where(element =>
                string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? root, string localName) =>
        root is null
            ? []
            : root.Elements().Where(element =>
                string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private sealed record ParsedPart(
        string Id,
        string Name,
        IReadOnlyList<ParsedMeasure> Measures,
        IReadOnlyList<ScoreNote> Notes,
        IReadOnlyList<ScoreRest> Rests,
        IReadOnlyList<ScoreMark> Marks,
        IReadOnlyList<ParsedTempoChange> TempoChanges,
        IReadOnlyList<ParsedMeterChange> MeterChanges,
        double TotalBeats)
    {
        public int NavigationDirectiveCount => Measures.Sum(measure =>
            measure.RepeatDirectives.Count + measure.EndingDirectives.Count);
    }

    private sealed record ParsedMeasure(
        MeasureSummary Summary,
        IReadOnlyList<ScoreNote> Notes,
        IReadOnlyList<ScoreRest> Rests,
        IReadOnlyList<ScoreMark> Marks,
        IReadOnlyList<ParsedTempoChange> TempoChanges,
        IReadOnlyList<ParsedMeterChange> MeterChanges,
        int Divisions,
        double DurationBeats,
        IReadOnlyList<ParsedRepeatDirective> RepeatDirectives,
        IReadOnlyList<ParsedEndingDirective> EndingDirectives,
        IReadOnlyList<int> InitialEndingPasses,
        (int Beats, int BeatType) EndingMeter)
    {
        public string Number => Summary.Number;
        public IReadOnlyList<int> EndingPasses { get; set; } = InitialEndingPasses;
        public IReadOnlyList<string> NavigationSignature => RepeatDirectives
            .Select(item => $"R:{item.Direction}:{item.Times}:{item.Location}")
            .Concat(EndingDirectives.Select(item => $"E:{item.Number}:{item.Type}:{item.Location}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ParsedPitch(int MidiNoteNumber, string Step, int Octave, int Alter);
    private sealed record ParsedRepeatDirective(string Direction, int Times, BarlineLocation Location);
    private sealed record ParsedEndingDirective(string Number, string Type, BarlineLocation Location);
    private sealed record ParsedTempoChange(double SourceBeat, double Bpm, string MeasureNumber, int SourceMeasureIndex);
    private sealed record ParsedMeterChange(double SourceBeat, int Beats, int BeatType, string MeasureNumber, int SourceMeasureIndex);
    private sealed record RepeatSection(int Id, int StartIndex, int RepeatEndIndex, int ExitIndex, int TotalPasses);

    private enum BarlineLocation
    {
        Left,
        Middle,
        Right
    }
}

internal static class DictionaryCollectionExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (dictionary.TryGetValue(key, out var value))
            return value;
        value = new TValue();
        dictionary.Add(key, value);
        return value;
    }
}
