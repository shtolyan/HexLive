using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HexLive.UnityPresentation.UI
{
    // §166: System.Diagnostics.Process.Start is not implemented in Unity IL2CPP.
    internal static class WindowsUpdaterProcess
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public uint cb;
            public IntPtr reserved, desktop, title;
            public uint x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
            public ushort showWindow, reservedSize;
            public IntPtr reservedBytes, standardInput, standardOutput, standardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo
        {
            public IntPtr process, thread;
            public uint processId, threadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
            string directory, ref StartupInfo startup, out ProcessInfo process);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static void Start(string helper, int protocol)
        {
            if (protocol <= 0) throw new ArgumentOutOfRangeException(nameof(protocol));
            helper = Path.GetFullPath(helper);
            if (!File.Exists(helper)) throw new FileNotFoundException("HexLiveUpdater.exe", helper);
            var command = new StringBuilder("\"" + helper + "\" --update --wait-pid " +
                GetCurrentProcessId() + " --required-protocol " + protocol);
            var startup = new StartupInfo { cb = (uint)Marshal.SizeOf(typeof(StartupInfo)) };
            if (!CreateProcess(helper, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(helper), ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(child.thread);
            CloseHandle(child.process);
        }
    }
}
