using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace ContextMole.Mcp;

internal sealed record DataDirectoryAccess(string RequestedDirectory, string PhysicalDirectory, bool IsRedirected);

internal static class WindowsDataDirectoryAccess
{
    public static DataDirectoryAccess Probe(string dataDirectory)
    {
        var requested = Path.GetFullPath(dataDirectory);
        if (!OperatingSystem.IsWindows()) return new(requested, requested, false);

        // Probe a write, since MSIX can read through to the real index while redirecting
        // newly created authentication and lifecycle files into a private cache.
        var ancestor = requested;
        while (!Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor)
                ?? throw new DirectoryNotFoundException($"No existing parent for {requested}.");
        var probePath = Path.Combine(ancestor, $".contextmole-access-{Guid.NewGuid():N}.tmp");
        using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        var physicalProbe = GetPhysicalPath(probe.SafeFileHandle);
        var physicalDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(physicalProbe)!,
            Path.GetRelativePath(ancestor, requested)));
        return new(requested, physicalDirectory, IsPackageRedirectedPath(requested, physicalDirectory));
    }

    internal static bool IsPackageRedirectedPath(string requested, string physical)
    {
        // A user may deliberately configure a physical package directory or a junction.
        // Only a different MSIX LocalCache location indicates inherited virtualization.
        return !string.Equals(requested.TrimEnd('\\'), physical.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && physical.Contains("\\AppData\\Local\\Packages\\", StringComparison.OrdinalIgnoreCase)
            && physical.Contains("\\LocalCache\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPhysicalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
