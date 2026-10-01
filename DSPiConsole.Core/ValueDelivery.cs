using DSPiConsole.Core.GraphEditing;

namespace DSPiConsole.Core;

/// <summary>
/// Coalesces a drag's value stream for the device: at most one value per
/// <c>interval</c> (30 a second by default), keeping one pending value rather
/// than a backlog, so a fast drag cannot queue a pile of writes on the bus. The
/// first value goes at once; later ones wait for the cooldown, and only the
/// latest survives. A value equal to the last one delivered is dropped. Port of
/// the macOS Console's SliderValueDelivery.
/// </summary>
public sealed class ValueDelivery
{
    private readonly IPeqScheduler _scheduler;
    private readonly double _interval;
    private IPeqTimer? _timer;
    private float? _pending;
    private float? _lastDelivered;

    public ValueDelivery(IPeqScheduler scheduler, double interval = 1.0 / 30.0)
    {
        _scheduler = scheduler;
        _interval = interval;
    }

    /// <summary>Receives each delivered value.</summary>
    public Action<float>? OnValue { get; set; }

    /// <summary>Forget anything pending and treat <paramref name="value"/> as
    /// already delivered, as at the start of a drag.</summary>
    public void Synchronize(float value)
    {
        Cancel();
        _lastDelivered = value;
    }

    /// <summary>A new value from the drag: delivered now if the cooldown has
    /// passed, else held as the one pending value.</summary>
    public void Submit(float value)
    {
        _pending = value;
        if (_timer != null) return;
        DeliverPending();
    }

    /// <summary>The drag's final value: anything pending is dropped and this is
    /// delivered at once.</summary>
    public void Finish(float value)
    {
        Cancel();
        Deliver(value);
    }

    public void Cancel()
    {
        _timer?.Cancel();
        _timer = null;
        _pending = null;
    }

    private void DeliverPending()
    {
        _timer = null;
        if (_pending is not { } value) return;
        _pending = null;
        // The cooldown goes in before the callback, which may rebuild the view.
        _timer = _scheduler.Schedule(_interval, DeliverPending);
        Deliver(value);
    }

    private void Deliver(float value)
    {
        if (_lastDelivered == value) return;
        _lastDelivered = value;
        OnValue?.Invoke(value);
    }
}
