namespace WitOS.Dev.Host;

/// <summary>
/// Text as keys of QEMU's keyboard (plan step C2.3): each character of a line the runner types on the guest's virtio
/// keyboard becomes a QKeyCode name for <see cref="QemuControl.SendKeyAsync"/>, with a chord ("shift+a") where the US
/// layout needs the shift key, which the guest's own layout turns back into the character. CR and LF are the Enter key,
/// which the layout gives a program as CR, as PSReadLine reads it raw.
/// </summary>
internal static class KeyTyping
{
    #region Constants

    // The keys typed without shift, by the character they give on the US layout.
    private static readonly Dictionary<char, string> PLAIN = new()
    {
        [' '] = "spc",
        ['-'] = "minus",
        ['='] = "equal",
        ['['] = "bracket_left",
        [']'] = "bracket_right",
        ['\\'] = "backslash",
        [';'] = "semicolon",
        ['\''] = "apostrophe",
        ['`'] = "grave_accent",
        [','] = "comma",
        ['.'] = "dot",
        ['/'] = "slash",
        ['\r'] = "ret",
        ['\n'] = "ret",
        ['\t'] = "tab",
        ['\b'] = "backspace",
        ['\x1b'] = "esc"
    };

    // The characters shift gives on the US layout, by the character of the same key without it.
    private static readonly Dictionary<char, char> SHIFTED = new()
    {
        ['!'] = '1',
        ['@'] = '2',
        ['#'] = '3',
        ['$'] = '4',
        ['%'] = '5',
        ['^'] = '6',
        ['&'] = '7',
        ['*'] = '8',
        ['('] = '9',
        [')'] = '0',
        ['_'] = '-',
        ['+'] = '=',
        ['{'] = '[',
        ['}'] = ']',
        ['|'] = '\\',
        [':'] = ';',
        ['"'] = '\'',
        ['~'] = '`',
        ['<'] = ',',
        ['>'] = '.',
        ['?'] = '/'
    };

    #endregion

    #region Functions

    /// <summary>
    /// The keys that type a text, separated by spaces, in the order of its characters.
    /// </summary>
    /// <param name="text">The text, of the characters the US layout types.</param>
    /// <returns>The QKeyCode names, a chord joined by '+'.</returns>
    /// <exception cref="ArgumentException">A character the layout does not type.</exception>
    public static string Keys(string text) => string.Join(' ', text.Select(Key));

    /// <summary>
    /// The key, or the chord, that types a character.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>The QKeyCode name, or "shift+" and the name.</returns>
    /// <exception cref="ArgumentException">A character the layout does not type.</exception>
    public static string Key(char c)
    {
        if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            return c.ToString();
        if (char.IsAsciiLetterUpper(c))
            return "shift+" + char.ToLowerInvariant(c);
        if (SHIFTED.TryGetValue(c, out var unshifted))
            return "shift+" + Key(unshifted);
        if (PLAIN.TryGetValue(c, out var name))
            return name;
        throw new ArgumentException($"The US keyboard types no '{c}' (U+{(int)c:X4}).", nameof(c));
    }

    #endregion
}
