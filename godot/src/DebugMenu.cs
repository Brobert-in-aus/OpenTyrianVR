using Godot;
using System;
using System.Text;

namespace OpenTyrianVR;

/// <summary>Host-side headset debug panel. It never enters release gameplay
/// menus: Main suspends the hosted tick while this panel is open and sends
/// explicit, ABI-gated debug commands when an action is chosen.</summary>
public partial class DebugMenu : Node3D
{
    public enum Command { None, Warp, KillHostiles, SkipLevel, Close }
    public readonly record struct LevelTarget(ushort Section, string Name);

    private static readonly LevelTarget[][] Levels =
    {
        Array.Empty<LevelTarget>(),
        new[] {
            new LevelTarget(4,"TYRIAN"), new LevelTarget(6,"ASTEROID1"), new LevelTarget(7,"ASTEROID2"),
            new LevelTarget(11,"SAVARA"), new LevelTarget(14,"MINES"), new LevelTarget(17,"BUBBLES"),
            new LevelTarget(20,"DELIANI"), new LevelTarget(22,"ASTEROID?"), new LevelTarget(24,"MINEMAZE"),
            new LevelTarget(26,"BONUS"), new LevelTarget(29,"HOLES"), new LevelTarget(30,"SAVARA2"),
            new LevelTarget(32,"SOH JIN"), new LevelTarget(34,"WINDY"), new LevelTarget(37,"ASSASSIN"),
            new LevelTarget(39,"SAVARA V"), new LevelTarget(42,"** ALE **"),
        },
        new[] {
            new LevelTarget(3,"TORM"), new LevelTarget(5,"GYGES"), new LevelTarget(7,"BONUS"),
            new LevelTarget(9,"ASTCITY"), new LevelTarget(11,"SOH JIN"), new LevelTarget(13,"GRYPHON"),
            new LevelTarget(16,"GEM WAR"), new LevelTarget(18,"MARKERS"), new LevelTarget(20,"MISTAKES"),
            new LevelTarget(22,"BOTANY A"), new LevelTarget(23,"BOTANY B"), new LevelTarget(25,"BONUS"),
        },
        new[] {
            new LevelTarget(3,"GAUNTLET"), new LevelTarget(5,"IXMUCANE"), new LevelTarget(7,"BONUS"),
            new LevelTarget(9,"STARGATE"), new LevelTarget(11,"CAMANIS"), new LevelTarget(13,"FLEET"),
            new LevelTarget(16,"AST. CITY"), new LevelTarget(18,"TYRIAN X"), new LevelTarget(20,"SAWBLADES"),
            new LevelTarget(22,"SAVARA Y"), new LevelTarget(24,"NEW DELI"), new LevelTarget(26,"MACES"),
        },
        new[] {
            new LevelTarget(3,"SURFACE"), new LevelTarget(5,"LAVA RUN"), new LevelTarget(7,"CORE"),
            new LevelTarget(9,"?TUNNEL?"), new LevelTarget(11,"ICE EXIT"), new LevelTarget(13,"HARVEST"),
            new LevelTarget(15,"UNDERDELI"), new LevelTarget(17,"SAVARA IV"), new LevelTarget(19,"DREAD-NOT"),
            new LevelTarget(21,"EYESPY"), new LevelTarget(23,"BRAINIAC"), new LevelTarget(26,"NOSE DRIP"),
            new LevelTarget(28,"WINDY"), new LevelTarget(30,"DESERTRUN"), new LevelTarget(32,"LAVA EXIT"),
            new LevelTarget(37,"SIDE EXIT"), new LevelTarget(41,"TIME WAR"), new LevelTarget(46,"SQUADRON"),
            new LevelTarget(48,"APPROACH"), new LevelTarget(51,"ICESECRET"),
        },
    };

    private const int RowInvulnerable = 0;
    private const int RowEpisode = 1;
    private const int RowLevel = 2;
    private const int RowWarp = 3;
    private const int RowKill = 4;
    private const int RowSkip = 5;
    private const int RowClose = 6;
    private const int RowCount = 7;

    private Label3D _label = null!;
    private int _row;
    private int _levelIndex;
    public bool IsOpen => Visible;
    public bool Invulnerable { get; private set; }
    public byte Episode { get; private set; } = 1;
    public LevelTarget Target => Levels[Episode][_levelIndex];

    public override void _Ready()
    {
        var backingMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.005f, 0.008f, 0.018f, 0.94f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 110,
        };
        AddChild(new MeshInstance3D
        {
            Name = "Backing",
            Mesh = new QuadMesh { Size = new Vector2(0.76f, 0.58f) },
            Position = new Vector3(0f, 0f, -0.002f),
            MaterialOverride = backingMaterial,
        });
        _label = new Label3D
        {
            Name = "DebugText",
            PixelSize = 0.0007f,
            FontSize = 34,
            OutlineSize = 9,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Position = new Vector3(-0.34f, 0.25f, 0f),
            DoubleSided = true,
            RenderPriority = 111,
        };
        AddChild(_label);
        Visible = false;
        Refresh();
    }

    public void Open(byte currentEpisode, bool invulnerable)
    {
        Episode = (byte)Math.Clamp(currentEpisode == 0 ? 1 : currentEpisode, 1, 4);
        Invulnerable = invulnerable;
        _row = 0;
        _levelIndex = Math.Clamp(_levelIndex, 0, Levels[Episode].Length - 1);
        Visible = true;
        Refresh();
    }

    public void Close() => Visible = false;

    public void Move(int direction)
    {
        _row = ((_row + direction) % RowCount + RowCount) % RowCount;
        Refresh();
    }

    public void Adjust(int direction)
    {
        if (_row == RowInvulnerable)
            Invulnerable = !Invulnerable;
        else if (_row == RowEpisode)
        {
            Episode = (byte)(((Episode - 1 + direction) % 4 + 4) % 4 + 1);
            _levelIndex = 0;
        }
        else if (_row == RowLevel)
        {
            int count = Levels[Episode].Length;
            _levelIndex = ((_levelIndex + direction) % count + count) % count;
        }
        Refresh();
    }

    public Command Activate()
    {
        Command command = _row switch
        {
            RowInvulnerable => Command.None,
            RowWarp => Command.Warp,
            RowKill => Command.KillHostiles,
            RowSkip => Command.SkipLevel,
            RowClose => Command.Close,
            _ => Command.None,
        };
        if (_row == RowInvulnerable)
            Adjust(1);
        return command;
    }

    private void Refresh()
    {
        string Mark(int row) => row == _row ? "> " : "  ";
        var target = Target;
        var s = new StringBuilder();
        s.Append("DEBUG / LEVEL WARP\n");
        s.Append("Both stick-clicks or F1: open/close\n");
        s.Append("Stick: select/change   A: activate   B: close\n\n");
        s.Append($"{Mark(RowInvulnerable)}Invulnerability: {(Invulnerable ? "ON" : "OFF")}\n");
        s.Append($"{Mark(RowEpisode)}Episode: {Episode}\n");
        s.Append($"{Mark(RowLevel)}Level: {_levelIndex + 1}/{Levels[Episode].Length}  {target.Name}  [section {target.Section}]\n");
        s.Append($"{Mark(RowWarp)}WARP TO SELECTED LEVEL\n");
        s.Append($"{Mark(RowKill)}KILL CURRENT HOSTILES\n");
        s.Append($"{Mark(RowSkip)}SKIP CURRENT LEVEL\n");
        s.Append($"{Mark(RowClose)}CLOSE\n");
        _label.Text = s.ToString();
    }
}
