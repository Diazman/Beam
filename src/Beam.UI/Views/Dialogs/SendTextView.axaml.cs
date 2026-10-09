using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Beam.App.Views.Dialogs;

public partial class SendTextView : UserControl
{
    public SendTextView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        this.FindControl<TextBox>("TextBox")?.Focus();
    }
}
