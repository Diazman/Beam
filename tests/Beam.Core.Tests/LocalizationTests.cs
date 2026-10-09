using System.Text.RegularExpressions;
using Beam.Core.Localization;

namespace Beam.Core.Tests;

/// <summary>
/// Every text wrapped in L.T / L.Plural / {l:T} must be translated in every catalog, with the same placeholders,
/// and catalogs must not keep text the app no longer uses. `python3 tools/l10n.py missing tr` lists what to add.
/// </summary>
public class LocalizationTests
{
    private const string Str = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    private static readonly Regex TCs = new(@"\bL\.T\(\s*" + Str);
    private static readonly Regex PluralCs = new(@"\bL\.Plural\([^;]*?,\s*" + Str + @"\s*,\s*" + Str);
    private static readonly Regex TXaml = new(@"\{l:T\s+'((?:[^'\\]|\\.)*)'(?:\s*,\s*Phone='((?:[^'\\]|\\.)*)')?\s*\}");
    private static readonly Regex ForDeviceCs = new(@"\bL\.ForDevice\(\s*" + Str + @"\s*,\s*" + Str);
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}");

    public static IEnumerable<object[]> Translations() => L.Languages.Where(l => l.Code != L.English).Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translations))]
    public void CatalogMatchesTheSource(string language)
    {
        var (plain, plurals) = SourceKeys();
        Assert.NotEmpty(plain);
        var catalog = L.Load(language);
        var problems = new List<string>();
        foreach (var key in plain.Concat(plurals.Keys).Distinct())
        {
            if (!catalog.TryGetValue(key, out var forms))
            {
                problems.Add($"missing: {key}");
                continue;
            }

            if (plurals.ContainsKey(key) && language == "ru" && forms.Length != 3) problems.Add($"needs 3 Russian forms: {key}");
            foreach (var form in forms)
            {
                if (string.IsNullOrWhiteSpace(form)) problems.Add($"empty: {key}");
                var expected = Placeholders(key);
                var actual = Placeholders(form);
                // A plural form may leave out the count ("Один файл").
                if (!(plurals.ContainsKey(key) ? actual.IsSubsetOf(expected) && expected.Except(actual).All(p => p == "0") : actual.SetEquals(expected)))
                    problems.Add($"placeholders differ: {key} -> {form}");
            }
        }

        problems.AddRange(catalog.Keys.Where(k => !plain.Contains(k) && !plurals.ContainsKey(k)).Select(k => $"unused: {k}"));
        Assert.True(problems.Count == 0, $"{language}.json:\n" + string.Join("\n", problems));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(21, 0)]
    [InlineData(101, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(22, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(111, 2)]
    [InlineData(0, 2)]
    public void RussianPluralForms(long count, int form) => Assert.Equal(form, L.PluralForm("ru", count));

    [Fact]
    public void EnglishAndTurkishPluralForms()
    {
        Assert.Equal(0, L.PluralForm("en", 1));
        Assert.Equal(1, L.PluralForm("en", 0));
        Assert.Equal(1, L.PluralForm("en", 2));
        Assert.Equal(0, L.PluralForm("tr", 5));
        Assert.Equal(0, L.PluralForm("uz", 1));
    }

    [Fact]
    public void UnknownLanguagesFallBackToEnglish()
    {
        Assert.Equal("en", L.Resolve("de"));
        Assert.Equal("ru", L.Resolve("RU"));
        Assert.Equal("uz", L.Resolve("uz"));
        Assert.Contains(L.Resolve(""), L.Languages.Select(l => l.Code));
    }

    private static HashSet<string> Placeholders(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static (HashSet<string> Plain, Dictionary<string, string> Plurals) SourceKeys()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var plain = new HashSet<string>(StringComparer.Ordinal);
        var plurals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.Contains("/Localization/")) continue;
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in TCs.Matches(text)) plain.Add(Unescape(m.Groups[1].Value));
                foreach (Match m in ForDeviceCs.Matches(text))
                {
                    plain.Add(Unescape(m.Groups[1].Value));
                    plain.Add(Unescape(m.Groups[2].Value));
                }
                foreach (Match m in PluralCs.Matches(text)) plurals[Unescape(m.Groups[2].Value)] = Unescape(m.Groups[1].Value);
            }
            else if (file.EndsWith(".axaml", StringComparison.Ordinal))
            {
                foreach (Match m in TXaml.Matches(File.ReadAllText(file)))
                {
                    plain.Add(m.Groups[1].Value.Replace("\\'", "'"));
                    if (m.Groups[2].Success) plain.Add(m.Groups[2].Value.Replace("\\'", "'"));
                }
            }
        }

        return (plain, plurals);
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Beam.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Beam.sln not found above " + AppContext.BaseDirectory);
    }
}
