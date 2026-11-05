using Beam.App.Infrastructure;
using Beam.Core.Identity;
using Beam.Core.Localization;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>A trusted device asks to browse this device's shared folders. Returns a <see cref="BrowseDecision"/>.</summary>
public sealed class BrowseRequestViewModel : DialogViewModel
{
    private bool _always;

    public BrowseRequestViewModel(IncomingBrowseRequest request, bool canAdd, IReadOnlyList<string> folders)
    {
        Request = request;
        Title = L.T("{0} wants to browse your files", request.DeviceName);
        Message = canAdd
            ? L.T("It can see and download what is in your shared folders, and add files to them. Nothing can be changed or deleted.")
            : L.T("It can see and download what is in your shared folders. Nothing can be changed, added or deleted.");
        FoldersText = folders.Count == 0 ? L.T("No folders are shared.") : L.T("Shared: {0}", string.Join(", ", folders));
        SecurityText = L.T("Security code {0}", DeviceIdentity.ShortCode(request.Fingerprint));
        AlwaysText = L.T("Always allow {0} to browse my files", request.DeviceName);
        AllowCommand = new RelayCommand(() => Close(Always ? BrowseDecision.AllowAlways : BrowseDecision.Allow));
        DenyCommand = new RelayCommand(() => Close(BrowseDecision.Deny));
    }

    public IncomingBrowseRequest Request { get; }

    public string Title { get; }

    public string Message { get; }

    public string FoldersText { get; }

    public string SecurityText { get; }

    public string AlwaysText { get; }

    public bool Always
    {
        get => _always;
        set => SetProperty(ref _always, value);
    }

    public RelayCommand AllowCommand { get; }

    public RelayCommand DenyCommand { get; }

    public override bool CanDismiss => true;

    public override object? DismissResult => BrowseDecision.Deny;
}
