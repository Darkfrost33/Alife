using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Alife.Function.Speech.FishAudio;

/// <summary>
/// Fish Audio WebSocket 事件所需的最小 MessagePack 编解码器。
/// 只实现协议实际使用的 map/string/bin/number 类型，避免为插件增加运行时依赖。
/// </summary>
internal static class FishAudioMessagePack
{
    internal readonly record struct Response(string Event, byte[]? Audio, string? Reason);

    public static byte[] SerializeStart(
        int sampleRate,
        int chunkLength,
        string latency,
        string? referenceId,
        double speed,
        double volume)
    {
        using MemoryStream stream = new();
        WriteMapHeader(stream, 2);
        WriteStringPair(stream, "event", "start");
        WriteString(stream, "request");

        int requestFields = referenceId == null ? 6 : 7;
        WriteMapHeader(stream, requestFields);
        WriteStringPair(stream, "text", "");
        WriteStringPair(stream, "format", "pcm");
        WriteStringIntPair(stream, "sample_rate", sampleRate);
        WriteStringIntPair(stream, "chunk_length", chunkLength);
        WriteStringPair(stream, "latency", latency);
        if (referenceId != null)
            WriteStringPair(stream, "reference_id", referenceId);

        WriteString(stream, "prosody");
        WriteMapHeader(stream, 2);
        WriteStringDoublePair(stream, "speed", speed);
        WriteStringDoublePair(stream, "volume", volume);
        return stream.ToArray();
    }

    public static byte[] SerializeText(string text)
    {
        using MemoryStream stream = new();
        WriteMapHeader(stream, 2);
        WriteStringPair(stream, "event", "text");
        WriteStringPair(stream, "text", text);
        return stream.ToArray();
    }

    public static byte[] SerializeEvent(string eventName)
    {
        using MemoryStream stream = new();
        WriteMapHeader(stream, 1);
        WriteStringPair(stream, "event", eventName);
        return stream.ToArray();
    }

