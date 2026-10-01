namespace DSPiConsole.Core.GraphEditing;

/// <summary>
/// Graph band selection and hover, shared between the response graph (main and
/// pop-out) and the band list. Raises its events only when the set or the
/// hovered band actually changes, never per mouse movement, so the rows that
/// observe it redraw on a click or when the pointer crosses onto another dot.
/// Port of the macOS Console's PeqGraphSelection.
/// </summary>
public sealed class PeqGraphSelection
{
    private HashSet<int> _selected = new();
    private int? _graphHovered;
    private int? _listHovered;

    /// <summary>The row a Shift-click in the list extends from.</summary>
    private int? _listAnchor;

    public IReadOnlySet<int> Selected => _selected;

    /// <summary>The band under the pointer on the graph; its row lights up.</summary>
    public int? GraphHovered
    {
        get => _graphHovered;
        set { if (_graphHovered == value) return; _graphHovered = value; HoverChanged?.Invoke(); }
    }

    /// <summary>The row under the pointer in the list; its dot and lobe light up.</summary>
    public int? ListHovered
    {
        get => _listHovered;
        set { if (_listHovered == value) return; _listHovered = value; HoverChanged?.Invoke(); }
    }

    /// <summary>The selection the list itself last made. The list follower
    /// leaves it alone: its rows were clicked where they sit, and a Shift range
    /// reaching past the top of the view must not scroll the click away.</summary>
    public IReadOnlySet<int>? MadeByList { get; private set; }

    public event Action? SelectionChanged;
    public event Action? HoverChanged;

    /// <summary>A band the pointer has rested on in the graph; the list scrolls
    /// its row into view. An event, not state, so the same band can ask again.</summary>
    public event Action<int>? RevealRow;

    public void SetSelected(IEnumerable<int> bands)
    {
        var next = new HashSet<int>(bands);
        if (next.SetEquals(_selected)) return;
        _selected = next;
        MadeByList = null;
        SelectionChanged?.Invoke();
    }

    public void RequestReveal(int band) => RevealRow?.Invoke(band);

    public void Reset()
    {
        SetSelected(Array.Empty<int>());
        GraphHovered = null;
        ListHovered = null;
        _listAnchor = null;
    }

    /// <summary>
    /// A click on a band's number in the list, with the modifiers of a click on
    /// its dot: Ctrl toggles the band, Shift takes the run of rows from the last
    /// one clicked (in row order, as the list shows them, where the graph uses
    /// frequency order), and a plain click selects it alone. <paramref name="isActive"/>
    /// says which rows hold a band; empty rows are skipped.
    /// </summary>
    public void ListClick(int band, bool ctrl, bool shift, Func<int, bool> isActive)
    {
        HashSet<int> next;
        int? from = _listAnchor is { } a && _selected.Contains(a) ? a
                  : _selected.Count == 1 ? _selected.First() : null;
        if (ctrl)
        {
            next = new HashSet<int>(_selected);
            if (!next.Remove(band)) next.Add(band);
            _listAnchor = band;
        }
        else if (shift && from is { } f)
        {
            // The anchor stays put, so a second Shift-click reshapes the run.
            next = new HashSet<int>(Enumerable.Range(Math.Min(f, band), Math.Abs(f - band) + 1).Where(isActive));
            _listAnchor = f;
        }
        else
        {
            next = new HashSet<int> { band };
            _listAnchor = band;
        }
        bool changed = !next.SetEquals(_selected);
        _selected = next;
        MadeByList = next;
        if (changed) SelectionChanged?.Invoke();
    }
}
