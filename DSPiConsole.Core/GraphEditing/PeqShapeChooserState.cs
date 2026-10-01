using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.GraphEditing;

/// <summary>
/// The two-step shape choice behind the chip's shape page and the Ctrl-click
/// card: a grid of shapes, then, for a shape with two orders, its two slopes
/// (or all-pass phases). The last click reports shape and order together;
/// nothing is reported before it. Port of the macOS PeqShapeChooser's logic,
/// without its buttons.
/// </summary>
public sealed class PeqShapeChooserState
{
    private IReadOnlySet<FilterType> _available = new HashSet<FilterType>();

    /// <summary>The shapes offered: those with at least one order the firmware supports.</summary>
    public IReadOnlyList<PeqShape> Shapes { get; private set; } = Array.Empty<PeqShape>();

    /// <summary>The shape picked in step one, awaiting its order in step two.</summary>
    public PeqShape? PendingShape { get; private set; }

    /// <summary>The shape under the pointer, named in the header.</summary>
    public PeqShape? HoveredShape { get; set; }

    /// <summary>The band's own shape and order, marked in its colour; null for
    /// the card, whose band does not exist yet.</summary>
    public (PeqShape Shape, int Order)? Own { get; set; }

    /// <summary>Offers the shapes the firmware supports, from the first step.</summary>
    public void Configure(IReadOnlySet<FilterType> available)
    {
        _available = available;
        Shapes = PeqShapes.All.Where(s => PeqShapes.DefaultOrder(s, available) != null).ToList();
        Reset();
    }

    /// <summary>Back to the first step.</summary>
    public void Reset()
    {
        PendingShape = null;
        HoveredShape = null;
    }

    /// <summary>The header: the picked shape in step two, else the one under
    /// the pointer, else the band's own, else "Add Band".</summary>
    public string Title => (PendingShape ?? HoveredShape ?? Own?.Shape)?.Title() ?? "Add Band";

    /// <summary>Step one. A shape with one order is chosen now (returned); one
    /// with two goes on to step two (null).</summary>
    public (PeqShape Shape, int Order)? PickShape(PeqShape shape)
    {
        var orders = PeqShapes.Orders(shape, _available);
        if (orders.Length == 2)
        {
            PendingShape = shape;
            HoveredShape = null;
            return null;
        }
        return orders.Length == 1 ? (shape, orders[0]) : null;
    }

    /// <summary>Step two: the order completes the choice.</summary>
    public (PeqShape Shape, int Order)? PickOrder(int order) =>
        PendingShape is { } shape ? (shape, order) : null;

    /// <summary>The back arrow. True when it left the chooser (step one), false
    /// when it only returned from the slopes to the shapes.</summary>
    public bool Back()
    {
        if (PendingShape == null) return true;
        PendingShape = null;
        return false;
    }

    /// <summary>Whether an order button in step two is marked: only for the
    /// band's own shape, on its own order.</summary>
    public bool IsOrderMarked(int order) =>
        PendingShape is { } p && Own is { } own && own.Shape == p && own.Order == order;
}
