using System.Buffers.Binary;

namespace LinkBridge.Input;

public enum InputEventType : byte { Key = 1, Move = 2, Button = 3, Wheel = 4, Heartbeat = 5, HeartbeatAck = 6, FileDrag = 7 }

public static class InputProtocol
{
    public static ReadOnlySpan<byte> CapabilityHeader => "LBI3"u8;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    public const int QueueCapacity = 256;
}

public readonly record struct InputPacket(InputEventType Type, byte Code, ushort Key, int X, int Y)
{
    public const int Size = 12;

    public void Write(Span<byte> buffer)
    {
        buffer[0] = (byte)Type;
        buffer[1] = Code;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], Key);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..8], X);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], Y);
    }

    public static InputPacket Read(ReadOnlySpan<byte> buffer) => new(
        (InputEventType)buffer[0], buffer[1],
        BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]),
        BinaryPrimitives.ReadInt32LittleEndian(buffer[4..8]),
        BinaryPrimitives.ReadInt32LittleEndian(buffer[8..12]));
}

