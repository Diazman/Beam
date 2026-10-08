using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.Core.Licensing;
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

    public string Title => _pro.IsLaunchPeriod ? "Claim Beam Pro" : "Upgrade to Beam Pro";

    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public IReadOnlyList<ProBenefit> Benefits => _pro.IsLaunchPeriod ? LaunchBenefits : RegularBenefits;

    private static readonly ProBenefit[] LaunchBenefits =
    {
        new("Yours to keep", "Everything in Beam is free while it's new. Claim Pro now and keep it when the launch period ends."),
        new("Send to several computers at once", "Pick as many nearby computers as you like and send to all of them in one go."),
        new("Full speed, unlimited sends", "Send as fast as your network allows, as often as you like."),
        new("Future Pro features included", "Every Pro feature added to Beam for Windows later is yours too."),
    };

    private static readonly ProBenefit[] RegularBenefits =
    {
        new ProBenefit("Send to several computers at once", "Pick as many nearby computers as you like and send to all of them in one go."),
        new ProBenefit("Full speed", $"Send as fast as your network allows. The free version sends at up to {Format.Bytes(FreeLimits.MaxSendBytesPerSecond)}/s."),
        new ProBenefit("Unlimited sends", $"No daily limit. The free version includes {FreeLimits.SendsPerDay} sends a day."),
        new ProBenefit("Phones and tablets, when they arrive", "Beam for Android and iPhone is planned, and Pro will include sending to them."),
    };

    public string BuyText => !_pro.CanPurchase
        ? "Get Beam from the Microsoft Store"
        : _pro.PriceIsFree ? "Claim for free"
        : string.IsNullOrEmpty(_pro.Price) ? "Upgrade" : $"Upgrade · {_pro.Price}";

    public string Footnote => _pro.CanPurchase && _pro.PriceIsFree
        ? "Claimed through the Microsoft Store with your Microsoft account. It costs nothing and stays yours on all your Windows PCs."
        : _pro.CanPurchase
        ? "One-time purchase through the Microsoft Store. Receiving files is always free and unlimited."
        : "Beam Pro is sold in the Microsoft Store version of Beam. Receiving files is always free and unlimited.";

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
                    Status = "Beam Pro isn't available in the Store right now. Please try again later.";
                    break;
                default:
                    Status = "The purchase couldn't be completed. Check your internet connection and try again.";
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