    public static Response DeserializeResponse(ReadOnlySpan<byte> payload)
    {
        Reader reader = new(payload);
        int fields = reader.ReadMapLength();
        string eventName = "";
        string? reason = null;
        byte[]? audio = null;

        for (int index = 0; index < fields; index++)
        {
            string key = reader.ReadString();
            switch (key)
            {
                case "event":
                    eventName = reader.ReadString();
                    break;
                case "reason":
                    reason = reader.ReadString();
                    break;
                case "audio":
                    audio = reader.ReadBinary();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        return new Response(eventName, audio, reason);
    }

    static void WriteStringPair(Stream stream, string key, string value)
    {
        WriteString(stream, key);
        WriteString(stream, value);
    }

    static void WriteStringIntPair(Stream stream, string key, int value)
    {
        WriteString(stream, key);
        WriteInt32(stream, value);
    }

    static void WriteStringDoublePair(Stream stream, string key, double value)
    {
        WriteString(stream, key);
        stream.WriteByte(0xcb);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        stream.Write(bytes);
    }

    static void WriteMapHeader(Stream stream, int count)
    {
        if (count <= 15)
        {
            stream.WriteByte((byte)(0x80 | count));
            return;
        }

        stream.WriteByte(0xde);
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)count));
        stream.Write(bytes);
    }

    static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= 31)
        {
            stream.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else if (bytes.Length <= byte.MaxValue)
        {
            stream.WriteByte(0xd9);
            stream.WriteByte((byte)bytes.Length);
        }
        else if (bytes.Length <= ushort.MaxValue)
        {
            stream.WriteByte(0xda);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
            stream.Write(length);
        }
        else
        {
            stream.WriteByte(0xdb);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            stream.Write(length);
        }
        stream.Write(bytes);
    }

    static void WriteInt32(Stream stream, int value)
    {
        if (value is >= 0 and <= 127)
        {
            stream.WriteByte((byte)value);
            return;
        }

        stream.WriteByte(0xd2);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    ref struct Reader(ReadOnlySpan<byte> payload)
    {
        readonly ReadOnlySpan<byte> payload = payload;
        int offset;

        public int ReadMapLength()
        {
            byte marker = ReadByte();
            if ((marker & 0xf0) == 0x80)
                return marker & 0x0f;
            return marker switch {
                0xde => ReadUInt16(),
                0xdf => checked((int)ReadUInt32()),
                _ => throw InvalidMarker(marker, "map"),
            };
        }

        public string ReadString()
        {
            byte marker = ReadByte();
            int length;
            if ((marker & 0xe0) == 0xa0)
                length = marker & 0x1f;
            else
            {
                length = marker switch {
                    0xd9 => ReadByte(),
                    0xda => ReadUInt16(),
                    0xdb => checked((int)ReadUInt32()),
                    _ => throw InvalidMarker(marker, "string"),
                };
            }

            ReadOnlySpan<byte> value = ReadBytes(length);
            return Encoding.UTF8.GetString(value);
        }

        public byte[] ReadBinary()
        {
            byte marker = ReadByte();
            int length = marker switch {
                0xc4 => ReadByte(),
                0xc5 => ReadUInt16(),
                0xc6 => checked((int)ReadUInt32()),
                _ => throw InvalidMarker(marker, "binary"),
            };
            return ReadBytes(length).ToArray();
        }

        public void SkipValue()
        {
            byte marker = ReadByte();
            if (marker <= 0x7f || marker >= 0xe0 || marker is 0xc0 or 0xc2 or 0xc3)
                return;
            if ((marker & 0xe0) == 0xa0)
            {
                ReadBytes(marker & 0x1f);
                return;
            }
            if ((marker & 0xf0) == 0x80)
            {
                int count = marker & 0x0f;
                for (int i = 0; i < count * 2; i++) SkipValue();
                return;
            }
            if ((marker & 0xf0) == 0x90)
            {
                int count = marker & 0x0f;
                for (int i = 0; i < count; i++) SkipValue();
                return;
            }

            switch (marker)
            {
                case 0xc4: ReadBytes(ReadByte()); break;
                case 0xc5: ReadBytes(ReadUInt16()); break;
                case 0xc6: ReadBytes(checked((int)ReadUInt32())); break;
                case 0xca: ReadBytes(4); break;
                case 0xcb: ReadBytes(8); break;
                case 0xcc: ReadBytes(1); break;
                case 0xcd: ReadBytes(2); break;
                case 0xce: ReadBytes(4); break;
                case 0xcf: ReadBytes(8); break;
                case 0xd0: ReadBytes(1); break;
                case 0xd1: ReadBytes(2); break;
                case 0xd2: ReadBytes(4); break;
                case 0xd3: ReadBytes(8); break;
                case 0xd9: ReadBytes(ReadByte()); break;
                case 0xda: ReadBytes(ReadUInt16()); break;
                case 0xdb: ReadBytes(checked((int)ReadUInt32())); break;
                case 0xdc:
                    for (int i = 0, count = ReadUInt16(); i < count; i++) SkipValue();
                    break;
                case 0xdd:
                    for (uint i = 0, count = ReadUInt32(); i < count; i++) SkipValue();
                    break;
                case 0xde:
                    for (int i = 0, count = ReadUInt16(); i < count * 2; i++) SkipValue();
                    break;
                case 0xdf:
                    for (uint i = 0, count = ReadUInt32(); i < count * 2; i++) SkipValue();
                    break;
                default:
                    throw InvalidMarker(marker, "value");
            }
        }

        byte ReadByte()
        {
            if ((uint)offset >= (uint)payload.Length)
                throw new InvalidDataException("MessagePack 数据意外结束。");
            return payload[offset++];
        }

        ushort ReadUInt16()
        {
            ReadOnlySpan<byte> bytes = ReadBytes(2);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        uint ReadUInt32()
        {
            ReadOnlySpan<byte> bytes = ReadBytes(4);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0 || offset > payload.Length - length)
                throw new InvalidDataException("MessagePack 数据长度无效。");
            ReadOnlySpan<byte> result = payload.Slice(offset, length);
            offset += length;
            return result;
        }

        static InvalidDataException InvalidMarker(byte marker, string expected) =>
            new($"MessagePack 标记 0x{marker:X2} 不是有效的 {expected}。");
    }
}
