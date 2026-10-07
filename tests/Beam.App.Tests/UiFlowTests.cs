using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Beam.App.ViewModels;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Discovery;
using Beam.Core.History;
using Beam.Core.Tests;
using Beam.Core.Transfer;

namespace Beam.App.Tests;

public class UiFlowTests
{
    [AvaloniaFact]
    public async Task FirstRunShowsWelcomeThenStartsNetworking()
    {
        await using var app = new UiHarness(firstRunDone: false);
        var init = app.InitializeAsync();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is WelcomeViewModel, "welcome dialog");
        app.Screenshot("01-welcome");
        Assert.False(app.Node.Transfers.IsListening, "network must not start before the user has read the welcome screen");

        var welcome = (WelcomeViewModel)app.ViewModel.Dialog!;
        welcome.DeviceName = "  Diaz's Laptop  ";
        welcome.StartCommand.Execute(null);
        await init;

        Assert.True(app.Node.Transfers.IsListening);
        Assert.True(app.Node.Settings.Current.FirstRunCompleted);
        Assert.Equal("Diaz's Laptop", app.Node.Settings.Current.DeviceName);
        Assert.False(app.ViewModel.HasDialog);
    }

    [AvaloniaFact]
    public async Task HomeScreenShowsDevicesSelectionAndSummary()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await UiHarness.PumpAsync();
        app.Screenshot("02-home-empty-searching");

        app.AddFakeDevice("Laptop", DeviceKinds.Laptop);
        app.AddFakeDevice("John's PC");
        app.AddFakeDevice("Living room PC");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 3, "devices");
        Assert.Null(app.ViewModel.Home.SelectedDevice); // more than one: user must choose
        Assert.False(app.ViewModel.Home.CanSend);

        app.ViewModel.Home.Devices[0].SelectCommand.Execute(null);
        var file1 = app.CreateFile("Quarterly report.pdf", 2_400_000, 1);
        var file2 = app.CreateFile("Photos/2026/January/IMG_0001.jpg", 3_100_000, 2);
        app.CreateFile("Photos/2026/February/IMG_0002.jpg", 2_900_000, 3);
        app.ViewModel.Home.AddPaths(new[] { file1, Path.Combine(app.Root, "source", "Photos") });
        await UiHarness.WaitForAsync(() => !app.ViewModel.Home.IsMeasuring, "folder size");

        Assert.Equal("John's PC", app.ViewModel.Home.SelectedDevice!.Name);
        Assert.True(app.ViewModel.Home.CanSend);
        Assert.Equal("Send 2 items (8.01 MB) to John's PC", app.ViewModel.Home.SendSummary);
        Assert.Contains("2 files", app.ViewModel.Home.Items[1].Details);
        app.Screenshot("03-home-ready-to-send");

        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await UiHarness.PumpAsync();
        app.Screenshot("04-home-ready-to-send-dark");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        _ = file2;
    }

    [AvaloniaFact]
    public async Task ReceivingAsksAcceptsAndShowsResult()
    {
        await using var app = new UiHarness("Diaz's PC");
        await app.InitializeAsync();
        await using var sender = new TestNode("Laptop");
        var a = sender.CreateFile("Holiday/beach.jpg", 1_500_000, 1);
        sender.CreateFile("Holiday/sunset.jpg", 2_500_000, 2);
        sender.CreateFile("Holiday/Videos/clip.mp4", 12_000_000, 3);
        var notes = sender.CreateFile("notes.txt", 2_000, 4);

        var outgoing = sender.Node.Send(UiDevice(app), new[] { sender.SourcePath("Holiday"), notes });
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "incoming request");
        var request = (IncomingRequestViewModel)app.ViewModel.Dialog!;
        Assert.Equal("Laptop wants to send you 4 files", request.Title);
        Assert.Equal("2 folders, 4 files \u00b7 15.3 MB", request.Summary);
        Assert.Equal(2, request.Items.Count);
        app.Screenshot("05-incoming-request");

        request.AcceptCommand.Execute(null);
        await UiHarness.WaitForAsync(() => outgoing.IsFinished, "transfer");
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.FirstOrDefault()?.IsFinished == true, "card update");
        var card = app.ViewModel.Transfers.Single();
        Assert.Equal("Received 4 files", card.StatusText);
        Assert.True(card.CanOpen);
        Assert.Equal(TestFiles.Hash(a), TestFiles.Hash(Path.Combine(app.ReceiveFolder, "Holiday", "beach.jpg")));
        await UiHarness.PumpAsync(100);
        app.Screenshot("06-received-complete");

        card.ShowInFolderCommand.Execute(null);
        Assert.Contains(app.Platform.Calls, c => c.StartsWith("reveal:") && c.Contains("Holiday"));

        await UiHarness.WaitForAsync(() => app.ViewModel.History.Items.Count == 1, "history");
        app.ViewModel.ShowHistoryCommand.Execute(null);
        await UiHarness.PumpAsync();
        Assert.True(app.ViewModel.History.Items[0].CanShow);
        app.Screenshot("07-history");
    }

    [AvaloniaFact]
    public async Task SendingShowsLiveProgressAndCompletes()
    {
        await using var app = new UiHarness("Diaz's PC");
        await app.InitializeAsync();
        await using var receiver = new TestNode("Laptop");
        receiver.Handler.DecisionDelay = TimeSpan.FromMilliseconds(700);
        // Throttle so there is always a mid-transfer moment to observe, however fast the machine is.
        using var throttle = new FlakyProxy(receiver.AsDevice().Endpoints[0]) { BytesPerSecond = 40L * 1024 * 1024 };
        app.Node.Discovery.ReportReachable(receiver.AsDevice(throttle.Port));
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.SelectedDevice != null, "auto-selected only device");

        var big = app.CreateFile("Project backup.zip", 160L * 1024 * 1024, 11);
        app.Ui.FilesToPick.Add(big);
        app.ViewModel.Home.ChooseFilesCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.CanSend, "file added");
        app.ViewModel.Home.SendCommand.Execute(null);
        Assert.False(app.ViewModel.Home.HasItems); // list clears once the send starts

        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Count == 1, "card");
        var card = app.ViewModel.Transfers[0];
        await UiHarness.WaitForAsync(() => card.State == TransferState.WaitingForAcceptance, "waiting state");
        Assert.Equal("Waiting for Laptop to accept…", card.StatusText);

        await UiHarness.WaitForAsync(() => card.Progress is > 15 and < 85 && card.SpeedText.Length > 0, "mid-transfer progress", 60000);
        Assert.Matches(@"^[\d.]+ MB of 160 MB$", card.SizeText);
        Assert.EndsWith("/s", card.SpeedText);
        Assert.Equal("Sending", card.StatusText);
        app.Screenshot("08-sending-progress");

        await UiHarness.WaitForAsync(() => card.IsFinished, "completion", 120000);
        Assert.Equal("Sent 1 file", card.StatusText);
        Assert.Equal(TestFiles.Hash(big), TestFiles.Hash(Path.Combine(receiver.ReceiveFolder, "Project backup.zip")));
    }

    [AvaloniaFact]
    public async Task ConflictsCanBeResolvedForAllAtOnce()
    {
        await using var app = new UiHarness("Diaz's PC");
        await app.InitializeAsync();
        await using var sender = new TestNode("Laptop");
        var files = Enumerable.Range(1, 3).Select(i => sender.CreateFile($"doc{i}.txt", 1000 + i, i)).ToArray();
        foreach (var i in Enumerable.Range(1, 3)) File.WriteAllText(Path.Combine(app.ReceiveFolder, $"doc{i}.txt"), "existing");

        var outgoing = sender.Node.Send(UiDevice(app), files);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "request");
        ((IncomingRequestViewModel)app.ViewModel.Dialog!).AcceptCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ConflictViewModel, "conflict dialog");
        var conflict = (ConflictViewModel)app.ViewModel.Dialog!;
        Assert.Equal("“doc1.txt” already exists", conflict.Title);
        Assert.True(conflict.HasMore);
        Assert.Equal("Do this for the other 2 conflicts", conflict.ApplyToAllText);
        app.Screenshot("09-conflict");

        conflict.ApplyToAll = true;
        conflict.KeepBothCommand.Execute(null);
        await UiHarness.WaitForAsync(() => outgoing.IsFinished, "transfer");

        Assert.Equal(TransferState.Completed, outgoing.State);
        foreach (var i in Enumerable.Range(1, 3))
        {
            Assert.Equal("existing", File.ReadAllText(Path.Combine(app.ReceiveFolder, $"doc{i}.txt")));
            Assert.True(File.Exists(Path.Combine(app.ReceiveFolder, $"doc{i} (1).txt")));
        }
    }

    [AvaloniaFact]
    public async Task DecliningRemovesTheCardButKeepsHistory()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await using var sender = new TestNode("Laptop");
        var outgoing = sender.Node.Send(UiDevice(app), new[] { sender.CreateFile("a.txt", 10) });
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "request");
        ((IncomingRequestViewModel)app.ViewModel.Dialog!).DeclineCommand.Execute(null);

        await UiHarness.WaitForAsync(() => outgoing.IsFinished, "sender notified");
        Assert.Equal(TransferState.Declined, outgoing.State);
        Assert.Equal("Diaz's PC declined the files.", outgoing.Error!.Message);
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.Count == 0, "card removed");
        await UiHarness.WaitForAsync(() => app.Node.History.Entries.Count == 1, "history");
        Assert.Equal(HistoryStatus.Declined, app.Node.History.Entries[0].Status);
    }

    [AvaloniaFact]
    public async Task SenderWithdrawingClosesThePrompt()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await using var sender = new TestNode("Laptop");
        var outgoing = sender.Node.Send(UiDevice(app), new[] { sender.CreateFile("a.txt", 10) });
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is IncomingRequestViewModel, "request");

        outgoing.Cancel();
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "prompt closed");
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.SingleOrDefault()?.IsFinished == true, "card");
        Assert.Equal("Laptop cancelled the request.", app.ViewModel.Transfers[0].StatusText);
    }

    [AvaloniaFact]
    public async Task FailedSendExplainsAndOffersRetry()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        var gone = new TestNode("Laptop");
        var device = gone.AsDevice();
        await gone.DisposeAsync();

        app.Node.Discovery.ReportReachable(device);
        app.ViewModel.StartSend(device, new[] { app.CreateFile("a.txt", 10) });
        await UiHarness.WaitForAsync(() => app.ViewModel.Transfers.SingleOrDefault()?.IsFinished == true, "failure", 30000);
        var card = app.ViewModel.Transfers[0];
        Assert.True(card.IsError);
        Assert.True(card.CanResume);
        Assert.StartsWith("Couldn't reach Laptop.", card.StatusText);
        Assert.Contains("Technical details", card.DetailsText);
        card.ToggleDetailsCommand.Execute(null);
        await UiHarness.PumpAsync();
        app.Screenshot("10-send-failed");
    }

    [AvaloniaFact]
    public async Task QuittingDuringATransferAsksFirst()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await using var receiver = new TestNode("Laptop");
        receiver.Handler.NeverAnswer = true;
        app.ViewModel.StartSend(receiver.AsDevice(), new[] { app.CreateFile("a.txt", 10) });
        await UiHarness.WaitForAsync(() => app.ViewModel.HasActiveTransfers, "active transfer");

        var decision = app.ViewModel.ConfirmQuitAsync();
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is ConfirmViewModel, "confirmation");
        var confirm = (ConfirmViewModel)app.ViewModel.Dialog!;
        Assert.Equal("A transfer is still in progress", confirm.Title);
        app.Screenshot("11-quit-confirmation");
        confirm.SecondaryCommand.Execute(null);
        Assert.False(await decision);
    }

    [AvaloniaFact]
    public async Task SettingsPageAppliesChanges()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        app.ViewModel.ShowSettingsCommand.Execute(null);
        await UiHarness.PumpAsync();
        app.Screenshot("12-settings");

        var settings = app.ViewModel.Settings;
        settings.DeviceName = "Studio PC";
        app.ViewModel.ShowHomeCommand.Execute(null); // leaving the page saves the name
        Assert.Equal("Studio PC", app.Node.Settings.Current.DeviceName);
        Assert.Equal("Studio PC", app.ViewModel.Home.LocalDeviceName);

        settings.StartWithWindows = true;
        Assert.Contains("startup:True", app.Platform.Calls);
        settings.Discoverable = false;
        Assert.False(app.Node.Discovery.Discoverable);
        settings.SelectedTheme = settings.ThemeOptions.Single(o => o.Value == Core.Settings.ThemePreference.Dark);
        Assert.Equal(Core.Settings.ThemePreference.Dark, app.Node.Settings.Current.Theme);

        app.Ui.FolderToPick = Path.Combine(app.Root, "Elsewhere");
        Directory.CreateDirectory(app.Ui.FolderToPick);
        settings.ChangeFolderCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.Node.Settings.Current.ReceiveFolder == app.Ui.FolderToPick, "folder change");
    }

    [AvaloniaFact]
    public async Task ConnectByAddressAddsAndSelectsDevice()
    {
        await using var app = new UiHarness();
        await app.InitializeAsync();
        await using var other = new TestNode("Basement PC");
        app.AddFakeDevice("Decoy");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.Devices.Count == 1, "decoy");

        app.ViewModel.Home.ConnectByAddressCommand.Execute(null);
        await UiHarness.WaitForAsync(() => app.ViewModel.Dialog is AddDeviceViewModel, "dialog");
        var dialog = (AddDeviceViewModel)app.ViewModel.Dialog!;
        dialog.Address = "127.0.0.1:1";
        dialog.ConnectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => dialog.HasError, "error shown");
        app.Screenshot("13-connect-by-address-error");

        dialog.Address = $"127.0.0.1:{other.Node.Transfers.Port}";
        dialog.ConnectCommand.Execute(null);
        await UiHarness.WaitForAsync(() => !app.ViewModel.HasDialog, "dialog closed");
        await UiHarness.WaitForAsync(() => app.ViewModel.Home.SelectedDevice?.Name == "Basement PC", "selected");
    }

    private static DeviceInfo UiDevice(UiHarness app) => new()
    {
        Id = app.Node.Identity.DeviceId,
        Name = app.Node.Settings.Current.DeviceName,
        Fingerprint = app.Node.Identity.Fingerprint,
        Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, app.Node.Transfers.Port) },
    };
}
