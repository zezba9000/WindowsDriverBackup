using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsDriverBackup
{
    internal static class WindowsTools
    {
        internal const int NoMatchingDevice = 259;
        internal const int RebootRequired = 3010;

        internal static string GetWindowsDirectory()
        {
            StringBuilder path = new StringBuilder(260);
            uint length = GetSystemWindowsDirectory(path, (uint)path.Capacity);
            if (length == 0 || length >= path.Capacity)
                throw new Win32Exception();
            return path.ToString();
        }

        internal static string GetNativeSystemDirectory(string windows)
        {
            string sysnative = Path.Combine(windows, "Sysnative");
            return IntPtr.Size == 4 && Directory.Exists(sysnative)
                ? sysnative
                : Path.Combine(windows, "System32");
        }

        internal static bool HasModernPnPUtil(string executable)
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executable);
            return version.FileMajorPart > 10 ||
                (version.FileMajorPart == 10 && (version.FileMinorPart > 0 || version.FileBuildPart >= 14393));
        }

        internal static void CreateNewDirectory(string path)
        {
            if (!CreateDirectory(path, IntPtr.Zero))
                throw new IOException(path + " — " + new Win32Exception().Message);
        }

        internal static string QuotePath(string path)
        {
            // A trailing backslash must be escaped before the closing quote.
            return "\"" + path + new string('\\', path.Length - path.TrimEnd('\\').Length) + "\"";
        }

        internal static int RunInConsole(string executable, string arguments)
        {
            const uint createNewConsole = 0x10;
            const uint infinite = 0xffffffff;
            string workingDirectory = GetWindowsDirectory();
            StringBuilder command = new StringBuilder(QuotePath(executable) + " " + arguments);
            StartupInfo startup = new StartupInfo();
            startup.Size = Marshal.SizeOf(typeof(StartupInfo));
            ProcessInformation process;

            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, createNewConsole,
                IntPtr.Zero, workingDirectory, ref startup, out process))
                throw new Win32Exception();

            CloseHandle(process.Thread);
            try
            {
                if (WaitForSingleObject(process.Process, infinite) != 0)
                    throw new Win32Exception();
                int exitCode;
                if (!GetExitCodeProcess(process.Process, out exitCode))
                    throw new Win32Exception();
                return exitCode;
            }
            finally
            {
                CloseHandle(process.Process);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public int ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetSystemWindowsDirectoryW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern uint GetSystemWindowsDirectory(StringBuilder path, uint size);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
            string directory, ref StartupInfo startup, out ProcessInformation process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
