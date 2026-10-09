using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.Core.Licensing;
using Beam.Core.Localization;
using Beam.Core.Util;

namespace Beam.App.ViewModels.Dialogs;

public sealed record ProBenefit(string Title, string Description);

/// <summary>Explains Beam Pro and sells it. Returns true when the user now has Pro.</summary>
public sealed class UpgradeViewModel : DialogViewModel
{
    private readonly ProService _pro;
    private readonly IPlatformServices _platform;
    private string _status = "";
    private bool _isBusy;

    public UpgradeViewModel(ProService pro, IPlatformServices platform, string? reason = null)
    {
        _pro = pro;
        _platform = platform;
        Reason = reason ?? "";
        BuyCommand = new AsyncCommand(BuyAsync, () => !IsBusy);
        NotNowCommand = new RelayCommand(() => Close(false));
    }

    public string Title => _pro.IsLaunchPeriod ? L.T("Claim Beam Pro") : L.T("Upgrade to Beam Pro");

    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public IReadOnlyList<ProBenefit> Benefits => _pro.IsLaunchPeriod ? LaunchBenefits : RegularBenefits;

    // Built when used (not in static fields) so they're in the language chosen at startup.
    private static ProBenefit[] LaunchBenefits => new ProBenefit[]
    {
        new(L.T("Yours to keep"), L.T("Everything in Beam is free while it's new. Claim Pro now and keep it when the launch period ends.")),
        new(L.T("Send to several computers at once"), L.T("Pick as many nearby computers as you like and send to all of them in one go.")),
        new(L.T("Full speed, unlimited sends"), L.T("Send as fast as your network allows, as often as you like.")),
        new(L.T("Future Pro features included"), L.T("Every Pro feature added to Beam for Windows later is yours too.")),
    };

    private static ProBenefit[] RegularBenefits => new ProBenefit[]
    {
        new(L.T("Send to several computers at once"), L.T("Pick as many nearby computers as you like and send to all of them in one go.")),
        new(L.T("Full speed"), L.T("Send as fast as your network allows. The free version sends at up to {0}/s.", Format.Bytes(FreeLimits.MaxSendBytesPerSecond))),
        new(L.T("Unlimited sends"), L.T("No daily limit. The free version includes {0} sends a day.", FreeLimits.SendsPerDay)),
        new(L.T("Phones and tablets, when they arrive"), L.T("Beam for Android and iPhone is planned, and Pro will include sending to them.")),
    };

    public string BuyText => !_pro.CanPurchase
        ? L.T("Get Beam from the Microsoft Store")
        : _pro.PriceIsFree ? L.T("Claim for free")
        : string.IsNullOrEmpty(_pro.Price) ? L.T("Upgrade") : L.T("Upgrade · {0}", _pro.Price);

    public string Footnote => _pro.CanPurchase && _pro.PriceIsFree
        ? L.T("Claimed through the Microsoft Store with your Microsoft account. It costs nothing and stays yours on all your Windows PCs.")
        : _pro.CanPurchase
        ? L.T("One-time purchase through the Microsoft Store. Receiving files is always free and unlimited.")
        : L.T("Beam Pro is sold in the Microsoft Store version of Beam. Receiving files is always free and unlimited.");

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => Status.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) BuyCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand BuyCommand { get; }

    public RelayCommand NotNowCommand { get; }

    public override bool CanDismiss => !IsBusy;

    public override object? DismissResult => false;

    private async Task BuyAsync()
    {
        if (!_pro.CanPurchase)
        {
            _platform.OpenUrl(ProService.StorePageUrl);
            return;
        }

        IsBusy = true;
        Status = "";
        try
        {
            switch (await _pro.PurchaseAsync())
            {
                case PurchaseOutcome.Purchased:
                case PurchaseOutcome.AlreadyOwned:
                    Close(true);
                    break;
                case PurchaseOutcome.Cancelled:
                    break;
                case PurchaseOutcome.NotAvailable:
                    Status = L.T("Beam Pro isn't available in the Store right now. Please try again later.");
                    break;
                default:
                    Status = L.T("The purchase couldn't be completed. Check your internet connection and try again.");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
