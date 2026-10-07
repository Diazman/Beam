using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Settings;
using Beam.Core.Storage;

namespace Beam.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private BeamNode? _node;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private TrayIcon? _trayIcon;
    private bool _quitting;

    internal static CommandLine StartupCommandLine { get; set; } = new(false, Array.Empty<string>());

    internal static SingleInstance? Instance { get; set; }

    internal static AppDataPaths DataPaths { get; set; } = AppDataPaths.Default();

    public static App? CurrentApp => Current as App;

    public bool IsQuitting => _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _node = BeamNode.Create(new BeamNodeOptions { Paths = DataPaths });
            var platform = PlatformServices.Create();
            _window = new MainWindow();
            _viewModel = new MainViewModel(_node, platform, _window);
            _window.DataContext = _viewModel;

            ApplyTheme(_node.Settings.Current.Theme);
            _node.Settings.Changed += s => Dispatcher.UIThread.Post(() => ApplyTheme(s.Theme));
            CreateTrayIcon();

            var startHidden = StartupCommandLine.StartMinimized && _node.Settings.Current.FirstRunCompleted;
            if (!startHidden)
            {
                desktop.MainWindow = _window;
                _window.Show();
            }

            if (Instance != null)
            {
                Instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() =>
                {
                    _window.BringToFront();
                    _viewModel.HandleCommandLine(CommandLine.Parse(args));
                });
            }

            desktop.ShutdownRequested += (_, _) => _quitting = true; // Windows sign-out / shutdown
            desktop.Exit += (_, _) => Shutdown();
            _ = _viewModel.InitializeAsync(StartupCommandLine);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Quits for real (from the tray menu or when close-to-tray is off), asking first if transfers are running.</summary>
    public async void Quit()
    {
        if (_viewModel == null || _desktop == null || _quitting) return;
        if (_viewModel.HasActiveTransfers)
        {
            _window?.BringToFront();
            if (!await _viewModel.ConfirmQuitAsync()) return;
        }

        _quitting = true;
        _window?.Close();
        _desktop.Shutdown();
    }

    private void Shutdown()
    {
        _quitting = true;
        if (_trayIcon != null) _trayIcon.IsVisible = false;
        try
        {
            _node?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Log.Warn("Error while shutting down", ex);
        }
    }

    private void ApplyTheme(ThemePreference theme) => RequestedThemeVariant = theme switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private void CreateTrayIcon()
    {
        try
        {
            var open = new NativeMenuItem("Open Beam");
            open.Click += (_, _) => _window?.BringToFront();
            var quit = new NativeMenuItem("Quit Beam");
            quit.Click += (_, _) => Quit();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Beam/Assets/beam.ico"))),
                ToolTipText = "Beam — ready to receive files",
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => _window?.BringToFront();
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon unavailable", ex);
        }
    }
}
