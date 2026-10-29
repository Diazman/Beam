using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beam.App;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Discovery;
using Beam.Core.Localization;
using Beam.Core.Storage;
using Beam.Core.Tests;

[assembly: AvaloniaTestApplication(typeof(Beam.App.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Beam.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class FakeUi : IUiServices
{
    public List<string> FilesToPick { get; } = new();

    public string? FolderToPick { get; set; }

    public bool IsWindowActive { get; set; } = true;

    public string? Clipboard { get; set; }

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(FilesToPick.ToList());

    public Task<IReadOnlyList<string>> PickFoldersAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(FolderToPick);

    public void BringToFront() { }

    /// <summary>Another app holds the clipboard: copying throws.</summary>
    public bool ClipboardBusy { get; set; }

    public Task CopyToClipboardAsync(string text)
    {
        if (ClipboardBusy) throw new InvalidOperationException("OpenClipboard failed");
        Clipboard = text;
        return Task.CompletedTask;
    }

    public Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);
}

public sealed class FakePlatform : IPlatformServices
{
    public List<string> Calls { get; } = new();

    public bool IsPhone { get; set; }

    public bool SupportsStartWithSystem => !IsPhone;

    public bool StartWithSystem { get; set; }

    public void OpenFolder(string path) => Calls.Add("open-folder:" + path);

    public string? DefaultReceiveFolder { get; set; }

    /// <summary>What the platform makes of a picked folder (phones: content:// addresses); default: use it as is.</summary>
    public Func<string, ReceiveFolderChoice> FolderCheck { get; set; } = picked => new ReceiveFolderChoice(picked);

    public ReceiveFolderChoice CheckReceiveFolder(string picked) => FolderCheck(picked);

    public void ShareLog(string logDirectory) => Calls.Add("share-log:" + logDirectory);

    /// <summary>Phones can scan codes (like the Android app); set to override.</summary>
    public bool CanScanCodes
    {
        get => _canScanCodes ?? IsPhone;
        set => _canScanCodes = value;
    }

    private bool? _canScanCodes;

    /// <summary>What the "camera" reads next (null: the user cancelled).</summary>
    public string? ScannedCode { get; set; }

    public Task<string?> ScanCodeAsync() => Task.FromResult(ScannedCode);

    public bool FirewallBlocks { get; set; }

    public Task<bool> FirewallBlocksDirectAsync() => Task.FromResult(FirewallBlocks);

    public bool FirewallBlocksLocalNetwork { get; set; }

    public Task<bool> FirewallBlocksLocalNetworkAsync() => Task.FromResult(FirewallBlocksLocalNetwork);

    public Task<bool> AllowDirectThroughFirewallAsync()
    {
        Calls.Add("allow-firewall");
        FirewallBlocks = false;
        FirewallBlocksLocalNetwork = false;
        return Task.FromResult(true);
    }

    public void RevealInFolder(string path) => Calls.Add("reveal:" + path);

    public void OpenFile(string path) => Calls.Add("open-file:" + path);

    public void OpenUrl(string url) => Calls.Add("open-url:" + url);

    public bool GetStartWithSystem() => StartWithSystem;

    public void SetStartWithSystem(bool enabled)
    {
        StartWithSystem = enabled;
        Calls.Add("startup:" + enabled);
    }

    public void ShowNotification(string title, string message, Action? onActivated = null) => Calls.Add($"notify:{title}|{message}");

    /// <summary>Question notifications on screen, by tag, with their buttons.</summary>
    public Dictionary<string, IReadOnlyList<NotificationButton>> Questions { get; } = new();

    public void ShowQuestionNotification(string tag, string title, string message, Action onActivated, IReadOnlyList<NotificationButton> buttons)
    {
        Calls.Add($"question:{title}|{message}");
        Questions[tag] = buttons;
    }

    public void CancelNotification(string tag) => Questions.Remove(tag);

    /// <summary>Permissions the "user" has granted; others are asked for (and granted when <see cref="GrantWhenAsked"/>).</summary>
    public HashSet<AppPermission> Granted { get; } = new() { AppPermission.Notifications, AppPermission.NearbyDevices };

    public bool GrantWhenAsked { get; set; } = true;

    public bool HasPermission(AppPermission permission) => Granted.Contains(permission);

    public Task<bool> RequestPermissionAsync(AppPermission permission)
    {
        Calls.Add("permission:" + permission);
        if (GrantWhenAsked) Granted.Add(permission);
        return Task.FromResult(GrantWhenAsked);
    }

    public bool SupportsBackup { get; set; }

    /// <summary>Whether background backups are scheduled (the last call).</summary>
    public bool? BackupsScheduled { get; private set; }

    public void ScheduleBackups(bool any) => BackupsScheduled = any;

    /// <summary>What the "owner" does at the fingerprint / screen lock prompt.</summary>
    public OwnerCheck OwnerAnswer { get; set; } = OwnerCheck.Confirmed;

    /// <summary>How often the owner was asked to confirm.</summary>
    public int OwnerChecks { get; private set; }

    public Task<OwnerCheck> ConfirmOwnerAsync(string title, string reason)
    {
        OwnerChecks++;
        Calls.Add("owner:" + reason);
        return Task.FromResult(OwnerAnswer);
    }
}

/// <summary>Stands in for the Microsoft Store.</summary>
public sealed class FakeStore : IStoreService
{
    public bool CanRequestReview { get; set; } = true;

    public int ReviewRequests { get; private set; }

    public Task RequestReviewAsync()
    {
        ReviewRequests++;
        return Task.CompletedTask;
    }
}

/// <summary>A Beam app (view model + real window + real node) running headless.</summary>
public sealed class UiHarness : IAsyncDisposable
{
    private readonly TempDir _dir = new();

    public UiHarness(string name = "Diaz's PC", bool firstRunDone = true, int width = 1180, int height = 760,
        string language = L.English, bool phone = false, Beam.Core.Direct.IDirectLink? directLink = null,
        Beam.Core.Files.IExternalFiles? externalFiles = null, Beam.Core.Nearby.INearbyRadio? nearbyRadio = null)
    {
        // Tests assert English text whatever the language of the machine running them.
        L.SetLanguage(language);
        L.IsPhone = phone;
        Store = new FakeStore();
        ReceiveFolder = _dir.Combine("Downloads");
        Directory.CreateDirectory(ReceiveFolder);
        Node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = new AppDataPaths(_dir.Combine("data")),
            TransferPort = 0,
            Discovery = new DiscoveryOptions { Port = 0, UseMulticastAndBroadcast = false },
            DirectLink = directLink,
            NearbyRadio = nearbyRadio,
            ExternalFiles = externalFiles,
            DeviceKind = phone ? DeviceKinds.Phone : DeviceKinds.Laptop,
        });
        Node.Settings.Update(s =>
        {
            s.DeviceName = name;
            s.ReceiveFolder = ReceiveFolder;
            s.FirstRunCompleted = firstRunDone;
        });
        Node.Nearby.ReceiveGrace = TimeSpan.Zero;
        Ui = new FakeUi();
        Platform = new FakePlatform { IsPhone = phone };
        Window = new MainWindow { Width = width, Height = height };
        ViewModel = new MainViewModel(Node, Platform, Ui, Store);
        Window.DataContext = ViewModel;
        if (phone)
        {
            // The phone layout (MobileView) inside a phone-sized window.
            Window.MinWidth = Window.MinHeight = 0;
            Window.Content = new MobileView();
        }

        Window.Show();
    }

    public BeamNode Node { get; }

    public FakeUi Ui { get; }

    public FakePlatform Platform { get; }

    public FakeStore Store { get; }

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public string ReceiveFolder { get; }

    public string Root => _dir.Path;

    public Task InitializeAsync() => ViewModel.InitializeAsync(new CommandLine(false, Array.Empty<string>()));

    public string CreateFile(string relativePath, long size, int seed = 1)
    {
        var path = Path.Combine(Root, "source", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestFiles.Write(path, size, seed);
        return path;
    }

    /// <summary>How other devices reach this one (bypassing discovery).</summary>
    public DeviceInfo AsDevice() => new()
    {
        Id = Node.Identity.DeviceId,
        Name = Node.Settings.Current.DeviceName,
        Fingerprint = Node.Identity.Fingerprint,
        Kind = Platform.IsPhone ? DeviceKinds.Phone : DeviceKinds.Laptop,
        DirectRoles = Node.Direct.AnnouncedRoles,
        Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Node.Transfers.Port) },
    };

    public void AddFakeDevice(string name, string kind = DeviceKinds.Desktop, string platform = "windows")
    {
        Node.Discovery.ReportReachable(new DeviceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Fingerprint = new string('a', 64),
            Kind = kind,
            Platform = platform,
            Endpoints = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1." + Random.Shared.Next(2, 250)), 47821) },
        });
    }

    /// <summary>Lets queued UI work and timers run.</summary>
    public static async Task PumpAsync(int milliseconds = 50)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await PumpAsync(25);
        }
    }

    /// <summary>Visible text that sticks out past the left or right edge of the window (clipped on a phone).</summary>
    public IReadOnlyList<string> TextOutsideWindow()
    {
        Dispatcher.UIThread.RunJobs();
        var problems = new List<string>();
        foreach (var block in Window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>())
        {
            if (!block.IsEffectivelyVisible || string.IsNullOrEmpty(block.Text) || block.Bounds.Width <= 0) continue;
            var topLeft = block.TranslatePoint(new Point(0, 0), Window);
            if (topLeft == null) continue;
            var right = topLeft.Value.X + block.Bounds.Width;
            if (topLeft.Value.X < -0.5 || right > Window.Bounds.Width + 0.5)
                problems.Add($"{block.Text} (x {topLeft.Value.X:0}–{right:0}, window {Window.Bounds.Width:0})");
        }

        return problems;
    }

    /// <summary>Text of every visible TextBlock in the window.</summary>
    public IReadOnlyList<string> VisibleTexts()
    {
        Dispatcher.UIThread.RunJobs();
        return Window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToList();
    }

    /// <summary>Renders the window to a PNG under artifacts/screenshots for visual review.</summary>
    public string Screenshot(string name, string folderName = "screenshots")
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        // Disposed right away: frames left to the finalizer have crashed Skia's encoder (access violation on Windows).
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered");
        var folder = Path.Combine(FindRepoRoot(), "artifacts", folderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        frame.Save(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Window.DataContext = null;
        await Node.DisposeAsync();
        _dir.Dispose();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Beam.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Path.GetTempPath();
    }
}
