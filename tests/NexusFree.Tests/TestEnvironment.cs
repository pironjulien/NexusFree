using System.Runtime.CompilerServices;

// The protocol and journal tests share the process environment and McpServer's static agent attribution.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace NexusFree.Tests;

internal static class TestEnvironment
{
    internal static readonly string JournalHome = Path.Combine(Path.GetTempPath(), "nexusfree-tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(JournalHome);
        Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", JournalHome);
        Environment.SetEnvironmentVariable("NEXUS_JOURNAL", "1");
        Environment.SetEnvironmentVariable("NEXUS_AGENT", null);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteOwnTemporary(JournalHome);
    }

    /// <summary>
    /// Deletes a folder a test created for itself, directly (a test's scratch never goes to the Recycle Bin). Only a
    /// direct child of the temporary folder named <c>nexusfree-*</c>, and never through a link: anything else stays.
    /// </summary>
    internal static void DeleteOwnTemporary(string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("nexusfree-", StringComparison.Ordinal)
                || !Directory.Exists(root)
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(root, recursive: true); // does not follow links inside: they are removed, not their targets
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a locked file: left for the OS
    }
}
