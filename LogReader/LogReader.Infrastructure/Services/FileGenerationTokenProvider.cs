namespace LogReader.Infrastructure.Services;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LogReader.Core.Models;

internal static class FileGenerationTokenProvider
{
    public static FileGenerationToken Capture(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!OperatingSystem.IsWindows() ||
            !GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            return FileGenerationToken.Unknown;
        }

        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return FileGenerationToken.Create(information.VolumeSerialNumber, fileId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}
