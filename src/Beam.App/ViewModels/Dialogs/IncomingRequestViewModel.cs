using Beam.App.Infrastructure;
using Beam.App.Services;
using Beam.Core.Identity;
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

        var what = request.Items.Count == 1 && request.Items[0].IsDirectory
            ? $"the folder “{request.Items[0].Name}”"
            : Format.Count(request.FileCount, "file");
        Title = $"{request.SenderName} wants to send you {what}";
        Summary = request.FolderCount > 0
            ? $"{Format.Contents(request.FileCount, request.FolderCount)} · {Format.Bytes(request.TotalBytes)}"
            : Format.Bytes(request.TotalBytes);
        Items = request.Items.Take(MaxListedItems).Select(i => new IncomingItemViewModel(
            i.Name,
            i.IsDirectory ? $"{Format.Count(i.FileCount, "file")} · {Format.Bytes(i.Size)}" : Format.Bytes(i.Size),
            i.IsDirectory)).ToList();
        MoreItemsText = request.Items.Count > MaxListedItems ? $"and {request.Items.Count - MaxListedItems} more" : "";
        VerificationCode = DeviceIdentity.ShortCode(request.SenderFingerprint);
        TrustText = $"Always accept files from {request.SenderName}";

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

    private async Task ChangeFolderAsync()
    {
        var folder = await _ui.PickFolderAsync("Choose where to save the files", Folder);
        if (!string.IsNullOrEmpty(folder)) Folder = folder;
    }

    private void UpdateSpace()
    {
        var available = DiskSpace.GetAvailableBytes(Folder);
        NotEnoughSpace = available >= 0 && available < Request.TotalBytes;
        SpaceWarning = NotEnoughSpace
            ? $"Not enough space: {Format.Bytes(Request.TotalBytes)} needed, {Format.Bytes(available)} free. Choose another folder or free up space."
            : "";
        OnPropertyChanged(nameof(NotEnoughSpace));
        OnPropertyChanged(nameof(SpaceWarning));
        AcceptCommand?.RaiseCanExecuteChanged();
    }
}
