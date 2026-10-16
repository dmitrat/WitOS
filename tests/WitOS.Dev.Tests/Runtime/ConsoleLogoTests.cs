using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The console marks (plan step C1.4, @Docs/WitOS-Console-Logos.md): the art files have the sizes and the characters
/// the guide gives, the palette names every role and every emblem row, and the relay sends what a terminal sends.
/// </summary>
[TestFixture]
public sealed class ConsoleLogoTests
{
    #region Functions

    [Test]
    public void ArtHasTheGuidesSizesTest()
    {
        var emblem = Lines("emblem.txt");
        Assert.That(emblem.Length, Is.EqualTo(7));
        Assert.That(emblem.Max(line => line.Length), Is.EqualTo(32));
        Assert.That(emblem.All(line => line.All(c => c is ' ' or '█' or '▓' or '▒' or '░')), Is.True, "The emblem is block elements and spaces");
        var wordmark = Lines("wordmark.txt");
        Assert.That(wordmark.Length, Is.EqualTo(6));
        Assert.That(wordmark.Max(line => line.Length), Is.EqualTo(42));
        var ascii = Lines("ascii.txt");
        Assert.That(ascii.Length, Is.EqualTo(4));
        Assert.That(ascii.Max(line => line.Length), Is.LessThanOrEqualTo(33));
        Assert.That(ascii.All(line => line.All(c => c >= ' ' && c <= '~')), Is.True, "The fallback is 7-bit ASCII");
    }

    [Test]
    public void PaletteNamesEveryRoleAndRowTest()
    {
        using var palette = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.Root, ConsoleLogo.SOURCE, "palette.json")));
        var roles = palette.RootElement.GetProperty("roles");
        foreach (var role in new[] { "layer3", "layer2", "layer1", "uhi" })
        {
            var colour = roles.GetProperty(role);
            Assert.That(colour.GetProperty("truecolor").GetString(), Does.Match("^#[0-9A-Fa-f]{6}$"));
            Assert.That(colour.GetProperty("256").GetInt32(), Is.InRange(0, 255));
            Assert.That(colour.GetProperty("16").GetInt32(), Is.InRange(30, 97));
        }
        var rows = palette.RootElement.GetProperty("emblemRows").EnumerateArray().Select(row => row.GetString()).ToArray();
        Assert.That(rows.Length, Is.EqualTo(7), "One role a row of the emblem");
        Assert.That(rows.All(row => roles.TryGetProperty(row!, out _)), Is.True);
        Assert.That(ConsoleLogo.PackageFiles(TestEnvironment.Root).Select(file => file.Name),
            Is.EquivalentTo(ConsoleLogo.FILES.Select(file => $"{ConsoleLogo.DIRECTORY}/{file}")));
        Assert.That(File.Exists(Path.Combine(TestEnvironment.Root, ConsoleLogo.PROFILE)), Is.True);
    }

    [Test]
    public void RelaySendsWhatATerminalSendsTest()
    {
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)), Is.EqualTo("\r"u8.ToArray()));
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)), Is.EqualTo("\x1B[A"u8.ToArray()));
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)), Is.EqualTo(new byte[] { 0x7F }));
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true)), Is.EqualTo(new byte[] { 0x03 }), "Ctrl-C reaches the guest");
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('ж', ConsoleKey.NoName, false, false, false)), Is.EqualTo(Encoding.UTF8.GetBytes("ж")));
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('x', ConsoleKey.X, false, true, false)), Is.EqualTo("\x1Bx"u8.ToArray()), "Alt prefixes ESC");
        Assert.That(TerminalRelay.Bytes(new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, false)), Is.Empty);
    }

    // The lines of an art file as they are, whichever line ending the checkout gave it (a Windows checkout with
    // autocrlf gave the files CRLF, which the character check then counted).
    private static string[] Lines(string file) =>
        File.ReadAllLines(Path.Combine(TestEnvironment.Root, ConsoleLogo.SOURCE, file));

    #endregion
}
