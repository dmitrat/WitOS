namespace OutWit.OS.Display;

/// <summary>
/// The display's 16 colours (plan step C2.6): xterm's default palette, the eight colours of SGR 30–37 and their bright
/// forms of 90–97, and the reduction of a colour of 256 (SGR 38;5) or of 24 bits (SGR 38;2) to the nearest of them,
/// since a cell holds a palette index.
/// </summary>
public static class TerminalPalette
{
    #region Constants

    /// <summary>
    /// The palette: black, red, green, yellow, blue, magenta, cyan, white, then their bright forms.
    /// </summary>
    public static readonly (byte Red, byte Green, byte Blue)[] COLORS =
    [
        (0, 0, 0), (205, 0, 0), (0, 205, 0), (205, 205, 0), (0, 0, 238), (205, 0, 205), (0, 205, 205), (229, 229, 229),
        (127, 127, 127), (255, 0, 0), (0, 255, 0), (255, 255, 0), (92, 92, 255), (255, 0, 255), (0, 255, 255), (255, 255, 255)
    ];

    private static readonly byte[] CUBE_LEVELS = [0, 95, 135, 175, 215, 255];

    #endregion

    #region Functions

    /// <summary>
    /// The palette index of one of xterm's 256 colours: the 16 themselves, the 6×6×6 cube and the 24 greys reduced to
    /// the nearest of the 16.
    /// </summary>
    /// <param name="color">The colour's number, from 0 to 255.</param>
    /// <returns>A palette index from 0 to 15.</returns>
    public static int From256(int color)
    {
        if (color < 16)
            return color;
        if (color < 232)
        {
            var cube = color - 16;
            return Nearest(CUBE_LEVELS[cube / 36], CUBE_LEVELS[cube / 6 % 6], CUBE_LEVELS[cube % 6]);
        }
        var grey = (byte)(8 + 10 * (color - 232));
        return Nearest(grey, grey, grey);
    }

    /// <summary>
    /// The palette index nearest a colour of 24 bits, by the distance in RGB.
    /// </summary>
    /// <param name="red">Red.</param>
    /// <param name="green">Green.</param>
    /// <param name="blue">Blue.</param>
    /// <returns>A palette index from 0 to 15.</returns>
    public static int Nearest(byte red, byte green, byte blue)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < COLORS.Length; i++)
        {
            var (r, g, b) = COLORS[i];
            var distance = (r - red) * (r - red) + (g - green) * (g - green) + (b - blue) * (b - blue);
            if (distance >= bestDistance)
                continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    #endregion
}
