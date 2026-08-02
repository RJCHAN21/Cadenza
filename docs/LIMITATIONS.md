# Known limitations

Cadenza is early-stage, pre-release software. The following limits are part of
the current product boundary, not a roadmap commitment.

## Platform and installation

- Automated validation covers Windows and .NET 8 only.
- There is no signed installer, automatic updater, stable release channel, or
  compatibility promise yet.
- The desktop renderer requires a working Microsoft Edge WebView2 Runtime.
- Packaging and LGPL corresponding-source delivery need a separate release
  review before binary distribution.

## Score compatibility

- Import is limited to score-partwise MusicXML/XML and MXL; score-timewise and
  other notation formats are unsupported.
- MusicXML is a large specification. Complex nested repeats, jumps, overlapping
  endings, unusual voices/divisions, layout directives, ornaments, percussion,
  microtonality, and vendor extensions may be rejected, warned about, ignored,
  or rendered differently than expected.
- A successful parse does not establish that an edition is musically correct.
- PDF is a visual reference workflow only; Cadenza does not convert PDF to
  notation.
- Imported MIDI is a local listen/reference source. SMPTE timing and parts of
  the Standard MIDI File ecosystem are unsupported.

## Practice and audio

- Expected-note matching is pitch/onset based and heuristic. It does not grade
  fingering, phrasing, voicing, dynamics, articulation, tone quality, or musical
  interpretation at a professional level.
- Latency, timing windows, and synthesized audio vary by machine and device.
- Sustain input can be observed, but grading depends on source information and
  is incomplete.
- There is no verified curriculum, pedagogical outcome, teacher workflow, or
  evidence that using Cadenza improves learning.
- User-owned score copyright and permission remain the user's responsibility.

## Accessibility and internationalization

- Keyboard operation, screen-reader semantics, contrast, zoom, reduced motion,
  high-DPI behavior, and color-independent feedback have not received a
  complete accessibility audit.
- UI text and parsing assumptions are primarily English-oriented; localization
  is not implemented.
- Music-specific accessibility needs require review with affected users rather
  than inference from automated checks.

## Privacy and security

- Scores, settings, diagnostics, and practice history remain local by design,
  but they are not encrypted at rest by Cadenza.
- Local diagnostics may include notation identifiers and runtime geometry;
  users should inspect them before sharing.
- The repository has not received a professional security audit or penetration
  test. Bounds and trust checks do not guarantee security.
- Dependency and vulnerability results describe the scanned revision only.

## Manual validation still required

Automated CI deliberately does not require peripherals or interactive UI. A
release candidate should still be checked manually on Windows for:

1. first-run and empty-library behavior;
2. MusicXML/MXL import through the file picker;
3. Page and Continuous cursor movement across repeat/ending passes;
4. WebView2 renderer startup, resize, zoom, and DPI behavior;
5. audible preview/metronome output and pause/stop behavior;
6. MIDI discovery, disconnect/reconnect, sustain, and monitor routing on real
   devices;
7. malformed-input error messages and recovery;
8. keyboard-only and screen-reader navigation;
9. local-state reset and corrupted-profile fallback;
10. packaged-binary third-party notices and corresponding-source materials.
