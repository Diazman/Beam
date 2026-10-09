using Avalonia.Headless.XUnit;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Settings;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class TextUiTests
{
    [AvaloniaFact]
    public async Task SendTextPrefillsClipboardAndReceiverCanCopyOrOpenTheLink()
    {
        await using var sender = new UiHarness("Office PC");
        await using var receiver = new UiHarness("Laptop");
        await sender.InitializeAsync();
        await receiver.InitializeAsync();
        sender.Node.Discovery.ReportReachable(new Beam.Core.Discovery.DeviceInfo
        {
            Id = receiver.Node.Identity.DeviceId,
            Name = "Laptop",
            Fingerprint = receiver.Node.Identity.Fingerprint,
            Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, receiver.Node.Transfers.Port) },
        });
        var home = sender.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "auto-selected device");

        sender.Ui.Clipboard = "https://example.com/slides";
        home.SendTextCommand.Execute(null);
        await UiHarness.WaitForAsync(() => sender.ViewModel.Dialog is SendTextViewModel, "compose dialog");
        var compose = (SendTextViewModel)sender.ViewModel.Dialog!;
        Assert.Equal("Send text to Laptop", compose.Title);
        Assert.Equal("https://example.com/slides", compose.Text);
        sender.Screenshot("text-01-compose");
        compose.SendCommand.Execute(null);

        await UiHarness.WaitForAsync(() => receiver.ViewModel.Dialog is IncomingTextViewModel, "incoming text");
        var incoming = (IncomingTextViewModel)receiver.ViewModel.Dialog!;
        Assert.Equal("Office PC sent you a link", incoming.Title);
        Assert.True(incoming.HasLink);
        receiver.Screenshot("text-02-incoming");
        incoming.CopyCommand.Execute(null);
        await UiHarness.PumpAsync();
        Assert.Equal("https://example.com/slides", receiver.Ui.Clipboard);
        incoming.OpenLinkCommand.Execute(null);
        Assert.Contains("open-url:https://example.com/slides", receiver.Platform.Calls);

        await UiHarness.WaitForAsync(() => sender.ViewModel.Transfers.Any(t => t.IsFinished), "sender card");
        var card = sender.ViewModel.Transfers[0];
        Assert.Equal("Text sent", card.StatusText);
        Assert.True(card.CanCopyText);
        await UiHarness.WaitForAsync(() => receiver.ViewModel.History.Items.Count == 1, "receiver history");
        var item = receiver.ViewModel.History.Items[0];
        Assert.True(item.CanCopyText);
        Assert.Equal("From Office PC · Text", item.Subtitle);
    }

    [AvaloniaFact]
    public async Task TrustedSenderTextIsCopiedWithoutAsking()
    {
        await using var app = new UiHarness("Laptop");
        await app.InitializeAsync();
        await using var phone = new TestNode("Office PC");
        app.Node.Settings.Update(s => s.TrustedDevices.Add(new TrustedDevice { Fingerprint = phone.Node.Identity.Fingerprint, DeviceId = phone.Node.Identity.DeviceId, Name = "Office PC" }));
        app.Ui.IsWindowActive = false;

        var session = phone.Node.SendText(new Beam.Core.Discovery.DeviceInfo
        {
            Id = app.Node.Identity.DeviceId,
            Name = "Laptop",
            Fingerprint = app.Node.Identity.Fingerprint,
            Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, app.Node.Transfers.Port) },
        }, "WiFi password: correct-horse");
        await UiHarness.WaitForAsync(() => session.IsFinished, "sent");
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal("WiFi password: correct-horse", app.Ui.Clipboard);
        Assert.False(app.ViewModel.HasDialog);
        Assert.Contains(app.Platform.Calls, c => c.StartsWith("notify:Text from Office PC copied"));
    }

    [AvaloniaFact]
    public async Task SharedTextAndFilesFromAnotherAppAreSentWithOneTap()
    {
        await using var phone = new UiHarness("Diaz's Galaxy", width: 400, height: 860, phone: true);
        await using var receiver = new UiHarness("Laptop");
        await phone.InitializeAsync();
        await receiver.InitializeAsync();
        phone.Node.Discovery.ReportReachable(new Beam.Core.Discovery.DeviceInfo
        {
            Id = receiver.Node.Identity.DeviceId,
            Name = "Laptop",
            Fingerprint = receiver.Node.Identity.Fingerprint,
            Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, receiver.Node.Transfers.Port) },
        });
        receiver.Node.Settings.Update(s => s.TrustedDevices.Add(new TrustedDevice { Fingerprint = phone.Node.Identity.Fingerprint, DeviceId = phone.Node.Identity.DeviceId, Name = "Diaz's Galaxy" }));
        var home = phone.ViewModel.Home;
        await UiHarness.WaitForAsync(() => home.SelectedDevice != null, "auto-selected device");

        // Android hands a shared link to Beam like a command line: it waits on the Send screen.
        phone.ViewModel.IsHistoryPage = true;
        phone.ViewModel.HandleCommandLine(new CommandLine(false, Array.Empty<string>(), SendText: "  https://example.com/recipe  "));
        await UiHarness.PumpAsync(100);
        Assert.True(phone.ViewModel.IsHomePage);
        Assert.Equal("https://example.com/recipe", home.SharedText);
        Assert.True(home.SharedTextIsLink);
        Assert.False(home.ShowDropZone);
        Assert.True(home.CanSend);
        Assert.Equal("Send the link to Laptop", home.SendSummary);
        Assert.Contains("Link to send", phone.VisibleTexts());
        Assert.Empty(phone.TextOutsideWindow());
        phone.Screenshot("share-01-link");

        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => receiver.Ui.Clipboard == "https://example.com/recipe", "link received");
        await UiHarness.WaitForAsync(() => home.SharedText == null, "shared text cleared");
        Assert.True(home.ShowDropZone);

        // Shared files and text together: the files go first, then the text.
        var photo = phone.CreateFile("IMG_2041.jpg", 200_000);
        phone.ViewModel.HandleCommandLine(new CommandLine(false, new[] { photo }, SendText: "Dinner photos"));
        await UiHarness.PumpAsync(100);
        Assert.Single(home.Items);
        Assert.False(home.SharedTextIsLink);
        Assert.Contains("Text to send", phone.VisibleTexts());
        phone.Screenshot("share-02-file-and-text");
        home.SendCommand.Execute(null);
        await UiHarness.WaitForAsync(() => receiver.Ui.Clipboard == "Dinner photos", "text received");
        await UiHarness.WaitForAsync(() => receiver.Node.History.Entries.Count(e => e.Direction == TransferDirection.Receive) == 3, "both received");
        Assert.Empty(home.Items);
        Assert.Null(home.SharedText);

        // Removing the shared text brings back the empty Send screen.
        phone.ViewModel.HandleCommandLine(new CommandLine(false, Array.Empty<string>(), SendText: "never mind"));
        home.ClearSharedTextCommand.Execute(null);
        Assert.True(home.ShowDropZone);
        Assert.False(home.CanSend);
    }

    [Fact]
    public void OnlyWebLinksCanBeOpened()
    {
        Assert.Equal("https://example.com/a?b=1", IncomingTextViewModel.TryGetLink("  https://example.com/a?b=1 "));
        Assert.Null(IncomingTextViewModel.TryGetLink("file:///C:/Windows/System32/calc.exe"));
        Assert.Null(IncomingTextViewModel.TryGetLink("javascript:alert(1)"));
        Assert.Null(IncomingTextViewModel.TryGetLink("see https://example.com"));
    }
}
