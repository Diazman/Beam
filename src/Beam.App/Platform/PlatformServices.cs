namespace Beam.App.Platform;

/// <summary>The desktop host's platform services: Windows integration on Windows, generic elsewhere.</summary>
public static class PlatformServices
{
    public static IPlatformServices Create() =>
        OperatingSystem.IsWindows() ? new WindowsPlatformServices() : new GenericPlatformServices();
}
