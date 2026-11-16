using RawFrameViewer.Core.Protocol;

namespace RawFrameViewer.Core.Tests.Protocol;

/// <summary>docs/protocol.md §3, §6.1, §6.2, §7 수신 동작 검증</summary>
public class MessageReaderTests
{
    [Fact(DisplayName = "§3 메시지 길이 = 40 + 가로×세로×2")]
    public void EncodedLength_MatchesSpec()
    {
        Assert.Equal(40, MessageEncoder.EncodeHeartbeat(0, 0).Length);
        Assert.Equal(40 + (4 * 3 * 2), MessageEncoder.EncodeFrame(TestFrames.Create(width: 4, height: 3)).Length);
        Assert.Equal(8_388_648, MessageEncoder.GetMessageLength(2048u * 2048 * 2));
    }

    [Fact(DisplayName = "Frame을 보내고 받으면 픽셀이 그대로 복원된다")]
    public async Task Frame_RoundTrips()
    {
        RawFrame sent = TestFrames.Create(frameId: 5, width: 16, height: 9);
        var reader = ReaderOf(MessageEncoder.EncodeFrame(sent));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.NotNull(received);
        Assert.Equal(ReceiveKind.Frame, received.Kind);
        Assert.Equal(0, received.SkippedBytes);
        RawFrame frame = received.Frame!;
        Assert.Equal(sent.FrameId, frame.FrameId);
        Assert.Equal(sent.TimestampUs, frame.TimestampUs);
        Assert.Equal(sent.Width, frame.Width);
        Assert.Equal(sent.Height, frame.Height);
        Assert.Equal(sent.BitsStored, frame.BitsStored);
        Assert.True(sent.Pixels.SequenceEqual(frame.Pixels));
    }

    [Fact(DisplayName = "§3.2 픽셀은 Little-endian 2바이트로 행 우선 배치된다")]
    public void Payload_IsLittleEndianRowMajor()
    {
        var frame = new RawFrame(1, 0, width: 2, height: 1, bitsStored: 16, pixels: [0x1234, 0xABCD]);
        byte[] message = MessageEncoder.EncodeFrame(frame);

        Assert.Equal(new byte[] { 0x34, 0x12, 0xCD, 0xAB }, message[36..40]);
    }

    [Fact(DisplayName = "Heartbeat는 FrameId만 담고 영상이 없다")]
    public async Task Heartbeat_IsReceived()
    {
        var reader = ReaderOf(MessageEncoder.EncodeHeartbeat(lastFrameId: 9, timestampUs: 100));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.Equal(ReceiveKind.Heartbeat, received!.Kind);
        Assert.Equal(9u, received.Header.FrameId);
        Assert.Null(received.Frame);
    }

