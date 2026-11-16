using System.Buffers.Binary;
using System.IO.Hashing;

namespace RawFrameViewer.Core.Protocol;

/// <summary>수신한 메시지의 종류</summary>
public enum ReceiveKind
{
    Frame,
    Heartbeat,
    /// <summary>헤더는 정상이지만 페이로드 CRC가 맞지 않아 버린 메시지. §6.1 ⑦</summary>
    PayloadCrcError,
}

/// <summary>
/// 수신 결과 하나.
/// </summary>
/// <param name="SkippedBytes">이 메시지를 찾기 전에 재동기화로 건너뛴 바이트 수. 0보다 크면 SyncLost가 있었다는 뜻이다.</param>
public sealed record ReceivedMessage(ReceiveKind Kind, FrameHeader Header, RawFrame? Frame, long SkippedBytes);

/// <summary>
/// 스트림에서 메시지를 하나씩 읽는다. docs/protocol.md §6.1 수신 처리 순서와 §6.2 재동기화를 구현한다.
/// FrameId 검사(§6.3)와 픽셀 범위 검사(§6.4)는 이 클래스의 책임이 아니다.
/// </summary>
public sealed class MessageReader
{
    /// <summary>재동기화 중 건너뛸 수 있는 최대 바이트 수. §6.2 ⑤</summary>
    public const long DefaultMaxResyncBytes = 64L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly long _maxResyncBytes;
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];

    public MessageReader(Stream stream, long maxResyncBytes = DefaultMaxResyncBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxResyncBytes);
        _stream = stream;
        _maxResyncBytes = maxResyncBytes;
    }

    /// <summary>
    /// 다음 메시지를 읽는다.
    /// </summary>
    /// <returns>메시지 경계에서 스트림이 정상 종료되면 null</returns>
    /// <exception cref="EndOfStreamException">메시지 중간에서 스트림이 끝남</exception>
    /// <exception cref="ProtocolException">회복할 수 없는 프로토콜 오류. 연결을 끊어야 한다.</exception>
    public async ValueTask<ReceivedMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        // ① 헤더 36바이트 읽기. 한 바이트도 못 읽고 끝나면 정상 종료로 본다.
        int read = await _stream.ReadAtLeastAsync(_header, ProtocolConstants.HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < ProtocolConstants.HeaderSize)
        {
            throw new EndOfStreamException("헤더를 읽는 중 스트림이 끝났습니다.");
        }

        // ②~⑤ 헤더 검사. Magic이나 HeaderCrc가 틀리면 재동기화를 반복한다.
        long skippedBytes = 0;
        FrameHeader header;
        while (true)
        {
            HeaderStatus status = FrameHeader.TryParse(_header, out header, out string? error);
            if (status == HeaderStatus.Ok)
            {
                break;
            }

            if (status == HeaderStatus.UnsupportedVersion)
            {
                throw new ProtocolException(ProtocolErrorKind.UnsupportedVersion, error!);
            }

            if (status == HeaderStatus.InvalidField)
            {
                throw new ProtocolException(ProtocolErrorKind.InvalidField, error!);
            }

            // BadMagic 또는 BadHeaderCrc: 경계를 잃었으므로 다음 "RFVF"를 찾는다. §6.2
            int shift = FindResyncShift(_header);
            skippedBytes += shift;
            if (skippedBytes > _maxResyncBytes)
            {
                throw new ProtocolException(ProtocolErrorKind.ResyncLimitExceeded, $"재동기화 중 {skippedBytes}바이트를 건너뛰었지만 메시지를 찾지 못했습니다.");
            }

            _header.AsSpan(shift).CopyTo(_header);
            await _stream.ReadExactlyAsync(_header.AsMemory(ProtocolConstants.HeaderSize - shift, shift), cancellationToken).ConfigureAwait(false);
        }

        // ⑥ 페이로드와 페이로드 CRC 읽기
        int payloadLength = (int)header.PayloadLength;
        byte[] body = new byte[payloadLength + ProtocolConstants.PayloadCrcSize];
        await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        // ⑦ 페이로드 CRC 확인. 틀리면 이 메시지만 버린다. 헤더가 정상이므로 경계는 유지된다.
        ReadOnlySpan<byte> payload = body.AsSpan(0, payloadLength);
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(payloadLength));
        if (Crc32.HashToUInt32(payload) != expectedCrc)
        {
            return new ReceivedMessage(ReceiveKind.PayloadCrcError, header, null, skippedBytes);
        }

        if (header.MessageType == MessageType.Heartbeat)
        {
            return new ReceivedMessage(ReceiveKind.Heartbeat, header, null, skippedBytes);
        }

        ushort[] pixels = new ushort[header.Width * header.Height];
        PixelCodec.ReadLittleEndian(payload, pixels);
        var frame = new RawFrame(header.FrameId, header.TimestampUs, header.Width, header.Height, header.BitsStored, pixels);
        return new ReceivedMessage(ReceiveKind.Frame, header, frame, skippedBytes);
    }

    /// <summary>
    /// 헤더 버퍼에서 다음 "RFVF" 후보 위치를 찾아, 앞에서 버릴 바이트 수를 돌려준다.
    /// 현재 위치(0)는 이미 실패했으므로 1부터 찾는다.
    /// 후보가 없으면 끝의 3바이트는 남긴다. 그 3바이트가 다음 Magic의 앞부분일 수 있기 때문이다.
    /// </summary>
    internal static int FindResyncShift(ReadOnlySpan<byte> header)
    {
        int index = header[1..].IndexOf(ProtocolConstants.Magic);
        if (index >= 0)
        {
            return index + 1;
        }

        return header.Length - (ProtocolConstants.Magic.Length - 1);
    }
}
