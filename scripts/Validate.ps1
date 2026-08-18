[CmdletBinding()]
param(
    [string] $DotnetCommand = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $initialStatus = @(git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($initialStatus.Count -ne 0) {
        throw "Validation requires a clean worktree. Existing changes:`n$($initialStatus -join "`n")"
    }

    $trackedGenerated = @(git ls-files | Where-Object { $_ -match '(^|/)(bin|obj)/' })
    if ($trackedGenerated.Count -ne 0) {
        throw "Tracked build output remains:`n$($trackedGenerated -join "`n")"
    }

    if (-not (Test-Path -LiteralPath 'LICENSE' -PathType Leaf)) {
        throw 'The root GPL-3.0-only LICENSE file is missing.'
    }
    $licenceText = Get-Content -LiteralPath 'LICENSE' -Raw
    if ($licenceText -notmatch 'GNU GENERAL PUBLIC LICENSE' -or
        $licenceText -notmatch 'Version 3, 29 June 2007' -or
        $licenceText -notmatch 'END OF TERMS AND CONDITIONS') {
        throw 'The root LICENSE does not contain the canonical GPL-3.0 text.'
    }

    Write-Host '==> Verify deterministic generated fixtures'
    $generatedFiles = @(
        'TestData/Fixtures/cadenza-timeline.mxl',
        'TestData/Fixtures/cadenza-reference.mid',
        'TestData/Fixtures/malformed-midi-oversized-track.mid',
        'TestData/Fixtures/malformed-midi-overlong-vlq.mid',
        'TestData/Fixtures/malformed-midi-running-status.mid',
        'TestData/Fixtures/malformed-midi-truncated-meta.mid',
        'TestData/Fixtures/malformed-midi-truncated-sysex.mid'
    )
    $beforeHashes = @{}
    foreach ($file in $generatedFiles) {
        $beforeHashes[$file] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
    }
    & $PSScriptRoot\Generate-TestFixtures.ps1
    foreach ($file in $generatedFiles) {
        $afterHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
        if ($afterHash -ne $beforeHashes[$file]) {
            throw "Fixture generation is not reproducible for $file."
        }
    }

    Write-Host '==> Verify application response character counts'
    $application = Get-Content -LiteralPath 'docs/CODEX-OSS-APPLICATION.json' -Raw | ConvertFrom-Json
    if ($application.submissionStatus -ne 'ready_to_submit') {
        throw "Application submission status is '$($application.submissionStatus)', not ready_to_submit."
    }
    if ($application.licence -ne 'GPL-3.0-only') {
        throw "Application licence is '$($application.licence)', not GPL-3.0-only."
    }
    if ($application.officialApplicationUrl -ne 'https://openai.com/form/codex-for-oss/') {
        throw "Application URL is not the current Codex for Open Source form."
    }
    if ($application.responses.Count -ne 4 -or
        (@($application.responses.id) -join ',') -ne 'A,B,C,D') {
        throw 'Application responses must contain exactly A, B, C, and D in order.'
    }
    foreach ($response in $application.responses) {
        $actualCount = $response.answer.Length
        if ($actualCount -ne $response.characterCount) {
            throw "Application response $($response.id) count is $actualCount; recorded $($response.characterCount)."
        }
        if ($actualCount -gt 500) {
            throw "Application response $($response.id) must be no more than 500 characters."
        }
        if ($response.answer -notmatch '(?i)early-stage' -or
            $response.answer -notmatch '(?i)incomplete') {
            throw "Application response $($response.id) must describe Cadenza as early-stage and incomplete."
        }
        if ($response.answer -notmatch [regex]::Escape('The maintainer reviews all AI-assisted changes.')) {
            throw "Application response $($response.id) must preserve the human-review statement."
        }
        Write-Host "Response $($response.id): $actualCount characters"
    }

    Write-Host '==> Tool versions'
    & $DotnetCommand --version
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK discovery failed.' }
    node --version
    if ($LASTEXITCODE -ne 0) { throw 'Node.js discovery failed.' }

    Write-Host '==> Restore with NuGet vulnerability audit'
    & $DotnetCommand restore Cadenza.sln -p:NuGetAudit=true -p:NuGetAuditMode=all
    if ($LASTEXITCODE -ne 0) { throw 'Solution restore or NuGet audit failed.' }

    Write-Host '==> Release build'
    & $DotnetCommand build Cadenza.sln --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    Write-Host '==> JavaScript syntax'
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-runtime-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Renderer runtime patch syntax validation failed.' }
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-runtime-edge-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Renderer edge patch syntax validation failed.' }
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-playable-position-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Playable-position renderer patch syntax validation failed.' }
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-bar-boundary-bridge-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Bar-boundary renderer patch syntax validation failed.' }
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-listen-highlight-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Listen-highlight renderer patch syntax validation failed.' }
    node --check PianoPractice.Desktop/Assets/Verovio/cadenza-continuous-motion-patch.js
    if ($LASTEXITCODE -ne 0) { throw 'Continuous-motion renderer patch syntax validation failed.' }
    node --check PianoPractice.RendererSmoke/renderer-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Renderer smoke syntax validation failed.' }

    Write-Host '==> Focused renderer regressions'
    node PianoPractice.RendererSmoke/playable-position-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Playable-position renderer regression failed.' }
    node PianoPractice.RendererSmoke/bar-boundary-bridge-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Bar-boundary renderer regression failed.' }
    node PianoPractice.RendererSmoke/listen-highlight-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Listen-highlight renderer regression failed.' }
    node PianoPractice.RendererSmoke/listen-highlight-halo-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Listen-highlight halo regression failed.' }
    node PianoPractice.RendererSmoke/continuous-motion-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Continuous-motion renderer regression failed.' }

    Write-Host '==> Parser and safety regressions'
    & $DotnetCommand run --project PianoPractice.ParserSmoke --configuration Release --no-build -- TestData/Fixtures/cadenza-timeline.musicxml
    if ($LASTEXITCODE -ne 0) { throw 'Parser regression failed.' }

    Write-Host '==> Deterministic simulation (no MIDI/audio hardware)'
    & $DotnetCommand run --project PianoPractice.SimulationSmoke --configuration Release --no-build -- TestData/Fixtures/cadenza-timeline.musicxml
    if ($LASTEXITCODE -ne 0) { throw 'Simulation regression failed.' }

    Write-Host '==> Application shortcut regressions'
    & $DotnetCommand run --project PianoPractice.ShortcutSmoke --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Application shortcut regression failed.' }

    Write-Host '==> Renderer mapping for MusicXML and MXL'
    node PianoPractice.RendererSmoke/renderer-smoke.cjs TestData/Fixtures/cadenza-timeline.musicxml
    if ($LASTEXITCODE -ne 0) { throw 'MusicXML renderer regression failed.' }
    node PianoPractice.RendererSmoke/renderer-smoke.cjs TestData/Fixtures/cadenza-timeline.mxl
    if ($LASTEXITCODE -ne 0) { throw 'MXL renderer regression failed.' }

    $finalStatus = @(git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Final git status failed.' }
    if ($finalStatus.Count -ne 0) {
        throw "Validation changed the worktree:`n$($finalStatus -join "`n")"
    }

    Write-Host 'PASS: clean Release build, parser, simulation, renderer, fixture, and safety validation.'
}
finally {
    Pop-Location
}
