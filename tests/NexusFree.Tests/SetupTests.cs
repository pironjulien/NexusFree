using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace NexusFree.Tests;

/// <summary>
/// The installation in the engine (<c>--install</c>, <c>--setup-system</c>), which replaced install.ps1's logic and
/// daemon\setup-system.ps1 in 1.4.0: the SYSTEM task's definition and rights, the spool ACLs, the command sent through an
/// installed NexusFree, Antigravity's plugin. The real registration runs only elevated (CI runners), into its own
/// folders and task, never the installed NexusFree.
/// </summary>
public sealed class SetupTests : IDisposable
{
    private const string Account = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private static readonly SecurityIdentifier User = new(Account);

    private readonly string root = Directory.CreateTempSubdirectory("nexusfree-setup-").FullName;

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(root);

    [Fact]
    public void Task_runs_the_installed_engine_under_SYSTEM_on_demand_in_parallel()
    {
        string xml = SystemSetup.TaskXml(@"C:\Program Files\A&B\NexusFree.exe");
        Assert.Contains(@"<Command>C:\Program Files\A&amp;B\NexusFree.exe</Command><Arguments>--dispatch</Arguments>", xml, StringComparison.Ordinal);
        Assert.Contains("<UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<Triggers />", xml, StringComparison.Ordinal);
        Assert.Contains("<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT30M</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_SYSTEM_Administrators_and_the_authorized_account_may_see_and_start_the_task() =>
        Assert.Equal($"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{Account})", SystemSetup.TaskSddl(User));

    [Theory]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(null)]
    public void Spool_folders_are_protected_and_owned_by_Administrators(FileSystemRights? userRights)
    {
        DirectorySecurity acl = SystemSetup.SpoolSecurity(User, userRights);
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(SystemSetup.Administrators, acl.GetOwner(typeof(SecurityIdentifier)));
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .ToDictionary(r => (SecurityIdentifier)r.IdentityReference, r => r);
        Assert.All(rules.Values, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.All(rules.Values, r => Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags));
        Assert.Equal(FileSystemRights.FullControl, rules[SystemSetup.System].FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, rules[SystemSetup.Administrators].FileSystemRights);
        if (userRights is null) Assert.Equal(2, rules.Count);
        else Assert.Equal(userRights | FileSystemRights.Synchronize, rules[User].FileSystemRights | FileSystemRights.Synchronize);
    }

    [Fact]
    public void Setup_sent_through_NexusFree_quotes_the_engine_and_passes_its_exit_code()
    {
        Assert.Equal($"& 'C:\\Users\\O''Brien\\.nexus\\nexus-free\\NexusFree.exe' --setup-system --user-sid {Account}; exit $LASTEXITCODE",
            Installer.SetupCommand(@"C:\Users\O'Brien\.nexus\nexus-free\NexusFree.exe", Account));
        Assert.DoesNotContain('"', Installer.SetupCommand(@"C:\x\NexusFree.exe", Account));
        Assert.Throws<ArgumentException>(() => Installer.SetupCommand(@"C:\x\NexusFree.exe", "S-1-5-18; Remove-Item C:\\"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-18")] // SYSTEM is not an account to authorize
    public void Setup_refuses_anything_but_an_account_SID_before_touching_anything(string? sid) =>
        Assert.Equal(2, SystemSetup.Run(sid));

    [Fact]
    public void Setup_refuses_to_run_without_elevation()
    {
        Assert.SkipWhen(SystemSetup.IsElevated(), "Elevated: the refusal cannot be observed (and Run would install for real).");
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(5, SystemSetup.Run(identity.User!.Value));
    }

    [Fact]
    public void Dispatcher_end_line_is_found_in_the_log_tail()
    {
        string log = Path.Join(root, "elevation.log");
        File.WriteAllText(log, new string('x', 100_000) + "\n[2026-11-08 12:00:00] [INFO] Request req-abc finished (ExitCode: 0)\n", Encoding.UTF8);
        Assert.True(Installer.LogEnded(log, "req-abc"));
        Assert.False(Installer.LogEnded(log, "req-ab"));
        Assert.False(Installer.LogEnded(Path.Join(root, "missing.log"), "req-abc"));
    }

    [Theory]
    [InlineData("en-US", "Direct route")]
    [InlineData("fr-FR", "Voie directe")]
    public void Antigravity_plugin_is_written_from_the_engine_in_the_display_language(string culture, string guide)
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            string engine = @"C:\Users\x\.nexus\nexus-free\NexusFree.exe";
            string plugin = Installer.WriteAntigravityPlugin(engine, root);
            Assert.Equal(Path.Join(root, ".gemini", "config", "plugins", "nexus-free"), plugin);
            string repo = GetScriptTests.Repository();
            Assert.Equal(File.ReadAllBytes(Path.Join(repo, "plugin", "plugin.json")), File.ReadAllBytes(Path.Join(plugin, "plugin.json")));
            Assert.Equal(File.ReadAllBytes(Path.Join(repo, "plugin", "logo.png")), File.ReadAllBytes(Path.Join(plugin, "logo.png")));
            string skill = File.ReadAllText(Path.Join(plugin, "skills", "nexus-free", "SKILL.md"));
            Assert.Contains(guide, skill, StringComparison.Ordinal);
            var mcp = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(Path.Join(plugin, "mcp_config.json"))));
            Assert.Equal(engine, mcp["mcpServers"]?["nexus-free"]?["command"]?.GetValue<string>());
            Assert.Equal("--mcp", mcp["mcpServers"]?["nexus-free"]?["args"]?[0]?.GetValue<string>());
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    [Fact]
    public void Replace_overwrites_the_target_and_removes_older_copies_set_aside()
    {
        string source = Path.Join(root, "new.exe");
        string target = Path.Join(root, "NexusFree.exe");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        File.WriteAllText(target + ".old-12345678", "older");
        SystemSetup.Replace(source, target);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(source));
        Assert.False(File.Exists(target + ".old-12345678"));
    }

    /// <summary>The whole privileged setup, for real, into this test's own folders and task (elevated runs only).</summary>
    [Fact]
    public void Setup_registers_the_task_and_protects_the_spool()
    {
        Assert.SkipUnless(SystemSetup.IsElevated(), "Needs an elevated run (the CI runners are).");
        string task = "NexusFree-Test-" + Guid.NewGuid().ToString("N")[..8];
        var layout = new SystemSetup.Layout(Path.Join(root, "install"), Path.Join(root, "spool"), task);
        string engine = Path.Join(root, "engine.exe");
        File.WriteAllText(engine, "engine");
        // A stand-in account: elevated runs may be SYSTEM itself, whose own rules would hide the account's.
        SecurityIdentifier user = User;
        try
        {
            SystemSetup.Install(layout, user, engine);
            SystemSetup.Install(layout, user, engine); // an update runs the same steps

            Assert.Equal("engine", File.ReadAllText(Path.Join(layout.InstallDir, "NexusFree.exe")));
            Assert.Equal(user.Value, Dispatcher.ReadAuthorizedSid(Path.Join(layout.InstallDir, "config.json")));

            foreach (var (folder, rights) in new (string, FileSystemRights?)[]
            {
                ("", FileSystemRights.ReadAndExecute), ("inbox", FileSystemRights.Modify), ("outbox", FileSystemRights.Modify),
                ("logs", FileSystemRights.ReadAndExecute), ("staging", null),
            })
            {
                var acl = new DirectoryInfo(Path.Join(layout.SpoolDir, folder)).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected, folder);
                Assert.Equal(SystemSetup.Administrators, acl.GetOwner(typeof(SecurityIdentifier)));
                var explicitRules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
                var userRules = explicitRules.Where(r => user.Equals(r.IdentityReference)).ToList();
                if (rights is null) Assert.Empty(userRules);
                else Assert.Equal(rights | FileSystemRights.Synchronize, Assert.Single(userRules).FileSystemRights | FileSystemRights.Synchronize);
                Assert.Equal(rights is null ? 2 : 3, explicitRules.Count);
            }

            string xml = SystemTask.Xml(task)!;
            Assert.Contains($"<Command>{Path.Join(layout.InstallDir, "NexusFree.exe")}</Command>", xml, StringComparison.Ordinal);
            Assert.Contains("<Arguments>--dispatch</Arguments>", xml, StringComparison.Ordinal);
            Assert.Contains("<UserId>S-1-5-18</UserId>", xml, StringComparison.Ordinal);
            Assert.Contains("<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>", xml, StringComparison.Ordinal);
            Assert.True(SystemTask.Query(task).Found);

            // Allow entries only, merged by account (Task Scheduler adds its own read entry for SYSTEM).
            var aces = new RawSecurityDescriptor(SystemTask.SecurityDescriptor(task)!).DiscretionaryAcl!.Cast<CommonAce>().ToList();
            Assert.All(aces, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
            var dacl = aces.GroupBy(a => a.SecurityIdentifier).ToDictionary(g => g.Key, g => g.Aggregate(0, (mask, a) => mask | a.AccessMask));
            Assert.Equal(3, dacl.Count);
            Assert.Equal(0x1F01FF, dacl[SystemSetup.System]);        // FILE_ALL_ACCESS
            Assert.Equal(0x1F01FF, dacl[SystemSetup.Administrators]);
            Assert.Equal(0x1200A9, dacl[user]);                       // FILE_GENERIC_READ | FILE_GENERIC_EXECUTE
        }
        finally
        {
            SystemTask.Delete(task);
        }
        Assert.False(SystemTask.Query(task).Found);
    }
}
