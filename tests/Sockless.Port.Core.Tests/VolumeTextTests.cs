using System.Text;
using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

public sealed class VolumeTextTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Decodes_utf8_text_without_its_byte_order_mark()
    {
        Assert.Equal(("name: web\n", null), VolumeText.Decode(Bytes("name: web\n")));
        Assert.Equal(("{}", null), VolumeText.Decode([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']));
        Assert.Equal(("", null), VolumeText.Decode([]));
        Assert.Equal(("a\r\nb\r\n", null), VolumeText.Decode(Bytes("a\r\nb\r\n")));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'a', 0, (byte)'b' }, "binary")]
    [InlineData(new byte[] { 0xC3, 0x28 }, "isn't UTF-8")]
    [InlineData(new byte[] { (byte)'a', (byte)'\r', (byte)'b' }, "carriage returns")]
    public void Refuses_what_a_text_area_would_change(byte[] bytes, string reason)
    {
        var (text, notEditable) = VolumeText.Decode(bytes);

        Assert.Null(text);
        Assert.Contains(reason, notEditable);
    }

    [Fact]
    public void Refuses_large_files()
    {
        Assert.Equal(VolumeText.TooLarge, VolumeText.Decode(new byte[VolumeText.MaxBytes + 1]).NotEditable);
        Assert.NotNull(VolumeText.Decode(Enumerable.Repeat((byte)'x', VolumeText.MaxBytes).ToArray()).Text);
    }

    [Theory]
    // Browsers send a text area with CRLF: a file with LF endings keeps them.
    [InlineData("a\nb\n", "a\r\nb\r\nc", "a\nb\nc")]
    [InlineData("a\nb\n", "a\nb", "a\nb")]
    // A file with CRLF endings keeps them, whatever the text has.
    [InlineData("a\r\nb\r\n", "a\r\nb\nc", "a\r\nb\r\nc")]
    // Mixed: the most common ending wins.
    [InlineData("a\r\nb\r\nc\n", "x\ny\nz", "x\r\ny\r\nz")]
    [InlineData("", "x\r\ny", "x\ny")]
    public void Encodes_with_the_original_line_endings(string original, string edited, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(VolumeText.Encode(edited, Bytes(original))));
    }

    [Fact]
    public void Keeps_the_byte_order_mark()
    {
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], VolumeText.Encode("x", [0xEF, 0xBB, 0xBF, (byte)'y']));
        Assert.Equal([(byte)'x'], VolumeText.Encode("x", Bytes("y")));
    }

    [Fact]
    public void Versions_name_the_content()
    {
        Assert.Equal(VolumeText.Version(Bytes("a")), VolumeText.Version(Bytes("a")));
        Assert.NotEqual(VolumeText.Version(Bytes("a")), VolumeText.Version(Bytes("a\n")));
        Assert.Equal(64, VolumeText.Version([]).Length);
    }
}
