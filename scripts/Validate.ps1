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
    foreach ($response in $application.responses) {
        $actualCount = $response.answer.Length
        if ($actualCount -ne $response.characterCount) {
            throw "Application response $($response.id) count is $actualCount; recorded $($response.characterCount)."
        }
        if ($actualCount -ge 500) {
            throw "Application response $($response.id) must remain under 500 characters."
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
    node --check PianoPractice.RendererSmoke/renderer-smoke.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Renderer smoke syntax validation failed.' }

    Write-Host '==> Parser and safety regressions'
    & $DotnetCommand run --project PianoPractice.ParserSmoke --configuration Release --no-build -- TestData/Fixtures/cadenza-timeline.musicxml
    if ($LASTEXITCODE -ne 0) { throw 'Parser regression failed.' }

    Write-Host '==> Deterministic simulation (no MIDI/audio hardware)'
    & $DotnetCommand run --project PianoPractice.SimulationSmoke --configuration Release --no-build -- TestData/Fixtures/cadenza-timeline.musicxml
    if ($LASTEXITCODE -ne 0) { throw 'Simulation regression failed.' }

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
