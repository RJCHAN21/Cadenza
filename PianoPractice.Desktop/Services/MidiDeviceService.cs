using System.Runtime.InteropServices;

namespace PianoPractice.Desktop.Services;

public sealed class MidiDeviceService : IDisposable
{
    private const uint NoError = 0;
    private const uint CallbackFunction = 0x00030000;
    private const uint MidiInputOpen = 0x03C1;
    private const uint MidiInputClose = 0x03C2;
    private const uint MidiInputData = 0x03C3;
    private const uint MidiInputError = 0x03C5;
    private nint _inputHandle;
    private MidiInProc? _callback;
    private bool _intentionalStop;

    public event EventHandler<MidiNoteOnEvent>? NoteOn;
    public event EventHandler<MidiNoteOffEvent>? NoteOff;
    public event EventHandler<MidiControlChangeEvent>? ControlChange;
    public event EventHandler<MidiRawEvent>? RawMessage;
    public event EventHandler<string>? InputError;
    public event EventHandler? InputDisconnected;
    public event EventHandler<string>? Diagnostic;
    public string? ActiveDeviceId { get; private set; }
    public bool IsCapturing => _inputHandle != nint.Zero;

    public MidiDeviceSnapshot DiscoverInputDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MidiDeviceSnapshot(false, [], "Windows MIDI discovery is only available on Windows.", null);
        }

        try
        {
            var count = midiInGetNumDevs();
            var devices = new List<MidiDeviceInfo>();
            var failures = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var result = midiInGetDevCaps((nint)index, out var capabilities, (uint)Marshal.SizeOf<MidiInCaps>());
                if (result == NoError)
                {
                    devices.Add(new MidiDeviceInfo(index.ToString(), capabilities.ProductName?.Trim() ?? $"MIDI input {index + 1}"));
                }
                else
                {
                    failures.Add(DescribeError($"midiInGetDevCaps({index})", result));
                }
            }

            var error = failures.Count > 0
                ? string.Join("; ", failures)
                : count == 0
                    ? "WinMM midiInGetNumDevs returned 0 input devices."
                    : null;
            return new MidiDeviceSnapshot(true, devices, error, (int)count);
        }
        catch (DllNotFoundException exception)
        {
            return new MidiDeviceSnapshot(false, [], $"winmm.dll was not available: {exception.Message}", null);
        }
        catch (EntryPointNotFoundException exception)
        {
            return new MidiDeviceSnapshot(false, [], $"WinMM MIDI entry point was not available: {exception.Message}", null);
        }
    }

    public MidiInputStartResult StartInput(string deviceId)
    {
        StopInput();
        Diagnostic?.Invoke(this, $"Opening WinMM input id {deviceId}.");
        if (!OperatingSystem.IsWindows())
        {
            return new MidiInputStartResult(false, "Native WinMM input is only available on Windows.");
        }

        if (!uint.TryParse(deviceId, out var numericDeviceId))
        {
            return new MidiInputStartResult(false, $"The MIDI device id '{deviceId}' was not a numeric WinMM device id.");
        }

        try
        {
            _callback = HandleMidiMessage;
            var openResult = midiInOpen(out _inputHandle, numericDeviceId, _callback, nint.Zero, CallbackFunction);
            Diagnostic?.Invoke(this, $"midiInOpen({numericDeviceId}) returned {openResult}; handle=0x{_inputHandle:X}.");
            if (openResult != NoError)
            {
                _inputHandle = nint.Zero;
                var error = DescribeError($"midiInOpen({numericDeviceId})", openResult);
                if (openResult == 4)
                {
                    error += " Close any DAW, browser MIDI page, or other music app using this keyboard, then reconnect it and scan again.";
                }
                return new MidiInputStartResult(false, error);
            }

            var startResult = midiInStart(_inputHandle);
            Diagnostic?.Invoke(this, $"midiInStart({numericDeviceId}) returned {startResult}.");
            if (startResult != NoError)
            {
                midiInClose(_inputHandle);
                _inputHandle = nint.Zero;
                return new MidiInputStartResult(false, DescribeError($"midiInStart({numericDeviceId})", startResult));
            }

            ActiveDeviceId = deviceId;
            Diagnostic?.Invoke(this, $"Capture started for WinMM input id {deviceId}.");
            return new MidiInputStartResult(true, null);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _inputHandle = nint.Zero;
            return new MidiInputStartResult(false, $"WinMM MIDI capture could not start: {exception.Message}");
        }
    }

    public void StopInput()
    {
        if (_inputHandle == nint.Zero)
        {
            return;
        }

        try
        {
            _intentionalStop = true;
            midiInStop(_inputHandle);
            midiInReset(_inputHandle);
            midiInClose(_inputHandle);
        }
        finally
        {
            _inputHandle = nint.Zero;
            ActiveDeviceId = null;
            _intentionalStop = false;
        }
    }

    public void Dispose()
    {
        StopInput();
        _callback = null;
    }

    private void HandleMidiMessage(nint inputHandle, uint message, nint instance, nint parameterOne, nint parameterTwo)
    {
        Diagnostic?.Invoke(this,
            $"callback 0x{message:X4} handle=0x{inputHandle:X} data=0x{parameterOne.ToInt64():X8}");
        if (message == MidiInputClose)
        {
            _inputHandle = nint.Zero;
            ActiveDeviceId = null;
            if (!_intentionalStop) InputDisconnected?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (message == MidiInputOpen)
        {
            return;
        }
        if (message == MidiInputError)
        {
            InputError?.Invoke(this, $"The Windows MIDI driver reported an invalid input message (MIM_ERROR, packed 0x{parameterOne.ToInt64():X8}).");
            return;
        }
        if (message != MidiInputData)
        {
            return;
        }

        var packedMessage = parameterOne.ToInt64();
        var status = (byte)(packedMessage & 0xFF);
        var note = (byte)((packedMessage >> 8) & 0xFF);
        var velocity = (byte)((packedMessage >> 16) & 0xFF);
        RawMessage?.Invoke(this, new MidiRawEvent(status, note, velocity, DateTimeOffset.UtcNow));
        var command = status & 0xF0;
        if (command == 0x90 && velocity > 0)
        {
            NoteOn?.Invoke(this, new MidiNoteOnEvent(note, velocity, status & 0x0F, DateTimeOffset.UtcNow));
        }
        else if (command == 0x80 || (command == 0x90 && velocity == 0))
        {
            NoteOff?.Invoke(this, new MidiNoteOffEvent(note, velocity, status & 0x0F, DateTimeOffset.UtcNow));
        }
        else if (command == 0xB0)
        {
            ControlChange?.Invoke(this, new MidiControlChangeEvent(note, velocity, status & 0x0F, DateTimeOffset.UtcNow));
        }
    }

    private static string DescribeError(string operation, uint code)
    {
        var buffer = new char[256];
        var textResult = midiInGetErrorText(code, buffer, (uint)buffer.Length);
        var description = textResult == NoError ? new string(buffer).TrimEnd('\0') : "No WinMM description was available.";
        return $"{operation} failed with WinMM code {code}: {description}";
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MidiInProc(nint inputHandle, uint message, nint instance, nint parameterOne, nint parameterTwo);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    private static extern uint midiInGetDevCaps(nint deviceId, out MidiInCaps capabilities, uint capabilitiesSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInOpen(out nint inputHandle, uint deviceId, MidiInProc callback, nint instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInStart(nint inputHandle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInStop(nint inputHandle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInReset(nint inputHandle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInClose(nint inputHandle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    private static extern uint midiInGetErrorText(uint errorCode, [Out] char[] errorText, uint errorTextLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? ProductName;

        public ushort Technology;
        public ushort Reserved;
        public uint Support;
    }
}

public sealed record MidiDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record MidiDeviceSnapshot(
    bool IsDiscoverySupported,
    IReadOnlyList<MidiDeviceInfo> Devices,
    string? ApiMessage,
    int? ApiDeviceCount);

public sealed record MidiInputStartResult(bool Success, string? Error);

public sealed record MidiNoteOnEvent(int NoteNumber, int Velocity, int Channel, DateTimeOffset Timestamp);
public sealed record MidiNoteOffEvent(int NoteNumber, int Velocity, int Channel, DateTimeOffset Timestamp);
public sealed record MidiControlChangeEvent(int Controller, int Value, int Channel, DateTimeOffset Timestamp);
public sealed record MidiRawEvent(int Status, int Data1, int Data2, DateTimeOffset Timestamp);
