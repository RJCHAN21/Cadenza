using System.Buffers.Binary;
using System.IO;
using System.Text;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class MidiFileImporter
{
    public MidiReference Import(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The MIDI file could not be found.", path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        RequireChunk(reader, "MThd");
        var headerLength = ReadUInt32BigEndian(reader);
        if (headerLength < 6) throw new InvalidDataException("The MIDI header is shorter than six bytes.");
        var format = ReadUInt16BigEndian(reader);
        var trackCount = ReadUInt16BigEndian(reader);
        var division = ReadUInt16BigEndian(reader);
        if ((division & 0x8000) != 0) throw new NotSupportedException("SMPTE-timed MIDI files are not supported by this local preview.");
        if (division == 0) throw new InvalidDataException("The MIDI file declares zero ticks per quarter note.");
        if (headerLength > 6) reader.ReadBytes(checked((int)headerLength - 6));

        var notes = new List<MidiReferenceNote>();
        var tracks = new List<MidiTrackReference>();
        var firstTempoMicroseconds = 500_000;

        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            RequireChunk(reader, "MTrk");
            var length = ReadUInt32BigEndian(reader);
            var end = checked(stream.Position + length);
            if (end > stream.Length) throw new InvalidDataException($"MIDI track {trackIndex + 1} extends beyond the file.");

            long tick = 0;
            byte runningStatus = 0;
            var trackName = $"Track {trackIndex + 1}";
            var active = new Dictionary<(int Channel, int Note), Stack<(long Tick, int Velocity)>>();
            var trackNotes = new List<MidiReferenceNote>();
            var isPercussion = false;

            while (stream.Position < end)
            {
                tick += ReadVariableLength(reader);
                var first = reader.ReadByte();
                byte status;
                int data1;
                if (first < 0x80)
                {
                    if (runningStatus == 0) throw new InvalidDataException($"Track {trackIndex + 1} uses running status before a status byte.");
                    status = runningStatus;
                    data1 = first;
                }
                else
                {
                    status = first;
                    data1 = -1;
                }

                if (status == 0xFF)
                {
                    runningStatus = 0;
                    var metaType = reader.ReadByte();
                    var metaLength = ReadVariableLength(reader);
                    var data = reader.ReadBytes(checked((int)metaLength));
                    if (data.Length != metaLength) throw new EndOfStreamException("Unexpected end of MIDI meta event.");
                    if (metaType == 0x03 && data.Length > 0) trackName = Encoding.UTF8.GetString(data).Trim('\0', ' ');
                    if (metaType == 0x51 && data.Length == 3 && firstTempoMicroseconds == 500_000)
                    {
                        firstTempoMicroseconds = (data[0] << 16) | (data[1] << 8) | data[2];
                    }
                    if (metaType == 0x2F) break;
                    continue;
                }

                if (status is 0xF0 or 0xF7)
                {
                    runningStatus = 0;
                    var sysexLength = ReadVariableLength(reader);
                    reader.ReadBytes(checked((int)sysexLength));
                    continue;
                }

                runningStatus = status;
                var command = status & 0xF0;
                var channel = status & 0x0F;
                if (channel == 9) isPercussion = true;
                var firstData = data1 >= 0 ? data1 : reader.ReadByte();
                var secondData = command is 0xC0 or 0xD0 ? 0 : reader.ReadByte();

                if (command == 0x90 && secondData > 0)
                {
                    var key = (channel, firstData);
                    if (!active.TryGetValue(key, out var stack)) active[key] = stack = new Stack<(long, int)>();
                    stack.Push((tick, secondData));
                }
                else if (command == 0x80 || (command == 0x90 && secondData == 0))
                {
                    var key = (channel, firstData);
                    if (active.TryGetValue(key, out var stack) && stack.Count > 0)
                    {
                        var start = stack.Pop();
                        trackNotes.Add(new MidiReferenceNote(
                            trackIndex,
                            channel,
                            firstData,
                            start.Velocity,
                            start.Tick / (double)division,
                            Math.Max(1, tick - start.Tick) / (double)division));
                    }
                }
            }

            stream.Position = end;
            notes.AddRange(trackNotes);
            tracks.Add(new MidiTrackReference(trackIndex, string.IsNullOrWhiteSpace(trackName) ? $"Track {trackIndex + 1}" : trackName, trackNotes.Count, isPercussion));
        }

        var tempo = 60_000_000d / Math.Max(1, firstTempoMicroseconds);
        return new MidiReference(path, format, division, tempo, tracks, notes.OrderBy(note => note.OnsetBeats).ToArray());
    }

    private static void RequireChunk(BinaryReader reader, string expected)
    {
        var actual = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected MIDI chunk '{expected}', found '{actual}'.");
        }
    }

    private static ushort ReadUInt16BigEndian(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (reader.Read(bytes) != bytes.Length) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (reader.Read(bytes) != bytes.Length) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static long ReadVariableLength(BinaryReader reader)
    {
        long value = 0;
        for (var count = 0; count < 4; count++)
        {
            var current = reader.ReadByte();
            value = (value << 7) | (uint)(current & 0x7F);
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("A MIDI variable-length value exceeds four bytes.");
    }
}
