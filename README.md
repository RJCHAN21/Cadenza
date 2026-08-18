# Cadenza (Piano Practice Studio)

Piano Practice Studio is a local Windows desktop piano-learning prototype. The app shell and lesson engine are native WPF/.NET 8. Standard score engraving is performed locally by bundled Verovio 6.2 and the Bravura SMuFL font inside WebView2; no score data is uploaded.

## Run

```powershell
dotnet run --project .\PianoPractice.Desktop\PianoPractice.Desktop.csproj
```

On startup the app looks for `Downloads\olivia-rodrigo-drivers-license.mxl` and imports it automatically when present. Import MusicXML accepts `.mxl`, `.musicxml`, and `.xml`.

## Implemented vertical slice

- Dashboard/library and dedicated notation-first lesson player.
- MusicXML/MXL import with editable title. The supplied sample displays exactly `drivers license`.
- Verovio/SMuFL grand-staff engraving with canonical clefs, notes, rests, beams, ties, key/time signatures, chord symbols, and lyrics from authoritative MusicXML.
- High-contrast ivory notation on charcoal, cyan rendered-position playhead, Page and Continuous layouts, zoom, directional page transitions, focused bar range, and 50-120% lesson tempo.
- Listen transport with previous/next-measure cueing, restart from the selected range start, real pause/resume, stop, and local piano-like score audio.
- Generic WinMM device discovery and persistent callback capture. Device Settings separates open/start state from real `MIM_DATA` callbacks and exposes last key, monitor/thru, volume, audio test, latency calibration, sustain setup, and a bounded diagnostic trace.
- Practice/Wait keeps the playhead anchored on the expected onset and advances only after the expected note/chord. Performance/Timed runs continuously with metronome and grades correct, missed, extra, timing, and hold duration.
- Safe app controls use `Ctrl+1`, `Ctrl+2`, and `Ctrl+3` to select Listen, Practice, and Performance without starting them. `Space` controls the selected mode, `Esc` stops, and `Ctrl+R` restarts. Mode selection is locked during active, paused, preparing, and results states.
- The optional MIDI Remote preserves note-key shortcuts without treating ordinary playing as commands: tap and release the reserved Remote key (`C6` by default) with no other notes held, then press one bound command key within three seconds. Commands are context-gated, held/repeated notes cannot retrigger, and `C4` restarts by default. Bindings can be learned or cleared in Settings.
- Immediate score feedback uses shape plus color: green check for correct, red diamond/cross for missed or extra, cyan hold guidance, and amber early-release feedback. Markers persist for static post-run score review.
- Left, right, and both-hand practice; CC64 monitoring; pedal grading only when the source contains pedal data.
- MIDI and PDF reference import. PDF is opened as a review source; it is not falsely converted by unreliable recognition.
- Clearly labeled computer-keyboard input is available without MIDI hardware. `A S D F G H J K L ;` are consecutive white keys and `W E T Y U O P` are the applicable black keys; both feed the same lesson/scoring pipeline while remaining visibly distinct from MIDI.
- Verovio time output uses the same non-expanded 50-measure timeline as the MusicXML lesson engine. WPF's monotonic clock is authoritative for audio, metronome, scoring, and the coalesced renderer cursor.
- Versioned user-local preferences persist MIDI device identity, monitor/audio controls, tempo, hand and reading modes, hints, zoom, lesson range, pedal setup, and latency calibration. A saved device is reopened only when a matching enumerated device is present; absence and disconnect are explicit states.
- Finalized practice attempts persist per song and date with mode, hand/range, accuracy, timing, hold, missed/extra counts, duration, streak, and last position. Interrupted or empty runs are not recorded. Dashboard and library summaries survive restart, and absent or malformed profile data falls back safely.

## Validation

```powershell
dotnet build .\PianoPractice.Desktop\PianoPractice.Desktop.csproj
dotnet run --project .\PianoPractice.ShortcutSmoke\PianoPractice.ShortcutSmoke.csproj
dotnet run --project .\PianoPractice.ParserSmoke\PianoPractice.ParserSmoke.csproj -- "C:\Users\RJ Chan\Downloads\olivia-rodrigo-drivers-license.mxl"
dotnet run --project .\PianoPractice.SimulationSmoke\PianoPractice.SimulationSmoke.csproj -- "C:\Users\RJ Chan\Downloads\olivia-rodrigo-drivers-license.mxl"
node .\PianoPractice.RendererSmoke\renderer-smoke.cjs "C:\Users\RJ Chan\Downloads\olivia-rodrigo-drivers-license.mxl" 306
```

The supplied score imports as 50 written measures and an authoritative 70-occurrence, 278-beat performance plan. The simulation smoke covers selected-range transport, pause/resume/restart, home-row mapping, preview audio, metronome, WinMM discovery and input open/close, piano output, MIDI-reference import, wait-for-you scoring, timed scoring, note hold/release, settings restart persistence, finalized-attempt persistence, partial-run exclusion, preferred-device matching, and malformed-profile recovery. The broad renderer regression separately exercises Verovio's 306-beat repeat-expanded timemap, requiring no unresolved SVG IDs or backward page transitions.

## Honest limits

Verovio provides established engraving rather than a custom approximation, but this prototype is not a substitute for manual edition review of malformed or unusually complex MusicXML. PDF recognition is not implemented; PDF is a working visual review/reference workflow. Pedal is monitored but not graded when the source has no pedal marks. Latency can be calibrated, but zero or perfect audio latency is not claimed. Vocal-audio alignment and production-grade assessment are not implemented.

## Bundled notation licenses

- Verovio is bundled under LGPL-3.0; see `Assets/Verovio/COPYING.txt` and `COPYING.LESSER.txt`.
- Bravura is bundled under the SIL Open Font License; see `Assets/Bravura/LICENSE.txt`.
