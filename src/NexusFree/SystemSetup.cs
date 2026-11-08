using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace NexusFree;

/// <summary>
/// The privileged setup (<c>NexusFree.exe --setup-system --user-sid &lt;SID&gt;</c>, run elevated: by an administrator,
/// a UAC prompt, or an installed NexusFree under SYSTEM). It installs this engine as the SYSTEM dispatcher in
/// %ProgramFiles%\NexusFree with the authorized account in its <c>config.json</c>, gives the spool in
/// %ProgramData%\NexusFree its explicit ACLs, and registers the SYSTEM scheduled task "NexusFree".
/// <para>
/// One Windows account is authorized: the one given by <c>--user-sid</c> (the account that ran the installer). Only
/// that account, SYSTEM and Administrators can drop a request in the inbox, read the answers or start the task; every
/// other account on the PC gets nothing. SIDs are used throughout, so it works whatever the Windows display language.
/// </para>
/// </summary>
internal static class SystemSetup
{
    internal static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal const string Description = "NexusFree: runs the requests of the authorized account under SYSTEM";

    /// <summary>Runs the setup; returns the process exit code. Writes what it did, or why it refused, to the console.</summary>
    internal static int Run(string? userSid)
    {
        SecurityIdentifier user;
        try { user = new SecurityIdentifier(userSid ?? ""); }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"--setup-system needs --user-sid with the authorized account's SID (got \"{userSid}\").");
            return 2;
        }
        if (!user.IsAccountSid())
        {
            Console.Error.WriteLine($"{user.Value} is not a Windows account.");
            return 2;
        }
        if (!IsElevated())
        {
            Console.Error.WriteLine("--setup-system must run elevated (administrator or SYSTEM).");
            return 5;
        }
        string engine = Environment.ProcessPath ?? throw new InvalidOperationException("The engine's own path is unknown.");
        Install(Layout.Installed, user, engine);
        Console.WriteLine($"NexusFree SYSTEM task configured for {user.Value}.");
        return 0;
    }

    /// <summary>Where the SYSTEM side lives. Only <see cref="Installed"/> is reachable from the command line; the tests
    /// install into their own folders and task.</summary>
    internal sealed record Layout(string InstallDir, string SpoolDir, string TaskName)
    {
        internal static Layout Installed => new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusFree"), Engine.SpoolDir, Engine.TaskName);
    }

    /// <summary>Installs <paramref name="engine"/> as the dispatcher of <paramref name="layout"/> for <paramref name="user"/>
    /// (idempotent: an update runs the same steps).</summary>
    internal static void Install(Layout layout, SecurityIdentifier user, string engine)
    {
        string installed = Path.Combine(layout.InstallDir, "NexusFree.exe");

        // %ProgramFiles%\NexusFree: the dispatcher and the authorized account, writable by SYSTEM and Administrators
        // only (the ACL %ProgramFiles% gives it).
        Directory.CreateDirectory(layout.InstallDir);
        if (!string.Equals(Path.GetFullPath(engine), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
            Replace(engine, installed);
        File.WriteAllText(Path.Combine(layout.InstallDir, "config.json"),
            new JsonObject { ["userSid"] = user.Value }.ToJsonString(Engine.ReadableJson), new UTF8Encoding(false));

        // The ACL of the root first, then each folder's own: a request carries its rights only while it is in the inbox.
        string spool = layout.SpoolDir;
        SetSpoolAcl(spool, user, FileSystemRights.ReadAndExecute);
        SetSpoolAcl(Path.Combine(spool, "inbox"), user, FileSystemRights.Modify);
        SetSpoolAcl(Path.Combine(spool, "outbox"), user, FileSystemRights.Modify);
        SetSpoolAcl(Path.Combine(spool, "logs"), user, FileSystemRights.ReadAndExecute);
        SetSpoolAcl(Path.Combine(spool, "staging"), user, null);

        // The task runs the engine itself: no shell between Task Scheduler and the dispatcher.
        SystemTask.Register(layout.TaskName, TaskXml(installed), TaskSddl(user));

        // The PowerShell dispatcher of 1.2.5 and earlier, replaced by the engine.
        TryDelete(Path.Combine(layout.InstallDir, "Dispatch-NexusFree.ps1"));
    }

    /// <summary>True for SYSTEM and for an elevated administrator's token.</summary>
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User == System || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="target"/>. A target still running (the dispatcher this very
    /// setup was sent through, or an AI session's engine) keeps its file: Windows lets a running program be renamed, not
    /// replaced, so it is renamed aside and removed by a later run once nothing runs it.
    /// </summary>
    internal static void Replace(string source, string target)
    {
        string directory = Path.GetDirectoryName(target)!;
        string name = Path.GetFileName(target);
        foreach (string old in Directory.EnumerateFiles(directory, name + ".old-*")) TryDelete(old);
        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(target, Path.Combine(directory, $"{name}.old-{Guid.NewGuid().ToString("N")[..8]}"));
            }
        }
        File.Copy(source, target);
    }

    /// <summary>
    /// The spool folder's protected ACL (nothing inherited from %ProgramData%, which lets every user create files):
    /// owned by Administrators, full control for SYSTEM and Administrators, <paramref name="userRights"/> for the
    /// authorized account (none when null), inherited by everything created inside.
    /// </summary>
    internal static DirectorySecurity SpoolSecurity(SecurityIdentifier user, FileSystemRights? userRights)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(Administrators);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(Rule(System, FileSystemRights.FullControl));
        acl.AddAccessRule(Rule(Administrators, FileSystemRights.FullControl));
        if (userRights is { } rights) acl.AddAccessRule(Rule(user, rights));
        return acl;
    }

    private static FileSystemAccessRule Rule(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);

    private static void SetSpoolAcl(string path, SecurityIdentifier user, FileSystemRights? userRights)
    {
        var directory = Directory.CreateDirectory(path);
        directory.SetAccessControl(SpoolSecurity(user, userRights));
    }

    /// <summary>Who may see and start the task: SYSTEM and Administrators (full), the authorized account (read and
    /// run), nobody else.</summary>
    internal static string TaskSddl(SecurityIdentifier user) => $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{user.Value})";

    /// <summary>
    /// The SYSTEM task: no trigger (started on demand by the authorized account), highest run level, on battery too,
    /// 30 minutes at most per run, instances in parallel (each takes its own requests from the inbox), running
    /// <paramref name="engine"/> <c>--dispatch</c>.
    /// </summary>
    internal static string TaskXml(string engine) =>
        "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        $"<RegistrationInfo><Description>{SecurityElement.Escape(Description)}</Description></RegistrationInfo>" +
        $"<Principals><Principal id=\"Author\"><UserId>{System.Value}</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
        "<Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
        "<ExecutionTimeLimit>PT30M</ExecutionTimeLimit><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>" +
        "<IdleSettings><Duration>PT10M</Duration><WaitTimeout>PT1H</WaitTimeout><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
        "<UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings>" +
        "<Triggers />" +
        $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(engine)}</Command><Arguments>--dispatch</Arguments></Exec></Actions>" +
        "</Task>";

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
