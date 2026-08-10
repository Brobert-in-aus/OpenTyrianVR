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
        "FULL WIDTH: ship reaches both cropped edges at target x about 16/280",
        "EDGE CROP: enemies scroll cleanly across hard playfield edges; no fade",
        "GROUND SCROLL: terrain below floating platforms glides without judder",
        "COMPOSITES: fast stacks and the small level-1 boss stay welded",
        "CLOUDS: translucent and always below level-1 aerial platforms",
        "LAYERING: ground/platform objects and flyers occupy the right planes",
        "TANK BOSS: linked body/turret stack stays together at correct offsets",
        "E2-E4: no Episode-1 height leakage; classified objects use right planes",
        "CAST SHADOWS: flyers/entities offset by height; clouds/platforms shade ground",
        "SHADOW LAYERS: shadows land on the intended surface without floating over holes",
        "EFFECTS: storm/flip stay 3D; lava/blur/searchlight switch cleanly to flat",
        "LIFECYCLE UI: HUD, pause, death, end-level and story screens stay complete/readable",
        "90 HZ: motion stays smooth without recurring judder",
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
        // The log is the durable record: headset screenshots crop the panel
        // and the state is session-only.
        GD.Print($"OpenTyrianVR: checklist {(_done[_cursor] ? "PASS" : "unchecked")}: {Items[_cursor]}");
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

        text.Append($"QUEST REGRESSION GATE  ({Items.Length - remaining}/{Items.Length})\n");
        text.Append("R-stick click: PASS   L-stick click: leave failed and advance\n");
        text.Append("Unchecked items are FAILS; report level + green frame number\n\n");
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
