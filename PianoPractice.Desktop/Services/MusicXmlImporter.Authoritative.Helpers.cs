using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
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
        var textualNavigation = Descendants(root, "words")
            .Select(words => new
            {
                Text = Value(words) ?? string.Empty,
                Measure = MeasureNumberOf(
                    words.Ancestors().FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "measure", StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("number")?.Value,
                    1)
            })
            .Where(item => ContainsTextualNavigation(item.Text))
            .ToArray();
        if (unsupported.Length == 0 && textualNavigation.Length == 0)
            return;

        var measureCount = Children(Children(root, "part").FirstOrDefault(), "measure").Count();
        warnings.Add(new ScoreValidationWarning(
            "navigation-directive",
            unsupported.Length > 0
                ? $"Unsupported score navigation was found ({string.Join(", ", unsupported)}). Playback and assessment are disabled until reviewed."
                : $"Unsupported textual score navigation was found ({string.Join(", ", textualNavigation.Select(item => item.Text).Distinct(StringComparer.OrdinalIgnoreCase))}). Playback and assessment are disabled until reviewed.",
            textualNavigation.Length > 0 ? textualNavigation.Min(item => item.Measure) : 1,
            textualNavigation.Length > 0 ? textualNavigation.Max(item => item.Measure) : Math.Max(1, measureCount),
            true,
            true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
    }

    private static bool ContainsTextualNavigation(string value) =>
        Regex.IsMatch(
            value,
            @"(?ix)(?:\bd\s*\.?\s*c\s*\.?|\bda\s+capo\b|\bd\s*\.?\s*s\s*\.?|\bdal\s+segno\b|\bto\s+coda\b|\bal\s+coda\b|\bal\s+fine\b|\bsegno\b|\bcoda\b|\bfine\b)");

    private static void ValidateNotationCapabilities(
        XElement root,
        ICollection<ScoreValidationWarning> warnings)
    {
        AddCapabilityWarning(
            root, warnings, ["octave-shift"], "unsupported-octave-shift",
            "Octave-shift notation changes sounding pitch but is not represented by the performed score model. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["transpose", "clef-octave-change"], "unsupported-written-pitch-transposition",
            "Transposing-score or octave-transposing-clef semantics are not represented by the performed pitch model. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["multiple-rest"], "unsupported-multiple-measure-rest",
            "Multiple-measure rest compression is visible but is not expanded authoritatively by the performed timeline. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["glissando", "slide"], "unsupported-continuous-pitch",
            "Glissando or slide notation is visible but its continuous pitch semantics are not represented. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["bend"], "unsupported-pitch-bend",
            "Pitch-bend notation is visible but its continuous pitch semantics are not represented. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["arpeggiate", "non-arpeggiate"], "unsupported-arpeggiation",
            "Arpeggiation changes performed onset order but is not represented by the score model. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["tremolo"], "unsupported-tremolo",
            "Tremolo repetition semantics are not represented by the score model. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["fermata", "breath-mark", "caesura"], "unsupported-expressive-timing",
            "Expressive timing notation is visible but does not define an authoritative performed duration. Playback and assessment are disabled for the affected measures.",
            blocksAssessment: true, blocksPlayback: true,
            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment);
        AddCapabilityWarning(
            root, warnings, ["wedge", "dynamics"], "limited-dynamic-expression",
            "Dynamic and wedge notation remains visible; synthesized playback does not reproduce its full expressive contour.",
            blocksAssessment: false, blocksPlayback: false,
            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation);
        AddCapabilityWarning(
            root, warnings,
            ["accent", "strong-accent", "staccato", "staccatissimo", "tenuto", "detached-legato"],
            "limited-articulation-expression",
            "Articulation notation remains visible; assessment uses written pitch and onset, and synthesized playback does not reproduce every articulation nuance.",
            blocksAssessment: false, blocksPlayback: false,
            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation);
        AddCapabilityWarning(
            root, warnings, ["fingering", "pluck", "string"], "fingering-advisory-only",
            "Fingering and string indications remain visible but are advisory and are not assessed.",
            blocksAssessment: false, blocksPlayback: false,
            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation);
        AddCapabilityWarning(
            root, warnings, ["slur"], "limited-slur-expression",
            "Slurs remain visible and connected, but synthesized playback does not reproduce legato phrasing.",
            blocksAssessment: false, blocksPlayback: false,
            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation);

        var pedalMeasures = Descendants(root, "pedal")
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (pedalMeasures.Length > 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "limited-pedal-playback",
                "Pedal cues can be monitored during practice, but automatic score playback does not reproduce pedal resonance. Listen playback is disabled for the affected measures.",
                pedalMeasures[0],
                pedalMeasures[^1],
                false,
                true,
                ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
        }
        var unsupportedPedalMeasures = Descendants(root, "pedal")
            .Where(pedal =>
                pedal.Attribute("pedal-type") is { Value: var pedalType } &&
                !string.Equals(pedalType, "sustain", StringComparison.OrdinalIgnoreCase))
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (unsupportedPedalMeasures.Length > 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "unsupported-pedal-type",
                "Soft or sostenuto pedal semantics are not represented by the playback or pedal-assessment model. Playback and assessment are disabled for the affected measures.",
                unsupportedPedalMeasures[0],
                unsupportedPedalMeasures[^1],
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }

        var textualTempoMeasures = Descendants(root, "words")
            .Where(words =>
            {
                var direction = words.Ancestors().FirstOrDefault(ancestor =>
                    string.Equals(ancestor.Name.LocalName, "direction", StringComparison.OrdinalIgnoreCase));
                return direction is not null &&
                       !Descendants(direction, "sound").Any(sound => sound.Attribute("tempo") is not null) &&
                       !Descendants(direction, "per-minute").Any();
            })
            .Where(words => Regex.IsMatch(
                Value(words) ?? string.Empty,
                @"(?ix)\b(?:grave|largo|lento|adagio|andante|moderato|allegro|vivace|presto|prestissimo|rit(?:ardando)?|rall(?:entando)?|accel(?:erando)?|a\s+tempo|rubato)\b"))
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (textualTempoMeasures.Length > 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "unsupported-textual-tempo",
                "Text-only tempo or rubato notation has no authoritative numeric timing. Playback and timed assessment are disabled for the affected measures.",
                textualTempoMeasures[0],
                textualTempoMeasures[^1],
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }

        var microtonalAccidentals = Descendants(root, "accidental")
            .Where(accidental => Regex.IsMatch(
                Value(accidental) ?? string.Empty,
                @"(?i)(quarter|three-quarters|sori|koron)"))
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (microtonalAccidentals.Length > 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "unsupported-microtonal-accidental",
                "Microtonal accidental notation is visible but cannot be mapped authoritatively to the MIDI pitch model. Playback and assessment are disabled for the affected measures.",
                microtonalAccidentals[0],
                microtonalAccidentals[^1],
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }

        var extendedStaffMeasures = Descendants(root, "staves")
            .Where(staves => ParseInt(Value(staves)) is > 2)
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (extendedStaffMeasures.Length > 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "extended-staff-assignment",
                "The score uses more than two staves. Playback remains available, but two-hand assessment cannot assign those staves authoritatively.",
                extendedStaffMeasures[0],
                extendedStaffMeasures[^1],
                true,
                false,
                ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
        }

        foreach (var measure in Descendants(root, "measure"))
        {
            var lyricNumbers = Descendants(measure, "lyric")
                .Select(lyric => (string?)lyric.Attribute("number") ?? "1")
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count();
            if (lyricNumbers < 2)
                continue;
            var number = MeasureNumberForCapability(measure);
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "multiple-lyrics-advisory-only",
                "Multiple lyric lines remain visible but are not part of playback or assessment.",
                number,
                number,
                false,
                false,
                ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
        }
    }

    private static void AddCapabilityWarning(
        XElement root,
        ICollection<ScoreValidationWarning> warnings,
        IReadOnlyCollection<string> elementNames,
        string code,
        string message,
        bool blocksAssessment,
        bool blocksPlayback,
        ScoreCapabilityDisposition disposition)
    {
        var measures = root.Descendants()
            .Where(element => elementNames.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))
            .Select(MeasureNumberForCapability)
            .Distinct()
            .Order()
            .ToArray();
        if (measures.Length == 0)
            return;

        warnings.Add(new ScoreValidationWarning(
            code,
            message,
            measures[0],
            measures[^1],
            blocksAssessment,
            blocksPlayback,
            disposition));
    }

    private static int MeasureNumberForCapability(XElement element)
    {
        var measure = string.Equals(element.Name.LocalName, "measure", StringComparison.OrdinalIgnoreCase)
            ? element
            : element.Ancestors().FirstOrDefault(ancestor =>
                string.Equals(ancestor.Name.LocalName, "measure", StringComparison.OrdinalIgnoreCase));
        return MeasureNumberOf((string?)measure?.Attribute("number"), 1);
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

    private static ParsedPitch? ParsePitch(
        XElement note,
        string partName,
        string measureNumber,
        int displayMeasure,
        ICollection<ScoreValidationWarning> warnings)
    {
        var pitch = Descendant(note, "pitch");
        if (pitch is null)
        {
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "unsupported-unpitched-note",
                $"Part {partName}, measure {measureNumber} contains a non-rest note without pitched-note semantics. It remains visible but is excluded from playback and assessment.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.UnsupportedVisualOnly));
            return null;
        }

        var step = Value(Descendant(pitch, "step"));
        var octave = ParseInt(Value(Descendant(pitch, "octave")));
        if (step is null || octave is null)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains a pitch without a valid step and octave.");

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
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains the invalid pitch step '{step}'.");

        var alterText = Value(Descendant(pitch, "alter"));
        var parsedAlter = 0d;
        if (alterText is not null &&
            (!double.TryParse(alterText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedAlter) ||
             !double.IsFinite(parsedAlter)))
        {
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains the invalid pitch alteration '{alterText}'.");
        }

        if (Math.Abs(parsedAlter - Math.Round(parsedAlter)) > 0.000001)
        {
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "unsupported-microtonal-pitch",
                $"Part {partName}, measure {measureNumber} contains a microtonal pitch alteration. It remains visible but is excluded from playback and assessment.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
            return null;
        }

        var alter = checked((int)Math.Round(parsedAlter));
        if (Math.Abs(alter) > 12)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains an unsupported pitch alteration of {alter} semitones.");
        var midi = (long)(octave.Value + 1) * 12 + pitchClass + alter;
        if (midi is < 0 or > 127)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains a pitch outside the MIDI 0-127 range.");
        return new ParsedPitch(
            (int)midi,
            step.ToUpperInvariant(),
            octave.Value,
            alter);
    }

    private static void AddWarningOnce(
        ICollection<ScoreValidationWarning> warnings,
        ScoreValidationWarning warning)
    {
        if (warnings.Any(existing =>
                string.Equals(existing.Code, warning.Code, StringComparison.Ordinal) &&
                existing.StartMeasure == warning.StartMeasure &&
                existing.EndMeasure == warning.EndMeasure))
        {
            return;
        }

        warnings.Add(warning);
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
