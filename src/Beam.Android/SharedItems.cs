using Android.Content;
using Android.Runtime;
using AndroidUri = Android.Net.Uri;

namespace Beam.Droid;

/// <summary>What another app shared to Beam from the Share sheet: files (content:// addresses) or text/a link.</summary>
internal sealed record SharedItems(IReadOnlyList<string> Files, string? Text)
{
    public bool IsEmpty => Files.Count == 0 && string.IsNullOrWhiteSpace(Text);

    /// <summary>Reads an ACTION_SEND / ACTION_SEND_MULTIPLE intent; null for any other intent (e.g. the launcher).</summary>
    public static SharedItems? From(Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple)) return null;

        var files = new List<string>();
        void Add(AndroidUri? uri)
        {
            var address = uri?.ToString();
            if (!string.IsNullOrEmpty(address) && !files.Contains(address)) files.Add(address);
        }

        // Apps put the files in ClipData (which also carries the read permission) and/or EXTRA_STREAM.
        if (intent.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++) Add(clip.GetItemAt(i)?.Uri);
        }

        if (intent.Action == Intent.ActionSendMultiple)
        {
            foreach (var uri in StreamList(intent)) Add(uri);
        }
        else
        {
            Add(Stream(intent));
        }

        // Text (a message, or a link from the browser) only when no files came with it: apps often add a caption to photos.
        var text = files.Count == 0 ? intent.GetCharSequenceExtra(Intent.ExtraText)?.ToString()?.Trim() : null;
        var shared = new SharedItems(files, string.IsNullOrWhiteSpace(text) ? null : text);
        return shared.IsEmpty ? null : shared;
    }

    private static AndroidUri? Stream(Intent intent)
    {
        Java.Lang.Object? value;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            value = intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri)));
        else
#pragma warning disable CA1422 // the untyped getter is the only one before Android 13
            value = intent.GetParcelableExtra(Intent.ExtraStream);
#pragma warning restore CA1422
        return value?.JavaCast<AndroidUri>();
    }

    private static IEnumerable<AndroidUri> StreamList(Intent intent)
    {
        System.Collections.IEnumerable? list;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            list = intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri)));
        else
#pragma warning disable CA1422 // the untyped getter is the only one before Android 13
            list = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
#pragma warning restore CA1422
        if (list == null) yield break;
        foreach (var item in list)
        {
            if (item is IJavaObject java && java.JavaCast<AndroidUri>() is { } uri) yield return uri;
        }
    }
}
