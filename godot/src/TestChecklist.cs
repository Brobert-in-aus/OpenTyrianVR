using Godot;
using System.Text;

namespace OpenTyrianVR;

/// <summary>
/// A floating in-headset test checklist, parked to the left of the lane so
/// the tester can tick off verification items without taking the headset off.
/// Right stick click (or C) cycles pass/fail/unchecked; left stick click
/// (or V) moves the cursor. Session-only state.
/// </summary>
public partial class TestChecklist : Node3D
{
    private static readonly string[] Items =
    {
        "FULL WIDTH: terrain ends at 0/264; ship reaches x about 16/280 with overhang visible",
        "EDGE CROP: enemies scroll cleanly across hard playfield edges; no fade",
        "GROUND SCROLL: terrain below floating platforms glides without judder",
        "COMPOSITES: fast stacks and the small level-1 boss stay welded",
        "CLOUDS: translucent and always below level-1 aerial platforms",
        "LAYERING: ground/platform objects and flyers occupy the right planes",
        "TANK BOSS: linked body/turret stack stays together at correct offsets",
        "E2-E4: no Episode-1 height leakage; classified objects use right planes",
        "CAST SHADOWS: flyers/entities offset by height; clouds/platforms shade ground",
        "SHADOW LAYERS: shadows land on the intended surface without floating over holes",
        "EFFECTS: storm/flip/lava/iced/blur/searchlight stay stereo-correct 3D",
        "LIFECYCLE UI: HUD, pause, death, end-level and story screens stay complete/readable",
        "AUDIO: music and sound effects play at the headset volume",
        "90 HZ: motion stays smooth without recurring judder",
    };

    private enum Result : byte { Unchecked, Pass, Fail }
    private readonly Result[] _results = new Result[Items.Length];
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

    /// <summary>Cycles the current item through unchecked -> pass -> fail ->
    /// unchecked. Cursor movement is separate so a repeated press edits the
    /// same result predictably.</summary>
    public void ToggleCurrent()
    {
        _results[_cursor] = _results[_cursor] switch
        {
            Result.Unchecked => Result.Pass,
            Result.Pass => Result.Fail,
            _ => Result.Unchecked,
        };
        // The log is the durable record: headset screenshots crop the panel
        // and the state is session-only.
        GD.Print($"OpenTyrianVR: checklist {_results[_cursor].ToString().ToUpperInvariant()}: {Items[_cursor]}");
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
        int passed = 0, failed = 0, pending = 0;
        foreach (Result result in _results)
            if (result == Result.Pass) passed++;
            else if (result == Result.Fail) failed++;
            else pending++;

        text.Append($"QUEST REGRESSION  PASS {passed}  FAIL {failed}  UNCHECKED {pending}\n");
        text.Append("R-stick: cycle PASS / FAIL / UNCHECKED   L-stick: next item\n");
        text.Append("For failures, report the level and green frame number\n\n");
        for (int i = 0; i < Items.Length; i++)
        {
            text.Append(i == _cursor ? "> " : "  ");
            text.Append(_results[i] switch
            {
                Result.Pass => "[x] ",
                Result.Fail => "[!] ",
                _ => "[ ] ",
            });
            text.Append(Items[i]);
            text.Append('\n');
        }
        if (pending == 0)
            text.Append(failed == 0
                ? "\nALL TESTED - ALL PASS"
                : $"\nALL TESTED - {failed} FAILURE{(failed == 1 ? "" : "S")}");
        _label.Text = text.ToString();
    }
}
