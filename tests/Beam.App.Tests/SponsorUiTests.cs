using Avalonia.Headless.XUnit;
using Beam.App.ViewModels;
using Beam.Core;

namespace Beam.App.Tests;

/// <summary>Support Beam: on computers in Settings → About; never on phones (their app stores allow only in-app purchases).</summary>
public class SponsorUiTests
{
    [AvaloniaFact]
    public async Task ComputersOfferSupportBeamInAbout()
    {
        await using var pc = new UiHarness("Diaz's PC");
        await pc.InitializeAsync();
        pc.ViewModel.IsSettingsPage = true;
        pc.ViewModel.Settings.Section = SettingsSection.About;
        await UiHarness.PumpAsync(100);
        Assert.Contains("Support Beam", pc.VisibleTexts());
        pc.Screenshot("settings-about-sponsor");

        pc.ViewModel.Settings.OpenSponsorCommand.Execute(null);
        Assert.Contains("open-url:" + AppInfo.SponsorUrl, pc.Platform.Calls);
        Assert.Equal("https://github.com/sponsors/Diazman", AppInfo.SponsorUrl);
    }

    [AvaloniaFact]
    public async Task PhonesDontShowIt()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        phone.ViewModel.IsSettingsPage = true;
        phone.ViewModel.Settings.Section = SettingsSection.About;
        await UiHarness.PumpAsync(100);
        Assert.False(phone.ViewModel.Settings.ShowsSponsor);
        Assert.DoesNotContain("Support Beam", phone.VisibleTexts());
        Assert.DoesNotContain("Sponsor", phone.VisibleTexts());
    }
}
