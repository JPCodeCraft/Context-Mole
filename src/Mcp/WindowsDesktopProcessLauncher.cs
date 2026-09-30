using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace ContextMole.Mcp;

internal static class WindowsDesktopProcessLauncher
{
    internal static bool HasDesktopShell => OperatingSystem.IsWindows() && GetShellWindow() != IntPtr.Zero;

    // Windows supports creating a process with the desktop shell's runtime context.
    // Keep the caller's environment, but do not inherit its MSIX filesystem view.
    // https://devblogs.microsoft.com/oldnewthing/20190425-00/?p=102443
    public static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero) throw new InvalidOperationException("The Windows desktop shell is unavailable.");
        GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        using var shell = OpenProcess(0x0080 | 0x1000, false, shellProcessId); // CREATE_PROCESS | QUERY_LIMITED_INFORMATION
        if (shell.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot access the Windows desktop shell.");
        if (!OpenProcessToken(shell, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var shellIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            if (shellIdentity.User != currentIdentity.User)
                throw new InvalidOperationException("The Windows desktop shell belongs to a different user.");
        }

        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((int)size));
        var parentValue = Marshal.AllocHGlobal(IntPtr.Size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.WriteIntPtr(parentValue, shell.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020000, parentValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfoEx
            {
                Info = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() },
                Attributes = attributes
            };
            var commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot launch Context Mole in the desktop runtime.");
            try
            {
                return Process.GetProcessById(checked((int)processInfo.ProcessId));
            }
            finally
            {
                CloseHandle(processInfo.Thread);
                CloseHandle(processInfo.Process);
            }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(parentValue);
            Marshal.FreeHGlobal(attributes);
        }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
