using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepoAgentController;

/// <summary>Requires positive exclusion rather than FileStream's best-effort Unix locking.</summary>
static partial class ExclusiveFileLease
{
    public static FileStream Open(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, options);
        try
        {
            // FileStream ignores errors such as EACCES and ENOTSUP on Unix. A controller
            // must not proceed without the exclusive lock, including on an SMB mount.
            const int Exclusive = 2, Nonblocking = 4;
            if (!OperatingSystem.IsWindows() && Flock(file.SafeFileHandle, Exclusive | Nonblocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException("Exclusive controller lease could not be acquired for '" + path + "'.", new Win32Exception(error));
            }
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle file, int operation);
}
