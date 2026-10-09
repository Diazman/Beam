using Beam.App.Infrastructure;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>A modal dialog shown as an overlay inside the main window.</summary>
public abstract class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<object?> Completion => _completion.Task;

    /// <summary>What Escape / clicking outside does. Null means the dialog cannot be dismissed that way.</summary>
    public virtual object? DismissResult => null;

    public virtual bool CanDismiss => false;

    protected void Close(object? result) => _completion.TrySetResult(result);

    internal void Dismiss()
    {
        if (CanDismiss) Close(DismissResult);
    }

    /// <summary>The reason for the dialog went away (e.g. the sender cancelled).</summary>
    internal void Abort() => _completion.TrySetCanceled();
}
