using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RepoAgentController;

sealed record UnixCaller(LocalCallerPermission Permission);

/// <summary>Kernel-authenticated connection identity; socket ownership is only the outer reachability boundary.</summary>
static partial class UnixPeer
{
    public static LocalCallerPermission Permission(Socket? socket)
    {
        if (socket?.AddressFamily != AddressFamily.Unix) return LocalCallerPermission.Unknown;
        try
        {
            uint peer;
            if (OperatingSystem.IsLinux())
            {
                uint length = 12; // Linux struct ucred: pid_t, uid_t, gid_t, each 32 bits.
                if (GetPeerCredentials(socket.SafeHandle, 1, 17, out var credentials, ref length) != 0 || length != 12)
                    return LocalCallerPermission.Unknown;
                peer = credentials.User;
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (GetPeerId(socket.SafeHandle, out peer, out _) != 0) return LocalCallerPermission.Unknown;
            }
            else return LocalCallerPermission.Unknown;
            // Access to this private container/host socket authorizes the trusted owner (ADR 0016).
            if (peer == EffectiveUserId()) return LocalCallerPermission.Administration;
            return peer == 0 ? LocalCallerPermission.Administration : LocalCallerPermission.Unknown;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or ObjectDisposedException)
        { return LocalCallerPermission.Unknown; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxCredentials { internal int Process; internal uint User; internal uint Group; }

    // SafeSocketHandle keeps the Kestrel-owned socket alive for these synchronous calls. No handle is retained.
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetPeerCredentials(SafeSocketHandle socket, int level, int option, out LinuxCredentials credentials, ref uint length);
    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerId(SafeSocketHandle socket, out uint user, out uint group);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUserId();
}
