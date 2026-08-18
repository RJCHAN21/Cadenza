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

if (-not ('CadenzaFixtureArchive' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class CadenzaFixtureArchive
{
    private sealed class Entry
    {
        public byte[] Name;
        public byte[] Data;
        public uint Crc;
        public uint Offset;
    }

    public static void Write(string outputPath, string scorePath)
    {
        const string containerXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"score.musicxml\" media-type=\"application/vnd.recordare.musicxml+xml\"/></rootfiles></container>";
        var entries = new List<Entry>
        {
            CreateEntry("META-INF/container.xml", new UTF8Encoding(false).GetBytes(containerXml)),
            CreateEntry("score.musicxml", File.ReadAllBytes(scorePath))
        };

        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
        {
            foreach (var entry in entries)
            {
                entry.Offset = checked((uint)stream.Position);
                writer.Write(0x04034b50u); // local file header
                writer.Write((ushort)20);  // version needed
                writer.Write((ushort)0);   // flags
                writer.Write((ushort)0);   // stored, no runtime-dependent compression
                writer.Write((ushort)0);   // DOS time: 00:00:00
                writer.Write((ushort)0x5c21); // DOS date: 2026-01-01
                writer.Write(entry.Crc);
                writer.Write((uint)entry.Data.Length);
                writer.Write((uint)entry.Data.Length);
                writer.Write((ushort)entry.Name.Length);
                writer.Write((ushort)0);
                writer.Write(entry.Name);
                writer.Write(entry.Data);
            }

            var centralOffset = checked((uint)stream.Position);
            foreach (var entry in entries)
            {
                writer.Write(0x02014b50u); // central directory header
                writer.Write((ushort)20);  // version made by
                writer.Write((ushort)20);  // version needed
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0x5c21);
                writer.Write(entry.Crc);
                writer.Write((uint)entry.Data.Length);
                writer.Write((uint)entry.Data.Length);
                writer.Write((ushort)entry.Name.Length);
                writer.Write((ushort)0); // extra length
                writer.Write((ushort)0); // comment length
                writer.Write((ushort)0); // disk number
                writer.Write((ushort)0); // internal attributes
                writer.Write(0u);        // external attributes
                writer.Write(entry.Offset);
                writer.Write(entry.Name);
            }

            var centralSize = checked((uint)stream.Position - centralOffset);
            writer.Write(0x06054b50u); // end of central directory
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)entries.Count);
            writer.Write((ushort)entries.Count);
            writer.Write(centralSize);
            writer.Write(centralOffset);
            writer.Write((ushort)0);
        }
    }

    private static Entry CreateEntry(string name, byte[] data)
    {
        return new Entry
        {
            Name = Encoding.UTF8.GetBytes(name),
            Data = data,
            Crc = Crc32(data)
        };
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }
}
'@
}

[CadenzaFixtureArchive]::Write($mxlPath, $musicXmlPath)

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
