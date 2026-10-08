using Avalonia.Headless.XUnit;
using Beam.App.ViewModels;
using Beam.Core.Discovery;
using Beam.Core.Localization;

namespace Beam.App.Tests;

/// <summary>
/// Opens Beam in each language: text is translated, nothing crashes, and the screens are rendered to
/// artifacts/screenshots/language-*.png (and Store screenshots to artifacts/store-screenshots/&lt;language&gt;/) for review.
/// </summary>
public class LanguageUiTests
{
    [AvaloniaTheory]
    [InlineData("tr", "Dosya gönder")]
    [InlineData("ru", "Отправка файлов")]
    [InlineData("uz", "Fayl yuborish")]
    public async Task ScreensAreTranslated(string language, string sendFilesTitle)
    {
        try
        {
            await using var app = new UiHarness("Diaz's PC", width: 1600, height: 900, language: language);
            await app.InitializeAsync();
            app.AddFakeDevice("Diaz's Laptop", DeviceKinds.Laptop);
            app.AddFakeDevice("Office desktop");
            await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 2, "devices");
            app.ViewModel.Home.Devices.First(d => d.Name == "Diaz's Laptop").SelectCommand.Execute(null);
            var report = app.CreateFile("Project proposal.pdf", 3_400_000, 1);
            app.CreateFile("Holiday photos/Beach/IMG_2041.jpg", 4_100_000, 2);
            app.CreateFile("Holiday photos/Beach/IMG_2042.jpg", 3_900_000, 3);
            app.ViewModel.Home.AddPaths(new[] { Path.Combine(app.Root, "source", "Holiday photos"), report });
            await UiHarness.WaitForAsync(() => !app.ViewModel.Home.IsMeasuring, "sizes");
            await UiHarness.PumpAsync(100);

            Assert.Contains(sendFilesTitle, app.VisibleTexts());
            Assert.Contains("Diaz's Laptop", app.ViewModel.Home.SendSummary);
            Assert.DoesNotContain("Send ", app.ViewModel.Home.SendSummary);
            app.Screenshot($"language-{language}-home");
            app.Screenshot("1-send-files", Path.Combine("store-screenshots", language));

            // The smallest window Beam allows, where longer translations would run out of room first.
            app.Window.Width = app.Window.MinWidth;
            app.Window.Height = app.Window.MinHeight;
            await UiHarness.PumpAsync(100);
            app.Screenshot($"language-{language}-home-small");

            app.ViewModel.ShowSettingsCommand.Execute(null);
            await UiHarness.PumpAsync(100);
            Assert.Contains(L.T("Language"), app.VisibleTexts());
            Assert.NotEqual("Language", L.T("Language"));
            app.Screenshot($"language-{language}-settings");

            app.ViewModel.ShowHistoryCommand.Execute(null);
            await UiHarness.PumpAsync(100);
            app.Screenshot($"language-{language}-history");
        }
        finally
        {
            L.SetLanguage(L.English);
        }
    }

    [AvaloniaFact]
    public async Task LanguageChoiceIsSavedAndNeedsARestart()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        var settings = app.ViewModel.Settings;
        Assert.Equal("", settings.SelectedLanguage.Code);
        Assert.False(settings.LanguageNeedsRestart);
        Assert.Contains(settings.LanguageOptions, o => o.Label == "Oʻzbekcha");

        settings.SelectedLanguage = settings.LanguageOptions.First(o => o.Code == "ru");
        await UiHarness.PumpAsync(50);
        Assert.Equal("ru", app.Node.Settings.Current.Language);
        Assert.True(settings.LanguageNeedsRestart);

        settings.SelectedLanguage = settings.LanguageOptions.First(o => o.Code == "en");
        await UiHarness.PumpAsync(50);
        Assert.False(settings.LanguageNeedsRestart);
    }
}
