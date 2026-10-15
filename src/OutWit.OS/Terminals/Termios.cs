namespace OutWit.OS.Terminals;

/// <summary>
/// The termios flags and control characters the terminal's attributes carry (witos/terminal.h, plan step N4.3):
/// Linux's generic values, which both ISAs share and the libc copies into a termios as they are.
/// </summary>
public static class Termios
{
    #region Constants

    // c_iflag.
    public const uint ISTRIP = 0x20;
    public const uint INLCR = 0x40;
    public const uint IGNCR = 0x80;
    public const uint ICRNL = 0x100;
    public const uint IXON = 0x400;
    public const uint IXANY = 0x800;
    public const uint IXOFF = 0x1000;
    public const uint IUTF8 = 0x4000;

    // c_oflag.
    public const uint OPOST = 0x1;
    public const uint ONLCR = 0x4;
    public const uint OCRNL = 0x8;
    public const uint ONLRET = 0x20;

    // c_cflag.
    public const uint CS8 = 0x30;
    public const uint CREAD = 0x80;
    public const uint HUPCL = 0x400;
    public const uint B38400 = 0xF;

    // c_lflag.
    public const uint ISIG = 0x1;
    public const uint ICANON = 0x2;
    public const uint ECHO = 0x8;
    public const uint ECHOE = 0x10;
    public const uint ECHOK = 0x20;
    public const uint ECHONL = 0x40;
    public const uint NOFLSH = 0x80;
    public const uint ECHOCTL = 0x200;
    public const uint ECHOKE = 0x800;
    public const uint IEXTEN = 0x8000;

    // c_cc.
    public const int VINTR = 0;
    public const int VQUIT = 1;
    public const int VERASE = 2;
    public const int VKILL = 3;
    public const int VEOF = 4;
    public const int VTIME = 5;
    public const int VMIN = 6;
    public const int VSTART = 8;
    public const int VSTOP = 9;
    public const int VSUSP = 10;
    public const int VEOL = 11;
    public const int VREPRINT = 12;
    public const int VDISCARD = 13;
    public const int VWERASE = 14;
    public const int VLNEXT = 15;
    public const int VEOL2 = 16;

    #endregion
}
