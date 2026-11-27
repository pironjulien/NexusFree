using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusFree;

/// <summary>
/// The process tree of one command, held in a Windows job object so that a stop ends all of it: the shell, what it
/// started, and what those started, even once a parent in between has exited (a walk by parent id would miss them).
/// The job carries no limit and is not "kill on close": a command that leaves a background process behind on purpose
/// keeps it when it ends by itself, as before; only <see cref="Stop"/> ends the tree.
/// </summary>
internal sealed partial class ProcessJob : IDisposable
{
    private readonly Process _process;
    private nint _job;

    internal ProcessJob(Process process)
    {
        _process = process;
        nint job = CreateJobObjectW(0, 0);
        try
        {
            if (job != 0 && AssignProcessToJobObject(job, process.Handle)) _job = job;
        }
        catch (InvalidOperationException) { } // already exited: nothing left to hold
        if (_job == 0 && job != 0) CloseHandle(job);
    }

    /// <summary>Ends every process of the tree. Without a job (it could not be created or joined), the processes
    /// still reachable from the shell by parent id. True when the shell itself was ended or had already exited.</summary>
    internal bool Stop()
    {
        if (_job != 0 && TerminateJobObject(_job, (uint)Engine.CancelledExitCode)) return true;
        try
        {
            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            return _process.HasExited;
        }
    }

    public void Dispose()
    {
        if (_job != 0) CloseHandle(_job);
        _job = 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint securityAttributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
