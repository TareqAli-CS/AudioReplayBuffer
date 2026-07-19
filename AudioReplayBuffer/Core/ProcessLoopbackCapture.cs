using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioReplayBuffer.Core;

/// <summary>
/// Captures the audio of a single process tree — or everything except it —
/// using the Windows process-loopback API (Windows 10 2004+). NAudio has
/// no wrapper for this, so the COM interop lives here. The API delivers
/// audio in whatever format we ask for, so we request the engine's native
/// 48 kHz / 16-bit / stereo and skip resampling entirely.
/// Implements IWaveIn so the capture engine can treat it like any other
/// NAudio capture source.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualLoopbackDevicePath = @"VAD\Process_Loopback";
    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const uint AudclntStreamflagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;

    private readonly int _targetPid;
    private readonly bool _excludeTarget;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private AutoResetEvent? _dataEvent;
    private Thread? _thread;
    private volatile bool _stopRequested;

    public WaveFormat WaveFormat { get; set; } = new(48000, 16, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <param name="targetPid">Root of the process tree to capture (or exclude).</param>
    /// <param name="excludeTarget">False: capture only that tree. True: capture everything except it.</param>
    public ProcessLoopbackCapture(int targetPid, bool excludeTarget)
    {
        _targetPid = targetPid;
        _excludeTarget = excludeTarget;
    }

    /// <summary>Finds the pid for a process name ("obs64" or "obs64.exe").</summary>
    public static int ResolvePid(string processName)
    {
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var candidates = Process.GetProcessesByName(name);
        if (candidates.Length == 0)
            throw new InvalidOperationException(
                $"\"{name}\" is not running. Start it, or set the capture back to all apps.");

        // Prefer the instance that owns a window; games/apps with several
        // helper processes usually play audio from that one (and children
        // are covered by tree capture anyway).
        var withWindow = candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        return (withWindow ?? candidates[0]).Id;
    }

    /// <summary>
    /// Starts capture. All COM work (activation, initialization and the
    /// read loop) happens on one MTA worker thread: WASAPI interfaces
    /// cannot be marshaled between apartments, so activating them from a
    /// caller's STA thread (e.g. the WPF UI thread) and reading from a
    /// worker would fail with E_NOINTERFACE.
    /// </summary>
    public void StartRecording()
    {
        _stopRequested = false;
        _startupError = null;
        _startupDone = new ManualResetEvent(false);
        _thread = new Thread(CaptureThread) { IsBackground = true, Name = "ProcessLoopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_startupDone.WaitOne(8000))
            throw new TimeoutException("Process-loopback capture did not start in time.");
        if (_startupError != null)
            throw new InvalidOperationException(
                "Per-app capture failed to start: " + _startupError.Message, _startupError);
    }

    private Exception? _startupError;
    private ManualResetEvent? _startupDone;

    private void InitializeClient()
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)_targetPid,
            ProcessLoopbackMode = _excludeTarget ? 1 : 0
        };

        IntPtr activationPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        IntPtr formatPtr = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(activation, activationPtr, false);
            var propVariant = new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = activationPtr
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationHandler();
            Guid audioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            ActivateAudioInterfaceAsync(VirtualLoopbackDevicePath, ref audioClientIid,
                propVariantPtr, handler, out var operation);

            if (!handler.Completed.WaitOne(5000))
                throw new TimeoutException("Process-loopback activation timed out.");

            operation.GetActivateResult(out int activateHr, out object activated);
            Marshal.ThrowExceptionForHR(activateHr);
            _client = (IAudioClient)activated;

            var fmt = new WaveFormatExStruct(WaveFormat);
            formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExStruct>());
            Marshal.StructureToPtr(fmt, formatPtr, false);

            int hr = _client.Initialize(0 /* shared */,
                AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback,
                5_000_000 /* 500 ms buffer */, 0, formatPtr, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);

            _dataEvent = new AutoResetEvent(false);
            hr = _client.SetEventHandle(_dataEvent.SafeWaitHandle.DangerousGetHandle());
            Marshal.ThrowExceptionForHR(hr);

            Guid captureIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            hr = _client.GetService(ref captureIid, out object captureService);
            Marshal.ThrowExceptionForHR(hr);
            _capture = (IAudioCaptureClient)captureService;

            hr = _client.Start();
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(activationPtr);
            Marshal.FreeHGlobal(propVariantPtr);
            if (formatPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(formatPtr);
        }
    }

    private void CaptureThread()
    {
        try
        {
            InitializeClient();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            ReleaseClient();
            _startupDone?.Set();
            return; // StartRecording rethrows; no RecordingStopped event.
        }
        _startupDone?.Set();

        Exception? failure = null;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond];
        try
        {
            while (!_stopRequested)
            {
                _dataEvent!.WaitOne(100);
                while (!_stopRequested)
                {
                    int hr = _capture!.GetNextPacketSize(out uint packetFrames);
                    Marshal.ThrowExceptionForHR(hr);
                    if (packetFrames == 0)
                        break;

                    hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    Marshal.ThrowExceptionForHR(hr);
                    int bytes = (int)frames * WaveFormat.BlockAlign;
                    if (bytes > buffer.Length)
                        buffer = new byte[bytes];
                    if ((flags & BufferFlagsSilent) != 0)
                        Array.Clear(buffer, 0, bytes);
                    else
                        Marshal.Copy(data, buffer, 0, bytes);
                    _capture.ReleaseBuffer(frames);

                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        try { _client?.Stop(); } catch { }
        ReleaseClient();
        RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
    }

    /// <summary>Releases COM objects; called only on the capture thread.</summary>
    private void ReleaseClient()
    {
        if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        if (_client != null) { Marshal.ReleaseComObject(_client); _client = null; }
        _dataEvent?.Dispose();
        _dataEvent = null;
    }

    public void StopRecording()
    {
        _stopRequested = true;
        _dataEvent?.Set();
        _thread?.Join(1500);
        _thread = null;
    }

    public void Dispose() => StopRecording();

    // ---------- interop ----------

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExStruct
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;

        public WaveFormatExStruct(WaveFormat f)
        {
            wFormatTag = 1; // PCM
            nChannels = (ushort)f.Channels;
            nSamplesPerSec = (uint)f.SampleRate;
            nAvgBytesPerSec = (uint)f.AverageBytesPerSecond;
            nBlockAlign = (ushort)f.BlockAlign;
            wBitsPerSample = (ushort)f.BitsPerSample;
            cbSize = 0;
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marker that lets the completion handler be called from any COM apartment.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Completed = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
