using System.Collections.ObjectModel;
using Beam.App.Infrastructure;
using Beam.App.Platform;
using Beam.App.ViewModels.Dialogs;
using Beam.Core.Diagnostics;
using Beam.Core.Direct;
using Beam.Core.Localization;
using Beam.Core.Settings;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels;

/// <summary>A folder other trusted devices can browse (Settings → Browsing).</summary>
public sealed class SharedFolderViewModel
{
    public SharedFolderViewModel(SharedFolder folder, Action<SharedFolderViewModel> remove)
    {
        Folder = folder;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public SharedFolder Folder { get; }

    public string Name => Folder.Name;

    public string Path => Folder.Path;

    public RelayCommand RemoveCommand { get; }
}

// Settings → Trusted devices (adding one with a code) and Settings → Browsing (what others may browse here).
public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan OwnerGrace = TimeSpan.FromMinutes(2);
    private DateTime _ownerConfirmedAt = DateTime.MinValue;
    private string _trustMessage = "";
    private bool _isAddingTrusted;

    public ObservableCollection<SharedFolderViewModel> SharedFolders { get; } = new();

    public bool HasSharedFolders => SharedFolders.Count > 0;

    public bool BrowsingEnabled
    {
        get => _node.Settings.Current.BrowsingEnabled;
        set
        {
            if (_suppress || value == _node.Settings.Current.BrowsingEnabled) return;
            if (value) EnableBrowsingAsync().Forget("Turning browsing on");
            else _node.Browse.SetEnabled(false);
        }
    }

    public bool BrowseAllowAdding
    {
        get => _node.Settings.Current.BrowseAllowAdding;
        set
        {
            if (_suppress || value == _node.Settings.Current.BrowseAllowAdding) return;
            if (value) AllowAddingAsync().Forget("Allowing adding files");
            else _node.Settings.Update(s => s.BrowseAllowAdding = false);
        }
    }

    /// <summary>What went wrong adding a trusted device, or turning browsing on.</summary>
    public string TrustMessage
    {
        get => _trustMessage;
        private set
        {
            if (SetProperty(ref _trustMessage, value)) OnPropertyChanged(nameof(HasTrustMessage));
        }
    }

    public bool HasTrustMessage => TrustMessage.Length > 0;

    public bool IsAddingTrusted
    {
        get => _isAddingTrusted;
        private set => SetProperty(ref _isAddingTrusted, value);
    }

    public AsyncCommand AddTrustedCommand { get; private set; } = null!;

    public AsyncCommand AddSharedFolderCommand { get; private set; } = null!;

    private void InitBrowse()
    {
        AddTrustedCommand = new AsyncCommand(AddTrustedAsync);
        AddSharedFolderCommand = new AsyncCommand(AddSharedFolderAsync);
    }

    private void ReloadBrowse(AppSettings settings)
    {
        OnPropertyChanged(nameof(BrowsingEnabled));
        OnPropertyChanged(nameof(BrowseAllowAdding));
        static string Key(SharedFolder f) => $"{f.Id}|{f.Name}|{f.Path}";
        if (!SharedFolders.Select(f => Key(f.Folder)).SequenceEqual(settings.SharedFolders.Select(Key)))
        {
            SharedFolders.Clear();
            foreach (var folder in settings.SharedFolders) SharedFolders.Add(new SharedFolderViewModel(folder, RemoveSharedFolder));
            OnPropertyChanged(nameof(HasSharedFolders));
        }
    }

    /// <summary>Settings → Trusted devices → Add a trusted device: shows this device's code (or scans the other's).</summary>
    private async Task AddTrustedAsync()
    {
        TrustMessage = "";
        if (!await EnsureOwnerAsync(L.T("Confirm it's you to add a trusted device."))) return;
        var dialog = new TrustCodeViewModel(_node, _platform.CanScanCodes);
        object? result;
        try
        {
            result = await _main.ShowDialogAsync(dialog);
        }
        finally
        {
            dialog.OnClosed();
        }

        if (result is TrustCodeResult.Scan) await ScanTrustedAsync();
    }

