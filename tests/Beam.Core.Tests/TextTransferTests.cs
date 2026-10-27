using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

public class TextTransferTests
{
    [Fact]
    public async Task TextArrivesAndIsRecordedOnBothSides()
    {
        await using var pc = new TestNode("Office PC");
        await using var laptop = new TestNode("Laptop");
        const string text = "https://example.com/meeting?id=42\nSee you at 3 — Привет, salom, merhaba 👋";

        var session = pc.Node.SendText(laptop.AsDevice(), text);
        await Wait.ForFinishAsync(session);

        Assert.Equal(TransferState.Completed, session.State);
        var received = Assert.Single(laptop.Handler.Texts);
        Assert.Equal(text, received.Text);
        Assert.Equal("Office PC", received.SenderName);
        Assert.False(received.IsTrusted);
        Assert.Equal("https://example.com/meeting?id=42", session.Title);

        await Wait.UntilAsync(() => laptop.Node.History.Entries.Count == 1 && pc.Node.History.Entries.Count == 1, because: "history");
        Assert.Equal(text, laptop.Node.History.Entries[0].Text);
        Assert.Equal(text, pc.Node.History.Entries[0].Text);
        Assert.True(laptop.Node.Transfers.Sessions.Single().IsText);
    }

    [Fact]
    public async Task TrustedSenderIsMarkedAndDismissIsReportedAsDeclined()
    {
        await using var pc = new TestNode("Office PC");
        await using var laptop = new TestNode("Laptop");
        laptop.Node.Settings.Update(s => s.TrustedDevices.Add(new TrustedDevice { Fingerprint = pc.Node.Identity.Fingerprint, DeviceId = pc.Node.Identity.DeviceId, Name = "Office PC" }));

        var first = pc.Node.SendText(laptop.AsDevice(), "trusted hello");
        await Wait.ForFinishAsync(first);
        Assert.True(Assert.Single(laptop.Handler.Texts).IsTrusted);

        laptop.Handler.ShowText = false;
        var second = pc.Node.SendText(laptop.AsDevice(), "ignored");
        await Wait.ForFinishAsync(second);
        Assert.Equal(TransferState.Declined, second.State);
    }

    [Fact]
    public async Task AnUnpairedDeviceGetsOneOpenTextAtATime()
    {
        await using var pc = new TestNode("Office PC");
        await using var laptop = new TestNode("Laptop");
        laptop.Handler.HoldTexts = true; // nobody has looked at the first one yet

        var first = pc.Node.SendText(laptop.AsDevice(), "first");
        await Wait.UntilAsync(() => laptop.Handler.Texts.Count == 1, because: "the first text is shown");
        var second = pc.Node.SendText(laptop.AsDevice(), "second");
        await Wait.ForFinishAsync(second);

        Assert.Equal(TransferState.Declined, second.State);
        Assert.Single(laptop.Handler.Texts); // not a second prompt
        Assert.False(first.IsFinished);
    }

    [Fact]
    public async Task ATextThatCantBeShownEndsInsteadOfWaitingForever()
    {
        await using var pc = new TestNode("Office PC");
        await using var laptop = new TestNode("Laptop");
        laptop.Handler.TextFails = new InvalidOperationException("the window is gone");

        var session = pc.Node.SendText(laptop.AsDevice(), "hello");
        await Wait.ForFinishAsync(session);

        Assert.NotEqual(TransferState.Completed, session.State);
        await Wait.UntilAsync(() => laptop.Node.Transfers.Sessions.Single().IsFinished, because: "the receiver doesn't keep it open");
        Assert.Equal(TransferState.Failed, laptop.Node.Transfers.Sessions.Single().State);

        laptop.Handler.TextFails = null; // and the next one gets through
        var again = pc.Node.SendText(laptop.AsDevice(), "hello again");
        await Wait.ForFinishAsync(again);
        Assert.Equal(TransferState.Completed, again.State);
    }

    [Fact]
    public async Task UnreachableAndOversizedTextFailClearly()
    {
        await using var pc = new TestNode("Office PC");
        await using var laptop = new TestNode("Laptop");
        var device = laptop.AsDevice();
        await laptop.Node.DisposeAsync();

        var session = pc.Node.SendText(device, "hello");
        await Wait.ForFinishAsync(session);
        Assert.Equal(TransferState.Failed, session.State);
        Assert.Equal(TransferErrorKind.ConnectFailed, session.Error!.Kind);

        var ex = Assert.Throws<TransferException>(() => pc.Node.SendText(device, new string('x', TextTransfer.MaxLength + 1)));
        Assert.Equal(TransferErrorKind.NothingToSend, ex.Error.Kind);
    }
}
