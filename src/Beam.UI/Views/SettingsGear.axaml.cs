using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Beam.App.ViewModels;

namespace Beam.App.Views;

public partial class SettingsGear : UserControl
{
    public SettingsGear()
    {
        InitializeComponent();
    }

    private void OnClick(object? sender, RoutedEventArgs e) =>
        this.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<MainViewModel>().FirstOrDefault()?.OpenSettingsCommand.Execute(null);
}
