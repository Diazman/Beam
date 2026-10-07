using System.Security.Cryptography;
using System.Text;

namespace Beam.Core.Identity;

/// <summary>Protects secrets at rest: DPAPI (current user) on Windows, owner-only file permissions elsewhere.</summary>
internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Beam.DeviceIdentity.v1");

    public static bool CanProtect => OperatingSystem.IsWindows();

    public static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return data;
    }

    public static byte[] Unprotect(byte[] data, bool isProtected)
    {
        if (!isProtected) return data;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected identity can only be read on Windows.");
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
    }
}
