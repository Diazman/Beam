using Beam.App.Infrastructure;
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
        Title = $"“{name}” already exists";
        Location = conflict.RelativePath == name
            ? $"There's already a file with this name in {folderName}."
            : $"There's already a file at {conflict.RelativePath.Replace('\\', '/')} in {folderName}.";
        ExistingText = Describe(conflict.ExistingSize, conflict.ExistingModifiedUtc);
        IncomingText = Describe(conflict.IncomingSize, conflict.IncomingModifiedUtc);
        RemainingAfterThis = remainingAfterThis;
        ApplyToAllText = remainingAfterThis == 1
            ? "Do this for the other conflict too"
            : $"Do this for the other {remainingAfterThis} conflicts";

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
            : $"{Format.Bytes(size)} · modified {modifiedUtc.ToLocalTime():g}";
}
