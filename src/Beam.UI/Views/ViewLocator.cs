using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Beam.App.ViewModels;
using Beam.App.ViewModels.Dialogs;
using Beam.App.Views.Dialogs;

namespace Beam.App.Views;

/// <summary>Which view shows which view model. Registered in each host's App.axaml (desktop window and phone screen).</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(HomeViewModel)] = () => new HomeView(),
        [typeof(PhoneViewModel)] = () => new PhoneView(),
        [typeof(HistoryViewModel)] = () => new HistoryView(),
        [typeof(SettingsViewModel)] = () => new SettingsView(),
        [typeof(TransfersPage)] = () => new TransfersView(),
        [typeof(IncomingRequestViewModel)] = () => new IncomingRequestView(),
        [typeof(ConflictViewModel)] = () => new ConflictView(),
        [typeof(ConfirmViewModel)] = () => new ConfirmView(),
        [typeof(AddDeviceViewModel)] = () => new AddDeviceView(),
        [typeof(WelcomeViewModel)] = () => new WelcomeView(),
        [typeof(SendTextViewModel)] = () => new SendTextView(),
        [typeof(IncomingTextViewModel)] = () => new IncomingTextView(),
        [typeof(UpgradeViewModel)] = () => new UpgradeView(),
    };

    public Control? Build(object? param) => param != null && Views.TryGetValue(param.GetType(), out var create) ? create() : null;

    public bool Match(object? data) => data != null && Views.ContainsKey(data.GetType());
}
