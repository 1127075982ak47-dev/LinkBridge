using System.Runtime.InteropServices;

namespace LinkBridge.Media;

/// <summary>
/// Explicit-device 48 kHz, signed little-endian 16-bit interleaved stereo output.
/// Write accepts complete four-byte frames and never waits for queue capacity.
/// Dispose must be called: a native callback lifetime roots this object until the
/// AudioQueue has been disposed. No default device or capture device is changed.
/// </summary>
public sealed class MacPcmOutput : IDisposable
{
    public const int SampleRate = 48000;
    public const int BytesPerFrame = 4;
    private const int BufferBytes = SampleRate * BytesPerFrame / 100; // 10 ms
    private const int RingBytes = BufferBytes * 8; // at most 80 ms managed backlog
    private const int BufferCount = 3; // 30 ms native queue
    private const uint LinearPcm = 0x6C70636D; // 'lpcm', AudioFormatID
    private const uint CurrentDevice = 0x61716364; // 'aqcd', CFStringRef
    private const uint SignedPacked = (1u << 2) | (1u << 3);
    private const uint Utf8 = 0x08000100;
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private readonly object _ringGate = new(), _lifecycleGate = new();
    private readonly byte[] _ring = new byte[RingBytes];
    private readonly Dictionary<IntPtr, BufferState> _buffers = new(BufferCount);
    private static readonly OutputCallback Callback = OnBufferReturned;
    private static readonly int ByteSizeOffset = (int)Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.AudioDataByteSize));
    private GCHandle _self;
    private IntPtr _queue;
    private int _read, _count, _stopped;
    private bool _stopCompleted;
    private long _droppedBytes;
    private Exception? _fault;
    private int _nativeStatus;

    public string DeviceUid { get; }
    public long DroppedFrames => Interlocked.Read(ref _droppedBytes) / BytesPerFrame;
    public Exception? Fault => Volatile.Read(ref _fault);
    public int NativeErrorStatus => Volatile.Read(ref _nativeStatus);

    public MacPcmOutput(string uid)
    {
        if (!OperatingSystem.IsMacOS() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("MacPcmOutput requires 64-bit macOS.");
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        DeviceUid = uid;
        var format = new StreamDescription
        {
            SampleRate = SampleRate, FormatId = LinearPcm, FormatFlags = SignedPacked,
            BytesPerPacket = BytesPerFrame, FramesPerPacket = 1,
            BytesPerFrame = BytesPerFrame, ChannelsPerFrame = 2, BitsPerChannel = 16
        };
        _self = GCHandle.Alloc(this);
        try
        {
            Check(AudioQueueNewOutput(in format, Callback, GCHandle.ToIntPtr(_self),
                IntPtr.Zero, IntPtr.Zero, 0, out _queue), "AudioQueueNewOutput");
            if (_queue == IntPtr.Zero) throw new InvalidOperationException("AudioQueueNewOutput returned a null queue.");
            var device = CFStringCreateWithCString(IntPtr.Zero, uid, Utf8);
            if (device == IntPtr.Zero) throw new InvalidOperationException("Unable to allocate CoreFoundation device UID.");
            try { Check(AudioQueueSetProperty(_queue, CurrentDevice, ref device, (uint)IntPtr.Size), "AudioQueueSetProperty(aqcd)"); }
            finally { CFRelease(device); }

            for (var i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(_queue, BufferBytes, out var buffer), "AudioQueueAllocateBuffer");
                var header = Marshal.PtrToStructure<QueueBuffer>(buffer);
                if (header.AudioData == IntPtr.Zero || header.AudioDataBytesCapacity < BufferBytes)
                    throw new InvalidOperationException("AudioQueue returned an invalid PCM buffer.");
                _buffers.Add(buffer, new BufferState(header.AudioData));
            }
            // All callback metadata is immutable before the native queue starts.
            foreach (var buffer in _buffers.Keys) FillAndEnqueue(_queue, buffer);
            Check(AudioQueueStart(_queue, IntPtr.Zero), "AudioQueueStart");
        }
        catch (Exception original)
        {
            Volatile.Write(ref _stopped, 1);
            if (_queue != IntPtr.Zero)
            {
                var cleanup = AudioQueueDispose(_queue, true);
                if (cleanup != 0)
                {
                    // Keep the GCHandle rooted if native disposal failed: the queue
                    // may still retain callback userData. Report both real failures.
                    throw new AggregateException(original, NativeException(cleanup, "AudioQueueDispose(constructor cleanup)"));
                }
                _queue = IntPtr.Zero;
            }
            if (_self.IsAllocated) _self.Free();
            throw;
        }
    }

    /// <summary>
    /// Enqueues whole frames without blocking. On overflow the oldest complete
    /// frames are dropped; data larger than the ring retains only its newest tail.
    /// A contended write is dropped as whole frames. Non-aligned input is rejected
    /// so a network byte fragment cannot silently swap channels or sample alignment.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % BytesPerFrame != 0)
            throw new ArgumentException("PCM writes must contain complete four-byte stereo frames.", nameof(pcm));
        if (Fault is { } fault) throw new InvalidOperationException("AudioQueue output failed.", fault);
        if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(MacPcmOutput), "PCM output is stopped.");
        if (pcm.IsEmpty) return;
        if (!Monitor.TryEnter(_ringGate))
        {
            Interlocked.Add(ref _droppedBytes, pcm.Length);
            return;
        }
        try
        {
            if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(MacPcmOutput));
            if (pcm.Length > RingBytes)
            {
                var discarded = pcm.Length - RingBytes;
                Interlocked.Add(ref _droppedBytes, discarded);
                pcm = pcm[discarded..];
            }
            var overflow = Math.Max(0, _count + pcm.Length - RingBytes);
            if (overflow != 0)
            {
                _read = (_read + overflow) % RingBytes;
                _count -= overflow;
                Interlocked.Add(ref _droppedBytes, overflow);
            }
            var write = (_read + _count) % RingBytes;
            var first = Math.Min(pcm.Length, RingBytes - write);
            pcm[..first].CopyTo(_ring.AsSpan(write, first));
            pcm[first..].CopyTo(_ring.AsSpan(0));
            _count += pcm.Length;
        }
        finally { Monitor.Exit(_ringGate); }
    }

    /// <summary>Stops playback immediately and clears pending managed samples.</summary>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _stopped, 1);
            if (_queue != IntPtr.Zero && !_stopCompleted)
            {
                Check(AudioQueueStop(_queue, true), "AudioQueueStop(immediate)");
                _stopCompleted = true;
            }
            lock (_ringGate) { _read = 0; _count = 0; }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _stopped, 1);
            if (_queue == IntPtr.Zero) return;
            // Immediate native disposal stops callbacks before releasing userData.
            // A failure leaves the queue/handle intact, permitting a disposal retry.
            Check(AudioQueueDispose(_queue, true), "AudioQueueDispose(immediate)");
            _queue = IntPtr.Zero;
            _buffers.Clear();
            lock (_ringGate) { _read = 0; _count = 0; }
            if (_self.IsAllocated) _self.Free();
        }
    }

    private static void OnBufferReturned(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        var output = (MacPcmOutput?)GCHandle.FromIntPtr(userData).Target;
        if (output is null || Volatile.Read(ref output._stopped) != 0) return;
        try { output.FillAndEnqueue(queue, buffer); }
        catch (Exception ex)
        {
            // Never unwind a managed exception into the native audio callback.
            Interlocked.CompareExchange(ref output._fault, ex, null);
            Volatile.Write(ref output._stopped, 1);
        }
    }

    private void FillAndEnqueue(IntPtr queue, IntPtr buffer)
    {
        var state = _buffers[buffer];
        var copied = 0;
        if (Monitor.TryEnter(_ringGate))
        {
            try
            {
                copied = Math.Min(_count, BufferBytes);
                var first = Math.Min(copied, RingBytes - _read);
                _ring.AsSpan(_read, first).CopyTo(state.Scratch);
                _ring.AsSpan(0, copied - first).CopyTo(state.Scratch.AsSpan(first));
                _read = (_read + copied) % RingBytes;
                _count -= copied;
            }
            finally { Monitor.Exit(_ringGate); }
        }
        state.Scratch.AsSpan(copied).Clear(); // underrun is silence, never stale audio
        Marshal.Copy(state.Scratch, 0, state.Data, BufferBytes);
        Marshal.WriteInt32(buffer, ByteSizeOffset, BufferBytes);
        if (Volatile.Read(ref _stopped) == 0)
            Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "AudioQueueEnqueueBuffer");
    }

    private void Check(int status, string operation)
    {
        if (status == 0) return;
        Interlocked.CompareExchange(ref _nativeStatus, status, 0);
        throw NativeException(status, operation);
    }
    private static Exception NativeException(int status, string operation) =>
        new InvalidOperationException($"{operation} failed: OSStatus {status} (0x{unchecked((uint)status):X8}).");
    private sealed class BufferState(IntPtr data)
    {
        public IntPtr Data { get; } = data;
        public byte[] Scratch { get; } = new byte[BufferBytes];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket,
            BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }
    // SDK AudioQueueBuffer: pointer-aligned fields, including packet pointer before count.
    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueNewOutput(in StreamDescription format,
        OutputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueSetProperty(IntPtr queue, uint property, ref IntPtr value, uint size);
    [DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packets, IntPtr descriptions);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
