using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LazerTagWebServer.Services;

internal static class UpdateAdmission
{
    // deploy.sh and rollback.sh take an exclusive flock on this same inode.
    internal const string ApplianceLock = "/run/lock/lazertag-deploy.lock";

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle descriptor, int operation);

    internal static FileStream? TryAcquire(string path)
    {
        try
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            const int sharedNonBlocking = 1 | 4; // LOCK_SH | LOCK_NB
            if (!OperatingSystem.IsLinux() || flock(file.SafeFileHandle, sharedNonBlocking) == 0)
                return file;
            file.Dispose();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
