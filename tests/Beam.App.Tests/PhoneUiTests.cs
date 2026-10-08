using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Beam.App.ViewModels;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class PhoneUiTests
{
    [AvaloniaFact]
    public async Task PhonePageShowsCodeAndAcceptsFilesFromAPhone()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        app.ViewModel.IsPhonePage = true;
        await UiHarness.PumpAsync();
        var phone = app.ViewModel.Phone;
        Assert.True(phone.IsOff);
        app.Screenshot("phone-01-off");

        phone.TurnOnCommand.Execute(null);
        await UiHarness.PumpAsync();
        Assert.True(phone.IsOn);
        Assert.NotNull(phone.Qr);
        Assert.Contains(app.Node.PhoneLink.Token, phone.Link);
        Assert.Equal("Waiting for a phone to open the link…", phone.ConnectionText);

        var report = app.CreateFile("Quarterly report.pdf", 1_200_000, 1);
        app.Ui.FilesToPick.Add(report);
        phone.ShareFilesCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.IsSharing, "shared file");
        Assert.Contains("Quarterly report.pdf", phone.SharedSummary);

        // A phone opens the page and sends a photo.
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.Node.PhoneLink.Port}/{app.Node.PhoneLink.Token}/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 15; Pixel 9) Mobile");
        Assert.Contains("Send to this computer", await client.GetStringAsync(""));
        await UiHarness.WaitForAsync(() => phone.IsConnected, "phone connected");
        Assert.StartsWith("Connected: Android phone", phone.ConnectionText);
        app.Screenshot("phone-02-on");

        var photo = new byte[400_000];
        Random.Shared.NextBytes(photo);
        var offer = client.PostAsync("api/offer", new StringContent(
            JsonSerializer.Serialize(new { files = new[] { new { name = "PXL_20261008.jpg", size = (long)photo.Length } } }), Encoding.UTF8, "application/json"));
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "approval prompt");
        var prompt = (IncomingRequestViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Android phone wants to send you 1 file", prompt.Title);
        Assert.False(prompt.CanTrust);
        Assert.StartsWith("Sent from a web browser", prompt.OriginText);
        app.Screenshot("phone-03-approval");
        prompt.AcceptCommand.Execute(null);

        var reply = JsonDocument.Parse(await (await offer).Content.ReadAsStringAsync()).RootElement;
        var id = reply.GetProperty("offerId").GetString();
        (await client.PutAsync($"api/upload/{id}/0", new ByteArrayContent(photo))).EnsureSuccessStatusCode();
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Any(t => t.Session.Direction == TransferDirection.Receive && t.IsFinished), "phone transfer card");
        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(app.ReceiveFolder, "PXL_20261008.jpg")));

        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await UiHarness.PumpAsync();
        app.Screenshot("phone-04-dark");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;

        phone.TurnOffCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.IsOff, "turned off");
        Assert.False(phone.IsSharing);
    }
}
