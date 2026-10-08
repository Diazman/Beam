namespace Beam.Core.Transfer;

/// <summary>
/// Settings for folders with many small files (source code, photos, documents), where the cost per
/// file — opening, closing, antivirus scans, network round trips — outweighs the data itself.
/// </summary>
internal static class TransferTuning
{
    /// <summary>Files up to this size are read whole by the sender and kept in memory by the receiver.</summary>
    public const int SmallFileLimit = 1024 * 1024;

    /// <summary>How many small files the sender reads ahead, in parallel, while earlier ones are sent.</summary>
    public const int ReadAhead = 32;

    /// <summary>How many received small files are written to disk at the same time.</summary>
    public const int ParallelWrites = 8;
}
