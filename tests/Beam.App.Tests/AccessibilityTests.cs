using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Beam.App.ViewModels;

namespace Beam.App.Tests;

public class AccessibilityTests
{
    [AvaloniaFact]
    public async Task IconOnlyButtonsAndCodesHaveANameForScreenReaders()
    {
        await using var app = new UiHarness("Laptop");
        await app.InitializeAsync();
        var checkedButtons = 0;
        var checkedCodes = 0;
        foreach (var page in new[] { Page.Home, Page.Receive, Page.Phone })
        {
            app.ViewModel.Navigate(page);
            await UiHarness.PumpAsync(100);
            foreach (var button in app.Window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("icon") && b.IsEffectivelyVisible))
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"an icon button on {page} has no name");
                checkedButtons++;
            }

            foreach (var code in app.Window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Width >= 200 && p.IsEffectivelyVisible))
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(code)), $"the code on {page} has no name");
                checkedCodes++;
            }
        }

        Assert.True(checkedButtons > 0 && checkedCodes > 0, $"checked {checkedButtons} buttons and {checkedCodes} codes");
    }
}
