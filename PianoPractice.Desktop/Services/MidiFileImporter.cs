using System.Buffers.Binary;
using System.IO;
using System.Text;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class MidiFileImporter
{
    private const long MaxFileLength = 64L * 1024 * 1024;
    private const uint MaxHeaderLength = 1024;
    private const ushort MaxTrackCount = 256;
    private const uint MaxTrackLength = 32U * 1024 * 1024;
    private const long MaxEventDataLength = 8L * 1024 * 1024;
    private const int MaxEventsPerTrack = 2_000_000;

    public MidiReference Import(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The MIDI file could not be found.", path);
        var fileLength = new FileInfo(path).Length;
        if (fileLength > MaxFileLength)
            throw new InvalidDataException($"The MIDI file exceeds the {MaxFileLength / 1024 / 1024} MiB safety limit.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        RequireChunk(reader, "MThd");
        var headerLength = ReadUInt32BigEndian(reader);
        if (headerLength < 6) throw new InvalidDataException("The MIDI header is shorter than six bytes.");
        if (headerLength > MaxHeaderLength)
            throw new InvalidDataException($"The MIDI header exceeds the {MaxHeaderLength}-byte safety limit.");
        if (headerLength > stream.Length - stream.Position)
            throw new InvalidDataException("The MIDI header extends beyond the file.");
        var format = ReadUInt16BigEndian(reader);
        var trackCount = ReadUInt16BigEndian(reader);
        var division = ReadUInt16BigEndian(reader);
        if (format > 2) throw new InvalidDataException($"The MIDI file declares unsupported format {format}.");
        if (trackCount == 0) throw new InvalidDataException("The MIDI file contains no tracks.");
        if (trackCount > MaxTrackCount)
            throw new InvalidDataException($"The MIDI file exceeds the {MaxTrackCount}-track safety limit.");
        if (format == 0 && trackCount != 1)
            throw new InvalidDataException("A format-0 MIDI file must contain exactly one track.");
        if ((division & 0x8000) != 0) throw new NotSupportedException("SMPTE-timed MIDI files are not supported by this local preview.");
        if (division == 0) throw new InvalidDataException("The MIDI file declares zero ticks per quarter note.");
        if (headerLength > 6) ReadExact(reader, checked((int)headerLength - 6), "MIDI header extension");

        var notes = new List<MidiReferenceNote>();
        var tracks = new List<MidiTrackReference>();
        var firstTempoMicroseconds = 500_000;

        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            RequireChunk(reader, "MTrk");
            var length = ReadUInt32BigEndian(reader);
            if (length > MaxTrackLength)
                throw new InvalidDataException($"MIDI track {trackIndex + 1} exceeds the {MaxTrackLength / 1024 / 1024} MiB safety limit.");
            var end = checked(stream.Position + length);
            if (end > stream.Length) throw new InvalidDataException($"MIDI track {trackIndex + 1} extends beyond the file.");

            long tick = 0;
            byte runningStatus = 0;
            var trackName = $"Track {trackIndex + 1}";
            var active = new Dictionary<(int Channel, int Note), Stack<(long Tick, int Velocity)>>();
            var trackNotes = new List<MidiReferenceNote>();
            var isPercussion = false;
            var eventCount = 0;

            while (stream.Position < end)
            {
                eventCount++;
                if (eventCount > MaxEventsPerTrack)
                    throw new InvalidDataException($"MIDI track {trackIndex + 1} exceeds the event-count safety limit.");

                tick = checked(tick + ReadVariableLength(reader, end));
                var first = ReadByteWithin(reader, end, "MIDI event status");
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
                    var metaType = ReadByteWithin(reader, end, "MIDI meta-event type");
                    var metaLength = ReadVariableLength(reader, end);
                    var data = ReadEventData(reader, end, metaLength, "MIDI meta event");
                    if (metaType == 0x03 && data.Length > 0) trackName = Encoding.UTF8.GetString(data).Trim('\0', ' ');
                    if (metaType == 0x51 && data.Length == 3 && firstTempoMicroseconds == 500_000)
                    {
                        firstTempoMicroseconds = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (firstTempoMicroseconds == 0)
                            throw new InvalidDataException("A MIDI tempo event declares zero microseconds per quarter note.");
                    }
                    if (metaType == 0x2F)
                    {
                        if (data.Length != 0) throw new InvalidDataException("A MIDI end-of-track event must have zero length.");
                        break;
                    }
                    continue;
                }

                if (status is 0xF0 or 0xF7)
                {
                    runningStatus = 0;
                    var sysexLength = ReadVariableLength(reader, end);
                    _ = ReadEventData(reader, end, sysexLength, "MIDI system-exclusive event");
                    continue;
                }

                if (status < 0x80 || status >= 0xF0)
                    throw new InvalidDataException($"Track {trackIndex + 1} contains unsupported status byte 0x{status:X2}.");
                runningStatus = status;
                var command = status & 0xF0;
                var channel = status & 0x0F;
                if (channel == 9) isPercussion = true;
                var firstData = data1 >= 0 ? data1 : ReadDataByte(reader, end, trackIndex);
                var secondData = command is 0xC0 or 0xD0 ? 0 : ReadDataByte(reader, end, trackIndex);

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
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new EndOfStreamException($"Expected MIDI chunk '{expected}', but the file ended early.");
        var actual = Encoding.ASCII.GetString(bytes);
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

    private static long ReadVariableLength(BinaryReader reader, long boundary)
    {
        long value = 0;
        for (var count = 0; count < 4; count++)
        {
            var current = ReadByteWithin(reader, boundary, "MIDI variable-length value");
            value = (value << 7) | (uint)(current & 0x7F);
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("A MIDI variable-length value exceeds four bytes.");
    }

    private static byte ReadDataByte(BinaryReader reader, long boundary, int trackIndex)
    {
        var value = ReadByteWithin(reader, boundary, "MIDI channel-event data");
        if (value >= 0x80)
            throw new InvalidDataException($"Track {trackIndex + 1} contains a status byte where channel-event data was required.");
        return value;
    }

    private static byte ReadByteWithin(BinaryReader reader, long boundary, string description)
    {
        if (reader.BaseStream.Position >= boundary)
            throw new InvalidDataException($"Unexpected end of {description}.");
        return reader.ReadByte();
    }

    private static byte[] ReadEventData(BinaryReader reader, long boundary, long length, string description)
    {
        if (length > MaxEventDataLength)
            throw new InvalidDataException($"The {description} exceeds the {MaxEventDataLength / 1024 / 1024} MiB safety limit.");
        if (length > boundary - reader.BaseStream.Position)
            throw new InvalidDataException($"The {description} extends beyond its MIDI track.");
        return ReadExact(reader, checked((int)length), description);
    }

    private static byte[] ReadExact(BinaryReader reader, int length, string description)
    {
        var data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException($"Unexpected end of {description}.");
        return data;
    }
}
