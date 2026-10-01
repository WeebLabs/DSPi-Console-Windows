using DSPiConsole.Core.GraphEditing;

namespace DSPiConsole.Core.Tests;

/// <summary>The drag coalescer behind ParameterRow and the channel sliders.</summary>
public class ValueDeliveryTests
{
    private sealed class Clock : IPeqScheduler
    {
        private sealed class Timer : IPeqTimer
        {
            public double Due;
            public Action Action = () => { };
            public bool Cancelled;
            public void Cancel() => Cancelled = true;
        }

        private readonly List<Timer> _timers = new();
        public double Now { get; private set; }

        public IPeqTimer Schedule(double seconds, Action action)
        {
            var t = new Timer { Due = Now + seconds, Action = action };
            _timers.Add(t);
            return t;
        }

        public void Advance(double seconds)
        {
            double end = Now + seconds;
            while (_timers.Where(t => !t.Cancelled && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault() is { } next)
            {
                _timers.Remove(next);
                Now = next.Due;
                next.Action();
            }
            Now = end;
        }
    }

    private static (ValueDelivery Delivery, Clock Clock, List<float> Sent) Make()
    {
        var clock = new Clock();
        var sent = new List<float>();
        var d = new ValueDelivery(clock) { OnValue = sent.Add };
        return (d, clock, sent);
    }

    [Fact]
    public void FirstValueGoesAtOnceAndOnlyTheLatestFollows()
    {
        var (d, clock, sent) = Make();
        d.Submit(1);
        d.Submit(2);
        d.Submit(3);
        Assert.Equal(new[] { 1f }, sent);
        clock.Advance(1.0 / 30);
        Assert.Equal(new[] { 1f, 3f }, sent);
        clock.Advance(1);
        Assert.Equal(new[] { 1f, 3f }, sent);
    }

    [Fact]
    public void FinishDropsThePendingValueAndSendsTheLast()
    {
        var (d, clock, sent) = Make();
        d.Submit(1);
        d.Submit(2);
        d.Finish(5);
        clock.Advance(1);
        Assert.Equal(new[] { 1f, 5f }, sent);
    }

    [Fact]
    public void RepeatsOfTheDeliveredValueAreDropped()
    {
        var (d, clock, sent) = Make();
        d.Synchronize(4);
        d.Submit(4);
        clock.Advance(1);
        d.Finish(4);
        Assert.Empty(sent);
        d.Submit(6);
        Assert.Equal(new[] { 6f }, sent);
    }

    [Fact]
    public void AtMostThirtyASecond()
    {
        var (d, clock, sent) = Make();
        for (int i = 0; i < 1000; i++)
        {
            d.Submit(i);
            clock.Advance(0.001);
        }
        Assert.InRange(sent.Count, 29, 32);
        Assert.Equal(0f, sent[0]);
    }
}
