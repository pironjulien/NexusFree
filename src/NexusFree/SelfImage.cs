using System.Diagnostics;
using System.Reflection;

namespace NexusFree;

/// <summary>
/// Keeps a running engine whole when an update replaces its file. A self-contained single-file .NET program loads each
/// assembly from its own executable the first time it is needed, by opening that file again by its path. An update
/// renames the running engine aside (Windows allows it) and copies the new version to the same path: a session opened
/// before the update then read the new version's bytes at the old offsets, and every tool call that needed an assembly
/// not loaded yet failed with an empty error. So the long-running modes (the MCP server, the SYSTEM dispatcher) load
/// every assembly they reference as soon as they start, while the file is still theirs; the file's identity is noted at
/// the same time, so a failure after a replacement says what happened and what to do.
/// </summary>
internal static class SelfImage
{
    private static (long Length, DateTime Written)? started;

    /// <summary>Loads the engine's whole reference graph and notes the executable's identity. Returns how many
    /// assemblies it loaded.</summary>
    internal static int Pin()
    {
        started = Identity(Environment.ProcessPath);
        return LoadReferences(typeof(SelfImage).Assembly);
    }

    /// <summary>Loads every assembly reachable from <paramref name="root"/> through its references. An optional
    /// reference absent from this runtime is skipped: the code that needs it would fail the same way later.</summary>
    internal static int LoadReferences(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) seen.Add(loaded.GetName().Name ?? "");
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);
        int count = 0;
        while (pending.Count > 0)
        {
            foreach (AssemblyName reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (!seen.Add(reference.Name ?? "")) continue;
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                    count++;
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { }
            }
        }
        return count;
    }

    /// <summary>The version now at the engine's path when the file this process started from was replaced since
    /// <see cref="Pin"/> (an update while this session was open), else null.</summary>
    internal static string? ReplacedBy()
    {
        if (started is not { } before) return null;
        string? path = Environment.ProcessPath;
        var now = Identity(path);
        if (now is null || now == before) return null;
        try { return (FileVersionInfo.GetVersionInfo(path!).ProductVersion ?? "").Split('+')[0]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static (long, DateTime)? Identity(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
