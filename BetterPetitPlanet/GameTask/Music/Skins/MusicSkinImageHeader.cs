using System;
using System.Buffers.Binary;
using System.IO;

namespace BetterPetitPlanet.GameTask.Music.Skins;

internal readonly record struct MusicSkinImageHeader(int Width, int Height)
{
    // Read only encoded headers, never instantiate a WIC decoder before admission.
    public static MusicSkinImageHeader Read(Stream stream, string extension, MusicSkinResourceLimits limits)
    {
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        MusicSkinImageHeader header;
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("PNG 图片签名无效");
            Span<byte> ihdr = stackalloc byte[16];
            stream.ReadExactly(ihdr);
            if (BinaryPrimitives.ReadUInt32BigEndian(ihdr) != 13 || !ihdr.Slice(4, 4).SequenceEqual("IHDR"u8))
                throw new InvalidOperationException("PNG 图片头无效");
            header = CheckDimensions(BinaryPrimitives.ReadUInt32BigEndian(ihdr[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(ihdr[12..]), limits);
            if (stream.Length < 33) throw new InvalidOperationException("PNG 图片头不完整");
        }
        else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (signature[0] != 0xff || signature[1] != 0xd8)
                throw new InvalidOperationException("JPEG 图片签名无效");
            stream.Position = 2;
            header = ReadJpeg(stream, limits);
        }
        else throw new InvalidOperationException("纹理仅支持 PNG/JPG/JPEG 图片");
        stream.Position = 0;
        return header;
    }

    private static MusicSkinImageHeader ReadJpeg(Stream stream, MusicSkinResourceLimits limits)
    {
        Span<byte> bytes = stackalloc byte[5];
        while (stream.Position < stream.Length)
        {
            if (stream.ReadByte() != 0xff) throw new InvalidOperationException("JPEG 标记无效");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker == 0xda || marker == 0xd9) break;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            stream.ReadExactly(bytes[..2]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            if (length < 2) throw new InvalidOperationException("JPEG 段长度无效");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) throw new InvalidOperationException("JPEG 帧头无效");
                stream.ReadExactly(bytes);
                var header = CheckDimensions(BinaryPrimitives.ReadUInt16BigEndian(bytes[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[1..]), limits);
                if (stream.Position + length - 7 > stream.Length) throw new InvalidOperationException("JPEG 帧头不完整");
                return header;
            }
            if (stream.Position + length - 2 > stream.Length) break;
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidOperationException("JPEG 缺少支持的尺寸帧头");
    }

    private static MusicSkinImageHeader CheckDimensions(uint width, uint height, MusicSkinResourceLimits limits)
    {
        if (width == 0 || height == 0 || width > limits.MaxImageDimension || height > limits.MaxImageDimension)
            throw new InvalidOperationException($"图片原始尺寸 {width}×{height} 超出 1–{limits.MaxImageDimension} 像素限制");
        return new((int)width, (int)height);
    }
}
