using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusFree;

/// <summary>
/// The SYSTEM task through the Task Scheduler 2.0 API (taskschd.dll, the interface schtasks.exe itself uses) with
/// source-generated COM interop, compiled ahead of time with the engine: no schtasks.exe process and its localized
/// text, no PowerShell cmdlet, no runtime COM binder. The task's state comes back as a number, and starting it costs no
/// child process. Every COM object is released as soon as the call ends.
/// </summary>
internal static partial class SystemTask
{
    private static readonly Guid TaskSchedulerClass = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    private static readonly Guid TaskServiceInterface = new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    internal const int FileNotFound = unchecked((int)0x80070002);
    internal const int PathNotFound = unchecked((int)0x80070003);
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonServiceAccount = 5;

    [ThreadStatic] private static bool comReady;

    internal sealed record Info(bool Found, int State, DateTime? LastRun, int? LastResult);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    /// <summary>Reads the task's state, or <c>Found = false</c> when it is not registered.</summary>
    internal static Info Query(string name)
    {
        using var scope = new Scope();
        ITaskFolder root = scope.Root();
        IRegisteredTask? task = scope.Task(root, name);
        if (task is null) return new Info(false, 0, null, null);
        int state = task.GetState();
        // A task that never ran reports the OLE zero date (1899-12-30).
        DateTime lastRun = DateTime.FromOADate(task.GetLastRunTime());
        if (lastRun.Year < 2000) return new Info(true, state, null, null);
        return new Info(true, state, lastRun, task.GetLastTaskResult());
    }

    /// <summary>Starts the task once (its own definition decides parallel instances).</summary>
    internal static void Run(string name)
    {
        using var scope = new Scope();
        IRegisteredTask task = scope.Task(scope.Root(), name) ?? throw new InvalidOperationException($"Task {name} is not registered.");
        nint running = task.Run(default);
        if (running != 0) Marshal.Release(running);
    }

    /// <summary>
    /// Creates or updates the task from its XML definition, as the service account its principal names, then sets who
    /// may read and start it (<paramref name="sddl"/>). Never stopped first: an update sent through NexusFree runs
    /// inside this very task.
    /// </summary>
    internal static void Register(string name, string xml, string sddl)
    {
        using var scope = new Scope();
        IRegisteredTask task = scope.Track(scope.Root().RegisterTask(name, xml, TaskCreateOrUpdate, default, default, TaskLogonServiceAccount, default));
        task.SetSecurityDescriptor(sddl, 0);
    }

    /// <summary>The task's security descriptor (SDDL, DACL only), or null when it is not registered.</summary>
    internal static string? SecurityDescriptor(string name)
    {
        using var scope = new Scope();
        IRegisteredTask? task = scope.Task(scope.Root(), name);
        return task?.GetSecurityDescriptor(4 /* DACL_SECURITY_INFORMATION */);
    }

    /// <summary>The task's registered XML definition, or null when it is not registered.</summary>
    internal static string? Xml(string name)
    {
        using var scope = new Scope();
        return scope.Task(scope.Root(), name)?.GetXml();
    }

    /// <summary>Deletes the task; false when it was not registered.</summary>
    internal static bool Delete(string name)
    {
        using var scope = new Scope();
        int hr = scope.Root().DeleteTask(name, 0);
        if (hr is FileNotFound or PathNotFound) return false;
        Marshal.ThrowExceptionForHR(hr);
        return true;
    }

    /// <summary>The COM objects of one call, all released when it ends (not left to the garbage collector).</summary>
    private sealed class Scope : IDisposable
    {
        private readonly List<object> objects = [];

        internal T Track<T>(T value) where T : class
        {
            objects.Add(value);
            return value;
        }

