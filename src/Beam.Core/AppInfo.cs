using System.Reflection;

namespace Beam.Core;

/// <summary>Product-wide constants. Change the product name here and in the app project only.</summary>
public static class AppInfo
{
    public const string ProductName = "Beam";

    /// <summary>Version of the wire protocol. Bump when making incompatible changes.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Oldest protocol version this build can still talk to.</summary>
    public const int MinimumProtocolVersion = 1;

    /// <summary>UDP port used for discovering nearby devices.</summary>
    public const int DiscoveryPort = 47820;

    /// <summary>Preferred TCP port for transfers. If it is busy, a nearby port is used and announced.</summary>
    public const int TransferPort = 47821;

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsAndroid() ? "android" :
        OperatingSystem.IsIOS() ? "ios" : "unknown";
}
