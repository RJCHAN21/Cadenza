using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static ParsedPart ParsePart(
        XElement partElement,
        string partId,
        string partName,
        ICollection<ScoreValidationWarning> warnings)
    {
        var measures = new List<ParsedMeasure>();
        var notes = new List<ScoreNote>();
        var rests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();
        var tempos = new List<ParsedTempoChange>();
        var meters = new List<ParsedMeterChange>();

        var sourceBeat = 0d;
        var divisions = 1;
        var meter = (Beats: 4, BeatType: 4);
        var measureIndex = 0;
        foreach (var measureElement in Children(partElement, "measure"))
        {
            var parsed = ParseMeasure(
                measureElement,
                partId,
                partName,
                measureIndex,
                sourceBeat,
                divisions,
                meter,
                warnings);
            divisions = parsed.Divisions;
            meter = parsed.EndingMeter;
            measures.Add(parsed);
            notes.AddRange(parsed.Notes);
            rests.AddRange(parsed.Rests);
            marks.AddRange(parsed.Marks);
            tempos.AddRange(parsed.TempoChanges);
            meters.AddRange(parsed.MeterChanges);
            sourceBeat += parsed.DurationBeats;
            if (!double.IsFinite(sourceBeat) || sourceBeat > MaxWrittenBeats)
                throw new InvalidDataException(
                    $"Part {partName} exceeds the {MaxWrittenBeats:0} written-beat safety limit.");
            measureIndex++;
        }

        return new ParsedPart(
            partId,
            partName,
            measures,
            notes,
            rests,
            marks,
            tempos,
            meters,
            sourceBeat);
    }

    private static ParsedMeasure ParseMeasure(
        XElement measure,
        string partId,
        string partName,
        int sourceMeasureIndex,
        double measureStartBeat,
        int inheritedDivisions,
        (int Beats, int BeatType) inheritedMeter,
        ICollection<ScoreValidationWarning> warnings)
    {
        var number = (string?)measure.Attribute("number")
                     ?? (sourceMeasureIndex + 1).ToString(CultureInfo.InvariantCulture);
        var displayMeasure = MeasureNumberOf(number, sourceMeasureIndex + 1);
        var attributes = Descendant(measure, "attributes");
        var divisionsElement = Descendant(attributes, "divisions");
        var divisions = inheritedDivisions;
        if (divisionsElement is not null)
        {
            var parsedDivisions = ParseInt(Value(divisionsElement));
            if (parsedDivisions is null or <= 0 || parsedDivisions > MaxDivisions)
                throw new InvalidDataException(
                    $"Part {partName}, measure {number} has invalid divisions. Expected 1-{MaxDivisions:N0}.");
            divisions = parsedDivisions.Value;
        }

        var meter = inheritedMeter;
        var meterChanges = new List<ParsedMeterChange>();
        var timeElement = Descendant(attributes, "time");
        var beatsText = Value(Descendant(timeElement, "beats"));
        var beatTypeText = Value(Descendant(timeElement, "beat-type"));
        var parsedBeats = ParseInt(beatsText);
        var parsedBeatType = ParseInt(beatTypeText);
        if (timeElement is not null && Descendant(timeElement, "senza-misura") is not null)
        {
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "unsupported-free-meter",
                $"Part {partName}, measure {number} uses senza misura. It remains visible but playback and assessment are disabled for this measure.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }
        else if (beatsText?.Contains('+', StringComparison.Ordinal) == true)
        {
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "unsupported-additive-meter",
                $"Part {partName}, measure {number} uses additive meter {beatsText}/{beatTypeText}. It remains visible but playback and assessment are disabled for this measure.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }
        else if (timeElement is not null &&
                 (parsedBeats is null or <= 0 or > 128 || parsedBeatType is null or <= 0 or > 1024))
        {
            throw new InvalidDataException(
                $"Part {partName}, measure {number} contains an invalid time signature.");
        }
        if (parsedBeats is > 0 && parsedBeatType is > 0)
        {
            meter = (parsedBeats.Value, parsedBeatType.Value);
            meterChanges.Add(new ParsedMeterChange(
                measureStartBeat,
                meter.Beats,
                meter.BeatType,
                number,
                sourceMeasureIndex));
        }

        var notes = new List<ScoreNote>();
        var rests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();
        var tempos = new List<ParsedTempoChange>();
        var cursor = 0;
        var maximumCursor = 0;
        var lastStarts = new Dictionary<(string Voice, int Staff), int>();
        var noteCount = 0;
        var restCount = 0;
        var chordCount = 0;
        var lyricCount = 0;
        var staffOne = 0;
        var staffTwo = 0;

        foreach (var element in measure.Elements())
        {
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "note":
                {
                    noteCount++;
                    var isGrace = Descendant(element, "grace") is not null;
                    var isCue = Descendant(element, "cue") is not null;
                    var durationElement = Descendant(element, "duration");
                    var parsedDuration = ParseInt(Value(durationElement));
                    if (!isGrace && (durationElement is null || parsedDuration is null or <= 0))
                        throw new InvalidDataException(
                            $"Part {partName}, measure {number} contains a note or rest without a positive duration.");
                    if (parsedDuration is < 0)
                        throw new InvalidDataException(
                            $"Part {partName}, measure {number} contains a negative note duration.");
                    var durationDivisions = isGrace ? 0 : parsedDuration!.Value;
                    ValidateCursorDelta(durationDivisions, divisions, partName, number, "note duration");
                    var voiceElement = Descendant(element, "voice");
                    var voice = Value(voiceElement) ?? "1";
                    if (voiceElement is not null &&
                        (string.IsNullOrWhiteSpace(voice) ||
                         voice.Length > MaxVoiceIdentifierLength ||
                         voice.Any(char.IsControl)))
                    {
                        throw new InvalidDataException(
                            $"Part {partName}, measure {number} contains an invalid voice identifier.");
                    }
                    var staffElement = Descendant(element, "staff");
                    var staff = ParseInt(Value(staffElement)) ?? 0;
                    if (staffElement is not null && (staff is <= 0 or > MaxStavesPerPart))
                        throw new InvalidDataException(
                            $"Part {partName}, measure {number} contains an invalid staff number.");
                    var key = (voice, staff);
                    var isChord = Descendant(element, "chord") is not null;
                    if (isChord)
                        chordCount++;

                    var start = isChord && lastStarts.TryGetValue(key, out var priorStart)
                        ? priorStart
                        : cursor;
                    if (!isChord)
                        lastStarts[key] = start;

                    var isRest = Descendant(element, "rest") is not null;
                    if (isRest)
                        restCount++;
                    lyricCount += Descendants(element, "lyric").Count();
                    if (staff == 1)
                        staffOne++;
                    else if (staff == 2)
                        staffTwo++;

                    var sourceOnset = measureStartBeat + start / (double)divisions;
                    var sourceDuration = isGrace ? 0 : durationDivisions / (double)divisions;
                    var noteType = Value(Descendant(element, "type"))
                                   ?? InferNoteType(durationDivisions, divisions);
                    var dotCount = Descendants(element, "dot").Count();
                    var pitch = isRest
                        ? null
                        : ParsePitch(element, partName, number, displayMeasure, warnings);
                    var notations = Descendant(element, "notations");
                    var articulations = Descendants(notations, "articulations")
                        .SelectMany(parent => parent.Elements())
                        .ToArray();
                    var isStaccato = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "staccato", StringComparison.OrdinalIgnoreCase));
                    var isAccent = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "accent", StringComparison.OrdinalIgnoreCase));
                    var isTenuto = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "tenuto", StringComparison.OrdinalIgnoreCase));
                    var isSlurred = Descendants(notations, "slur").Any();
                    var ornaments = Descendants(notations, "ornaments")
                        .SelectMany(parent => parent.Elements())
                        .Select(item => item.Name.LocalName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (ornaments.Length > 0)
                    {
                        AddWarningOnce(warnings, new ScoreValidationWarning(
                            "unsupported-ornament-semantics",
                            $"Part {partName}, measure {number} contains ornament notation ({string.Join(", ", ornaments)}). It remains visible, but strict assessment is disabled because the ornament is not interpreted semantically.",
                            displayMeasure,
                            displayMeasure,
                            true,
                            false,
                            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
                    }

                    if (isGrace)
                    {
                        AddWarningOnce(warnings, new ScoreValidationWarning(
                            "unsupported-grace-note-semantics",
                            $"Part {partName}, measure {number} contains grace-note notation. It remains visible but is excluded from playback and assessment because Cadenza does not invent grace timing.",
                            displayMeasure,
                            displayMeasure,
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }

                    if (isCue)
                    {
                        AddWarningOnce(warnings, new ScoreValidationWarning(
                            "cue-note-advisory-only",
                            $"Part {partName}, measure {number} contains cue notes. They remain visible but are excluded from playback and assessment.",
                            displayMeasure,
                            displayMeasure,
                            false,
                            false,
                            ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
                    }

                    if (!isGrace && !isCue && !isRest && pitch is not null)
                    {
                        var sourceNoteId = (string?)element.Attribute("id") ?? string.Empty;
                        notes.Add(new ScoreNote(
                            pitch.MidiNoteNumber,
                            sourceOnset,
                            sourceDuration,
                            staff,
                            number,
                            pitch.Step,
                            pitch.Octave,
                            pitch.Alter,
                            noteType,
                            dotCount,
                            voice,
                            Value(Descendant(element, "stem")) ?? string.Empty,
                            isChord,
                            Descendants(element, "beam")
                                .Select(beam => beam.Value.Trim().ToLowerInvariant())
                                .ToArray(),
                            Descendants(element, "lyric")
                                .Select(lyric => Value(Descendant(lyric, "text")))
                                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                            HasTieType(element, "start"),
                            HasTieType(element, "stop"),
                            0,
                            isStaccato,
                            isAccent,
                            isTenuto,
                            isSlurred,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset,
                            sourceNoteId,
                            string.IsNullOrWhiteSpace(sourceNoteId) ? [] : [sourceNoteId]));

                        foreach (var articulation in articulations)
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Articulation,
                                articulation.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }
                    else if (!isGrace && isRest)
                    {
                        rests.Add(new ScoreRest(
                            sourceOnset,
                            sourceDuration,
                            staff,
                            number,
                            noteType,
                            dotCount,
                            voice,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    if (!isGrace)
                    {
                        maximumCursor = Math.Max(maximumCursor, checked(start + durationDivisions));
                        if (!isChord)
                        {
                            cursor = checked(cursor + durationDivisions);
                            maximumCursor = Math.Max(maximumCursor, cursor);
                        }
                        ValidateCursorPosition(maximumCursor, divisions, partName, number);
                    }
                    break;
                }

                case "direction":
                {
                    var offset = ParseInt(Value(Descendant(element, "offset"))) ?? 0;
                    ValidateCursorDelta(Math.Abs((long)offset), divisions, partName, number, "direction offset");
                    var requestedDirectionCursor = (long)cursor + offset;
                    var tempo = TempoFromDirection(element, partName, number, displayMeasure, warnings);
                    if (requestedDirectionCursor < 0)
                    {
                        var blocksTiming = tempo is > 0;
                        AddWarningOnce(warnings, new ScoreValidationWarning(
                            "direction-before-measure",
                            blocksTiming
                                ? $"Part {partName}, measure {number} contains a tempo direction before the measure start. It remains visible at the barline, but playback and assessment are disabled because the requested timing cannot be represented authoritatively."
                                : $"Part {partName}, measure {number} contains a direction before the measure start. The visual direction was anchored at the barline.",
                            displayMeasure,
                            displayMeasure,
                            blocksTiming,
                            blocksTiming,
                            blocksTiming
                                ? ScoreCapabilityDisposition.BlocksPlaybackAndAssessment
                                : ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation));
                    }
                    var directionCursor = (int)Math.Max(0, requestedDirectionCursor);
                    var sourceOnset = measureStartBeat + directionCursor / (double)divisions;
                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 1;

                    foreach (var dynamics in Descendants(element, "dynamics"))
                    {
                        foreach (var dynamic in dynamics.Elements())
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Dynamic,
                                dynamic.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }

                    foreach (var pedal in Descendants(element, "pedal"))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Pedal,
                            (string?)pedal.Attribute("type") ?? "start",
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    var words = Descendants(element, "words")
                        .Select(Value)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (!string.IsNullOrWhiteSpace(words))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Direction,
                            words,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    if (tempo is > 0)
                        tempos.Add(new ParsedTempoChange(sourceOnset, tempo.Value, number, sourceMeasureIndex));
                    break;
                }

                case "backup":
                {
                    var backup = ParseRequiredCursorDelta(element, divisions, partName, number, "backup");
                    if (backup > cursor)
                        throw new InvalidDataException(
                            $"Part {partName}, measure {number} contains a backup before the measure start.");
                    cursor -= backup;
                    break;
                }

                case "forward":
                {
                    var forward = ParseRequiredCursorDelta(element, divisions, partName, number, "forward");
                    cursor = checked(cursor + forward);
                    maximumCursor = Math.Max(maximumCursor, cursor);
                    ValidateCursorPosition(maximumCursor, divisions, partName, number);
                    break;
                }
            }
        }

        var rawDuration = maximumCursor / (double)divisions;
        var nominalDuration = Math.Max(0.25, meter.Beats * 4d / Math.Max(1, meter.BeatType));
        var implicitMeasure = string.Equals(
            (string?)measure.Attribute("implicit"),
            "yes",
            StringComparison.OrdinalIgnoreCase);
        var duration = rawDuration > BeatEpsilon ? rawDuration : nominalDuration;
        if (!implicitMeasure && rawDuration > nominalDuration + 0.01)
        {
            warnings.Add(new ScoreValidationWarning(
                "measure-overflow",
                $"Part {partName}, measure {number} serializes {rawDuration:0.###} beats in a {meter.Beats}/{meter.BeatType} bar. " +
                "Playback and assessment are disabled until the source voices are corrected.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
            duration = nominalDuration;
        }

        var boundedNotes = new List<ScoreNote>();
        foreach (var note in notes)
        {
            var localStart = note.SourceOnsetBeats - measureStartBeat;
            if (localStart >= duration - BeatEpsilon)
            {
                warnings.Add(new ScoreValidationWarning(
                    "event-past-barline",
                    $"Part {partName}, measure {number} contains a note after the written barline. It was excluded from playback.",
                    displayMeasure,
                    displayMeasure,
                    true,
                    true,
                    ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                continue;
            }

            var boundedDuration = Math.Min(note.DurationBeats, Math.Max(0.05, duration - localStart));
            boundedNotes.Add(note with { DurationBeats = boundedDuration });
        }

        var boundedRests = new List<ScoreRest>();
        foreach (var rest in rests)
        {
            var localStart = rest.SourceOnsetBeats - measureStartBeat;
            if (localStart >= duration - BeatEpsilon)
                continue;
            boundedRests.Add(rest with
            {
                DurationBeats = Math.Min(rest.DurationBeats, Math.Max(0.05, duration - localStart))
            });
        }

        var repeatDirectives = new List<ParsedRepeatDirective>();
        var endingDirectives = new List<ParsedEndingDirective>();
        foreach (var barline in Children(measure, "barline"))
        {
            var location = ParseBarlineLocation((string?)barline.Attribute("location"));
            foreach (var repeat in Children(barline, "repeat"))
            {
                var direction = (string?)repeat.Attribute("direction") ?? string.Empty;
                var times = ParseInt((string?)repeat.Attribute("times")) ?? 2;
                repeatDirectives.Add(new ParsedRepeatDirective(direction, times, location));
            }

            foreach (var ending in Children(barline, "ending"))
            {
                endingDirectives.Add(new ParsedEndingDirective(
                    (string?)ending.Attribute("number") ?? string.Empty,
                    (string?)ending.Attribute("type") ?? string.Empty,
                    location));
            }
        }

        var boundedMarks = marks
            .Where(mark => mark.SourceOnsetBeats - measureStartBeat <= duration + BeatEpsilon)
            .ToArray();
        var boundedTempos = tempos
            .Where(change => change.SourceBeat - measureStartBeat <= duration + BeatEpsilon)
            .ToArray();
        if (boundedTempos.Length != tempos.Count)
        {
            warnings.Add(new ScoreValidationWarning(
                "tempo-past-barline",
                $"Part {partName}, measure {number} contains a tempo event after the written barline. It was excluded.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }

        var firstTempo = boundedTempos.FirstOrDefault()?.Bpm;
        return new ParsedMeasure(
            new MeasureSummary(
                number,
                noteCount,
                restCount,
                chordCount,
                lyricCount,
                staffOne,
                staffTwo,
                firstTempo is > 0 ? $"{firstTempo:0.##} BPM" : null,
                measureStartBeat,
                duration),
            boundedNotes,
            boundedRests,
            boundedMarks,
            boundedTempos,
            meterChanges,
            divisions,
            duration,
            repeatDirectives,
            endingDirectives,
            [],
            meter);
    }

    private static BarlineLocation ParseBarlineLocation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "left" => BarlineLocation.Left,
            "middle" => BarlineLocation.Middle,
            _ => BarlineLocation.Right
        };

    private static bool HasTieType(XElement note, string type) =>
        Descendants(note, "tie").Any(tie =>
            string.Equals((string?)tie.Attribute("type"), type, StringComparison.OrdinalIgnoreCase)) ||
        Descendants(note, "tied").Any(tied =>
            string.Equals((string?)tied.Attribute("type"), type, StringComparison.OrdinalIgnoreCase));

    private static int ParseRequiredCursorDelta(
        XElement element,
        int divisions,
        string partName,
        string measureNumber,
        string elementName)
    {
        var duration = ParseInt(Value(Descendant(element, "duration")));
        if (duration is null or <= 0)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains a {elementName} without a positive duration.");
        ValidateCursorDelta(duration.Value, divisions, partName, measureNumber, elementName);
        return duration.Value;
    }

    private static void ValidateCursorDelta(
        long durationDivisions,
        int divisions,
        string partName,
        string measureNumber,
        string valueName)
    {
        var maximum = checked((long)divisions * MaxMeasureBeats);
        if (durationDivisions < 0 || durationDivisions > maximum)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} has a {valueName} outside the {MaxMeasureBeats:N0}-beat measure safety limit.");
    }

    private static void ValidateCursorPosition(
        int cursor,
        int divisions,
        string partName,
        string measureNumber)
    {
        if ((long)cursor > (long)divisions * MaxMeasureBeats)
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} exceeds the {MaxMeasureBeats:N0}-beat measure safety limit.");
    }

    private static double? TempoFromDirection(
        XElement direction,
        string partName,
        string measureNumber,
        int displayMeasure,
        ICollection<ScoreValidationWarning> warnings)
    {
        var soundTempo = Descendants(direction, "sound")
            .Select(sound => (string?)sound.Attribute("tempo"))
            .FirstOrDefault(value => value is not null);
        if (soundTempo is not null)
            return ParseTempoValue(soundTempo, partName, measureNumber);

        var metronome = Descendants(direction, "metronome").FirstOrDefault();
        var metronomeTempo = Value(Descendant(metronome, "per-minute"));
        if (metronomeTempo is null)
            return null;
        var beatUnit = Value(Descendant(metronome, "beat-unit"));
        if (!string.IsNullOrWhiteSpace(beatUnit) &&
            !string.Equals(beatUnit, "quarter", StringComparison.OrdinalIgnoreCase))
        {
            AddWarningOnce(warnings, new ScoreValidationWarning(
                "unsupported-tempo-beat-unit",
                $"Part {partName}, measure {measureNumber} uses a {beatUnit} metronome unit. It remains visible, but playback and assessment are disabled because the tempo cannot be converted authoritatively.",
                displayMeasure,
                displayMeasure,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
            return null;
        }
        return ParseTempoValue(metronomeTempo, partName, measureNumber);
    }

    private static double ParseTempoValue(string value, string partName, string measureNumber)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo) ||
            !double.IsFinite(tempo) || tempo is <= 0 or > 1000)
        {
            throw new InvalidDataException(
                $"Part {partName}, measure {measureNumber} contains an invalid tempo value '{value}'.");
        }

        return tempo;
    }
}
