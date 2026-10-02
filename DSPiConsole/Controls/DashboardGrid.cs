using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace DSPiConsole.Controls;

/// <summary>
/// The dashboard's card grid: cards in rows of equal-width columns, each row as
/// tall as its tallest card. <see cref="CardsPerRow"/> fixes the count; 0 fits
/// as many as the width allows at <see cref="MinCardWidth"/> each. Never more
/// columns than cards, so a short list fills the width. After the macOS
/// Console's DashboardOverview grid.
/// </summary>
public sealed class DashboardGrid : Panel
{
    private double _spacing = 16;
    private int _cardsPerRow;
    private double _lastWidth = double.NaN;
    private int _lastColumns;
    private TransitionCollection? _held;

    /// <summary>Gap between cards, across and down.</summary>
    public double Spacing
    {
        get => _spacing;
        set { _spacing = value; InvalidateMeasure(); }
    }

    /// <summary>Cards per row, or 0 for as many as fit.</summary>
    public int CardsPerRow
    {
        get => _cardsPerRow;
        set
        {
            if (_cardsPerRow == value) return;
            _cardsPerRow = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Narrowest a stereo card's two filter lists stay readable side by
    /// side: each row's fixed columns take about 246 px a side here (the macOS
    /// Console's 440 pt suits its narrower rows).</summary>
    public const double MinCardWidth = 500;

    private int Columns(double width)
    {
        int count = Children.Count(c => c.Visibility == Visibility.Visible);
        int wanted = _cardsPerRow > 0
            ? _cardsPerRow
            : double.IsInfinity(width) ? 1 : (int)((width + _spacing) / (MinCardWidth + _spacing));
        return Math.Max(1, Math.Min(wanted, Math.Max(1, count)));
    }

    private void HoldTransitions()
    {
        if (_held != null || ChildrenTransitions is not { Count: > 0 } transitions) return;
        _held = transitions;
        ChildrenTransitions = new TransitionCollection();
        // Back once this layout pass is done.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_held == null) return;
            ChildrenTransitions = _held;
            _held = null;
        });
    }

    private static double ColumnWidth(double width, int columns, double spacing) =>
        Math.Max(0, (width - (columns - 1) * spacing) / columns);

    protected override Size MeasureOverride(Size available)
    {
        double width = available.Width;
        int columns = Columns(width);
        // A resize that keeps the column count still moves every card past the
        // first column, which would replay the reposition slide on each step of
        // the drag. Only a change of column count is worth animating.
        if (!double.IsNaN(_lastWidth) && width != _lastWidth && columns == _lastColumns) HoldTransitions();
        _lastWidth = width;
        _lastColumns = columns;
        double column = double.IsInfinity(width) ? double.PositiveInfinity : ColumnWidth(width, columns, _spacing);
        double height = 0, rowHeight = 0, widest = 0;
        int index = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            child.Measure(new Size(column, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++index % columns == 0)
            {
                height += rowHeight + _spacing;
                rowHeight = 0;
            }
        }
        if (index % columns != 0) height += rowHeight;
        else if (index > 0) height -= _spacing;
        return new Size(double.IsInfinity(width) ? widest : width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int columns = Columns(final.Width);
        double column = ColumnWidth(final.Width, columns, _spacing);
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        double y = 0;
        for (int row = 0; row * columns < visible.Count; row++)
        {
            var cells = visible.Skip(row * columns).Take(columns).ToList();
            // A row lines up at its tallest card.
            double rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (int i = 0; i < cells.Count; i++)
                cells[i].Arrange(new Rect(i * (column + _spacing), y, column, rowHeight));
            y += rowHeight + _spacing;
        }
        foreach (var child in Children)
            if (child.Visibility != Visibility.Visible) child.Arrange(new Rect(0, 0, 0, 0));
        return final;
    }
}
