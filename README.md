# Cadenza

Cadenza is an early-stage, local-first Windows piano sight-reading practice
application. It imports user-provided MusicXML or MXL scores, engraves them
locally, and supports computer-keyboard or optional MIDI-controller practice.
It does not include a commercial song catalogue or upload scores to a service.

> **Licence status:** the project licence is awaiting maintainer approval.
> Until a root `LICENSE` is added, the repository is not yet open source and no
> permission to copy, modify, or redistribute the project source should be
> inferred. Bundled third-party assets retain their own licences; see
> [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Status

Cadenza is pre-release software under active development. It is not a proven
curriculum, a professional performance assessor, or production-ready. MusicXML
coverage, assessment behavior, accessibility, hardware compatibility, and UI
polish remain incomplete. See [docs/LIMITATIONS.md](docs/LIMITATIONS.md).

## Verified capabilities

- Local MusicXML, XML, and compressed MXL import with bounded archive/XML
  processing.
- Written-score parsing plus an explicit repeat/volta-aware performance plan.
- Local grand-staff engraving through bundled Verovio and Bravura assets.
- Page and continuous reading modes with occurrence-aware cursor mapping.
- Listen, guided practice, and timed performance prototypes.
- Both-hand, left-hand, and right-hand expected-note groups.
- Computer-keyboard input and optional Windows MIDI input through WinMM.
- Local synthesized previews; optional MIDI files are listen/reference sources.
- User-local library, preferences, and completed-attempt records.

These are implementation capabilities, not claims of educational effectiveness
or complete format support.

## Requirements

- Windows 11 (Windows 10 may work but is not part of current validation)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Node.js 22 for renderer validation
- Microsoft Edge WebView2 Runtime for the desktop UI
- Optional: a class-compliant or vendor-supported Windows MIDI keyboard

The SDK selection is recorded in `global.json`. No MIDI or audio hardware is
required by the automated validation.

## Build and run

```powershell
git clone https://github.com/RJCHAN21/Cadenza-development.git
cd Cadenza-development
dotnet restore Cadenza.sln
dotnet build Cadenza.sln --configuration Release --no-restore
dotnet run --project PianoPractice.Desktop --configuration Release --no-build
```

The app starts with an empty library. Use **Import MusicXML** to choose a score
you own or are permitted to use. The repository's original synthetic fixture
at `TestData/Fixtures/cadenza-timeline.musicxml` is suitable for testing.

## Validate

From a clean clone:

```powershell
./scripts/Validate.ps1
```

That command regenerates and verifies the deterministic fixtures, performs a
NuGet vulnerability audit, restores and builds the Release solution, checks
JavaScript syntax, and runs parser, malformed-input, simulation, and renderer
regressions for both MusicXML and MXL. It fails if validation dirties the
worktree or if generated `bin`/`obj` output is tracked.

Hardware input, audible output, WebView2 interaction, and accessibility remain
manual checks documented in [docs/LIMITATIONS.md](docs/LIMITATIONS.md).

## Deterministic timeline fixture

`TestData/Fixtures/cadenza-timeline.musicxml` was composed for this project. Its
five written measures and 18 written quarter-note beats expand to seven
performed occurrences in the order `1, 2, 3, 1, 2, 4, 5`, totaling 26
performance beats. The parser, simulator, and renderer all verify this
independently documented contract. See
[TestData/Fixtures/README.md](TestData/Fixtures/README.md) and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Local data

Cadenza stores imported library copies, the library manifest, preferences, and
practice history under `%LOCALAPPDATA%\CadenzaPianoStudio`. WebView2 browser data
uses `%LOCALAPPDATA%\CadenzaPianoStudio\WebView2`. Renderer diagnostics use
`%LOCALAPPDATA%\Cadenza\Diagnostics`. Deleting these folders resets local state;
back up user-owned scores first.

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md), then read
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Useful entry points include the
MusicXML importer in `PianoPractice.Desktop/Services`, the occurrence model in
`PianoPractice.Desktop/Models/ScoreDocument.cs`, and the deterministic smoke
projects. Compatibility reports should contain a minimal, legally shareable
score rather than a copyrighted composition.

Community expectations, support boundaries, and security reporting are in
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), [SUPPORT.md](SUPPORT.md), and
[SECURITY.md](SECURITY.md).
