namespace LogReader.Launcher.Mcp;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class ProcessLifetimeJob
{
    // Root the handle until OS process teardown. Closing it also kills this launcher.
    // Joining before creating the child removes the start/assign orphan window.
    private static SafeFileHandle? _job;

    public static void Initialize()
    {
        if (_job != null)
            return;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create MCP lifetime job.");
        try
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure MCP lifetime job.");
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain MCP launcher process.");
            _job = job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
