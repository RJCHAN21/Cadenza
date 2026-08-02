[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $repositoryRoot 'TestData\Fixtures'
$musicXmlPath = Join-Path $fixtureRoot 'cadenza-timeline.musicxml'
$mxlPath = Join-Path $fixtureRoot 'cadenza-timeline.mxl'

if (-not (Test-Path -LiteralPath $musicXmlPath -PathType Leaf)) {
    throw "Missing source fixture: $musicXmlPath"
}

Add-Type -AssemblyName System.IO.Compression
$mxlStream = [System.IO.File]::Open($mxlPath, [System.IO.FileMode]::Create)
try {
    $archive = [System.IO.Compression.ZipArchive]::new(
        $mxlStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        $container = $archive.CreateEntry('META-INF/container.xml', [System.IO.Compression.CompressionLevel]::Optimal)
        $container.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $writer = [System.IO.StreamWriter]::new($container.Open(), [System.Text.UTF8Encoding]::new($false))
        try {
            $writer.Write('<?xml version="1.0" encoding="UTF-8"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="score.musicxml" media-type="application/vnd.recordare.musicxml+xml"/></rootfiles></container>')
        }
        finally {
            $writer.Dispose()
        }

        $score = $archive.CreateEntry('score.musicxml', [System.IO.Compression.CompressionLevel]::Optimal)
        $score.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $source = [System.IO.File]::OpenRead($musicXmlPath)
        try {
            $target = $score.Open()
            try { $source.CopyTo($target) }
            finally { $target.Dispose() }
        }
        finally {
            $source.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $mxlStream.Dispose()
}

function Write-Fixture([string] $Name, [byte[]] $Bytes) {
    [System.IO.File]::WriteAllBytes((Join-Path $fixtureRoot $Name), $Bytes)
}

# Format 0, one 480-PPQ track, 120 BPM, a one-beat middle-C note.
Write-Fixture 'cadenza-reference.mid' ([byte[]] @(
    0x4D,0x54,0x68,0x64, 0x00,0x00,0x00,0x06, 0x00,0x00, 0x00,0x01, 0x01,0xE0,
    0x4D,0x54,0x72,0x6B, 0x00,0x00,0x00,0x1B,
    0x00,0xFF,0x03,0x07,0x43,0x61,0x64,0x65,0x6E,0x7A,0x61,
    0x00,0xFF,0x51,0x03,0x07,0xA1,0x20,
    0x00,0x90,0x3C,0x40,
    0x83,0x60,0x80,0x3C,0x00,
    0x00,0xFF,0x2F,0x00))

$header = [byte[]] @(0x4D,0x54,0x68,0x64, 0x00,0x00,0x00,0x06, 0x00,0x00, 0x00,0x01, 0x01,0xE0)
$track = [byte[]] @(0x4D,0x54,0x72,0x6B)

Write-Fixture 'malformed-midi-oversized-track.mid' ($header + $track + [byte[]] @(0x7F,0xFF,0xFF,0xFF))
Write-Fixture 'malformed-midi-overlong-vlq.mid' ($header + $track + [byte[]] @(0x00,0x00,0x00,0x05, 0x81,0x80,0x80,0x80,0x00))
Write-Fixture 'malformed-midi-running-status.mid' ($header + $track + [byte[]] @(0x00,0x00,0x00,0x03, 0x00,0x3C,0x40))
Write-Fixture 'malformed-midi-truncated-meta.mid' ($header + $track + [byte[]] @(0x00,0x00,0x00,0x06, 0x00,0xFF,0x03,0x05,0x41,0x42))
Write-Fixture 'malformed-midi-truncated-sysex.mid' ($header + $track + [byte[]] @(0x00,0x00,0x00,0x05, 0x00,0xF0,0x05,0x01,0x02))

Write-Host "Generated deterministic MXL and MIDI fixtures in $fixtureRoot"
