using System.Collections.Generic;

namespace OutWit.OS.FileSystems;

/// <summary>
/// What a check of a volume found (storage document §72–73).
/// </summary>
/// <param name="Problems">Each problem, for a person; none for a clean volume.</param>
/// <param name="Files">The files the check walked.</param>
/// <param name="Directories">The directories the check walked.</param>
public sealed record FileSystemCheckReport(IReadOnlyList<string> Problems, int Files, int Directories)
{
    #region Properties

    /// <summary>
    /// Whether the check found nothing wrong.
    /// </summary>
    public bool IsClean => Problems.Count == 0;

    #endregion
}
