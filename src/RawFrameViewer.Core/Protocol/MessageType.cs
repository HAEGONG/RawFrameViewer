namespace RawFrameViewer.Core.Protocol;

/// <summary>메시지 종류. §3.1 MessageType</summary>
public enum MessageType : ushort
{
    Frame = 1,
    Heartbeat = 2,
}
