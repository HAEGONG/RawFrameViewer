using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RawFrameViewer.Core.Protocol;

/// <summary>
/// 픽셀 배열과 Little-endian 바이트 배열 사이의 변환.
/// PC(x86/x64)는 Little-endian이라 그대로 복사하고, 그렇지 않은 환경에서만 바이트를 뒤집는다.
/// </summary>
internal static class PixelCodec
{
    public static void WriteLittleEndian(ReadOnlySpan<ushort> pixels, Span<byte> destination)
    {
        Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(destination[..(pixels.Length * 2)]);
        if (BitConverter.IsLittleEndian)
        {
            pixels.CopyTo(target);
        }
        else
        {
            BinaryPrimitives.ReverseEndianness(pixels, target);
        }
    }

    public static void ReadLittleEndian(ReadOnlySpan<byte> source, Span<ushort> pixels)
    {
        ReadOnlySpan<ushort> src = MemoryMarshal.Cast<byte, ushort>(source[..(pixels.Length * 2)]);
        if (BitConverter.IsLittleEndian)
        {
            src.CopyTo(pixels);
        }
        else
        {
            BinaryPrimitives.ReverseEndianness(src, pixels);
        }
    }
}
