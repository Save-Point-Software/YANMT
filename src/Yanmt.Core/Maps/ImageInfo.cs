using System.Buffers.Binary;

namespace Yanmt.Core.Maps;

public enum ImageFormat
{
    Png,
    Jpeg,
    Gif,
}

/// <summary>
/// Identifies a map background image by its magic bytes (never by file name or browser-supplied
/// type) and reads its pixel size. Only raster formats are accepted: SVG is refused because the
/// file is served from YANMT's own origin and could carry script.
/// </summary>
public static class ImageInfo
{
    public static bool TryRead(ReadOnlySpan<byte> data, out ImageFormat format, out int width, out int height)
    {
        format = default;
        width = height = 0;

        // PNG: signature, then the IHDR chunk with big-endian width and height.
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            && data[12..16].SequenceEqual("IHDR"u8))
        {
            format = ImageFormat.Png;
            width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue);
            height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[20..]), int.MaxValue);
            return width > 0 && height > 0;
        }

        // GIF87a/GIF89a: little-endian logical screen size.
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            format = ImageFormat.Gif;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            return width > 0 && height > 0;
        }

        // JPEG: walk the segments to the first start-of-frame marker.
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            format = ImageFormat.Jpeg;
            var i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF)
                {
                    return false;
                }

                var marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++; // fill byte
                    continue;
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    i += 2; // markers without a length
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
                if (length < 2)
                {
                    return false;
                }

                // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC).
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    if (i + 9 > data.Length)
                    {
                        return false;
                    }

                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]);
                    return width > 0 && height > 0;
                }

                if (marker is 0xD9 or 0xDA)
                {
                    return false; // end of image / start of scan before any frame header
                }

                i += 2 + length;
            }

            return false;
        }

        return false;
    }

    public static string ContentType(ImageFormat format) => format switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Gif => "image/gif",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Gif => ".gif",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
