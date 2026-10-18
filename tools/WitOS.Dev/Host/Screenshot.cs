using System.Text;
using OutWit.OS.Display;

namespace WitOS.Dev.Host;

/// <summary>
/// A screendump QEMU wrote (plan step C2.1): a binary PPM (P6) of the display, read for the judge of the display scenario
/// and kept in the logs for a person to look at.
/// </summary>
internal sealed class Screenshot
{
    #region Fields

    private readonly byte[] m_pixels;

    #endregion

    #region Constructors

    private Screenshot(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        m_pixels = pixels;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads a binary PPM.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The screenshot.</returns>
    /// <exception cref="InvalidDataException">The file is not a binary PPM of 8-bit samples.</exception>
    public static Screenshot Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // The header: P6, then the width, the height and the maximum sample, each separated by whitespace, then one
        // whitespace byte before the samples; a comment line may follow the magic.
        var at = 0;
        var fields = new List<string>();
        while (fields.Count < 4 && at < bytes.Length)
        {
            while (at < bytes.Length && char.IsWhiteSpace((char)bytes[at]))
                at++;
            if (at < bytes.Length && bytes[at] == '#')
            {
                while (at < bytes.Length && bytes[at] != '\n')
                    at++;
                continue;
            }
            var start = at;
            while (at < bytes.Length && !char.IsWhiteSpace((char)bytes[at]))
                at++;
            fields.Add(Encoding.ASCII.GetString(bytes, start, at - start));
        }
        if (fields.Count != 4 || fields[0] != "P6" || !int.TryParse(fields[1], out var width) || !int.TryParse(fields[2], out var height) ||
            fields[3] != "255" || width <= 0 || height <= 0)
            throw new InvalidDataException($"{path} is not a binary PPM of 8-bit samples.");
        at++; // the one whitespace byte after the maximum sample
        if (bytes.Length - at < (long)width * height * 3)
            throw new InvalidDataException($"{path} holds fewer samples than its {width} by {height} say.");
        return new Screenshot(width, height, bytes[at..(at + width * height * 3)]);
    }

    /// <summary>
    /// The colour of a pixel.
    /// </summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>Red, green and blue.</returns>
    public (byte Red, byte Green, byte Blue) Pixel(int x, int y)
    {
        var at = (y * Width + x) * 3;
        return (m_pixels[at], m_pixels[at + 1], m_pixels[at + 2]);
    }

    /// <summary>
    /// The colours the screen shows, each once (plan step C2.6).
    /// </summary>
    /// <returns>The distinct colours.</returns>
    public IReadOnlySet<(byte Red, byte Green, byte Blue)> Colours()
    {
        var colours = new HashSet<(byte, byte, byte)>();
        for (var at = 0; at + 2 < m_pixels.Length; at += 3)
            colours.Add((m_pixels[at], m_pixels[at + 1], m_pixels[at + 2]));
        return colours;
    }

    /// <summary>
    /// Requires the screen to equal a frame the host rendered, pixel for pixel (plan step C2.2): the display service and
    /// the host draw the same screen with the same renderer and font, so the two frames differ only where the display
    /// drew something else.
    /// </summary>
    /// <param name="expected">The frame the host rendered, of the screen's size.</param>
    /// <exception cref="InvalidOperationException">The sizes differ, or a pixel does.</exception>
    public void RequireFrame(MemoryFrame expected)
    {
        if (expected.Geometry.Width != Width || expected.Geometry.Height != Height)
            throw new InvalidOperationException($"The screen is {Width} by {Height} where the frame rendered is {expected.Geometry.Width} by {expected.Geometry.Height}.");
        var differing = 0;
        (int X, int Y, (byte, byte, byte) Expected, (byte, byte, byte) Actual)? first = null;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var rendered = expected.Pixel(x, y);
                var actual = Pixel(x, y);
                if (rendered == actual)
                    continue;
                differing++;
                first ??= (x, y, rendered, actual);
            }
        }
        if (first is { } mismatch)
            throw new InvalidOperationException($"The screen differs from the frame the host rendered in {differing} of {Width * Height} pixels; " +
                $"the first, ({mismatch.X}, {mismatch.Y}), is {mismatch.Actual} where {mismatch.Expected} was rendered.");
    }

    /// <summary>
    /// Requires the device manager's test pattern on the screen: the frame's corners and the middle of each band, as
    /// <see cref="TestPattern"/> draws them for the screen's own size.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sampled pixel differs from the pattern.</exception>
    public void RequireTestPattern()
    {
        var samples = new List<(int X, int Y)> { (0, 0), (Width - 1, 0), (0, Height - 1), (Width - 1, Height - 1), (TestPattern.FRAME / 2, Height / 2) };
        for (var band = 0; band < TestPattern.BANDS; band++)
        {
            var row = TestPattern.BandMiddle(band, Height);
            samples.Add((TestPattern.FRAME, row));
            samples.Add((Width / 2, row));
            samples.Add((Width - TestPattern.FRAME - 1, row));
        }
        foreach (var (x, y) in samples)
        {
            var expected = TestPattern.Pixel(x, y, Width, Height);
            var actual = Pixel(x, y);
            if (actual != expected)
                throw new InvalidOperationException($"The screen's pixel ({x}, {y}) is {actual} where the test pattern has {expected} " +
                    $"on {Width} by {Height}.");
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// The columns.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// The rows.
    /// </summary>
    public int Height { get; }

    #endregion
}
