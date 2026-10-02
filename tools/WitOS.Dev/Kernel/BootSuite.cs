namespace WitOS.Dev.Kernel;

/// <summary>
/// Guest test suite compiled into a boot image; selects the additional markers a successful boot must report.
/// </summary>
internal enum BootSuite
{
    Kernel,
    RuntimeConfig,
    RuntimeBoot,
    CoreClrMemory,
    CoreClrStorage
}
