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
using Beam.Core.Localization;
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

    public override void Initialize()
    {
        // Before any style or window loads: text is translated when it is created.
        try
        {
            L.SetLanguage(new SettingsStore(DataPaths.SettingsFile).Current.Language);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the language setting", ex);
        }

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                SettingsStore? settings = null;
                _node = BeamNode.Create(new BeamNodeOptions
                {
                    Paths = DataPaths,
                    DirectLink = OperatingSystem.IsWindows() ? WindowsDirectLink.TryCreate(() => settings?.Current.DeviceName ?? Environment.MachineName) : null,
                });
                settings = _node.Settings;
            }
            catch (Exception ex)
            {
                Log.Error("Beam could not start", ex);
                ShowStartupError(desktop, ex);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var platform = PlatformServices.Create();
            _window = new MainWindow();
            var window = _window;
            var store = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
                ? WindowsStoreService.Create(() => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)
                : new UnavailableStoreService();
            _viewModel = new MainViewModel(_node, platform, _window, store);
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

    /// <summary>Last-resort window when Beam can't initialise (instead of a crash dialog).</summary>
    private void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex)
    {
        var close = new Button { Content = L.T("Close"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 96 };
        close.Classes.Add("accent");
        var heading = new TextBlock { Text = L.T("Beam couldn't start"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var window = new Window
        {
            Title = "Beam",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = this.FindResource("WindowBackgroundBrush") as Avalonia.Media.IBrush,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    heading,
                    new TextBlock
                    {
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = L.T("Something on this computer stopped Beam from starting. Restarting the computer often helps. If it keeps happening, the details below help with troubleshooting."),
                    },
                    new SelectableTextBlock
                    {
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 12,
                        Text = L.T("{0}: {1}\nLog folder: {2}", ex.GetType().Name, ex.Message, DataPaths.LogDirectory),
                    },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => desktop.Shutdown(1);
        desktop.MainWindow = window;
        window.Show();
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
            var open = new NativeMenuItem(L.T("Open Beam"));
            open.Click += (_, _) => _window?.BringToFront();
            var quit = new NativeMenuItem(L.T("Quit Beam"));
            quit.Click += (_, _) => Quit();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Beam/Assets/beam.ico"))),
                ToolTipText = L.T("Beam — ready to receive files"),
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
