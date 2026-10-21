namespace LogReader.Tests;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class InteractiveDesktop
{
    private const string Requirement = "InteractiveDesktop tests require a logged-in, unlocked Windows input desktop. " +
        "Run the runner as an interactive user process, not a service. " +
        "For service-safe validation use --filter Category!=InteractiveDesktop.";

    public static void RequireAvailable()
    {
        using var process = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || process.SessionId == 0)
            Validate(Environment.UserInteractive, process.SessionId, false);

        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == IntPtr.Zero ||
            !GetUserObjectInformation(desktop, 6 /* UOI_IO */, out var receivesInput, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), Requirement);
        Validate(Environment.UserInteractive, process.SessionId, receivesInput != 0);
    }

    internal static void Validate(bool userInteractive, int sessionId, bool receivesInput)
    {
        if (!userInteractive || sessionId == 0 || !receivesInput)
            throw new InvalidOperationException(Requirement);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index,
        out int value, uint length, out uint needed);
}
