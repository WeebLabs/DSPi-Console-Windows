using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Control › Auxiliary Outputs (firmware caps v18): GPIOs the DSPi switches or
/// dims for something it knows nothing about, such as an amplifier trigger or a
/// panel lamp, driven by controls on the Control Surfaces page. Renders the Aux
/// section of <see cref="ControlSurfacesPanel"/>; see
/// <see cref="ControlSurfacesPage"/> for why these pages aren't SettingsModules.
/// </summary>
public sealed class ControlAuxPage : ISettingsPage
{
    public string Id => "control.aux";
    public string Title => "Auxiliary Outputs";
    public SettingsCategory Category => SettingsCategory.Control;
    public string IconGlyph => "\uE7E8"; // Power
    public int Order => 50;
    public bool IsAvailable(MainViewModel vm) => vm.ControlSurfacesSupported && vm.CsAuxSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
        => new ControlSurfacesPanel(vm, CsSection.Aux);
}
