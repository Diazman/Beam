using Avalonia.Headless.XUnit;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Settings;

namespace Beam.App.Tests;

/// <summary>When Beam asks the user for something: a store rating, and permissions on phones.</summary>
public class PromptTests
{
    [AvaloniaFact]
    public async Task TheRatingIsAskedForOnceAfterAFewTransfersAndOnlyWhereTheStoreCan()
    {
        await using var app = new UiHarness("Laptop");
        await app.InitializeAsync();
        var rating = app.ViewModel.Rating;

        app.Node.Settings.Update(s => s.CompletedTransfers = RatingService.TransfersBeforeRatingPrompt - 1);
        Assert.False(await rating.MaybeAskForRatingAsync()); // too early
        Assert.Equal(0, app.Store.ReviewRequests);

        app.Store.CanRequestReview = false; // a GitHub build: no store to rate in
        app.Node.Settings.Update(s => s.CompletedTransfers = RatingService.TransfersBeforeRatingPrompt);
        Assert.False(await rating.MaybeAskForRatingAsync());
        Assert.Equal(0, app.Store.ReviewRequests);

        app.Store.CanRequestReview = true;
        Assert.True(await rating.MaybeAskForRatingAsync());
        Assert.Equal(1, app.Store.ReviewRequests);
        Assert.True(app.Node.Settings.Current.RatingRequested);

        app.Node.Settings.Update(s => s.CompletedTransfers += 10);
        Assert.False(await rating.MaybeAskForRatingAsync()); // never twice
        Assert.Equal(1, app.Store.ReviewRequests);
    }

    [AvaloniaFact]
    public async Task SendingDirectlyStaysOffWhenThePhoneDoesntGetThePermission()
    {
        await using var phone = new UiHarness("Galaxy", width: 400, height: 860, phone: true);
        await phone.InitializeAsync();
        phone.Platform.Granted.Remove(AppPermission.NearbyDevices);
        phone.Platform.GrantWhenAsked = false; // the user says no in Android's own prompt
        var home = phone.ViewModel.Home;

        // "Not now" on Beam's explanation: Android isn't even asked.
        home.PreferDirect = true;
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel, "Beam explains why first");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).SecondaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog == null, "closed");
        Assert.DoesNotContain("permission:NearbyDevices", phone.Platform.Calls);
        Assert.False(home.PreferDirect);

        // "Continue", then refused by Android: still off, nothing half-set.
        home.PreferDirect = true;
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel, "asked again");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => phone.Platform.Calls.Contains("permission:NearbyDevices"), "Android asked");
        await UiHarness.PumpAsync();
        Assert.False(home.PreferDirect);
        Assert.Equal(ConnectionMethod.SameNetwork, phone.Node.Settings.Current.Connection);

        // Allowed: on.
        phone.Platform.GrantWhenAsked = true;
        home.PreferDirect = true;
        await UiHarness.WaitForAsync(() => phone.ViewModel.Dialog is ConfirmViewModel, "asked once more");
        ((ConfirmViewModel)phone.ViewModel.Dialog!).PrimaryCommand.Execute(null);
        await UiHarness.WaitForAsync(() => home.PreferDirect, "on once allowed");
    }
}
