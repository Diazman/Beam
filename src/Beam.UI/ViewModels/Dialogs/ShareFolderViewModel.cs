using Beam.App.Infrastructure;
using Beam.Core.Localization;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>One folder in the "Share a folder" list.</summary>
public sealed class ShareFolderChoice
{
    public ShareFolderChoice(string name, string path, bool isShared, Action<ShareFolderChoice> choose)
    {
        Name = name;
        Path = path;
        IsShared = isShared;
        ChooseCommand = new RelayCommand(() => choose(this), () => !isShared);
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsShared { get; }

    public string Details => IsShared ? L.T("Shared already") : Path;

    public RelayCommand ChooseCommand { get; }
}

/// <summary>
/// Phones: "Share a folder" offers the whole storage and its usual folders by name (Android's picker refuses some of
/// them), or "Another folder…" with the picker. Returns the chosen path, <see cref="OtherFolder"/>, or null.
/// </summary>
public sealed class ShareFolderViewModel : DialogViewModel
{
    public const string OtherFolder = "\0other";

    public ShareFolderViewModel(IReadOnlyList<(string Name, string Path)> folders, Func<string, bool> isShared)
    {
        Folders = folders.Select(f => new ShareFolderChoice(f.Name, f.Path, isShared(f.Path), c => Close(c.Path))).ToList();
        OtherCommand = new RelayCommand(() => Close(OtherFolder));
        CancelCommand = new RelayCommand(() => Close(null));
    }

    public IReadOnlyList<ShareFolderChoice> Folders { get; }

    public RelayCommand OtherCommand { get; }

    public RelayCommand CancelCommand { get; }

    public override bool CanDismiss => true;
}
