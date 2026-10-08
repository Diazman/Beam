using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.Core.Localization;

namespace Beam.App.Views;

public partial class MainWindow : Window, IUiServices
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public bool IsWindowActive => IsVisible && IsActive && WindowState != WindowState.Minimized;

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Nudge Windows into bringing the window above others without keeping it on top.
        Topmost = true;
        Topmost = false;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Choose files to send"),
            AllowMultiple = true,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("Choose folders to send"),
            AllowMultiple = true,
        });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
            start = await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            return Clipboard == null ? null : await Clipboard.TryGetTextAsync();
        }
        catch
        {
            return null; // another app holds the clipboard
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var app = App.CurrentApp;
        var vm = ViewModel;
        if (app == null || vm == null || app.IsQuitting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (vm.Node.Settings.Current.CloseToTray)
        {
            Hide();
            vm.OnWindowHiddenToTray();
        }
        else
        {
            app.Quit();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { HasDialog: true } vm)
        {
            vm.DismissDialogCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { HasDialog: false } vm || !HasFiles(e)) return;
        vm.IsDragOver = true;
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel is { HasDialog: false } && HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel != null) ViewModel.IsDragOver = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        vm.IsDragOver = false;
        if (vm.HasDialog) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is not { Count: > 0 }) return;
        if (vm.CurrentPageKind == Page.Phone)
        {
            _ = vm.Phone.ShareAsync(paths); // dropped on the Phone page: share with the phone
        }
        else
        {
            vm.Navigate(Page.Home);
            vm.Home.AddPaths(paths);
        }

        e.Handled = true;
    }
}
