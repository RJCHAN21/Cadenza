<!-- SPDX-License-Identifier: GPL-3.0-only -->

# Architecture

This document describes the current pre-release implementation and its
coordinate contracts. It is not a promise of complete MusicXML or MIDI support.

## System boundary

Cadenza is a Windows WPF application targeting .NET 8. `MainWindowViewModel`
coordinates imported score state, lesson modes, input, audio preview, and
local persistence. `MainWindow` owns WebView2 and translates authoritative
.NET state into calls on the local notation page. There is no required cloud
service or account.

## Import validation

`MusicXmlImporter.Authoritative.Core.cs` accepts local score-partwise XML or MXL
archives. Before semantic parsing it enforces a 64 MiB source limit, 32 MiB
score-XML limit, 256-entry/96 MiB expanded archive limits, safe relative archive
paths, and compression-ratio checks. `XmlReader` has no resolver, ignores DTDs,
and permits no entity expansion. Repeat counts and expanded occurrence counts
are also bounded.

The parser selects a canonical part for timing/navigation, retains all parsed
parts for note content, reports unsupported or ambiguous navigation as
validation warnings, and blocks assessment over affected ranges where the
performance sequence cannot be justified.

## Timeline vocabulary

| Term | Meaning |
| --- | --- |
| Written measure | A source `<measure>` at a stable zero-based source index. |
| Written beat | Quarter-note coordinate in the unexpanded canonical score. `ScoreDocument.TotalBeats` is the written duration. |
| Performed occurrence | One traversal of a written measure, identified by `OccurrenceIndex`, source index, repeat pass, and optional repeat section. |
| Performance beat | Monotonic quarter-note coordinate across the ordered occurrences. `TotalPerformanceBeats` is the performed duration. |
| Renderer time | Verovio's unexpanded `qstamp`, therefore a written beat. |
| Playback time | Performance beat converted through the occurrence-aware tempo map. |
| Expected-note time | `ScoreNote.OnsetBeats` and `ScoreNoteGroup.OnsetBeats` on the expanded performance coordinate. |

## Authoritative performance plan

`MusicXmlImporter.Authoritative.NavigationV2.cs` analyzes forward/backward
repeats and ending regions, then emits one bounded list of
`ScoreMeasureOccurrence`. `MusicXmlImporter.Authoritative.Expansion.cs` expands
notes, rests, marks, tempo changes, and meter changes from that same list. Every
.NET consumer therefore observes the same performance identity and coordinate.

`ScoreDocument.PerformanceToSourceBeat` maps a performance position to the
written coordinate of its occurrence. `SourceToPerformanceBeat` requires a
preferred occurrence when a written beat appears more than once.

The original fixture provides the independently calculable contract:

```text
Written measures:       1  2  3  4  5
Written durations:      4  4  4  3  3  = 18 beats
Performance traversal: 1  2  3  1  2  4  5
Performed durations:    4+ 4+ 4+ 4+ 4+ 3+ 3 = 26 beats
```

Measure 3 is the first ending and jumps back to measure 1. On pass two it is
skipped; measure 4 is the second ending. These numbers are authored in
`cadenza-timeline.expected.json`, not learned from parser output.

## Renderer boundary

Verovio engraves the written score with `expandNever: true`. Its timemap remains
in written beats and is not allowed to create a competing repeat expansion.
After a score loads, `MainWindow.SecurityAndNotationPatch.cs` serializes
`ScoreDocument.PerformanceMeasures` to `setPerformanceTimeline`. The local
runtime patch selects the performed occurrence, converts performance beat to
written beat, and resolves that position in Verovio's SVG/timemap. First and
second passes can therefore point to the same engraving while keeping distinct
performance identities.

This explicit mapping resolves the former 198/278/306 disagreement: those
values mixed an unexpanded written model, Cadenza's occurrence expansion,
Verovio's separate expansion, and a stale private-score assertion. Renderer
validation now checks 18 written beats and the same 26-beat/7-occurrence plan
used by parser and simulation.

## Audio, input, and practice matching

`PianoAudioService` synthesizes bounded in-memory WAV previews from performance
beats and the expanded tempo map. Imported MIDI is a listen/reference source;
`MidiFileImporter` bounds file, header, track, event, and payload sizes and does
not claim full Standard MIDI File support.

`MidiDeviceService` receives optional live Windows MIDI messages. Computer-key
simulation reaches the same lesson matching path but is visibly a different
input source. Wait-for-you matching advances after all unique pitches in the
current `ScoreNoteGroup` are accepted. Timed mode uses a heuristic beat window.
Neither path is a professional performance assessment.

## Persistence

`LibraryStore` copies imported score files into a user-local library and keeps a
JSON manifest. `UserProfileStore` persists versioned settings and completed
attempt summaries using a temporary-file replacement. Invalid or newer profile
schemas fall back to defaults. Paths are under `%LOCALAPPDATA%`; no remote sync
or account exists.

## Security boundaries

- Score, archive, MIDI, profile, and WebView messages are untrusted inputs.
- MXL paths are validated before entry selection; archives are read, not
  extracted to arbitrary filesystem paths.
- WebView2 serves checked-in assets through `https://cadenza.local`, cancels
  untrusted navigation/new windows/downloads, denies permissions, and accepts
  bridge messages only from the trusted renderer origin.
- Imported library destinations are resolved under the local library root.
- Device and audio operations remain local Windows capabilities and are not
  exercised by mandatory CI.

These controls reduce risk but are not a claim that the application is secure
or has received a professional audit.

## Test strategy

- `ParserSmoke` verifies parser/navigation semantics and malformed XML, MXL,
  and MIDI rejection.
- `SimulationSmoke` verifies occurrence mapping, clocks, synthesized bytes,
  reference import, and a complete 15-group guided performance without hardware.
- `RendererSmoke` verifies Verovio's written duration/SVG mapping and the
  Cadenza-provided occurrence mapping for both MusicXML and MXL.
- `scripts/Validate.ps1` regenerates binary fixtures, builds Release, runs every
  mandatory check, and confirms that the worktree stays clean.

Original fixtures and their provenance live in `TestData/Fixtures`.
