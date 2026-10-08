using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core.Identity;
using Beam.Core.Localization;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels.Dialogs;

public sealed record IncomingItemViewModel(string Name, string Details, bool IsFolder)
{
    public Avalonia.Media.Geometry? Icon => Icons.Get(IsFolder ? Icons.Folder : Icons.File);
}

/// <summary>"Diaz's PC wants to send you 3 files" — accept or decline.</summary>
public sealed class IncomingRequestViewModel : DialogViewModel
{
    private const int MaxListedItems = 5;
    private readonly IUiServices _ui;
    private string _folder;
    private bool _trustDevice;

    public IncomingRequestViewModel(IncomingRequest request, IUiServices ui, bool trustedDevicesAvailable)
    {
        Request = request;
        _ui = ui;
        _folder = request.DefaultFolder;
        CanTrust = trustedDevicesAvailable;

        Title = request.Items.Count == 1 && request.Items[0].IsDirectory
            ? L.T("{0} wants to send you the folder “{1}”", request.SenderName, request.Items[0].Name)
            : L.Plural(request.FileCount, "{1} wants to send you {0} file", "{1} wants to send you {0} files", request.SenderName);
        Summary = request.FolderCount > 0
            ? $"{Contents(request.FileCount, request.FolderCount)} · {Format.Bytes(request.TotalBytes)}"
            : Format.Bytes(request.TotalBytes);
        Items = request.Items.Take(MaxListedItems).Select(i => new IncomingItemViewModel(
            i.Name,
            i.IsDirectory ? $"{L.Plural(i.FileCount, "{0} file", "{0} files")} · {Format.Bytes(i.Size)}" : Format.Bytes(i.Size),
            i.IsDirectory)).ToList();
        MoreItemsText = request.Items.Count > MaxListedItems ? L.T("and {0} more", request.Items.Count - MaxListedItems) : "";
        VerificationCode = DeviceIdentity.ShortCode(request.SenderFingerprint);
        TrustText = L.T("Always accept files from {0}", request.SenderName);

        AcceptCommand = new RelayCommand(() => Close(true), () => !NotEnoughSpace);
        DeclineCommand = new RelayCommand(() => Close(false));
        ChangeFolderCommand = new AsyncCommand(ChangeFolderAsync);
        UpdateSpace();
    }

    public IncomingRequest Request { get; }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<IncomingItemViewModel> Items { get; }

    public string MoreItemsText { get; }

    public bool HasMoreItems => MoreItemsText.Length > 0;

    public string VerificationCode { get; }

    public string SenderAddress => Request.SenderAddress;

    /// <summary>Where the request comes from: the sender's security code, or the phone's address for browser uploads.</summary>
    public string OriginText => VerificationCode.Length > 0
        ? L.T("Security code {0} · from {1}", VerificationCode, SenderAddress)
        : L.T("Sent from a web browser at {0}, using the link this computer is showing", SenderAddress);

    public bool CanTrust { get; }

    public string TrustText { get; }

    public string Folder
    {
        get => _folder;
        private set
        {
            if (SetProperty(ref _folder, value)) UpdateSpace();
        }
    }

    public bool TrustDevice
    {
        get => _trustDevice;
        set => SetProperty(ref _trustDevice, value);
    }

    public bool NotEnoughSpace { get; private set; }

    public string SpaceWarning { get; private set; } = "";

    public RelayCommand AcceptCommand { get; }

    public RelayCommand DeclineCommand { get; }

    public AsyncCommand ChangeFolderCommand { get; }

    /// <summary>"3 files", "1 folder, 12 files".</summary>
    private static string Contents(int files, int folders)
    {
        if (folders == 0) return L.Plural(files, "{0} file", "{0} files");
        if (files == 0) return L.Plural(folders, "{0} folder", "{0} folders");
        return $"{L.Plural(folders, "{0} folder", "{0} folders")}, {L.Plural(files, "{0} file", "{0} files")}";
    }

    private async Task ChangeFolderAsync()
    {
        var folder = await _ui.PickFolderAsync(L.T("Choose where to save the files"), Folder);
        if (!string.IsNullOrEmpty(folder)) Folder = folder;
    }

    private void UpdateSpace()
    {
        var available = DiskSpace.GetAvailableBytes(Folder);
        NotEnoughSpace = available >= 0 && available < Request.TotalBytes;
        SpaceWarning = NotEnoughSpace
            ? L.T("Not enough space: {0} needed, {1} free. Choose another folder or free up space.", Format.Bytes(Request.TotalBytes), Format.Bytes(available))
            : "";
        OnPropertyChanged(nameof(NotEnoughSpace));
        OnPropertyChanged(nameof(SpaceWarning));
        AcceptCommand?.RaiseCanExecuteChanged();
    }
}
