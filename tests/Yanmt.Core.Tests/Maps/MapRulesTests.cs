using System.Text;
using Yanmt.Core.Maps;

namespace Yanmt.Core.Tests.Maps;

public class MapRulesTests
{
    [Theory]
    [InlineData("Network", true)]
    [InlineData("  Site A  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("bad\u0007name", false)]
    public void Map_names(string? name, bool valid) => Assert.Equal(valid, MapRules.ValidateName(name) is null);

    [Fact]
    public void Map_name_has_a_length_limit() =>
        Assert.NotNull(MapRules.ValidateName(new string('m', MapRules.MaxNameLength + 1)));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Gi1/0/1 - Gi0/0/0", true)]
    [InlineData("tab\there", false)]
    public void Link_labels(string? label, bool valid) => Assert.Equal(valid, MapRules.ValidateLinkLabel(label) is null);

    [Theory]
    [InlineData(1600, 1000, true)]
    [InlineData(399, 1000, false)]
    [InlineData(1600, 10001, false)]
    public void Canvas_size(int w, int h, bool valid) => Assert.Equal(valid, MapRules.ValidateCanvas(w, h) is null);

    [Fact]
    public void Positions_are_clamped_to_the_canvas_and_rounded()
    {
        Assert.Equal((0, 1000), MapRules.Clamp(-50, 5000, 1600, 1000));
        Assert.Equal((12.3, 45.7), MapRules.Clamp(12.34, 45.66, 1600, 1000));
    }

    [Fact]
    public void Non_finite_positions_land_in_the_middle()
    {
        Assert.Equal((800, 500), MapRules.Clamp(double.NaN, double.PositiveInfinity, 1600, 1000));
    }
}

public class ImageInfoTests
{
    internal static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(data, 0);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        return data;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]); // APP0 (JFIF), length 16
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]); // SOF0, length 17, 8-bit precision
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        bytes.AddRange(new byte[10]);
        return [.. bytes];
    }

    [Fact]
    public void Reads_png_size()
    {
        Assert.True(ImageInfo.TryRead(Png(1920, 1080), out var format, out var w, out var h));
        Assert.Equal((ImageFormat.Png, 1920, 1080), (format, w, h));
    }

    [Fact]
    public void Reads_gif_size()
    {
        byte[] gif = [.. "GIF89a"u8, 0x20, 0x03, 0x58, 0x02, 0, 0, 0];

        Assert.True(ImageInfo.TryRead(gif, out var format, out var w, out var h));
        Assert.Equal((ImageFormat.Gif, 800, 600), (format, w, h));
    }

    [Fact]
    public void Reads_jpeg_size_after_other_segments()
    {
        Assert.True(ImageInfo.TryRead(Jpeg(1024, 768), out var format, out var w, out var h));
        Assert.Equal((ImageFormat.Jpeg, 1024, 768), (format, w, h));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><body>not an image</body></html>")]
    [InlineData("")]
    public void Refuses_svg_and_anything_else(string text) =>
        Assert.False(ImageInfo.TryRead(Encoding.UTF8.GetBytes(text), out _, out _, out _));

    [Fact]
    public void Refuses_truncated_and_zero_sized_images()
    {
        Assert.False(ImageInfo.TryRead(Png(100, 100).AsSpan(0, 20), out _, out _, out _));
        Assert.False(ImageInfo.TryRead(Png(0, 100), out _, out _, out _));
        Assert.False(ImageInfo.TryRead(Jpeg(640, 480).AsSpan(0, 24), out _, out _, out _));
    }
}
