using System.Diagnostics;
using DSPiConsole.Core.GraphEditing;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace DSPiConsole.Controls;

/// <summary>One-shot timers on a UI thread, for the Core classes that take an
/// <see cref="IPeqScheduler"/> (the graph editor, the slider coalescer).</summary>
public sealed class DispatcherScheduler : IPeqScheduler
{
    private readonly DispatcherQueue _queue;

    public DispatcherScheduler(DispatcherQueue queue) => _queue = queue;

    public double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public IPeqTimer Schedule(double seconds, Action action)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(Math.Max(seconds, 0.001));
        timer.IsRepeating = false;
        var handle = new Handle(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!handle.Cancelled) action();
        };
        timer.Start();
        return handle;
    }

    private sealed class Handle : IPeqTimer
    {
        private readonly DispatcherQueueTimer _timer;
        public bool Cancelled { get; private set; }
        public Handle(DispatcherQueueTimer timer) => _timer = timer;
        public void Cancel()
        {
            Cancelled = true;
            _timer.Stop();
        }
    }
}
