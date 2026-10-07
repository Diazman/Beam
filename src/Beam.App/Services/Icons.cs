using Avalonia;
using Avalonia.Media;

namespace Beam.App.Services;

/// <summary>Looks up icon geometries defined in Styles/Icons.axaml.</summary>
public static class Icons
{
    public const string Send = "IconSend";
    public const string Receive = "IconDownload";
    public const string Desktop = "IconDesktop";
    public const string Laptop = "IconLaptop";
    public const string Phone = "IconPhone";
    public const string File = "IconFile";
    public const string Folder = "IconFolder";
    public const string Check = "IconCheckCircle";
    public const string Error = "IconError";
    public const string Warning = "IconWarning";
    public const string Close = "IconClose";

    public static Geometry? Get(string key)
    {
        if (Application.Current?.TryGetResource(key, null, out var value) == true && value is Geometry geometry)
            return geometry;
        return null;
    }
}
