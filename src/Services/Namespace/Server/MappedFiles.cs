using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.Namespaces;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// The files clients map (plan step R4.2): one record per file of a volume, by its mount point and path, shared by every
/// session, so that every shared mapping of the same chunk of the same file shows the same memory object.
/// </summary>
internal sealed class MappedFiles(IChunkSource source)
{
    #region Fields

    private readonly Dictionary<string, MappedFile> m_files = [];

    #endregion

    #region Functions

    /// <summary>
    /// The record of a file some handle mapped, or null.
    /// </summary>
    /// <param name="mount">The file's mount.</param>
    /// <param name="path">Its path in the volume.</param>
    /// <returns>The record.</returns>
    public MappedFile? Find(MountTable.Mount mount, string path)
    {
        lock (m_files)
            return m_files.GetValueOrDefault(Key(mount, path));
    }

    /// <summary>
    /// The record of a file a handle maps, made at the first; the handle counts as a mapper until it leaves.
    /// </summary>
    /// <param name="mount">The file's mount.</param>
    /// <param name="path">Its path in the volume.</param>
    /// <returns>The record.</returns>
    public MappedFile Join(MountTable.Mount mount, string path)
    {
        lock (m_files)
        {
            if (!m_files.TryGetValue(Key(mount, path), out var file))
                m_files[Key(mount, path)] = file = new MappedFile(this);
            file.Mappers++;
            return file;
        }
    }

    /// <summary>
    /// A handle that mapped the file closed: with the last, the record and its chunks go.
    /// </summary>
    /// <param name="file">The record.</param>
    public void Leave(MappedFile file)
    {
        lock (m_files)
        {
            if (--file.Mappers > 0)
                return;
            foreach (var (key, value) in m_files.Where(pair => pair.Value == file).ToArray())
                m_files.Remove(key);
        }
        file.Dispose();
    }

    /// <summary>
    /// A new chunk of zeros.
    /// </summary>
    /// <returns>The chunk.</returns>
    public ISharedChunk Create() => source.Create(NamespaceMessages.CHUNK_BYTES);

    private static string Key(MountTable.Mount mount, string path) => mount.Point + path;

    #endregion
}

/// <summary>
/// A file clients map (plan step R4.2): its shared chunks by index, the handles that write it back, and a gate that
/// serializes the service's own use of the chunks.
/// </summary>
internal sealed class MappedFile : IDisposable
{
    #region Constants

    private const int BLOCK = 4096;

    private const int CHUNK = NamespaceMessages.CHUNK_BYTES;

    #endregion

    #region Constructors

    internal MappedFile(MappedFiles owner)
    {
        Owner = owner;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Copies the chunks' bytes over what a read of the file gave, where a chunk covers them: a mapping may have
    /// written them since the file did.
    /// </summary>
    /// <param name="offset">The read's offset in the file.</param>
    /// <param name="buffer">What the read gave.</param>
    public void Overlay(long offset, Span<byte> buffer)
    {
        foreach (var (index, chunk) in Chunks)
        {
            if (Overlap(index, offset, buffer.Length) is var (from, to) && from < to)
                chunk.Bytes.Slice((int)(from - index * CHUNK), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
        }
    }

    /// <summary>
    /// Copies what a write gave the file into the chunks that cover it, so that every mapping shows it.
    /// </summary>
    /// <param name="offset">The write's offset in the file.</param>
    /// <param name="bytes">The bytes written.</param>
    public void Update(long offset, ReadOnlySpan<byte> bytes)
    {
        foreach (var (index, chunk) in Chunks)
        {
            if (Overlap(index, offset, bytes.Length) is var (from, to) && from < to)
                bytes.Slice((int)(from - offset), (int)(to - from)).CopyTo(chunk.Bytes[(int)(from - index * CHUNK)..]);
        }
    }

    /// <summary>
    /// Zeroes the chunks' bytes past a new end of the file, as a mapping shows a file cut shorter.
    /// </summary>
    /// <param name="length">The file's new length.</param>
    public void Cut(long length)
    {
        foreach (var (index, chunk) in Chunks)
        {
            var start = index * CHUNK;
            if (length < start + CHUNK)
                chunk.Bytes[(int)Math.Max(0, length - start)..].Clear();
        }
    }

    /// <summary>
    /// Writes the chunks' changed blocks back to the file through a handle that maps it writable, up to the file's end;
    /// nothing when no handle does, since no mapping could change them then.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write-back.</param>
    /// <returns>When the blocks are written.</returns>
    public async Task WriteBackAsync(CancellationToken cancellationToken)
    {
        if (Writers.Count == 0)
            return;
        var writer = Writers[0];
        var length = writer.Length;
        var block = new byte[BLOCK];
        foreach (var index in Chunks.Keys.Order().ToArray())
        {
            var start = index * CHUNK;
            for (var at = start; at < Math.Min(start + CHUNK, length); at += BLOCK)
            {
                var bytes = (int)Math.Min(BLOCK, length - at);
                var read = await writer.ReadAsync(at, block.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                if (Changed(index, at, block, read, bytes) is { } changed)
                    await writer.WriteAsync(at, changed, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A chunk's block where it differs from the file's, or null.
    private byte[]? Changed(long index, long at, byte[] file, int read, int bytes)
    {
        var mine = Chunks[index].Bytes.Slice((int)(at - index * CHUNK), bytes);
        return read == bytes && mine.SequenceEqual(file.AsSpan(0, bytes)) ? null : mine.ToArray();
    }

    // The part of [offset, offset + length) a chunk covers.
    private static (long From, long To)? Overlap(long index, long offset, int length)
    {
        var start = index * CHUNK;
        return (Math.Max(start, offset), Math.Min(start + CHUNK, offset + length));
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var chunk in Chunks.Values)
            chunk.Dispose();
        Chunks.Clear();
        Gate.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The registry the record belongs to.
    /// </summary>
    public MappedFiles Owner { get; }

    /// <summary>
    /// The chunks clients mapped, by index from the file's start.
    /// </summary>
    public Dictionary<long, ISharedChunk> Chunks { get; } = [];

    /// <summary>
    /// The handles that map the file writable, through which it is written back.
    /// </summary>
    public List<IFileHandle> Writers { get; } = [];

    /// <summary>
    /// Serializes the service's reads, writes and write-backs of the chunks across sessions.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// The open handles that mapped the file.
    /// </summary>
    internal int Mappers { get; set; }

    #endregion
}
