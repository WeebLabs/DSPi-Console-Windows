using DSPiConsole.Models;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog()
    {
        InitializeComponent();

        var settings = AppSettings.Instance;

        GlowToggle.IsOn = settings.ShowGraphGlow;
        LineWidthSlider.Value = settings.GraphLineWidth;
        AnimSpeedSlider.Value = settings.GraphAnimationSpeed;
        DebugToggle.IsOn = settings.ShowDebugInfo;

        LineWidthText.Text = settings.GraphLineWidth.ToString("F1");
        AnimSpeedText.Text = settings.GraphAnimationSpeed.ToString("F2");

        LineWidthSlider.ValueChanged += (s, e) => LineWidthText.Text = e.NewValue.ToString("F1");
        AnimSpeedSlider.ValueChanged += (s, e) => AnimSpeedText.Text = e.NewValue.ToString("F2");

        PrimaryButtonClick += OnSave;
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var settings = AppSettings.Instance;
        settings.ShowGraphGlow = GlowToggle.IsOn;
        settings.GraphLineWidth = LineWidthSlider.Value;
        settings.GraphAnimationSpeed = AnimSpeedSlider.Value;
        settings.ShowDebugInfo = DebugToggle.IsOn;
        settings.Save();
        settings.NotifyChanged();
    }
}
