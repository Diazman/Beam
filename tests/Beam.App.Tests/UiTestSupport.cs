using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Beam.App;
using Beam.App.Platform;
using Beam.App.Services;
using Beam.App.ViewModels;
using Beam.App.Views;
using Beam.Core;
using Beam.Core.Discovery;
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

    public List<string> FoldersToPick { get; } = new();

    public string? FolderToPick { get; set; }

    public bool IsWindowActive { get; set; } = true;

    public int BringToFrontCount { get; private set; }

    public string? Clipboard { get; private set; }

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(FilesToPick.ToList());

    public Task<IReadOnlyList<string>> PickFoldersAsync() => Task.FromResult<IReadOnlyList<string>>(FoldersToPick.ToList());

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(FolderToPick);

    public void BringToFront() => BringToFrontCount++;

    public Task CopyToClipboardAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }
}

public sealed class FakePlatform : IPlatformServices
{
    public List<string> Calls { get; } = new();

    public bool SupportsStartWithSystem => true;

    public bool StartWithSystem { get; set; }

    public void OpenFolder(string path) => Calls.Add("open-folder:" + path);

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
}

/// <summary>Stands in for the Microsoft Store.</summary>
public sealed class FakeStore : IStoreService
{
    public bool CanPurchase { get; set; } = true;

    public bool Owns { get; set; }

    public PurchaseOutcome NextOutcome { get; set; } = PurchaseOutcome.Purchased;

    public int Purchases { get; private set; }

    public event Action? LicenseChanged;

    public Task<bool?> OwnsProAsync() => Task.FromResult<bool?>(Owns);

    public string Price { get; set; } = "$4.99";

    public Task<string?> GetProPriceAsync() => Task.FromResult<string?>(CanPurchase ? Price : null);

    public Task<PurchaseOutcome> PurchaseProAsync()
    {
        Purchases++;
        if (NextOutcome == PurchaseOutcome.Purchased) Owns = true;
        return Task.FromResult(NextOutcome);
    }

    public int ReviewRequests { get; private set; }

    public Task RequestReviewAsync()
    {
        ReviewRequests++;
        return Task.CompletedTask;
    }

    public void RaiseLicenseChanged() => LicenseChanged?.Invoke();
}

/// <summary>A Beam app (view model + real window + real node) running headless.</summary>
public sealed class UiHarness : IAsyncDisposable
{
    private readonly TempDir _dir = new();

    /// <param name="pro">Most tests run as Pro so the free edition's limits don't get in the way.</param>
    public UiHarness(string name = "Diaz's PC", bool firstRunDone = true, int width = 1180, int height = 760, bool pro = true, bool launchPeriod = false)
    {
        Store = new FakeStore { Owns = pro };
        ReceiveFolder = _dir.Combine("Downloads");
        Directory.CreateDirectory(ReceiveFolder);
        Node = BeamNode.Create(new BeamNodeOptions
        {
            Paths = new AppDataPaths(_dir.Combine("data")),
            TransferPort = 0,
            Edition = new Beam.Core.Licensing.Edition(pro, launchPeriod),
            Discovery = new DiscoveryOptions { Port = 0, UseMulticastAndBroadcast = false },
        });
        Node.Settings.Update(s =>
        {
            s.DeviceName = name;
            s.ReceiveFolder = ReceiveFolder;
            s.FirstRunCompleted = firstRunDone;
        });
        Ui = new FakeUi();
        Platform = new FakePlatform();
        Window = new MainWindow { Width = width, Height = height };
        ViewModel = new MainViewModel(Node, Platform, Ui, Store);
        Window.DataContext = ViewModel;
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

    /// <summary>Renders the window to a PNG under artifacts/screenshots for visual review.</summary>
    public string Screenshot(string name, string folderName = "screenshots")
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered");
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
