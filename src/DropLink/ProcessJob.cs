using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DropLink;

/// <summary>
/// A Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Every backend
/// process the host starts is assigned to it, so the kernel tears the whole tree
/// down when the host disappears for any reason — normal exit, logoff, a crash,
/// or Task Manager. That is the only reliable answer to orphaned backends that
/// keep holding the radio and block the next launch.
/// Not marked SupportedOSPlatform: the type is never constructed off Windows
/// (see ProcessJob.Shared), which keeps call sites free of CA1416 noise.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private IntPtr handle;

    private KillOnCloseJob(IntPtr handle) => this.handle = handle;

    public bool IsValid => handle != IntPtr.Zero;

    /// <summary>Returns null when the platform refuses to create the job.</summary>
    public static KillOnCloseJob? TryCreate()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };
        var size = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                Native.CloseHandle(job);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new KillOnCloseJob(job);
    }

    /// <summary>Assigns a freshly started process. Failure is not fatal.</summary>
    public bool TryAssign(Process process)
    {
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (process.HasExited) return false;
            return Native.AssignProcessToJobObject(handle, process.Handle);
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (current != IntPtr.Zero) Native.CloseHandle(current);
    }

    private static class Native
    {
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        internal const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>One shared job for every child process the host starts.</summary>
internal static class ProcessJob
{
    private static readonly object Gate = new();
    private static KillOnCloseJob? shared;
    private static bool tried;

    internal static KillOnCloseJob? Shared
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            lock (Gate)
            {
                if (tried) return shared;
                tried = true;
                try { shared = KillOnCloseJob.TryCreate(); }
                catch (Exception) { shared = null; }
                AppLog.Write(shared is { IsValid: true }
                    ? "process job ready: children die with the host"
                    : "process job unavailable: falling back to explicit cleanup");
                return shared;
            }
        }
    }

    internal static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        Shared?.TryAssign(process);
    }
}
