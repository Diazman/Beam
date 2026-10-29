using Avalonia.Media;
using Avalonia.Threading;
using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core;
using Beam.Core.Backup;
using Beam.Core.Localization;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>
/// On the computer: the one-time code a phone scans to back up here. It works once, for a few minutes; closing the
/// dialog ends it. When a phone has used it, the dialog says which one.
/// </summary>
public sealed class BackupCodeViewModel : DialogViewModel
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly BeamNode _node;
    private readonly DispatcherTimer _expiry;
    private Geometry? _code;
    private string? _addedName;
    private bool _expired;

    public BackupCodeViewModel(BeamNode node)
    {
        _node = node;
        _expiry = new DispatcherTimer(Lifetime, DispatcherPriority.Background, (_, _) => Expire());
        node.Backup.PartnerAdded += OnPartnerAdded;
        NewCodeCommand = new RelayCommand(ShowCode);
        CloseCommand = new RelayCommand(() => Close(_addedName != null));
        ShowCode();
    }

    public Geometry? Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    /// <summary>What the code says (tests read it instead of a camera).</summary>
    internal Beam.Core.Direct.PairingCode? SetupCode { get; private set; }

    public bool ShowsCode => _addedName == null && !_expired;

    public bool IsExpired => _addedName == null && _expired;

    public bool IsAdded => _addedName != null;

    public string AddedText => L.T("{0} backs up to this computer now, whenever both are on the same Wi-Fi.", _addedName ?? "");

    public string CloseText => IsAdded ? L.T("Done") : L.T("Cancel");

    public RelayCommand NewCodeCommand { get; }

    public RelayCommand CloseCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => _addedName != null;

    private void ShowCode()
    {
        SetupCode = _node.Backup.CreateSetupCode();
        Code = QrCode.Create(SetupCode.AppendTo(null)).Geometry;
        _expired = false;
        _expiry.Stop();
        _expiry.Start();
        Refresh();
    }

    private void Expire()
    {
        _expiry.Stop();
        _node.Backup.CancelSetupCode();
        _expired = true;
        Code = null;
        Refresh();
    }

    private void OnPartnerAdded(BackupPartner partner) => Dispatcher.UIThread.Post(() =>
    {
        _expiry.Stop();
        _addedName = partner.Name;
        Code = null;
        Refresh();
    });

    private void Refresh()
    {
        foreach (var name in new[] { nameof(ShowsCode), nameof(IsExpired), nameof(IsAdded), nameof(AddedText), nameof(CloseText) })
            OnPropertyChanged(name);
    }

    /// <summary>The dialog closed (any way): the code stops working.</summary>
    internal void OnClosed()
    {
        _expiry.Stop();
        _node.Backup.PartnerAdded -= OnPartnerAdded;
        _node.Backup.CancelSetupCode();
    }
}
