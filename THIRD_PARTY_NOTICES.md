# Third-party notices

This document is an inventory, not legal advice. Before distributing binaries,
the releaser must review the versions actually shipped and comply with their
licence terms. The Cadenza project licence is still awaiting maintainer approval.

## Bundled components

### Verovio 6.2.0-43f8060

- Purpose: MusicXML/MEI conversion, engraving, SVG output, and written-score
  timemap generation in the local WebView2 renderer.
- Bundled files: `PianoPractice.Desktop/Assets/Verovio/verovio-toolkit-wasm.js`.
- Licence: GNU Lesser General Public License version 3; the bundled notices are
  `PianoPractice.Desktop/Assets/Verovio/COPYING.LESSER.txt` and
  `PianoPractice.Desktop/Assets/Verovio/COPYING.txt`.
- Upstream source: <https://github.com/rism-digital/verovio>.

`player.html` and both `cadenza-runtime-*.js` files are Cadenza integration
code, not upstream Verovio files. A binary distributor must keep the Verovio
licence notices and satisfy the LGPL's corresponding-source and relinking
requirements applicable to the exact build. The repository currently bundles
a generated WebAssembly/JavaScript artifact but does not vendor Verovio's full
corresponding source; use the recorded upstream version/commit as the starting
point and verify it before release.

### Bravura 1.392

- Purpose: SMuFL music font and engraving metadata.
- Bundled files: `PianoPractice.Desktop/Assets/Bravura/Bravura.otf` and
  `bravura_metadata.json`.
- Copyright: Steinberg Media Technologies GmbH.
- Licence: SIL Open Font License 1.1 in
  `PianoPractice.Desktop/Assets/Bravura/LICENSE.txt`.
- Upstream: <https://github.com/steinbergmedia/bravura>.

The OFL notice and Reserved Font Name conditions must accompany redistributed
font files. Modified font builds require separate review under the OFL.

## Package and runtime dependencies

### Microsoft.Web.WebView2 1.0.3537.50

- Purpose: hosts the local notation renderer in the WPF application.
- Declared in `PianoPractice.Desktop/PianoPractice.Desktop.csproj`.
- Package information and current licence link:
  <https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3537.50>.

The NuGet package is restored during build and is not committed as a package
cache. The separately installed Microsoft Edge WebView2 Runtime is a user/system
prerequisite governed by Microsoft's terms. Future packaged releases must be
checked against the WebView2 distribution terms current at release time.

### .NET 8 and Windows desktop frameworks

Cadenza targets `net8.0-windows` with WPF. The SDK and framework packs are
restored or supplied by the developer/runtime environment and are not vendored
in this repository. See <https://github.com/dotnet/core> and the notices in the
specific .NET distribution used to build or ship the application.

## Test data

The `TestData/Fixtures` MusicXML, MXL, and MIDI fixtures were created
specifically for Cadenza. They are not transcriptions of third-party music.
Their redistribution terms will follow the Cadenza project licence once that
licence is approved and added at the repository root.

## Release checklist

Before distributing a binary:

1. Reconfirm every bundled version and file hash.
2. Include this notice and the bundled Verovio and Bravura licence texts.
3. Provide the Verovio corresponding-source/relinking materials required for
   the shipped artifact.
4. Review WebView2 distribution terms for the chosen deployment method.
5. Confirm the root project `LICENSE` exists and covers Cadenza-owned code and
   fixtures.
