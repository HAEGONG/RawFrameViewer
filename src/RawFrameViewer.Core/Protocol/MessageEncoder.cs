using System.Buffers.Binary;
using System.IO.Hashing;

namespace RawFrameViewer.Core.Protocol;

/// <summary>
/// 메시지를 전송용 바이트 배열로 만든다. 구조: 헤더(36) + 페이로드 + 페이로드 CRC(4). §3
/// 바이트 배열을 돌려주므로 시뮬레이터는 전송 전에 일부러 바이트를 망가뜨려 오류를 주입할 수 있다. §7
/// </summary>
public static class MessageEncoder
{
    public static int GetMessageLength(uint payloadLength) =>
        checked(ProtocolConstants.FixedOverhead + (int)payloadLength);

    public static byte[] EncodeFrame(RawFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        FrameHeader header = FrameHeader.ForFrame(frame.FrameId, frame.TimestampUs, frame.Width, frame.Height, frame.BitsStored);
        if (header.Validate() is { } error)
        {
            throw new ArgumentException(error, nameof(frame));
        }

        byte[] buffer = new byte[GetMessageLength(header.PayloadLength)];
        header.WriteTo(buffer);

        Span<byte> payload = buffer.AsSpan(ProtocolConstants.HeaderSize, (int)header.PayloadLength);
        PixelCodec.WriteLittleEndian(frame.Pixels, payload);
        WritePayloadCrc(buffer, payload);
        return buffer;
    }

    public static byte[] EncodeHeartbeat(uint lastFrameId, long timestampUs)
    {
        byte[] buffer = new byte[ProtocolConstants.FixedOverhead];
        FrameHeader.ForHeartbeat(lastFrameId, timestampUs).WriteTo(buffer);
        WritePayloadCrc(buffer, Span<byte>.Empty);
        return buffer;
    }

    private static void WritePayloadCrc(byte[] buffer, ReadOnlySpan<byte> payload)
    {
        uint crc = Crc32.HashToUInt32(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ProtocolConstants.HeaderSize + payload.Length), crc);
    }
}
