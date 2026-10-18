using System.Text;
using OutWit.OS.Display;

namespace OutWit.OS.Tests.Display;

/// <summary>
/// The US layout (plan step C2.2): characters under the modifiers, the control characters, the terminal's sequences for
/// the keys that are no character, nothing for a release.
/// </summary>
[TestFixture]
public sealed class UsKeyboardLayoutTests
{
    #region Constants

    private const ushort KEY_1 = 2;
    private const ushort KEY_BACKSPACE = 14;
    private const ushort KEY_TAB = 15;
    private const ushort KEY_ENTER = 28;
    private const ushort KEY_LEFTCTRL = 29;
    private const ushort KEY_A = 30;
    private const ushort KEY_LEFTSHIFT = 42;
    private const ushort KEY_C = 46;
    private const ushort KEY_LEFTALT = 56;
    private const ushort KEY_SPACE = 57;
    private const ushort KEY_CAPSLOCK = 58;
    private const ushort KEY_F1 = 59;
    private const ushort KEY_X = 45;
    private const ushort KEY_UP = 103;
    private const ushort KEY_DELETE = 111;

    private const int UP = 0;
    private const int DOWN = 1;
    private const int REPEAT = 2;

    #endregion

    #region Tests

    [Test]
    public void CharactersFollowShiftAndCapsLockTest()
    {
        var layout = new UsKeyboardLayout();
        Assert.That(Text(layout.Translate(KEY_A, DOWN)), Is.EqualTo("a"));
        Assert.That(layout.Translate(KEY_A, UP), Is.Empty);
        Assert.That(Text(layout.Translate(KEY_A, REPEAT)), Is.EqualTo("a"));
        Assert.That(layout.Translate(KEY_LEFTSHIFT, DOWN), Is.Empty);
        Assert.That(Text(layout.Translate(KEY_A, DOWN)), Is.EqualTo("A"));
        Assert.That(Text(layout.Translate(KEY_1, DOWN)), Is.EqualTo("!"));
        layout.Translate(KEY_LEFTSHIFT, UP);
        Assert.That(Text(layout.Translate(KEY_1, DOWN)), Is.EqualTo("1"));
        layout.Translate(KEY_CAPSLOCK, DOWN);
        layout.Translate(KEY_CAPSLOCK, UP);
        Assert.That(layout.CapsLock, Is.True);
        Assert.That(Text(layout.Translate(KEY_A, DOWN)), Is.EqualTo("A"));
        Assert.That(Text(layout.Translate(KEY_1, DOWN)), Is.EqualTo("1"), "Caps Lock leaves the digits alone");
        layout.Translate(KEY_LEFTSHIFT, DOWN);
        Assert.That(Text(layout.Translate(KEY_A, DOWN)), Is.EqualTo("a"), "Shift under Caps Lock gives the small letter");
        Assert.That(Text(layout.Translate(KEY_SPACE, DOWN)), Is.EqualTo(" "));
    }

    [Test]
    public void ControlAndAltMakeControlCharactersAndEscapesTest()
    {
        var layout = new UsKeyboardLayout();
        layout.Translate(KEY_LEFTCTRL, DOWN);
        Assert.That(layout.Translate(KEY_C, DOWN), Is.EqualTo(new byte[] { 0x03 }));
        layout.Translate(KEY_LEFTCTRL, UP);
        layout.Translate(KEY_LEFTALT, DOWN);
        Assert.That(layout.Translate(KEY_X, DOWN), Is.EqualTo(new byte[] { 0x1B, (byte)'x' }));
        layout.Translate(KEY_LEFTALT, UP);
        Assert.That(Text(layout.Translate(KEY_X, DOWN)), Is.EqualTo("x"));
    }

    // C2.6: xterm's modifier parameter on the cursor, editing and function keys, Alt among the modifiers instead of an ESC.
    [Test]
    public void ModifiedSpecialKeysCarryTheModifierParameterTest()
    {
        var layout = new UsKeyboardLayout();
        layout.Translate(KEY_LEFTSHIFT, DOWN);
        Assert.That(Text(layout.Translate(KEY_F1 + 3, DOWN)), Is.EqualTo("\x1B[1;2S"), "Shift+F4");
        layout.Translate(KEY_LEFTSHIFT, UP);
        layout.Translate(KEY_LEFTCTRL, DOWN);
        Assert.That(Text(layout.Translate(KEY_UP, DOWN)), Is.EqualTo("\x1B[1;5A"), "Ctrl+Up");
        Assert.That(Text(layout.Translate(KEY_DELETE, DOWN)), Is.EqualTo("\x1B[3;5~"), "Ctrl+Delete");
        layout.Translate(KEY_LEFTCTRL, UP);
        layout.Translate(KEY_LEFTALT, DOWN);
        Assert.That(Text(layout.Translate(KEY_F1 + 6, DOWN)), Is.EqualTo("\x1B[18;3~"), "Alt+F7");
        Assert.That(Text(layout.Translate(KEY_A, DOWN)), Is.EqualTo("\u001Ba"), "a character keeps Alt's ESC");
        layout.Translate(KEY_LEFTALT, UP);
        Assert.That(Text(layout.Translate(KEY_F1 + 4, DOWN)), Is.EqualTo("\x1B[15~"), "F5 alone");
    }

    [Test]
    public void SpecialKeysSendTheTerminalSequencesTest()
    {
        var layout = new UsKeyboardLayout();
        Assert.That(Text(layout.Translate(KEY_ENTER, DOWN)), Is.EqualTo("\r"));
        Assert.That(layout.Translate(KEY_BACKSPACE, DOWN), Is.EqualTo(new byte[] { 0x7F }));
        Assert.That(Text(layout.Translate(KEY_TAB, DOWN)), Is.EqualTo("\t"));
        Assert.That(Text(layout.Translate(KEY_UP, DOWN)), Is.EqualTo("\x1B[A"));
        Assert.That(Text(layout.Translate(KEY_DELETE, DOWN)), Is.EqualTo("\x1B[3~"));
        Assert.That(Text(layout.Translate(KEY_F1, DOWN)), Is.EqualTo("\x1BOP"));
        Assert.That(layout.Translate(999, DOWN), Is.Empty, "a key the layout does not know sends nothing");
    }

    #endregion

    #region Tools

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    #endregion
}
