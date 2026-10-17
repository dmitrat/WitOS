using WitOS.Dev.Host;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// Text as the keys of QEMU's keyboard (plan step C2.3): letters, digits and punctuation by their US keys, shifted
/// characters as chords, Enter for CR and LF, and a refusal of what the layout does not type.
/// </summary>
[TestFixture]
public sealed class KeyTypingTests
{
    #region Tests

    [Test]
    public void TextBecomesKeysTest()
    {
        Assert.That(KeyTyping.Keys("Get-Date\r"), Is.EqualTo("shift+g e t minus shift+d a t e ret"));
        Assert.That(KeyTyping.Keys("$PSVersionTable\n"), Is.EqualTo("shift+4 shift+p shift+s shift+v e r s i o n shift+t a b l e ret"));
        Assert.That(KeyTyping.Keys("1 + 2"), Is.EqualTo("1 spc shift+equal spc 2"));
        Assert.That(KeyTyping.Keys("ls /data; echo \"a\""), Is.EqualTo("l s spc slash d a t a semicolon spc e c h o spc shift+apostrophe a shift+apostrophe"));
        Assert.That(KeyTyping.Key('?'), Is.EqualTo("shift+slash"));
        Assert.That(KeyTyping.Key('~'), Is.EqualTo("shift+grave_accent"));
        Assert.That(KeyTyping.Key('\t'), Is.EqualTo("tab"));
    }

    [Test]
    public void UntypableCharactersAreRefusedTest()
    {
        Assert.Throws<ArgumentException>(() => KeyTyping.Key('é'));
        Assert.Throws<ArgumentException>(() => KeyTyping.Keys("a\u0001"));
    }

    #endregion
}
