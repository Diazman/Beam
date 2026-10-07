using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Beam.App.ViewModels;

namespace Beam.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SettingsViewModel vm)
            {
                vm.SaveName();
                e.Handled = true;
            }
        };
        NameBox.LostFocus += (_, _) => (DataContext as SettingsViewModel)?.SaveName();
    }
}
