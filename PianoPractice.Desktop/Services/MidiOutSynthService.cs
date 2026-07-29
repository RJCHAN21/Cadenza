using System.Runtime.InteropServices;

namespace PianoPractice.Desktop.Services;

public sealed class MidiOutSynthService : IDisposable
{
    private const uint MidiMapper = 0xFFFFFFFF;
    private const uint NoError = 0;
    private nint _handle;

    public bool IsOpen => _handle != nint.Zero;
    public int VolumePercent { get; set; } = 85;

    public MidiOutputResult Open()
    {
        if (IsOpen) return new MidiOutputResult(true, "Windows MIDI piano output is ready.");
        if (!OperatingSystem.IsWindows()) return new MidiOutputResult(false, "Windows MIDI output is only available on Windows.");

        var result = midiOutOpen(out _handle, MidiMapper, nint.Zero, nint.Zero, 0);
        if (result != NoError)
        {
            _handle = nint.Zero;
            return new MidiOutputResult(false, DescribeError("midiOutOpen", result));
        }

        var programResult = SendShort(0xC0, 0, 0); // General MIDI acoustic grand piano.
        var channelVolumeResult = programResult == NoError
            ? SendShort(0xB0, 7, 127) // Keep the synth channel at full calibrated gain; the mixer controls note velocity.
            : programResult;
        return channelVolumeResult == NoError
            ? new MidiOutputResult(true, "Windows MIDI piano output is ready (Acoustic Grand Piano).")
            : new MidiOutputResult(false, DescribeError("midiOutShortMsg(piano setup)", channelVolumeResult));
    }

    public MidiOutputResult NoteOn(int note, int velocity, int channel = 0)
    {
        var ready = Open();
        if (!ready.Success) return ready;
        var scaledVelocity = MapMonitorVelocity(velocity, VolumePercent);
        var result = SendShort(0x90 | (channel & 0x0F), Math.Clamp(note, 0, 127), scaledVelocity);
        return result == NoError
            ? new MidiOutputResult(true, $"Piano monitor sounded note {note}.")
            : new MidiOutputResult(false, DescribeError("midiOutShortMsg(note-on)", result));
    }

    public MidiOutputResult NoteOff(int note, int velocity = 0, int channel = 0)
    {
        if (!IsOpen) return new MidiOutputResult(true, "No active output note.");
        var result = SendShort(0x80 | (channel & 0x0F), Math.Clamp(note, 0, 127), Math.Clamp(velocity, 0, 127));
        return result == NoError
            ? new MidiOutputResult(true, $"Piano monitor released note {note}.")
            : new MidiOutputResult(false, DescribeError("midiOutShortMsg(note-off)", result));
    }

    public MidiOutputResult ControlChange(int controller, int value, int channel = 0)
    {
        if (!IsOpen)
        {
            var ready = Open();
            if (!ready.Success) return ready;
        }

        var result = SendShort(0xB0 | (channel & 0x0F), Math.Clamp(controller, 0, 127), Math.Clamp(value, 0, 127));
        return result == NoError
            ? new MidiOutputResult(true, $"Controller {controller} sent.")
            : new MidiOutputResult(false, DescribeError("midiOutShortMsg(control change)", result));
    }

    public void AllNotesOff()
    {
        if (!IsOpen) return;
        for (var channel = 0; channel < 16; channel++) SendShort(0xB0 | channel, 123, 0);
    }

    public void Dispose()
    {
        if (!IsOpen) return;
        AllNotesOff();
        midiOutReset(_handle);
        midiOutClose(_handle);
        _handle = nint.Zero;
    }

    public static int MapMonitorVelocity(int inputVelocity, int volumePercent)
    {
        if (inputVelocity <= 0 || volumePercent <= 0) return 0;

        // Physical controllers commonly send mid-range velocities even for an
        // intentional firm press. A musical response curve raises quiet and
        // medium strikes while preserving dynamics and the MIDI 7-bit ceiling.
        var normalizedInput = Math.Clamp(inputVelocity, 1, 127) / 127d;
        var normalizedVolume = Math.Clamp(volumePercent, 0, 100) / 100d;
        var expressiveVelocity = Math.Pow(normalizedInput, 0.58d);
        var mixerGain = Math.Pow(normalizedVolume, 0.70d);
        return Math.Clamp((int)Math.Round(127d * expressiveVelocity * mixerGain), 1, 127);
    }

    private uint SendShort(int status, int data1, int data2)
    {
        if (!IsOpen) return 1;
        var packed = (uint)(status | (data1 << 8) | (data2 << 16));
        return midiOutShortMsg(_handle, packed);
    }

    private static string DescribeError(string operation, uint code)
    {
        var buffer = new char[256];
        var textResult = midiOutGetErrorText(code, buffer, (uint)buffer.Length);
        var text = textResult == NoError ? new string(buffer).TrimEnd('\0') : "No WinMM description was available.";
        return $"{operation} failed with WinMM code {code}: {text}";
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutOpen(out nint handle, uint deviceId, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutShortMsg(nint handle, uint message);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutReset(nint handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutClose(nint handle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    private static extern uint midiOutGetErrorText(uint errorCode, [Out] char[] errorText, uint errorTextLength);
}

public sealed record MidiOutputResult(bool Success, string Message);
