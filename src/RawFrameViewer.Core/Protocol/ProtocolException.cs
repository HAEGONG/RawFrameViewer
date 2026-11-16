namespace RawFrameViewer.Core.Protocol;

/// <summary>프로토콜 오류의 종류. 모두 연결을 끊어야 하는 상황이다. §6.1, §6.2</summary>
public enum ProtocolErrorKind
{
    UnsupportedVersion,
    InvalidField,
    ResyncLimitExceeded,
}

/// <summary>회복할 수 없는 프로토콜 오류. 수신 측은 연결을 끊고 기록해야 한다.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
    {
    }

    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProtocolException(ProtocolErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public ProtocolErrorKind Kind { get; }
}
