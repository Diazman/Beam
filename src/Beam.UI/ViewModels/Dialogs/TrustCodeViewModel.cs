using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Localization;
using Beam.Core.Settings;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>What the "Add a trusted device" dialog ended with.</summary>
public enum TrustCodeResult
{
    Closed,
    Added,

    /// <summary>The user chose to scan the other device's code instead (phones).</summary>
    Scan,
}

/// <summary>
/// Settings → Trusted devices → Add a trusted device: this device's one-time code, for the other device to scan. Both
/// trust each other once it has. The code works once, for 10 minutes; closing the dialog ends it.
/// </summary>
public sealed class TrustCodeViewModel : DialogViewModel
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly BeamNode _node;
    private readonly DispatcherTimer _expiry;
    private Geometry? _code;
    private string? _addedName;
    private bool _expired;

    public TrustCodeViewModel(BeamNode node, bool canScan)
    {
        _node = node;
        CanScan = canScan;
        _expiry = new DispatcherTimer(DispatcherPriority.Background) { Interval = Lifetime };
        _expiry.Tick += (_, _) => Expire();
        node.Trust.Added += OnAdded;
        NewCodeCommand = new RelayCommand(ShowCode);
        ScanCommand = new RelayCommand(() => Close(TrustCodeResult.Scan));
        CloseCommand = new RelayCommand(() => Close(_addedName != null ? TrustCodeResult.Added : TrustCodeResult.Closed));
        ShowCode();
    }

    public Geometry? Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    /// <summary>What the code says (tests read it instead of a camera).</summary>
    internal Beam.Core.Direct.PairingCode? TrustCode { get; private set; }

    /// <summary>This device has a camera: it can scan the other one's code instead.</summary>
    public bool CanScan { get; }

    public bool ShowsCode => _addedName == null && !_expired;

    public bool ShowsScan => ShowsCode && CanScan;

    public bool IsExpired => _addedName == null && _expired;

    public bool IsAdded => _addedName != null;

    public string AddedText => L.T("{0} and this device trust each other now. Files between them are accepted without asking, and each can ask to browse the other's shared folders.", _addedName ?? "");

    public string CloseText => IsAdded ? L.T("Done") : L.T("Cancel");

    public RelayCommand NewCodeCommand { get; }

    public RelayCommand ScanCommand { get; }

    public RelayCommand CloseCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => _addedName != null ? TrustCodeResult.Added : TrustCodeResult.Closed;

    private void ShowCode()
    {
        TrustCode = _node.Trust.CreateCode();
        Code = QrCode.Create(TrustCode.AppendTo(null)).Geometry;
        _expired = false;
        _expiry.Stop();
        _expiry.Start();
        Refresh();
    }

    private void Expire()
    {
        _expiry.Stop();
        _node.Trust.CancelCode();
        _expired = true;
        Code = null;
        Refresh();
    }

    private void OnAdded(TrustedDevice device) => Dispatcher.UIThread.Post(() =>
    {
        _expiry.Stop();
        _addedName = device.Name;
        Code = null;
        Refresh();
    });

    private void Refresh()
    {
        foreach (var name in new[] { nameof(ShowsCode), nameof(ShowsScan), nameof(IsExpired), nameof(IsAdded), nameof(AddedText), nameof(CloseText) })
            OnPropertyChanged(name);
    }

    /// <summary>The dialog closed (any way): the code stops working.</summary>
    internal void OnClosed()
    {
        _expiry.Stop();
        _node.Trust.Added -= OnAdded;
        _node.Trust.CancelCode();
    }
}
