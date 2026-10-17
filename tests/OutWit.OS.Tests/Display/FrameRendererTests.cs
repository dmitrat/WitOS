using System;
using System.Text;
using OutWit.OS.Display;

namespace OutWit.OS.Tests.Display;

/// <summary>
/// The renderer (plan step C2.2): a screen of cells drawn white on black with the font, the cursor an inverted cell,
/// the frame kept right across changes, the sizes checked.
/// </summary>
[TestFixture]
public sealed class FrameRendererTests
{
    #region Constants

    private static readonly (byte, byte, byte) WHITE = (255, 255, 255);
    private static readonly (byte, byte, byte) BLACK = (0, 0, 0);

    #endregion

    #region Tests

    [Test]
    public void CellsAndCursorAreDrawnTest()
    {
        var font = BitmapFont.ParseBdf(BitmapFontTests.SAMPLE);
        var geometry = new FramebufferGeometry(16, 32, 16, FramebufferGeometry.BGRX);
        var frame = new MemoryFrame(geometry);
        var renderer = new FrameRenderer(geometry, font, frame);
        Assert.That((renderer.Columns, renderer.Rows), Is.EqualTo((2, 2)));
        var screen = new TerminalScreen(2, 2);
        renderer.Clear();
        screen.Feed("A"u8);
        renderer.Render(screen);
        // 'A' at (0, 0): its row 1 is 0x18, pixels 3 and 4 lit; the cursor at (0, 1) inverts that cell.
        Assert.That(frame.Pixel(3, 1), Is.EqualTo(WHITE));
        Assert.That(frame.Pixel(4, 1), Is.EqualTo(WHITE));
        Assert.That(frame.Pixel(0, 1), Is.EqualTo(BLACK));
        Assert.That(frame.Pixel(8, 0), Is.EqualTo(WHITE), "the cursor's cell is inverted");
        Assert.That(frame.Pixel(15, 15), Is.EqualTo(WHITE));
        Assert.That(frame.Pixel(0, 16), Is.EqualTo(BLACK), "the second row is blank");

        // A new line moves the cursor to (1, 0): the old cell goes back to black, the new one is inverted.
        screen.Feed("\r\n"u8);
        renderer.Render(screen);
        Assert.That(frame.Pixel(8, 0), Is.EqualTo(BLACK));
        Assert.That(frame.Pixel(0, 16), Is.EqualTo(WHITE));
        Assert.That(frame.Pixel(3, 1), Is.EqualTo(WHITE), "the 'A' stays");
    }

    [Test]
    public void FormatIsHonouredTest()
    {
        var font = BitmapFont.ParseBdf(BitmapFontTests.SAMPLE);
        var geometry = new FramebufferGeometry(8, 16, 8, FramebufferGeometry.RGBX);
        var frame = new MemoryFrame(geometry);
        var renderer = new FrameRenderer(geometry, font, frame);
        var screen = new TerminalScreen(1, 1);
        renderer.Clear();
        screen.Feed("A"u8); // the cursor stays on the one cell, so the cell is inverted: lit pixels black, the rest white
        renderer.Render(screen);
        Assert.That(frame.Pixel(3, 1), Is.EqualTo(BLACK));
        Assert.That(frame.Pixel(0, 0), Is.EqualTo(WHITE));
        Assert.That(FrameRenderer.Decode(TestPattern.Encode((1, 2, 3), FramebufferGeometry.RGBX), FramebufferGeometry.RGBX), Is.EqualTo(((byte)1, (byte)2, (byte)3)));
        Assert.That(FrameRenderer.Decode(TestPattern.Encode((1, 2, 3), FramebufferGeometry.BGRX), FramebufferGeometry.BGRX), Is.EqualTo(((byte)1, (byte)2, (byte)3)));
    }

    [Test]
    public void SizesAreCheckedTest()
    {
        var font = BitmapFont.ParseBdf(BitmapFontTests.SAMPLE);
        var geometry = new FramebufferGeometry(16, 32, 16, FramebufferGeometry.BGRX);
        var renderer = new FrameRenderer(geometry, font, new MemoryFrame(geometry));
        Assert.Throws<ArgumentException>(() => renderer.Render(new TerminalScreen(3, 2)));
        Assert.Throws<ArgumentException>(() => new FrameRenderer(new FramebufferGeometry(4, 4, 4, FramebufferGeometry.BGRX), font, new MemoryFrame(geometry)));
    }

    [Test]
    public void TextRendersTheSameFrameTwiceTest()
    {
        var font = BitmapFont.ParseBdf(BitmapFontTests.SAMPLE);
        var geometry = new FramebufferGeometry(16, 32, 16, FramebufferGeometry.BGRX);
        var text = Encoding.ASCII.GetBytes("A?\r\nA");
        var first = Render(geometry, font, text, incremental: false);
        var second = Render(geometry, font, text, incremental: true);
        Assert.That(second, Is.EqualTo(first), "a frame drawn in steps equals the frame drawn at once");
    }

    #endregion

    #region Tools

    private static byte[] Render(FramebufferGeometry geometry, BitmapFont font, byte[] text, bool incremental)
    {
        var frame = new MemoryFrame(geometry);
        var renderer = new FrameRenderer(geometry, font, frame);
        var screen = new TerminalScreen(renderer.Columns, renderer.Rows);
        renderer.Clear();
        if (incremental)
        {
            foreach (var b in text)
            {
                screen.Feed([b]);
                renderer.Render(screen);
            }
        }
        else
        {
            screen.Feed(text);
            renderer.Render(screen);
        }
        return frame.Bytes;
    }

    #endregion
}
