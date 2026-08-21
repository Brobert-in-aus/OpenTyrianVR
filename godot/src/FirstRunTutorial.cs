using Godot;
using System;

namespace OpenTyrianVR;

/// <summary>A small, non-blocking first-run coach. It teaches one control at
/// a time in the first playable level and persists completion in user://.</summary>
public partial class FirstRunTutorial : Node3D
{
    private enum Step { Recenter, Move, MainWeapon, SecondaryWeapon, Complete }

    private const string CompletionPath = "user://first_run_tutorial_v1.complete";
    private Step _step;
    private Label3D _label = null!;
    private bool _started;
    private bool _lastMenu;
    private bool _lastCancel;
    private bool _haveHandOrigin;
    private Vector3 _handOrigin;
    private double _successRemaining;

    public bool Active => _started && _step != Step.Complete;
    public bool NeedsHandGuide => Active;
    public bool ConsumeMenuThisFrame { get; private set; }
    public bool ConsumeCancelThisFrame { get; private set; }

    public override void _Ready()
    {
        var backing = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.015f, 0.025f, 0.06f, 0.92f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            RenderPriority = 118,
        };
        AddChild(new MeshInstance3D
        {
            Name = "Backing",
            Mesh = new QuadMesh { Size = new Vector2(0.70f, 0.20f) },
            Position = new Vector3(0f, 0f, -0.002f),
            MaterialOverride = backing,
        });
        _label = new Label3D
        {
            Name = "TutorialText",
            PixelSize = 0.00065f,
            FontSize = 34,
            OutlineSize = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Modulate = new Color(0.88f, 0.94f, 1f),
            DoubleSided = true,
            NoDepthTest = true,
            RenderPriority = 119,
        };
        AddChild(_label);
        Visible = false;
    }

    public void Update(double delta, bool xrActive, bool inGameplay, bool tracking,
                       bool menuPressed, bool cancelPressed, Vector3 handLocal,
                       bool mainWeaponPressed, bool secondaryWeaponPressed)
    {
        ConsumeMenuThisFrame = false;
        ConsumeCancelThisFrame = false;

        bool force = System.Environment.GetEnvironmentVariable("OTYR_TUTORIAL") == "1";
        bool disabled = System.Environment.GetEnvironmentVariable("OTYR_TUTORIAL") == "0";
        if (!_started && xrActive && inGameplay && !disabled &&
            (force || !Godot.FileAccess.FileExists(CompletionPath)))
        {
            _started = true;
            _step = Step.Recenter;
            Refresh(tracking);
            GD.Print("OpenTyrianVR: first-run controls tutorial started");
        }

        if (!_started)
            return;

        bool menuEdge = menuPressed && !_lastMenu;
        bool cancelEdge = cancelPressed && !_lastCancel;
        _lastMenu = menuPressed;
        _lastCancel = cancelPressed;

        if (_step == Step.Complete)
        {
            _successRemaining -= delta;
            Visible = _successRemaining > 0 && inGameplay;
            return;
        }

        Visible = inGameplay;
        if (!inGameplay)
            return;

        if (cancelEdge)
        {
            ConsumeCancelThisFrame = true;
            Finish(skipped: true);
            return;
        }

        switch (_step)
        {
            case Step.Recenter when menuEdge:
                ConsumeMenuThisFrame = true;
                Advance(Step.Move, tracking);
                break;
            case Step.Move:
                if (!_haveHandOrigin && tracking)
                {
                    _handOrigin = handLocal;
                    _haveHandOrigin = true;
                }
                else if (tracking && handLocal.DistanceTo(_handOrigin) >= 0.035f)
                {
                    Advance(Step.MainWeapon, tracking);
                }
                break;
            case Step.MainWeapon when mainWeaponPressed:
                Advance(Step.SecondaryWeapon, tracking);
                break;
            case Step.SecondaryWeapon when secondaryWeaponPressed:
                Finish(skipped: false);
                break;
        }

        Refresh(tracking);
    }

    private void Advance(Step next, bool tracking)
    {
        _step = next;
        _haveHandOrigin = false;
        Refresh(tracking);
        GD.Print($"OpenTyrianVR: tutorial -> {_step}");
    }

    private void Finish(bool skipped)
    {
        _step = Step.Complete;
        _successRemaining = skipped ? 0 : 2.0;
        _label.Text = "YOU'RE READY\nThe steering guide will fade as you play.";
        Visible = !skipped;
        using var file = Godot.FileAccess.Open(CompletionPath, Godot.FileAccess.ModeFlags.Write);
        if (file == null)
            GD.PushWarning($"OpenTyrianVR: could not persist tutorial completion ({Godot.FileAccess.GetOpenError()})");
        else
            file.StoreString(skipped ? "skipped\n" : "complete\n");
        GD.Print($"OpenTyrianVR: first-run tutorial {(skipped ? "skipped" : "complete")}");
    }

    private void Refresh(bool tracking)
    {
        if (!tracking && _step == Step.Move)
        {
            _label.Text = "WAKE YOUR LEFT CONTROLLER\nThe tutorial will continue when tracking returns.\n\nB  Skip tutorial";
            return;
        }
        _label.Text = _step switch
        {
            Step.Recenter => "1 / 4   RECENTER YOUR VIEW\nFace the board, then press ☰ on the left controller.\n\nB  Skip tutorial",
            Step.Move => "2 / 4   MOVE THE SHIP\nMove your left controller inside the blue guide.\nThumbsticks also steer.\n\nB  Skip tutorial",
            Step.MainWeapon => "3 / 4   FIRE MAIN WEAPON\nSqueeze either trigger.\n\nB  Skip tutorial",
            Step.SecondaryWeapon => "4 / 4   FIRE SECONDARY WEAPONS\nSqueeze either grip to fire that sidekick.\n\nB  Skip tutorial",
            _ => _label.Text,
        };
    }
}
