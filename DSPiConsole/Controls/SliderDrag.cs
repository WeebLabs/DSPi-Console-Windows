using DSPiConsole.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DSPiConsole.Controls;

/// <summary>
/// Gives a <see cref="Slider"/> the macOS Console's drag delivery. While the
/// pointer holds the slider, values go to <c>live</c> (device only, coalesced
/// to 30 a second with one pending value, see <see cref="ValueDelivery"/>) and
/// the model is left alone; on release the final value is committed once
/// through <c>commit</c>. A keyboard step or any change outside a drag commits
/// at once. Model echoes shown through <see cref="Show"/> are ignored while a
/// drag runs, so a late echo cannot pull the thumb back.
/// </summary>
public sealed class SliderDrag
{
    private readonly Slider _slider;
    private readonly ValueDelivery _delivery;
    private readonly Action<float> _commit;
    private readonly bool _hasLive;
    private bool _dragging;
    private bool _showing;

    /// <summary>Every value the slider takes, drag or not, for a readout.</summary>
    public event Action<float>? Moved;

    /// <param name="snap">Applied to every value before it is shown, sent or
    /// committed, for sliders whose step the control itself does not enforce.</param>
    public SliderDrag(Slider slider, Action<float>? live, Action<float> commit, Func<float, float>? snap = null)
    {
        _slider = slider;
        _commit = commit;
        _hasLive = live != null;
        Snap = snap ?? (v => v);
        _delivery = new ValueDelivery(new DispatcherScheduler(slider.DispatcherQueue)) { OnValue = live };
        // handledEventsToo: the slider's own template handles the press.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnReleased), true);
        slider.ValueChanged += OnValueChanged;
        slider.Unloaded += (_, _) => _delivery.Cancel();
    }

    public Func<float, float> Snap { get; }

    public bool IsDragging => _dragging;

    /// <summary>Shows a model value. Ignored while a drag is in progress.</summary>
    public void Show(double value)
    {
        if (_dragging) return;
        _showing = true;
        try { _slider.Value = value; }
        finally { _showing = false; }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_slider.IsEnabled || !e.GetCurrentPoint(_slider).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _delivery.Synchronize(Snap((float)_slider.Value));
    }

    private void OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_showing) return;
        float v = Snap((float)e.NewValue);
        Moved?.Invoke(v);
        if (_dragging)
        {
            if (_hasLive) _delivery.Submit(v);
        }
        else
        {
            _commit(v);
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        // The release value goes to the device now, replacing any live value
        // still waiting. The commit may send nothing: a model setter skips a
        // value equal to the one it holds, which is what a drag that ends
        // where it began commits, and the device would keep the last live one.
        float final = Snap((float)_slider.Value);
        if (_hasLive) _delivery.Finish(final); else _delivery.Cancel();
        _commit(final);
    }
}
