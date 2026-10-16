namespace WitOS.Dev.Kernel;

/// <summary>
/// Guest test suite compiled into a boot image; selects the additional markers a successful boot must report.
/// </summary>
internal enum BootSuite
{
    Kernel,
    Release,
    Foundation,

    /// <summary>
    /// An ISA's port before its kernel suite exists (plan step V1.1): the loader's and the bring-up's lines alone, since
    /// the banner and the markers come from the kernel's entry, which the port does not reach yet.
    /// </summary>
    Loader
}
