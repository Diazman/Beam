using Beam.App.Infrastructure;
using Beam.Core.Localization;
using Beam.Core.Transfer;
using Beam.Core.Util;

namespace Beam.App.ViewModels.Dialogs;

public sealed record ConflictChoice(ConflictAction Action, bool ApplyToAll);

/// <summary>"report.pdf already exists" — Replace, Keep both or Skip, optionally for all remaining conflicts.</summary>
public sealed class ConflictViewModel : DialogViewModel
{
    private bool _applyToAll;

    public ConflictViewModel(FileConflict conflict, string folderName, int remainingAfterThis)
    {
        var name = Path.GetFileName(conflict.RelativePath);
        Title = L.T("“{0}” already exists", name);
        Location = conflict.RelativePath == name
            ? L.T("There's already a file with this name in {0}.", folderName)
            : L.T("There's already a file at {0} in {1}.", conflict.RelativePath.Replace('\\', '/'), folderName);
        ExistingText = Describe(conflict.ExistingSize, conflict.ExistingModifiedUtc);
        IncomingText = Describe(conflict.IncomingSize, conflict.IncomingModifiedUtc);
        RemainingAfterThis = remainingAfterThis;
        ApplyToAllText = L.Plural(remainingAfterThis, "Do this for the other conflict too", "Do this for the other {0} conflicts");

        ReplaceCommand = new RelayCommand(() => Close(new ConflictChoice(ConflictAction.Replace, ApplyToAll)));
        KeepBothCommand = new RelayCommand(() => Close(new ConflictChoice(ConflictAction.KeepBoth, ApplyToAll)));
        SkipCommand = new RelayCommand(() => Close(new ConflictChoice(ConflictAction.Skip, ApplyToAll)));
    }

    public string Title { get; }

    public string Location { get; }

    public string ExistingText { get; }

    public string IncomingText { get; }

    public int RemainingAfterThis { get; }

    public bool HasMore => RemainingAfterThis > 0;

    public string ApplyToAllText { get; }

    public bool ApplyToAll
    {
        get => _applyToAll;
        set => SetProperty(ref _applyToAll, value);
    }

    public RelayCommand ReplaceCommand { get; }

    public RelayCommand KeepBothCommand { get; }

    public RelayCommand SkipCommand { get; }

    private static string Describe(long size, DateTime modifiedUtc) =>
        modifiedUtc == default
            ? Format.Bytes(size)
            : L.T("{0} · modified {1:g}", Format.Bytes(size), modifiedUtc.ToLocalTime());
}
