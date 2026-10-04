using System.Security.Cryptography;
using System.Text.Json;

namespace WitOS.Dev.Pe;

/// <summary>
/// Source identity is the key; link groups never depend on positional indices.
/// </summary>
internal sealed class NativePlatformObjects
{
    #region Fields

    internal static readonly string[] SOURCES = [
        "security_cookie.witos.cpp",
        "security_handler.witos.cpp",
        "security_cookie.asm",
        "native_random.witos.cpp",
        "native_random.asm",
        "native_memory.witos.cpp",
        "native_memory.asm",
        "native_services.witos.cpp",
        "native_services.asm",
        "pal_events.witos.cpp",
        "native_thread_handles.witos.cpp",
        "native_thread_handles.asm",
        "native_wait.witos.cpp",
        "native_wait.asm",
        "native_console.witos.cpp",
        "native_processor.witos.cpp",
        "native_console.asm",
        "native_processor.asm",
        "native_encoding.witos.cpp",
        "native_encoding.asm",
        "native_module.witos.cpp",
        "native_module.asm",
        "pal_thread_name.witos.cpp",
        "native_new.witos.cpp",
        "native_diagnostics.witos.cpp",
        "native_diagnostics.asm",
        "native_com.witos.cpp",
        "native_com.asm",
        "gc_policy.witos.cpp",
        "gc_policy.asm",
        "pal_context_storage.witos.cpp",
        "native_suspend.witos.cpp",
        "native_suspend.asm",
        "native_thread_create.witos.cpp",
        "native_thread_create.asm",
    ];

    private readonly Dictionary<string, string> m_files;

    #endregion

    #region Constructors

    internal NativePlatformObjects(IEnumerable<KeyValuePair<string, string>> objects)
    {
        m_files = objects.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!m_files.Keys.Order().SequenceEqual(SOURCES.Order()))
            throw new InvalidDataException("Native platform source manifest differs from the required set.");
    }

    #endregion

    #region Functions

    /// <summary>
    /// Selects objects by source name.
    /// </summary>
    /// <param name="names">Source names.</param>
    /// <returns>Object paths.</returns>
    internal string[] Select(params string[] names) => names.Select(name => m_files.TryGetValue(name, out var file)
        ? file : throw new InvalidDataException("Native platform object missing: " + name)).ToArray();

    /// <summary>
    /// Copies every object into a directory.
    /// </summary>
    /// <param name="directory">Destination directory.</param>
    /// <returns>Manifest entries of the copies.</returns>
    internal object[] CopyTo(string directory)
    {
        return SOURCES.Select(name =>
        {
            var destination = Path.Combine(directory, name + ".obj");
            File.Copy(m_files[name], destination, overwrite: true);
            return (object)new { source = name, file = Path.GetFileName(destination), sha256 = Hash(destination) };
        }).ToArray();
    }

    /// <summary>
    /// Reads and hash-checks the objects listed in a manifest.
    /// </summary>
    /// <param name="directory">Object directory.</param>
    /// <param name="manifest">Manifest entries.</param>
    /// <returns>The verified objects.</returns>
    internal static NativePlatformObjects Read(string directory, JsonElement manifest)
    {
        var items = manifest.EnumerateArray().Select(item =>
        {
            var name = item.GetProperty("source").GetString()!;
            var file = item.GetProperty("file").GetString()!;
            if (file != name + ".obj" || Path.GetFileName(file) != file)
                throw new InvalidDataException("Invalid native platform object filename.");
            var full = Path.Combine(directory, file);
            if (Hash(full) != item.GetProperty("sha256").GetString())
                throw new InvalidDataException("Native platform object hash changed: " + name);
            return KeyValuePair.Create(name, full);
        });
        return new(items);
    }

    #endregion

    #region Tools

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    #endregion

    #region Properties

    /// <summary>
    /// Every object, in source order.
    /// </summary>
    internal string[] All => SOURCES.Select(name => m_files[name]).ToArray();

    /// <summary>
    /// Objects linked into the CPU probe image.
    /// </summary>
    internal string[] Cpu => Select(
        "security_cookie.witos.cpp",
        "security_handler.witos.cpp",
        "security_cookie.asm",
        "native_random.witos.cpp",
        "native_random.asm",
        "native_memory.witos.cpp",
        "native_memory.asm",
        "native_services.witos.cpp",
        "native_services.asm",
        "pal_events.witos.cpp",
        "native_thread_handles.witos.cpp",
        "native_thread_handles.asm",
        "native_wait.witos.cpp",
        "native_wait.asm",
        "native_console.witos.cpp",
        "native_processor.witos.cpp",
        "native_console.asm",
        "native_processor.asm");

    /// <summary>
    /// Objects linked into the thread probe images.
    /// </summary>
    internal string[] Thread => Select(
        "security_cookie.witos.cpp",
        "security_handler.witos.cpp",
        "security_cookie.asm",
        "native_random.witos.cpp",
        "native_random.asm",
        "native_memory.witos.cpp",
        "native_memory.asm",
        "native_services.witos.cpp",
        "native_services.asm",
        "pal_events.witos.cpp",
        "native_thread_handles.witos.cpp",
        "native_thread_handles.asm",
        "native_wait.witos.cpp",
        "native_wait.asm",
        "native_console.witos.cpp",
        "native_processor.witos.cpp",
        "native_console.asm",
        "native_processor.asm",
        "native_encoding.witos.cpp",
        "native_encoding.asm",
        "native_module.witos.cpp",
        "native_module.asm",
        "pal_thread_name.witos.cpp",
        "native_new.witos.cpp",
        "native_diagnostics.witos.cpp",
        "native_diagnostics.asm",
        "native_suspend.witos.cpp",
        "native_suspend.asm",
        "native_thread_create.witos.cpp",
        "native_thread_create.asm");

    /// <summary>
    /// Service, event and thread-handle bindings linked into the runtime-config probe image.
    /// </summary>
    internal string[] Record => Select(
        "native_services.witos.cpp",
        "native_services.asm",
        "pal_events.witos.cpp",
        "native_thread_handles.witos.cpp",
        "native_thread_handles.asm");

    /// <summary>
    /// Every object except the context storage, for the COM probe.
    /// </summary>
    internal string[] Com => SOURCES.Where(name => name != "pal_context_storage.witos.cpp").Select(name => m_files[name]).ToArray();

    #endregion
}
