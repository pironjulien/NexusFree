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
            try { Directory.Delete(JournalHome, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}
