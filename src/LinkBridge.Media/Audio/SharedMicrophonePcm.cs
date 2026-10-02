using System.Buffers.Binary;

namespace LinkBridge.Media.Audio;

/// <summary>Explicit virtual input routing; implementations must retain only a bounded PCM backlog.</summary>
public interface ISharedMicrophoneSink : IDisposable
{
    string EndpointName { get; }
    void Write(ReadOnlySpan<byte> pcm);
}

/// <summary>A capture created only after paired authentication and explicit sharing demand.</summary>
public interface ISharedMicrophoneCapture : IDisposable
{
    Stream PcmStream { get; }
    /// <summary>Synchronously closes the source and stops hardware capture before returning.</summary>
    void Stop();
}

internal static class SharedMicrophonePcm
{
    public const string Context="LBM1";
    public const int SampleRate=48000, Bits=16, Channels=2, FrameBytes=4, ChunkBytes=3840;
    public static readonly TimeSpan IdleTimeout=TimeSpan.FromSeconds(8), WriteTimeout=TimeSpan.FromSeconds(2);
    public static byte[] Header()
    {
        var result=new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(result,SampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(4),Bits);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(6),Channels);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(8),FrameBytes);
        return result;
    }
    public static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if(!header.SequenceEqual(Header()))throw new InvalidDataException("共享麦克风仅支持48kHz/16bit/双声道PCM，请同时更新两端应用。");
    }
}
