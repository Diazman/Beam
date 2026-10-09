using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Beam.App.Views;
using Beam.Core.Diagnostics;
using Beam.Core.Localization;
using Beam.Core.Settings;

namespace Beam.Droid;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        // Before any style or view loads: text is translated (and uses its phone wording) when it is created.
        L.IsPhone = true;
        try
        {
            L.SetLanguage(new SettingsStore(AndroidHost.Paths.SettingsFile).Current.Language);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the language setting", ex);
        }

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var view = new MobileView();
            var viewModel = AndroidHost.CreateViewModel(view);
            view.DataContext = viewModel;
            single.MainView = view;
            ApplyTheme(viewModel.Node.Settings.Current.Theme);
            viewModel.Node.Settings.Changed += s => Dispatcher.UIThread.Post(() => ApplyTheme(s.Theme));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(ThemePreference theme) => RequestedThemeVariant = theme switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