    private async Task ScanTrustedAsync()
    {
        var scanned = await _platform.ScanCodeAsync();
        if (string.IsNullOrWhiteSpace(scanned)) return; // cancelled
        if (PairingCode.TryParse(scanned) is not { TrustToken: not null } code)
        {
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("That's not a code for adding a trusted device"),
                L.T("On the other device, open Beam → Settings → Trusted devices → Add a trusted device, then scan the code shown there."), L.T("OK"), ""));
            return;
        }

        await AddTrustedFromCodeAsync(code, ownerConfirmed: true);
    }

    /// <summary>A trusted-device code was scanned (here, or with the scan button on Send): both trust each other.</summary>
    internal async Task AddTrustedFromCodeAsync(PairingCode code, bool ownerConfirmed = false)
    {
        TrustMessage = "";
        if (!ownerConfirmed && !await EnsureOwnerAsync(L.T("Confirm it's you to add a trusted device."))) return;
        IsAddingTrusted = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var device = await _node.Trust.AddAsync(code, cts.Token);
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("{0} is a trusted device now", device.Name),
                L.T("Files between {0} and this device are accepted without asking, and each can ask to browse the other's shared folders.", device.Name),
                L.T("OK"), ""));
        }
        catch (Exception ex)
        {
            Log.Info($"Trust: adding a device failed ({ex.GetType().Name})");
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Couldn't add the trusted device"),
                ex is TransferException te ? te.Error.Message : L.T("Something went wrong. Try again; if it keeps happening, send the log from Settings."),
                L.T("OK"), ""));
        }
        finally
        {
            IsAddingTrusted = false;
        }
    }

    private async Task EnableBrowsingAsync()
    {
        TrustMessage = "";
        try
        {
            if (!await EnsureOwnerAsync(L.T("Confirm it's you to let trusted devices browse this phone.")))
                return;
            // A phone's usual folders (Pictures, Download…) need Android's "All files access" to be listed.
            if (_platform.IsPhone && !_platform.HasPermission(AppPermission.AllFiles) && !await _platform.RequestPermissionAsync(AppPermission.AllFiles))
            {
                TrustMessage = L.T("Beam needs “All files access” to show this phone's folders to your trusted devices. Allow it in Android's settings, then switch browsing on again.");
                return;
            }

            _node.Browse.SetEnabled(true);
        }
        finally
        {
            OnPropertyChanged(nameof(BrowsingEnabled)); // the switch shows what really happened
        }
    }

    private async Task AllowAddingAsync()
    {
        try
        {
            if (await EnsureOwnerAsync(L.T("Confirm it's you to let trusted devices add files here.")))
                _node.Settings.Update(s => s.BrowseAllowAdding = true);
        }
        finally
        {
            OnPropertyChanged(nameof(BrowseAllowAdding));
        }
    }

    private async Task AddSharedFolderAsync()
    {
        // Phones: the storage and its usual folders by name first (Android's picker refuses the storage and Download).
        var offered = _platform.ShareableFolders;
        if (offered.Count > 0)
        {
            var shared = _node.Settings.Current.SharedFolders;
            var choice = await _main.ShowDialogAsync(new ShareFolderViewModel(offered,
                path => shared.Any(f => string.Equals(f.Path.TrimEnd('/'), path.TrimEnd('/'), StringComparison.Ordinal))));
            if (choice is not string chosen) return;
            if (chosen != ShareFolderViewModel.OtherFolder)
            {
                var name = offered.First(f => f.Path == chosen).Name;
                _node.Browse.AddFolder(chosen, name);
                return;
            }
        }

        var picked = await _ui.PickFolderAsync(L.T("Choose a folder to share with your trusted devices"), null);
        if (string.IsNullOrEmpty(picked)) return;
        // Phones give a content:// address for the picked folder: browsing needs the folder itself.
        if (_platform.ToLocalFolder(picked) is not { } folder)
        {
            await _main.ShowDialogAsync(new ConfirmViewModel(L.T("Can't share this folder"),
                L.T("Beam can only share folders on this device's own storage. Choose another one."), L.T("OK"), ""));
            return;
        }

        _node.Browse.AddFolder(folder);
    }

    private void RemoveSharedFolder(SharedFolderViewModel folder) => _node.Browse.RemoveFolder(folder.Folder.Id);

    private void BrowseTrusted(TrustedDeviceViewModel device)
    {
        var info = _node.Discovery.Find(device.Device.DeviceId)
                   ?? new Beam.Core.Discovery.DeviceInfo { Id = device.Device.DeviceId, Name = device.Name, Fingerprint = device.Device.Fingerprint };
        _main.OpenBrowseAsync(info).Forget("Opening browsing");
    }

    private void AskAgain(TrustedDeviceViewModel device) => _node.Browse.Forget(device.Device.Fingerprint);

    /// <summary>
    /// Phones: the owner confirms with the phone's own lock before anything that lets another device in. Computers
    /// (no such check) carry on.
    /// </summary>
    private async Task<bool> EnsureOwnerAsync(string reason)
    {
        if (!_platform.IsPhone || DateTime.UtcNow - _ownerConfirmedAt < OwnerGrace) return true;
        switch (await _platform.ConfirmOwnerAsync(L.T("Confirm it's you"), reason))
        {
            case OwnerCheck.Confirmed:
                break;
            case OwnerCheck.NoScreenLock:
                if (await _main.ShowDialogAsync(new ConfirmViewModel(L.T("This phone has no screen lock"),
                        L.T("Anyone who picks up this phone could let their own device in. To prevent that, set a screen lock (PIN, pattern, fingerprint or face) in the phone's settings."),
                        L.T("Continue anyway"), L.T("Cancel"))) is not true)
                    return false;
                break;
            default:
                TrustMessage = L.T("Nothing was changed. Confirm with your fingerprint, face or screen lock to change this.");
                return false;
        }

        _ownerConfirmedAt = DateTime.UtcNow;
        return true;
    }
}
