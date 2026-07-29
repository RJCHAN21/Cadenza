using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Controls;

public sealed class MusicScoreView : FrameworkElement
{
    private int _lastAutoScrollKey = int.MinValue;
    private const double TopMargin = 44;
    private const double BottomMargin = 30;
    private const double SystemHeight = 360;
    private const double StaffSpacing = 12;
    private const double StaffHeight = StaffSpacing * 4;
    private const double GrandStaffGap = 46;
    private const double LeftMargin = 24;
    private const double RightMargin = 22;
    private const double SystemPrefix = 136;
    private static readonly Brush PaperBrush = new SolidColorBrush(Color.FromRgb(16, 19, 24));
    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(242, 239, 231));
    private static readonly Brush MutedInkBrush = new SolidColorBrush(Color.FromRgb(117, 125, 137));
    private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromArgb(35, 215, 169, 87));
    private static readonly Brush CursorInkBrush = new SolidColorBrush(Color.FromRgb(221, 174, 91));
    private static readonly Brush PracticeAccentBrush = new SolidColorBrush(Color.FromRgb(225, 184, 104));
    private static readonly Pen StaffPen = new(InkBrush, 0.9);
    private static readonly Pen BarlinesPen = new(InkBrush, 1.15);

    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score),
        typeof(ScoreDocument),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutChanged));

    public static readonly DependencyProperty CursorBeatProperty = DependencyProperty.Register(
        nameof(CursorBeat),
        typeof(double),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnCursorBeatChanged));

    public static readonly DependencyProperty PracticeModeProperty = DependencyProperty.Register(
        nameof(PracticeMode),
        typeof(PracticeMode),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(PracticeMode.BothHands, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FeedbackBeatProperty = DependencyProperty.Register(
        nameof(FeedbackBeat),
        typeof(double),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FeedbackPulseProperty = DependencyProperty.Register(
        nameof(FeedbackPulse),
        typeof(int),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(0, OnFeedbackPulseChanged));

    public static readonly DependencyProperty ReadingModeProperty = DependencyProperty.Register(
        nameof(ReadingMode),
        typeof(ScoreReadingMode),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(ScoreReadingMode.Page,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutChanged));

    public static readonly DependencyProperty HeldNoteNumbersProperty = DependencyProperty.Register(
        nameof(HeldNoteNumbers),
        typeof(IReadOnlySet<int>),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty AnimatedCursorBeatProperty = DependencyProperty.Register(
        "AnimatedCursorBeat",
        typeof(double),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty FeedbackProgressProperty = DependencyProperty.Register(
        "FeedbackProgress",
        typeof(double),
        typeof(MusicScoreView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ScoreDocument? Score
    {
        get => (ScoreDocument?)GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    public double CursorBeat
    {
        get => (double)GetValue(CursorBeatProperty);
        set => SetValue(CursorBeatProperty, value);
    }

    public PracticeMode PracticeMode
    {
        get => (PracticeMode)GetValue(PracticeModeProperty);
        set => SetValue(PracticeModeProperty, value);
    }

    public double FeedbackBeat
    {
        get => (double)GetValue(FeedbackBeatProperty);
        set => SetValue(FeedbackBeatProperty, value);
    }

    public int FeedbackPulse
    {
        get => (int)GetValue(FeedbackPulseProperty);
        set => SetValue(FeedbackPulseProperty, value);
    }

    public ScoreReadingMode ReadingMode
    {
        get => (ScoreReadingMode)GetValue(ReadingModeProperty);
        set => SetValue(ReadingModeProperty, value);
    }

    public IReadOnlySet<int>? HeldNoteNumbers
    {
        get => (IReadOnlySet<int>?)GetValue(HeldNoteNumbersProperty);
        set => SetValue(HeldNoteNumbersProperty, value);
    }

    private double AnimatedCursorBeat => (double)GetValue(AnimatedCursorBeatProperty);
    private double FeedbackProgress => (double)GetValue(FeedbackProgressProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ReadingMode == ScoreReadingMode.Continuous)
        {
            var continuousMeasureWidth = Math.Max(360, Score?.Measures.Select(EstimateMeasureWidth).DefaultIfEmpty(360).Max() ?? 360);
            var continuousWidth = LeftMargin + SystemPrefix + Math.Max(1, Score?.Measures.Count ?? 1) * continuousMeasureWidth + RightMargin;
            return new Size(continuousWidth, TopMargin + SystemHeight + BottomMargin);
        }
        var width = double.IsInfinity(availableSize.Width) ? 1040 : Math.Max(760, availableSize.Width);
        var systems = Math.Max(1, BuildPageSystems(width).Count);
        return new Size(width, TopMargin + systems * SystemHeight + BottomMargin);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(PaperBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Score is null || Score.Measures.Count == 0)
        {
            DrawCenteredText(drawingContext, "Import a MusicXML piano score to display notation.", ActualWidth / 2, 160, 18, MutedInkBrush);
            return;
        }

        var systems = ReadingMode == ScoreReadingMode.Continuous
            ? new List<IReadOnlyList<MeasureSummary>> { Score.Measures }
            : BuildPageSystems(ActualWidth);
        var measureOffset = 0;
        for (var systemIndex = 0; systemIndex < systems.Count; systemIndex++)
        {
            var systemMeasures = systems[systemIndex];
            DrawSystem(drawingContext, systemMeasures, systemIndex, measureOffset);
            measureOffset += systemMeasures.Count;
        }
    }

    private void DrawSystem(DrawingContext dc, IReadOnlyList<MeasureSummary> measures, int systemIndex, int measureOffset)
    {
        if (Score is null || measures.Count == 0) return;
        var systemTop = TopMargin + systemIndex * SystemHeight;
        var trebleTop = systemTop + 16;
        var bassTop = trebleTop + StaffHeight + GrandStaffGap;
        var contentStart = LeftMargin + SystemPrefix;
        var contentWidth = Math.Max(500, ActualWidth - contentStart - RightMargin);
        var measureWidth = contentWidth / Math.Max(1, measures.Count);

        DrawCurrentMeasureHighlight(dc, measures, contentStart, measureWidth, trebleTop, bassTop);
        DrawGrandStaff(dc, trebleTop, bassTop, contentStart, contentWidth);
        DrawBrace(dc, trebleTop, bassTop);
        DrawClefs(dc, trebleTop, bassTop);
        DrawKeySignature(dc, trebleTop, bassTop);
        DrawTimeSignature(dc, trebleTop, bassTop);

        for (var index = 0; index < measures.Count; index++)
        {
            var measure = measures[index];
            var x = contentStart + index * measureWidth;
            dc.DrawLine(BarlinesPen, new Point(x, trebleTop), new Point(x, bassTop + StaffHeight));
            DrawText(dc, measure.Number, x + 5, trebleTop - 18, 10, MutedInkBrush, FontWeights.SemiBold);
            DrawMeasureContent(dc, measure, x, measureWidth, trebleTop, bassTop);
        }

        var finalX = contentStart + measures.Count * measureWidth;
        dc.DrawLine(BarlinesPen, new Point(finalX, trebleTop), new Point(finalX, bassTop + StaffHeight));
        if (measureOffset + measures.Count >= Score.Measures.Count)
        {
            dc.DrawLine(new Pen(InkBrush, 2.6), new Point(finalX - 4, trebleTop), new Point(finalX - 4, bassTop + StaffHeight));
        }

        DrawCursor(dc, measures, contentStart, measureWidth, trebleTop, bassTop);
        DrawUpcomingHint(dc, measures, contentStart, measureWidth, trebleTop, bassTop);
    }

    private void DrawGrandStaff(DrawingContext dc, double trebleTop, double bassTop, double contentStart, double contentWidth)
    {
        for (var line = 0; line < 5; line++)
        {
            var trebleY = trebleTop + line * StaffSpacing;
            var bassY = bassTop + line * StaffSpacing;
            dc.DrawLine(StaffPen, new Point(LeftMargin + 15, trebleY), new Point(contentStart + contentWidth, trebleY));
            dc.DrawLine(StaffPen, new Point(LeftMargin + 15, bassY), new Point(contentStart + contentWidth, bassY));
        }

        dc.DrawLine(new Pen(InkBrush, 1.5), new Point(LeftMargin + 15, trebleTop), new Point(LeftMargin + 15, bassTop + StaffHeight));
    }

    private void DrawBrace(DrawingContext dc, double trebleTop, double bassTop)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var centerY = (trebleTop + bassTop + StaffHeight) / 2;
            context.BeginFigure(new Point(LeftMargin + 10, trebleTop), false, false);
            context.BezierTo(
                new Point(LeftMargin - 2, trebleTop + 22),
                new Point(LeftMargin + 4, centerY - 18),
                new Point(LeftMargin - 4, centerY),
                true,
                true);
            context.BezierTo(
                new Point(LeftMargin + 4, centerY + 18),
                new Point(LeftMargin - 2, bassTop + 18),
                new Point(LeftMargin + 10, bassTop + StaffHeight),
                true,
                true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(InkBrush, 1.8), geometry);
    }

    private void DrawClefs(DrawingContext dc, double trebleTop, double bassTop)
    {
        DrawText(dc, "\U0001D11E", LeftMargin + 22, trebleTop - 17, 47, InkBrush, FontWeights.Normal, "Segoe UI Symbol");
        DrawText(dc, "\U0001D122", LeftMargin + 25, bassTop - 9, 39, InkBrush, FontWeights.Normal, "Segoe UI Symbol");
    }

    private void DrawKeySignature(DrawingContext dc, double trebleTop, double bassTop)
    {
        if (Score is null || Score.KeyFifths == 0) return;
        var isFlat = Score.KeyFifths < 0;
        var glyph = isFlat ? "\u266D" : "\u266F";
        var count = Math.Min(7, Math.Abs(Score.KeyFifths));
        var trebleOffsets = isFlat
            ? new[] { 20d, 5d, 25d, 10d, 30d, 15d, 35d }
            : new[] { 10d, 25d, 5d, 20d, 35d, 15d, 30d };
        var bassOffsets = isFlat
            ? new[] { 10d, 25d, 5d, 20d, 0d, 15d, 30d }
            : new[] { 20d, 35d, 15d, 30d, 10d, 25d, 5d };

        for (var index = 0; index < count; index++)
        {
            var x = LeftMargin + 69 + index * 10;
            DrawText(dc, glyph, x, trebleTop + trebleOffsets[index] - 12, 20, InkBrush, FontWeights.Normal, "Segoe UI Symbol");
            DrawText(dc, glyph, x, bassTop + bassOffsets[index] - 12, 20, InkBrush, FontWeights.Normal, "Segoe UI Symbol");
        }
    }

    private void DrawTimeSignature(DrawingContext dc, double trebleTop, double bassTop)
    {
        if (Score is null) return;
        var x = LeftMargin + 102;
        DrawCenteredText(dc, Score.BeatsPerMeasure.ToString(CultureInfo.InvariantCulture), x, trebleTop + 8, 18, InkBrush, FontWeights.Bold);
        DrawCenteredText(dc, Score.BeatType.ToString(CultureInfo.InvariantCulture), x, trebleTop + 28, 18, InkBrush, FontWeights.Bold);
        DrawCenteredText(dc, Score.BeatsPerMeasure.ToString(CultureInfo.InvariantCulture), x, bassTop + 8, 18, InkBrush, FontWeights.Bold);
        DrawCenteredText(dc, Score.BeatType.ToString(CultureInfo.InvariantCulture), x, bassTop + 28, 18, InkBrush, FontWeights.Bold);
    }

    private void DrawMeasureContent(DrawingContext dc, MeasureSummary measure, double measureX, double measureWidth, double trebleTop, double bassTop)
    {
        if (Score is null) return;
        var measureDuration = Math.Max(0.25, measure.DurationBeats);
        var rightBeat = measure.StartBeat + measureDuration + 0.0001;
        var notes = Score.Notes
            .Where(note => note.OnsetBeats >= measure.StartBeat - 0.0001 && note.OnsetBeats < rightBeat)
            .ToArray();
        var rests = Score.Rests
            .Where(rest => rest.OnsetBeats >= measure.StartBeat - 0.0001 && rest.OnsetBeats < rightBeat)
            .ToArray();

        foreach (var staff in new[] { 1, 2 })
        {
            var staffTop = staff == 1 ? trebleTop : bassTop;
            var staffNotes = notes.Where(note => ResolveStaff(note.StaffNumber, note.MidiNoteNumber) == staff).ToArray();
            var beamVisuals = new List<BeamVisual>();
            foreach (var group in staffNotes.GroupBy(note => Math.Round(note.OnsetBeats, 5)).OrderBy(group => group.Key))
            {
                var x = BeatToXWithSpacing(group.Key, measure, measureX, measureWidth, notes, rests);
                var chord = group
                    .GroupBy(note => note.MidiNoteNumber)
                    .Select(noteGroup => noteGroup.First())
                    .OrderBy(note => note.MidiNoteNumber)
                    .ToArray();
                beamVisuals.Add(DrawChord(dc, chord, x, staffTop, staff));
            }
            DrawBeams(dc, beamVisuals);

            foreach (var rest in rests.Where(rest => ResolveStaff(rest.StaffNumber, 60) == staff))
            {
                var x = BeatToXWithSpacing(rest.OnsetBeats, measure, measureX, measureWidth, notes, rests) +
                        (rest.Voice == "1" ? -5 : 7);
                DrawRest(dc, rest, x, staffTop, IsStaffActive(staff) ? InkBrush : MutedInkBrush);
            }
        }

        var lastLyricRight = measureX + 5;
        foreach (var lyricNote in notes.Where(note => !string.IsNullOrWhiteSpace(note.Lyric)).OrderBy(note => note.OnsetBeats))
        {
            var noteX = BeatToXWithSpacing(lyricNote.OnsetBeats, measure, measureX, measureWidth, notes, rests);
            var estimatedWidth = Math.Max(8, lyricNote.Lyric!.Length * 5.6);
            var x = Math.Max(noteX, lastLyricRight + 4 + estimatedWidth / 2);
            x = Math.Min(x, measureX + measureWidth - estimatedWidth / 2 - 4);
            if (Math.Abs(x - noteX) > 5)
            {
                dc.DrawLine(new Pen(MutedInkBrush, .65), new Point(noteX, bassTop + StaffHeight + 7), new Point(x, bassTop + StaffHeight + 14));
            }
            DrawCenteredText(dc, lyricNote.Lyric!, x, bassTop + StaffHeight + 22, 10.5, InkBrush);
            lastLyricRight = x + estimatedWidth / 2;
        }

        foreach (var mark in Score.Marks.Where(mark =>
                     mark.OnsetBeats >= measure.StartBeat - 0.0001 &&
                     mark.OnsetBeats < rightBeat))
        {
            var x = BeatToX(mark.OnsetBeats, measure, measureX, measureWidth);
            var staffTop = mark.StaffNumber == 2 ? bassTop : trebleTop;
            var text = mark.Kind switch
            {
                ScoreMarkKind.Dynamic => mark.Text.ToLowerInvariant(),
                ScoreMarkKind.Pedal when mark.Text.Equals("stop", StringComparison.OrdinalIgnoreCase) => "*",
                ScoreMarkKind.Pedal => "Ped.",
                ScoreMarkKind.Articulation when mark.Text.Equals("staccato", StringComparison.OrdinalIgnoreCase) => "•",
                ScoreMarkKind.Articulation when mark.Text.Contains("accent", StringComparison.OrdinalIgnoreCase) => ">",
                ScoreMarkKind.Articulation when mark.Text.Equals("tenuto", StringComparison.OrdinalIgnoreCase) => "–",
                _ => mark.Text
            };
            var y = mark.Kind == ScoreMarkKind.Articulation ? staffTop - 17 : staffTop + StaffHeight + 9;
            DrawCenteredText(dc, text, x, y, mark.Kind == ScoreMarkKind.Dynamic ? 12 : 9.5, InkBrush,
                mark.Kind == ScoreMarkKind.Dynamic ? FontWeights.SemiBold : FontWeights.Normal);
        }
    }

    private BeamVisual DrawChord(DrawingContext dc, IReadOnlyList<ScoreNote> chord, double x, double staffTop, int staff)
    {
        var isActiveStaff = IsStaffActive(staff);
        var baseBrush = isActiveStaff ? InkBrush : MutedInkBrush;
        var isCurrent = chord.Any(note => Math.Abs(note.OnsetBeats - AnimatedCursorBeat) < 0.02);
        var isCorrectFeedback = FeedbackProgress > 0.001 && chord.Any(note => Math.Abs(note.OnsetBeats - FeedbackBeat) < 0.02);
        var noteBrush = isCurrent ? PracticeAccentBrush : baseBrush;
        var noteYs = chord.Select(note => PitchToY(note, staffTop, staff)).ToArray();
        var stemValue = chord.Select(note => note.Stem).FirstOrDefault(stem => !string.IsNullOrWhiteSpace(stem));
        var stemUp = string.Equals(stemValue, "up", StringComparison.OrdinalIgnoreCase) ||
                     (!string.Equals(stemValue, "down", StringComparison.OrdinalIgnoreCase) && noteYs.Average() >= staffTop + StaffHeight / 2);
        var noteType = chord[0].NoteType;
        var filled = noteType is not ("whole" or "half");
        var hasStem = noteType != "whole";

        for (var index = 0; index < chord.Count; index++)
        {
            var note = chord[index];
            var y = noteYs[index];
            var offsetX = index > 0 && Math.Abs(noteYs[index] - noteYs[index - 1]) <= 5.1 ? (stemUp ? -5 : 5) : 0;
            DrawLedgerLines(dc, x + offsetX, y, staffTop, baseBrush);
            var accidentalColumn = 0;
            for (var prior = 0; prior < index; prior++)
            {
                if (chord[prior].Alter != 0 && Math.Abs(noteYs[prior] - y) < 16) accidentalColumn++;
            }
            DrawAccidental(dc, note, x + offsetX - accidentalColumn * 9, y, baseBrush);
            if (isCorrectFeedback)
            {
                DrawCorrectFeedback(dc, x + offsetX, y);
            }
            DrawNoteHead(dc, x + offsetX, y, filled, noteBrush);
            if (HeldNoteNumbers?.Contains(note.MidiNoteNumber) == true)
            {
                var holdBrush = new SolidColorBrush(Color.FromArgb(205, 28, 139, 147));
                dc.DrawEllipse(null, new Pen(holdBrush, 1.8), new Point(x + offsetX, y), 8, 6);
                var tailLength = Math.Clamp(14 + note.DurationBeats * 16, 18, 65);
                dc.DrawLine(new Pen(holdBrush, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(x + offsetX + 7, y), new Point(x + offsetX + tailLength, y));
            }
            for (var dot = 0; dot < note.DotCount; dot++)
            {
                dc.DrawEllipse(noteBrush, null, new Point(x + offsetX + 9 + dot * 5, y - 1), 1.7, 1.7);
            }
        }

        var stemX = stemUp ? x + 4 : x - 4;
        var stemStartY = stemUp ? noteYs.Max() : noteYs.Min();
        var stemEndY = stemUp ? noteYs.Min() - 31 : noteYs.Max() + 31;
        if (hasStem)
        {
            dc.DrawLine(new Pen(baseBrush, 1.25), new Point(stemX, stemStartY), new Point(stemX, stemEndY));
        }

        var beams = chord.Select(note => note.Beams).FirstOrDefault(list => list.Count > 0) ?? [];
        if (hasStem && beams.Count == 0 && noteType is "eighth" or "16th" or "32nd")
        {
            DrawFlag(dc, stemX, stemEndY, stemUp, baseBrush, noteType == "eighth" ? 1 : noteType == "16th" ? 2 : 3);
        }

        return new BeamVisual(stemX, stemEndY, stemUp, beams, baseBrush);
    }

    private void DrawBeams(DrawingContext dc, IReadOnlyList<BeamVisual> notes)
    {
        for (var index = 0; index < notes.Count - 1; index++)
        {
            var current = notes[index];
            var next = notes[index + 1];
            var levelCount = Math.Min(current.Beams.Count, next.Beams.Count);
            for (var level = 0; level < levelCount; level++)
            {
                var currentType = current.Beams[level];
                var nextType = next.Beams[level];
                if (currentType is not ("begin" or "continue") || nextType is not ("continue" or "end")) continue;
                var direction = current.StemUp ? 1 : -1;
                var offset = level * 6 * direction;
                dc.DrawLine(
                    new Pen(current.Brush, 4.2),
                    new Point(current.X, current.StemEndY + offset),
                    new Point(next.X, next.StemEndY + offset));
            }
        }
    }

    private void DrawNoteHead(DrawingContext dc, double x, double y, bool filled, Brush brush)
    {
        dc.PushTransform(new RotateTransform(-15, x, y));
        dc.DrawEllipse(filled ? brush : PaperBrush, new Pen(brush, 1.35), new Point(x, y), 5.2, 3.7);
        dc.Pop();
    }

    private void DrawAccidental(DrawingContext dc, ScoreNote note, double x, double y, Brush brush)
    {
        if (note.Alter == 0) return;
        var glyph = note.Alter < 0 ? "\u266D" : "\u266F";
        DrawText(dc, glyph, x - 15, y - 11, 15, brush, FontWeights.Normal, "Segoe UI Symbol");
    }

    private void DrawLedgerLines(DrawingContext dc, double x, double y, double staffTop, Brush brush)
    {
        var pen = new Pen(brush, 0.95);
        if (y > staffTop + StaffHeight + 1)
        {
            for (var ledgerY = staffTop + StaffHeight + StaffSpacing; ledgerY <= y + 1; ledgerY += StaffSpacing)
            {
                dc.DrawLine(pen, new Point(x - 8, ledgerY), new Point(x + 8, ledgerY));
            }
        }
        else if (y < staffTop - 1)
        {
            for (var ledgerY = staffTop - StaffSpacing; ledgerY >= y - 1; ledgerY -= StaffSpacing)
            {
                dc.DrawLine(pen, new Point(x - 8, ledgerY), new Point(x + 8, ledgerY));
            }
        }
    }

    private void DrawFlag(DrawingContext dc, double stemX, double stemEndY, bool stemUp, Brush brush, int count)
    {
        for (var flag = 0; flag < count; flag++)
        {
            var offset = flag * 6 * (stemUp ? 1 : -1);
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            var start = new Point(stemX, stemEndY + offset);
            context.BeginFigure(start, false, false);
            context.BezierTo(
                new Point(stemX + 11, stemEndY + (stemUp ? 4 : -4) + offset),
                new Point(stemX + 10, stemEndY + (stemUp ? 15 : -15) + offset),
                new Point(stemX + 4, stemEndY + (stemUp ? 19 : -19) + offset),
                true,
                false);
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 2.2), geometry);
        }
    }

    private void DrawRest(DrawingContext dc, ScoreRest rest, double x, double staffTop, Brush brush)
    {
        var middleY = staffTop + StaffSpacing * 2;
        switch (rest.NoteType)
        {
            case "whole":
                dc.DrawRectangle(brush, null, new Rect(x - 6, middleY, 12, 4));
                break;
            case "half":
                dc.DrawRectangle(brush, null, new Rect(x - 6, middleY - 4, 12, 4));
                break;
            case "eighth":
            case "16th":
            case "32nd":
                dc.DrawLine(new Pen(brush, 1.7), new Point(x, middleY - 12), new Point(x, middleY + 10));
                dc.DrawEllipse(brush, null, new Point(x + 4, middleY - 10), 3.5, 3);
                if (rest.NoteType != "eighth") dc.DrawEllipse(brush, null, new Point(x + 4, middleY - 3), 3.5, 3);
                break;
            default:
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(x + 3, middleY - 14), false, false);
                    context.LineTo(new Point(x - 3, middleY - 4), true, false);
                    context.LineTo(new Point(x + 4, middleY + 1), true, false);
                    context.LineTo(new Point(x - 4, middleY + 12), true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, new Pen(brush, 2.2), geometry);
                break;
        }

        for (var dot = 0; dot < rest.DotCount; dot++)
        {
            dc.DrawEllipse(brush, null, new Point(x + 10 + dot * 5, middleY), 1.5, 1.5);
        }
    }

    private void DrawCurrentMeasureHighlight(DrawingContext dc, IReadOnlyList<MeasureSummary> measures, double contentStart, double measureWidth, double trebleTop, double bassTop)
    {
        for (var index = 0; index < measures.Count; index++)
        {
            var measure = measures[index];
            if (AnimatedCursorBeat < measure.StartBeat - 0.001 || AnimatedCursorBeat > measure.StartBeat + measure.DurationBeats + 0.001) continue;
            var x = contentStart + index * measureWidth;
            dc.DrawRoundedRectangle(CursorBrush, null, new Rect(x + 1, trebleTop - 13, measureWidth - 2, bassTop + StaffHeight - trebleTop + 25), 4, 4);
            break;
        }
    }

    private void DrawCursor(DrawingContext dc, IReadOnlyList<MeasureSummary> measures, double contentStart, double measureWidth, double trebleTop, double bassTop)
    {
        for (var index = 0; index < measures.Count; index++)
        {
            var measure = measures[index];
            if (AnimatedCursorBeat < measure.StartBeat - 0.001 || AnimatedCursorBeat > measure.StartBeat + measure.DurationBeats + 0.001) continue;
            var notes = Score?.Notes.Where(note => note.MeasureNumber == measure.Number).ToArray() ?? [];
            var rests = Score?.Rests.Where(rest => rest.MeasureNumber == measure.Number).ToArray() ?? [];
            var x = BeatToXWithSpacing(AnimatedCursorBeat, measure, contentStart + index * measureWidth, measureWidth, notes, rests);
            dc.DrawLine(new Pen(CursorInkBrush, 2), new Point(x, trebleTop - 12), new Point(x, bassTop + StaffHeight + 10));
            dc.DrawEllipse(CursorInkBrush, null, new Point(x, trebleTop - 14), 4.2, 4.2);
            break;
        }
    }

    private void DrawUpcomingHint(DrawingContext dc, IReadOnlyList<MeasureSummary> measures, double contentStart, double measureWidth, double trebleTop, double bassTop)
    {
        if (Score is null) return;
        var nextBeat = Score.Notes
            .Where(note => note.OnsetBeats > AnimatedCursorBeat + 0.02)
            .Select(note => note.OnsetBeats)
            .DefaultIfEmpty(-1)
            .Min();
        if (nextBeat < 0) return;

        for (var index = 0; index < measures.Count; index++)
        {
            var measure = measures[index];
            if (nextBeat < measure.StartBeat - 0.001 || nextBeat > measure.StartBeat + measure.DurationBeats + 0.001) continue;
            var notes = Score.Notes.Where(note => note.MeasureNumber == measure.Number).ToArray();
            var rests = Score.Rests.Where(rest => rest.MeasureNumber == measure.Number).ToArray();
            var x = BeatToXWithSpacing(nextBeat, measure, contentStart + index * measureWidth, measureWidth, notes, rests);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(95, 28, 139, 147)), 1.2) { DashStyle = DashStyles.Dot };
            dc.DrawLine(pen, new Point(x, trebleTop - 7), new Point(x, bassTop + StaffHeight + 5));
            break;
        }
    }

    private double PitchToY(ScoreNote note, double staffTop, int staff)
    {
        var stepIndex = note.Step switch
        {
            "C" => 0,
            "D" => 1,
            "E" => 2,
            "F" => 3,
            "G" => 4,
            "A" => 5,
            "B" => 6,
            _ => 0
        };
        var diatonicIndex = note.Octave * 7 + stepIndex;
        var bottomLineIndex = staff == 1 ? 30 : 18;
        return staffTop + StaffHeight - (diatonicIndex - bottomLineIndex) * (StaffSpacing / 2);
    }

    private static int ResolveStaff(int staff, int midiNote) => staff is 1 or 2 ? staff : midiNote >= 60 ? 1 : 2;

    private bool IsStaffActive(int staff) => PracticeMode switch
    {
        PracticeMode.LeftHand => staff == 2,
        PracticeMode.RightHand => staff == 1,
        _ => true
    };

    private static double BeatToX(double beat, MeasureSummary measure, double measureX, double measureWidth)
    {
        var localBeat = Math.Clamp(beat - measure.StartBeat, 0, Math.Max(0.01, measure.DurationBeats));
        return measureX + 16 + localBeat / Math.Max(0.01, measure.DurationBeats) * Math.Max(20, measureWidth - 32);
    }

    private double BeatToXWithSpacing(
        double beat,
        MeasureSummary measure,
        double measureX,
        double measureWidth,
        IReadOnlyList<ScoreNote> notes,
        IReadOnlyList<ScoreRest> rests)
    {
        var onsets = notes.Select(note => note.OnsetBeats)
            .Concat(rests.Select(rest => rest.OnsetBeats))
            .Append(measure.StartBeat)
            .Append(measure.StartBeat + Math.Max(.01, measure.DurationBeats))
            .DistinctBy(value => Math.Round(value, 5))
            .OrderBy(value => value)
            .ToArray();
        if (onsets.Length <= 1) return BeatToX(beat, measure, measureX, measureWidth);

        var left = measureX + 22;
        var usable = Math.Max(40, measureWidth - 44);
        var positions = new double[onsets.Length];
        for (var index = 0; index < onsets.Length; index++)
        {
            var natural = left + Math.Clamp((onsets[index] - measure.StartBeat) / Math.Max(.01, measure.DurationBeats), 0, 1) * usable;
            var even = left + index / (double)(onsets.Length - 1) * usable;
            positions[index] = natural * .42 + even * .58;
        }

        if (beat <= onsets[0]) return positions[0];
        if (beat >= onsets[^1]) return positions[^1];
        for (var index = 0; index < onsets.Length - 1; index++)
        {
            if (beat > onsets[index + 1]) continue;
            var span = Math.Max(.0001, onsets[index + 1] - onsets[index]);
            var progress = Math.Clamp((beat - onsets[index]) / span, 0, 1);
            return positions[index] + (positions[index + 1] - positions[index]) * progress;
        }
        return positions[^1];
    }

    private List<IReadOnlyList<MeasureSummary>> BuildPageSystems(double width)
    {
        var result = new List<IReadOnlyList<MeasureSummary>>();
        if (Score is null || Score.Measures.Count == 0) return result;
        var available = Math.Max(460, width - LeftMargin - SystemPrefix - RightMargin);
        var current = new List<MeasureSummary>();
        var used = 0d;
        foreach (var measure in Score.Measures)
        {
            var estimate = Math.Min(available, EstimateMeasureWidth(measure));
            if (current.Count > 0 && (used + estimate > available || current.Count >= 2))
            {
                result.Add(current.ToArray());
                current.Clear();
                used = 0;
            }
            current.Add(measure);
            used += estimate;
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    private double EstimateMeasureWidth(MeasureSummary measure)
    {
        if (Score is null) return 360;
        var notes = Score.Notes.Where(note => note.MeasureNumber == measure.Number).ToArray();
        var rests = Score.Rests.Where(rest => rest.MeasureNumber == measure.Number).ToArray();
        var onsets = notes.Select(note => Math.Round(note.OnsetBeats, 5))
            .Concat(rests.Select(rest => Math.Round(rest.OnsetBeats, 5)))
            .Distinct()
            .Count();
        var accidentalCount = notes.Count(note => note.Alter != 0);
        var maximumChord = notes.GroupBy(note => Math.Round(note.OnsetBeats, 5)).Select(group => group.Select(note => note.MidiNoteNumber).Distinct().Count()).DefaultIfEmpty(1).Max();
        var lyricCharacters = notes.Where(note => !string.IsNullOrWhiteSpace(note.Lyric)).Sum(note => note.Lyric!.Length + 1);
        return Math.Clamp(170 + onsets * 28 + accidentalCount * 3 + Math.Max(0, maximumChord - 2) * 14 + lyricCharacters * 4.6, 260, 820);
    }

    private void BringCursorIntoView()
    {
        if (Score is null || ActualWidth <= 0) return;
        var index = Score.Measures
            .Select((measure, measureIndex) => (measure, measureIndex))
            .FirstOrDefault(pair => CursorBeat >= pair.measure.StartBeat - 0.001 && CursorBeat <= pair.measure.StartBeat + pair.measure.DurationBeats + 0.001)
            .measureIndex;
        if (ReadingMode == ScoreReadingMode.Continuous)
        {
            var width = Math.Max(360, Score.Measures.Select(EstimateMeasureWidth).DefaultIfEmpty(360).Max());
            var x = LeftMargin + SystemPrefix + index * width;
            if (_lastAutoScrollKey == index) return;
            _lastAutoScrollKey = index;
            BringIntoView(new Rect(Math.Max(0, x - 210), 0, 720, TopMargin + SystemHeight));
        }
        else
        {
            var systems = BuildPageSystems(ActualWidth);
            var runningIndex = 0;
            var systemIndex = 0;
            for (; systemIndex < systems.Count; systemIndex++)
            {
                if (index < runningIndex + systems[systemIndex].Count) break;
                runningIndex += systems[systemIndex].Count;
            }
            var key = 10_000 + systemIndex;
            if (_lastAutoScrollKey == key) return;
            _lastAutoScrollKey = key;
            BringIntoView(new Rect(0, TopMargin + systemIndex * SystemHeight - 8, ActualWidth, SystemHeight));
        }
    }

    private static void OnCursorBeatChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not MusicScoreView scoreView) return;
        var to = (double)args.NewValue;
        var delta = Math.Abs(to - scoreView.AnimatedCursorBeat);
        if (!SystemParameters.ClientAreaAnimation || delta < .18)
        {
            scoreView.BeginAnimation(AnimatedCursorBeatProperty, null);
            scoreView.SetValue(AnimatedCursorBeatProperty, to);
        }
        else
        {
            var animation = new DoubleAnimation
            {
                From = scoreView.AnimatedCursorBeat,
                To = to,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            scoreView.BeginAnimation(AnimatedCursorBeatProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        scoreView.Dispatcher.BeginInvoke(DispatcherPriority.Background, scoreView.BringCursorIntoView);
    }

    private static void OnLayoutChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MusicScoreView scoreView) scoreView._lastAutoScrollKey = int.MinValue;
    }

    private static void OnFeedbackPulseChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not MusicScoreView scoreView) return;
        scoreView.BeginAnimation(FeedbackProgressProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            scoreView.SetValue(FeedbackProgressProperty, 0d);
            return;
        }

        scoreView.BeginAnimation(
            FeedbackProgressProperty,
            new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(620))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void DrawCorrectFeedback(DrawingContext dc, double x, double y)
    {
        var progress = Math.Clamp(FeedbackProgress, 0d, 1d);
        var glowAlpha = (byte)(54 * progress);
        var sparkleAlpha = (byte)(210 * progress);
        var glowBrush = new SolidColorBrush(Color.FromArgb(glowAlpha, 57, 197, 162));
        dc.DrawEllipse(glowBrush, null, new Point(x, y), 10 + (1 - progress) * 8, 8 + (1 - progress) * 7);

        var travel = 8 + (1 - progress) * 18;
        var sparkleBrush = new SolidColorBrush(Color.FromArgb(sparkleAlpha, 236, 190, 86));
        for (var index = 0; index < 5; index++)
        {
            var angle = -Math.PI * 0.85 + index * Math.PI * 0.42;
            var sparkleX = x + Math.Cos(angle) * travel;
            var sparkleY = y + Math.Sin(angle) * travel * 0.72;
            var radius = 1.2 + progress * 1.3;
            dc.DrawEllipse(sparkleBrush, null, new Point(sparkleX, sparkleY), radius, radius);
        }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, FontWeight? weight = null, string fontFamily = "Segoe UI")
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(fontFamily), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private void DrawCenteredText(DrawingContext dc, string text, double centerX, double y, double size, Brush brush, FontWeight? weight = null)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, y));
    }

    private sealed record BeamVisual(double X, double StemEndY, bool StemUp, IReadOnlyList<string> Beams, Brush Brush);
}