    [Fact(DisplayName = "§7 F2 페이로드가 깨지면 그 프레임만 버리고 다음 프레임은 정상 수신")]
    public async Task PayloadCorruption_DropsOnlyThatFrame()
    {
        byte[] corrupted = MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 1));
        corrupted[ProtocolConstants.HeaderSize + 3] ^= 0x01; // CRC 계산 후 페이로드 1비트 반전
        var reader = ReaderOf(corrupted, MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 2)));

        ReceivedMessage? first = await reader.ReadAsync();
        ReceivedMessage? second = await reader.ReadAsync();

        Assert.Equal(ReceiveKind.PayloadCrcError, first!.Kind);
        Assert.Null(first.Frame);
        Assert.Equal(1u, first.Header.FrameId);
        Assert.Equal(ReceiveKind.Frame, second!.Kind);
        Assert.Equal(2u, second.Frame!.FrameId);
        Assert.Equal(0, second.SkippedBytes);
    }

    [Fact(DisplayName = "§7 F3 헤더가 깨지면 재동기화해서 다음 프레임을 받는다")]
    public async Task HeaderCorruption_ResyncsToNextMessage()
    {
        byte[] corrupted = MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 1));
        corrupted[10] ^= 0xFF; // FrameId 영역. HeaderCrc 계산 후 변경
        var reader = ReaderOf(corrupted, MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 2)));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.Equal(ReceiveKind.Frame, received!.Kind);
        Assert.Equal(2u, received.Frame!.FrameId);
        Assert.Equal(corrupted.Length, received.SkippedBytes); // 깨진 메시지 전체를 건너뜀
    }

    [Theory(DisplayName = "§6.2 앞에 쓰레기 바이트가 있으면 정확히 그만큼 건너뛴다")]
    [InlineData(1)]
    [InlineData(3)]   // Magic 길이 - 1
    [InlineData(35)]  // 헤더 길이 - 1
    [InlineData(36)]
    [InlineData(1000)]
    public async Task LeadingGarbage_IsSkippedExactly(int garbageLength)
    {
        byte[] garbage = Enumerable.Repeat((byte)0xEE, garbageLength).ToArray();
        var reader = ReaderOf(garbage, MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 3)));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.Equal(3u, received!.Frame!.FrameId);
        Assert.Equal(garbageLength, received.SkippedBytes);
    }

    [Fact(DisplayName = "§6.2 Magic의 앞부분만 있는 가짜 시작(RFV)에 속지 않는다")]
    public async Task PartialMagicInGarbage_IsSkipped()
    {
        byte[] garbage = "xxRFVxRFxR"u8.ToArray();
        var reader = ReaderOf(garbage, MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 4)));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.Equal(4u, received!.Frame!.FrameId);
        Assert.Equal(garbage.Length, received.SkippedBytes);
    }

    [Fact(DisplayName = "§6.2 가짜 RFVF가 있어도 HeaderCrc로 걸러낸다")]
    public async Task FakeMagic_IsRejectedByHeaderCrc()
    {
        byte[] fake = new byte[50];
        "RFVF"u8.CopyTo(fake); // Magic만 맞고 나머지는 0 → HeaderCrc 불일치
        var reader = ReaderOf(fake, MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 6)));

        ReceivedMessage? received = await reader.ReadAsync();

        Assert.Equal(6u, received!.Frame!.FrameId);
        Assert.Equal(fake.Length, received.SkippedBytes);
    }

    [Fact(DisplayName = "§6.2⑤ 건너뛴 바이트가 한도를 넘으면 연결을 끊어야 한다")]
    public async Task ResyncLimit_Throws()
    {
        var reader = new MessageReader(new MemoryStream(new byte[500]), maxResyncBytes: 100);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync().AsTask());
        Assert.Equal(ProtocolErrorKind.ResyncLimitExceeded, ex.Kind);
    }

    [Fact(DisplayName = "§6.1⑤ CRC가 맞는데 필드가 틀리면 ProtocolException(InvalidField)")]
    public async Task InvalidField_Throws()
    {
        byte[] b = new byte[ProtocolConstants.FixedOverhead];
        new FrameHeader(MessageType.Frame, 1, 0, 4, 3, 14, 999).WriteTo(b);
        var reader = ReaderOf(b);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync().AsTask());
        Assert.Equal(ProtocolErrorKind.InvalidField, ex.Kind);
    }

    [Fact(DisplayName = "메시지 경계에서 스트림이 끝나면 null")]
    public async Task EndOfStreamAtBoundary_ReturnsNull()
    {
        var reader = ReaderOf(MessageEncoder.EncodeHeartbeat(0, 0));

        Assert.NotNull(await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync());
    }

    [Theory(DisplayName = "메시지 중간에서 스트림이 끝나면 EndOfStreamException")]
    [InlineData(10)]  // 헤더 도중
    [InlineData(40)]  // 페이로드 도중
    [InlineData(-2)]  // 페이로드 CRC 도중
    public async Task EndOfStreamMidMessage_Throws(int cutAt)
    {
        byte[] message = MessageEncoder.EncodeFrame(TestFrames.Create());
        int length = cutAt > 0 ? cutAt : message.Length + cutAt;
        var reader = ReaderOf(message[..length]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync().AsTask());
    }

    [Fact(DisplayName = "TCP처럼 1바이트씩 쪼개져 도착해도 정상 수신")]
    public async Task FragmentedStream_IsReassembled()
    {
        byte[] data = [.. MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 1)), .. MessageEncoder.EncodeFrame(TestFrames.Create(frameId: 2))];
        var reader = new MessageReader(new OneByteAtATimeStream(data));

        Assert.Equal(1u, (await reader.ReadAsync())!.Frame!.FrameId);
        Assert.Equal(2u, (await reader.ReadAsync())!.Frame!.FrameId);
    }

    [Fact(DisplayName = "FindResyncShift: 후보가 없으면 끝의 3바이트를 남긴다")]
    public void FindResyncShift_KeepsLastThreeBytes()
    {
        byte[] header = new byte[ProtocolConstants.HeaderSize];
        Assert.Equal(33, MessageReader.FindResyncShift(header));

        "RFVF"u8.CopyTo(header.AsSpan(10));
        Assert.Equal(10, MessageReader.FindResyncShift(header));
    }

    private static MessageReader ReaderOf(params byte[][] parts) =>
        new(new MemoryStream(parts.SelectMany(p => p).ToArray()));

    /// <summary>Read 한 번에 최대 1바이트만 돌려주는 스트림. 네트워크에서 데이터가 잘게 나뉘어 도착하는 상황을 흉내 낸다.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
    }
}
