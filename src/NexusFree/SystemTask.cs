using System.Runtime.InteropServices;

namespace NexusFree;

/// <summary>
/// The SYSTEM task through the Task Scheduler API (COM <c>Schedule.Service</c>, the interface schtasks.exe itself
/// uses) rather than a schtasks.exe process and its localized text: the task's state comes back as a number, and
/// starting it costs no child process.
/// </summary>
internal static class SystemTask
{
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    internal sealed record Info(bool Found, int State, DateTime? LastRun, int? LastResult);

    /// <summary>Reads the task's state, or <c>Found = false</c> when it is not registered.</summary>
    internal static Info Query(string name)
    {
        object? service = null, folder = null, task = null;
        try
        {
            if (!TryGetTask(name, out service, out folder, out task)) return new Info(false, 0, null, null);
            dynamic t = task!;
            int state = (int)t.State;
            DateTime lastRun = (DateTime)t.LastRunTime;
            int lastResult = (int)t.LastTaskResult;
            // A task that never ran reports 1899-12-30 (OLE zero date).
            return new Info(true, state, lastRun.Year < 2000 ? null : lastRun, lastRun.Year < 2000 ? null : lastResult);
        }
        finally { Release(task, folder, service); }
    }

    /// <summary>Starts the task once (its own definition decides parallel instances).</summary>
    internal static void Run(string name)
    {
        object? service = null, folder = null, task = null, running = null;
        try
        {
            if (!TryGetTask(name, out service, out folder, out task))
                throw new InvalidOperationException($"Task {name} is not registered.");
            dynamic t = task!;
            running = t.Run(null);
        }
        finally { Release(running, task, folder, service); }
    }

    private static bool TryGetTask(string name, out object? service, out object? folder, out object? task)
    {
        service = folder = task = null;
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        service = Activator.CreateInstance(type)!;
        dynamic s = service;
        s.Connect();
        folder = s.GetFolder("\\");
        dynamic f = folder!;
        try
        {
            task = f.GetTask(name);
            return task is not null;
        }
        // Late binding raises a missing task as FileNotFoundException (or COMException), both with this HRESULT.
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound && ex is COMException or IOException)
        {
            return false;
        }
    }

    private static void Release(params object?[] objects)
    {
        foreach (object? o in objects)
        {
            if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
    }
}
