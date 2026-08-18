<!-- SPDX-License-Identifier: GPL-3.0-only -->

# Development branch migration report

## Baseline and preservation rule

This report was prepared from GPL-3.0-only `main` at
`22c4facdcd22b84f71b6f4c3e391e53b572a5aae`. The existing `development`
branch had no unique commits and was safely fast-forwarded from `36f70ee` to
the updated `main`. No feature or fix branch was merged, rebased, deleted,
force-pushed, or otherwise rewritten.

The old product branches descend from the pre-OSS baseline
`f492c737c5eaa8ce98261d74ee7fab5df90c6252`. Their useful behavior must be
ported by concern onto current `development`; merging any old branch wholesale
would cross the repository-cleanliness and timeline contracts established on
`main`.

## Branch topology inspected

| Branch | Tip | Commits beyond its merge base with main | Relationship and scope |
| --- | --- | ---: | --- |
| `fix/repeat-render-audio-hardening` | `f492c73` | 0 | Ancestor of `main`; its accepted work is already contained and extended by OSS readiness. |
| `fix/strict-end-bar-boundary` | `c6e88d9` | 18 | Selection boundaries, real-time playback preparation/scheduling, shared WinMM output, and startup-failure handling. |
| `fix/continuous-mode-motion` | `c21091f` | 35 | Strict-boundary stack plus renderer comfort motion and bar-boundary bridging. |
| `fix/leading-rest-and-library-metadata` | `c21091f` | 35 | Same tip as `fix/continuous-mode-motion`; no separate work to port. |
| `fix/playable-positioning-double-barline` | `5259100` | 41 | Continuous-motion stack plus playable-position and double-barline handling. |
| `feat/listen-highlight-polish` | `e3c9424` | 49 | Continuous-motion stack plus Listen note/lyric glow and renderer regressions. |

## Work already superseded

- `4b328a2` and `bf9b266` attempted to isolate builds from tracked legacy WPF
  output. OSS readiness removed all 1,434 tracked `bin`/`obj` files, strengthened
  ignore rules, and made `scripts/Validate.ps1` reject tracked build output.
  Do not cherry-pick these commits.
- `fix/repeat-render-audio-hardening` ends at `f492c73`, which is already an
  ancestor of current `main`. The split authoritative importer and renderer
  mapping on `main` supersede its older repository layout.
- Old branch-local CI edits are superseded by the root solution, deterministic
  fixture generator, `scripts/Validate.ps1`, and the current GitHub Actions
  workflow. Port regression assertions into the existing smoke projects and
  root validator instead of restoring branch workflow files.
- Private-score startup and fixture expectations, song-specific title logic,
  personal paths, and old README claims are superseded by the neutral startup
  and original `cadenza-timeline` fixtures on `main`.

## Useful product work to port

1. **Selected-range and end-boundary behavior** — use `bc85194` through
   `fe79a5f` and `c6e88d9` as behavioral references for first-occurrence end
   resolution, stop-at-end behavior, preserved selections, terminal repeats,
   and non-fatal playback startup errors.
2. **Playback preparation and real-time scheduling** — evaluate `ef95d37`,
   `85ee6c6`, `db662eb`, `70346a1`, `3b3785c`, and `cf6d3e5` for unified
   preparation feedback, lifecycle cleanup, incremental scheduling, regression
   coverage, and shared WinMM output ownership.
3. **Continuous motion and boundary bridging** — port the project-owned
   renderer ideas from `3cf5488` through `c21091f`, including the continuous
   motion patch, same-system bar-boundary bridge, velocity/displacement caps,
   and authoritative visual-playhead smoothing.
4. **Playable positioning and double barlines** — after continuous motion,
   evaluate `77dff20` through `5259100` for separating playable cursor targets
   from structural barlines and stabilizing leading-rest geometry.
5. **Listen highlight polish** — after the shared motion work, evaluate
   `59f06e5` through `e3c9424` for occurrence-timed note/lyric highlighting,
   contained screen-space halos, mode isolation, and renderer smoke coverage.

## Changes that must not be carried forward

Every inspected legacy product tip contains 189 tracked generated `bin`/`obj`
files, two obsolete importer implementations, and the old private-score/title
assumptions. The pre-fast-forward `development` tip contained 575 generated
files and the same private fixture references. Whole-branch integration would
therefore risk reintroducing:

- generated executables, assemblies, PDBs, NuGet intermediates, WebView2
  binaries, and copied third-party assets;
- `olivia-rodrigo-drivers-license` startup, README, simulation, MIDI, and PDF
  assumptions;
- personal absolute package-cache paths from tracked `obj` files;
- `MusicXmlImporter.cs` and
  `MusicXmlImporter.Authoritative.Navigation.cs`, which current `main` removed;
- competing renderer/repeat behavior that bypasses the authoritative
  `ScoreDocument.PerformanceMeasures` plan or expands Verovio repeats;
- old workflow changes that omit current licence, fixture, application-count,
  MIDI-boundary, clean-tree, or MXL determinism checks.

## Safe port plan

1. Create `feature/strict-selection-boundaries` from current `development`.
   Reimplement the boundary contract against the current occurrence model and
   add original-fixture ParserSmoke/SimulationSmoke coverage. Do not restore old
   importer files or private fixtures.
2. Create `feature/realtime-playback` only after step 1 is accepted. Port the
   scheduler, lifecycle feedback, and shared-output concepts into current
   services while preserving bounded MIDI parsing and hardware-free CI.
3. Create `feature/continuous-motion-port` independently from current
   `development`. Manually port only the project-owned runtime patches and
   current renderer wiring. Adapt tests into `PianoPractice.RendererSmoke` and
   `scripts/Validate.ps1`; do not cherry-pick the old workflow.
4. Port playable positioning on top of accepted continuous motion. Recreate
   leading-rest and ordinary-double-barline cases as small original fixtures or
   deterministic inline test data and prove that written/performance timelines
   remain unchanged.
5. Port Listen highlighting on top of accepted motion infrastructure. Preserve
   occurrence identity and the current unexpanded Verovio mapping; validate
   mode isolation and no duplicate overlays.
6. For each port, use `git show <commit> -- <project-owned-paths>` as reference
   and reimplement against current files. Cherry-pick an individual commit only
   after `git show --stat` proves it contains no `bin`, `obj`, private fixture,
   obsolete importer, old workflow, or conflicting timeline changes.
7. Run `./scripts/Validate.ps1` from a clean worktree for every port, then add
   the relevant Windows/WebView2/MIDI/audio manual evidence before merge.

No legacy branch is approved for wholesale cherry-pick or merge.
