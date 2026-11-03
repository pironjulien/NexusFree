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
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var root = Path.GetFullPath(JournalHome);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("nexusfree-tests-", StringComparison.Ordinal)) return;
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (!Path.GetExtension(entry).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
                    }
                }
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(root,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            }
            catch (Exception) { } // Keep the owned fixture when recycling is unavailable; never delete as a fallback.
        };
    }
}
