using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Beam.Core.Localization;

/// <summary>A language Beam's interface is available in.</summary>
public sealed record Language(string Code, string NativeName);

/// <summary>
/// Translates user-facing text. The English text is the key: <c>L.T("Send")</c>,
/// <c>L.T("{0} declined the files.", name)</c>, <c>L.Plural(n, "{0} file", "{0} files")</c>.
/// Each language is a JSON file in this folder mapping English to the translation; a plural is an array of forms
/// (Russian: one, few, many). Anything missing falls back to English.
/// </summary>
public static class L
{
    public const string English = "en";

    /// <summary>Languages in the order the language picker shows them.</summary>
    public static readonly IReadOnlyList<Language> Languages = new Language[]
    {
        new(English, "English"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
        new("uz", "Oʻzbekcha"),
    };

    private static Dictionary<string, string[]> _catalog = new();

    /// <summary>The language in use ("en", "tr", "ru" or "uz").</summary>
    public static string Current { get; private set; } = English;

    /// <summary>The supported language for a preference: an explicit code, or the system language when empty.</summary>
    public static string Resolve(string? preference)
    {
        var code = string.IsNullOrWhiteSpace(preference) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : preference.Trim();
        code = code.ToLowerInvariant();
        return Languages.Any(l => l.Code == code) ? code : English;
    }

    /// <summary>Switches the language. Text already shown keeps its language, so the app applies this at startup.</summary>
    public static void SetLanguage(string? preference)
    {
        var code = Resolve(preference);
        _catalog = code == English ? new() : Load(code);
        Current = code;
        if (code != English)
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary>The translation of an English text.</summary>
    public static string T(string english) =>
        _catalog.TryGetValue(english, out var forms) && forms.Length > 0 && forms[0].Length > 0 ? forms[0] : english;

    /// <summary>The translation of an English format string ("{0} declined the files."), filled in.</summary>
    public static string T(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>
    /// "{0} file" / "{0} files" in the current language; <paramref name="count"/> is {0} (with thousands separators),
    /// <paramref name="args"/> are {1}, {2}…
    /// </summary>
    public static string Plural(long count, string one, string other, params object?[] args)
    {
        string format;
        if (_catalog.TryGetValue(other, out var forms) && forms.Length > 0)
        {
            var index = Math.Min(PluralForm(Current, count), forms.Length - 1);
            format = forms[index];
        }
        else
        {
            format = count == 1 ? one : other;
        }

        var all = new object?[args.Length + 1];
        all[0] = count.ToString("N0", CultureInfo.CurrentCulture);
        Array.Copy(args, 0, all, 1, args.Length);
        return string.Format(CultureInfo.CurrentCulture, format, all);
    }

    /// <summary>Which plural form a count takes: English one/other; Russian one/few/many; Turkish and Uzbek have one form.</summary>
    public static int PluralForm(string language, long count)
    {
        var n = Math.Abs(count);
        switch (language)
        {
            case "ru":
                if (n % 10 == 1 && n % 100 != 11) return 0;
                if (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) return 1;
                return 2;
            case "tr":
            case "uz":
                return 0;
            default:
                return n == 1 ? 0 : 1;
        }
    }

    /// <summary>The translations for a language (English text → forms).</summary>
    public static Dictionary<string, string[]> Load(string code)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Beam.Core.Localization.{code}.json");
        if (stream == null) return result;
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in json.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : new[] { property.Value.GetString() ?? "" };
        }

        return result;
    }
}
