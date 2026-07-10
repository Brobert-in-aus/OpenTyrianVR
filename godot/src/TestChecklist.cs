using Godot;
using System.Text;

namespace OpenTyrianVR;

/// <summary>
/// A floating in-headset test checklist, parked to the left of the lane so
/// the tester can tick off verification items without taking the headset off.
/// Right stick click (or C) checks the current item and advances; left stick
/// click (or V) moves the cursor without checking.  Session-only state.
/// </summary>
public partial class TestChecklist : Node3D
{
    private static readonly string[] Items =
    {
        "Ground tiles: aligned with tanks/wrecks (no parallax)",
        "Clouds: float above lane, scroll smooth at 120 Hz",
        "Clouds layer OVER sky enemies, UNDER top enemies",
        "HUD / sidebar / menus / text composite intact",
        "Entity motion smooth (35 Hz -> refresh interp)",
        "Hand steering still feels right",
        "Known: INSERT COIN under clouds - acceptable?",
        "Pause menu: frozen terrain/clouds - acceptable?",
        "Perf: render fps holds at headset rate",
    };

    private readonly bool[] _done = new bool[Items.Length];
    private int _cursor;
    private Label3D _label = null!;

    public override void _Ready()
    {
        _label = new Label3D
        {
            Name = "ChecklistText",
            PixelSize = 0.00055f,
            FontSize = 30,
            OutlineSize = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Modulate = new Color(0.85f, 0.9f, 1.0f),
            DoubleSided = false,
        };
        AddChild(_label);
        Refresh();
    }

    /// <summary>Checks/unchecks the cursor item; on check, advances to the
    /// next unchecked item.</summary>
    public void ToggleCurrent()
    {
        _done[_cursor] = !_done[_cursor];
        if (_done[_cursor])
        {
            for (int step = 1; step <= Items.Length; step++)
            {
                int candidate = (_cursor + step) % Items.Length;
                if (!_done[candidate])
                {
                    _cursor = candidate;
                    break;
                }
            }
        }
        Refresh();
    }

    /// <summary>Moves the cursor to the next item without checking.</summary>
    public void MoveCursor()
    {
        _cursor = (_cursor + 1) % Items.Length;
        Refresh();
    }

    private void Refresh()
    {
        var text = new StringBuilder();
        int remaining = 0;
        foreach (bool done in _done)
            if (!done)
                remaining++;

        text.Append($"TEST CHECKLIST  ({Items.Length - remaining}/{Items.Length})\n");
        text.Append("R-stick click: check   L-stick click: skip\n\n");
        for (int i = 0; i < Items.Length; i++)
        {
            text.Append(i == _cursor ? "> " : "  ");
            text.Append(_done[i] ? "[x] " : "[  ] ");
            text.Append(Items[i]);
            text.Append('\n');
        }
        if (remaining == 0)
            text.Append("\nALL DONE - thanks for testing!");
        _label.Text = text.ToString();
    }
}