        internal ITaskFolder Root()
        {
            if (!comReady)
            {
                // The calling thread joins the multithreaded apartment (a thread already initialized keeps its own).
                _ = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
                comReady = true;
            }
            Marshal.ThrowExceptionForHR(CoCreateInstance(TaskSchedulerClass, 0, 1 /* CLSCTX_INPROC_SERVER */, TaskServiceInterface, out nint pointer));
            try
            {
                var service = Track((ITaskService)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance));
                service.Connect(default, default, default, default);
                return Track(service.GetFolder(@"\"));
            }
            finally { Marshal.Release(pointer); }
        }

        internal IRegisteredTask? Task(ITaskFolder root, string name)
        {
            int hr = root.GetTask(name, out IRegisteredTask? task);
            if (task is not null) Track(task);
            if (hr is FileNotFound or PathNotFound) return null;
            Marshal.ThrowExceptionForHR(hr);
            return task;
        }

        public void Dispose()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                if (objects[i] is ComObject com) com.FinalRelease();
            }
            objects.Clear();
        }
    }
}

/// <summary>
/// A VARIANT passed by value, always VT_EMPTY here (the defaults of Connect, RegisterTask and Run): 24 bytes on 64-bit
/// Windows. Its marshaller passes it as is, so the source generator handles it without disabling runtime marshalling.
/// </summary>
[NativeMarshalling(typeof(EmptyVariantMarshaller))]
[StructLayout(LayoutKind.Sequential)]
internal readonly struct EmptyVariant
{
    private readonly ushort type;
    private readonly ushort reserved1, reserved2, reserved3;
    private readonly nint value1, value2;
}

[CustomMarshaller(typeof(EmptyVariant), MarshalMode.Default, typeof(EmptyVariantMarshaller))]
internal static class EmptyVariantMarshaller
{
    public static EmptyVariant ConvertToUnmanaged(EmptyVariant managed) => managed;

    public static EmptyVariant ConvertToManaged(EmptyVariant unmanaged) => unmanaged;
}

// The interfaces of taskschd.h used here, in their vtable order (Windows SDK 10.0.26100). They derive from IDispatch,
// whose four methods are declared as placeholders (never called) so that the slots after them line up.

[GeneratedComInterface]
[Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
internal partial interface ITaskService
{
    void DispatchGetTypeInfoCount();
    void DispatchGetTypeInfo();
    void DispatchGetIDsOfNames();
    void DispatchInvoke();
    ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
    void GetRunningTasks();
    void NewTask();
    void Connect(EmptyVariant serverName, EmptyVariant user, EmptyVariant domain, EmptyVariant password);
}

[GeneratedComInterface]
[Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
internal partial interface ITaskFolder
{
    void DispatchGetTypeInfoCount();
    void DispatchGetTypeInfo();
    void DispatchGetIDsOfNames();
    void DispatchInvoke();
    void GetName();
    void GetPath();
    void GetSubFolder();
    void GetFolders();
    void CreateFolder();
    void DeleteFolder();
    [PreserveSig]
    int GetTask([MarshalAs(UnmanagedType.BStr)] string path, out IRegisteredTask? task);
    void GetTasks();
    [PreserveSig]
    int DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    IRegisteredTask RegisterTask([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xml, int flags,
        EmptyVariant userId, EmptyVariant password, int logonType, EmptyVariant sddl);
}

[GeneratedComInterface]
[Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
internal partial interface IRegisteredTask
{
    void DispatchGetTypeInfoCount();
    void DispatchGetTypeInfo();
    void DispatchGetIDsOfNames();
    void DispatchInvoke();
    void GetName();
    void GetPath();
    int GetState();
    short GetEnabled();
    void SetEnabled(short enabled);
    nint Run(EmptyVariant parameters);
    void RunEx();
    void GetInstances();
    double GetLastRunTime();
    int GetLastTaskResult();
    void GetNumberOfMissedRuns();
    void GetNextRunTime();
    void GetDefinition();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetXml();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetSecurityDescriptor(int securityInformation);
    void SetSecurityDescriptor([MarshalAs(UnmanagedType.BStr)] string sddl, int flags);
}
