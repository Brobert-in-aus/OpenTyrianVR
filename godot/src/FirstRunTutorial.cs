using Godot;
using System;
using System.Collections.Generic;

namespace OpenTyrianVR;

/// <summary>First-run onboarding that runs before the native game session.
/// It owns a frozen practice scene, controller-ray menu, demonstrated control
/// steps, and a retry-until-collected pickup.</summary>
public partial class FirstRunTutorial : Node3D
{
    public enum Result { Continue, LaunchGame }
    private enum Step { Intro, Corners, MainWeapon, SecondaryWeapon, Pickup, Complete }
    private const string CompletionPath = "user://first_run_tutorial_v5.complete";
    private const float TriggerThreshold = 0.55f, GripThreshold = 0.60f;

    private sealed class RayButton
    {
        public required Vector2 Center, Size;
        public required Label3D Label;
        public required StandardMaterial3D Material;
    }

    private Step _step;
    private Node3D _introPanel = null!, _popup = null!, _practiceBoard = null!;
    private Label3D _introBody = null!, _popupText = null!;
    private RayButton _startButton = null!, _skipButton = null!;
    private MeshInstance3D _ship = null!, _target = null!, _pickup = null!, _laserVisual = null!;
    private ImmediateMesh _laserMesh = null!;
    private readonly List<(MeshInstance3D Mesh, Vector2 Velocity, double Life)> _shots = new();
    private byte _corners;
    private bool _lastLeftTrigger, _lastRightTrigger, _lastGrip, _lastMenu, _launchPending;
    private double _completeDelay;
    private int _pickupAttempt;
    private Vector2 _playerPosition, _pickupPosition, _playerSim, _targetSim;
    private int _targetRampX, _targetRampY;
    private double _pursuitAccumulator;

    public bool NeedsHandGuide => _step is Step.Corners or Step.MainWeapon or
        Step.SecondaryWeapon or Step.Pickup;

    public override void _Ready()
    {
        BuildIntro();
        BuildPracticeScene();
        BuildLasers();
        Visible = false;
    }

    /// <returns>True when startup must wait for onboarding.</returns>
    public bool Begin(bool xrActive)
    {
        bool force = System.Environment.GetEnvironmentVariable("OTYR_TUTORIAL") == "1";
        bool disabled = System.Environment.GetEnvironmentVariable("OTYR_TUTORIAL") == "0";
        if (!xrActive || disabled || (!force && Godot.FileAccess.FileExists(CompletionPath)))
            return false;
        _step = Step.Intro;
        Visible = true;
        _introPanel.Visible = true;
        _popup.Visible = false;
        _practiceBoard.Visible = false;
        _laserVisual.Visible = true;
        GD.Print("OpenTyrianVR: pre-game first-run tutorial menu opened");
        return true;
    }

    public Result UpdatePreGame(double delta,
        XRController3D? left, bool leftTracking, XRController3D? right, bool rightTracking,
        Vector2 handNormalized, bool menuPressed)
    {
        bool leftTrigger = leftTracking && left != null && left.GetFloat("trigger") > TriggerThreshold;
        bool rightTrigger = rightTracking && right != null && right.GetFloat("trigger") > TriggerThreshold;
        bool grip = (leftTracking && left != null && left.GetFloat("grip") > GripThreshold) ||
                    (rightTracking && right != null && right.GetFloat("grip") > GripThreshold);
        bool leftTriggerEdge = leftTrigger && !_lastLeftTrigger;
        bool rightTriggerEdge = rightTrigger && !_lastRightTrigger;
        bool triggerEdge = leftTriggerEdge || rightTriggerEdge;
        bool gripEdge = grip && !_lastGrip;
        bool menuEdge = menuPressed && !_lastMenu;
        _lastLeftTrigger = leftTrigger;
        _lastRightTrigger = rightTrigger;
        _lastGrip = grip;
        _lastMenu = menuPressed;

        if (_step == Step.Intro)
        {
            UpdateLasers(left, leftTracking, right, rightTracking,
                         out RayButton? leftHovered, out RayButton? rightHovered);
            SetHover(_startButton, leftHovered == _startButton || rightHovered == _startButton);
            SetHover(_skipButton, leftHovered == _skipButton || rightHovered == _skipButton);
            if (menuEdge)
                _introBody.Text = IntroCopy("View recentered. Point at a button and squeeze Trigger.");
            RayButton? selected = leftTriggerEdge ? leftHovered :
                                  rightTriggerEdge ? rightHovered : null;
            if (selected == _startButton) StartPractice();
            else if (selected == _skipButton) Finish(skipped: true);
        }
        else
        {
            _laserVisual.Visible = false;
            UpdatePractice(delta, handNormalized, leftTracking, triggerEdge, gripEdge);
        }

        if (_launchPending && (_completeDelay -= delta) <= 0)
        {
            _launchPending = false;
            return Result.LaunchGame;
        }
        return Result.Continue;
    }

    private void BuildIntro()
    {
        _introPanel = new Node3D
        {
            Name = "FirstRunMenu", Position = new Vector3(0f, 1.08f, -0.65f),
            RotationDegrees = new Vector3(-4f, 0f, 0f),
        };
        AddChild(_introPanel);
        _introPanel.AddChild(PanelQuad("Backing", new Vector2(0.82f, 0.50f),
            new Color(0.012f, 0.022f, 0.055f, 0.97f), -0.004f, 110));
        var title = MakeLabel("FIRST FLIGHT TRAINING", 42, new Vector3(0f, 0.175f, 0f));
        title.Modulate = new Color(0.35f, 0.78f, 1f);
        _introPanel.AddChild(title);
        _introBody = MakeLabel(IntroCopy(""), 25, new Vector3(0f, 0.035f, 0f));
        _introPanel.AddChild(_introBody);
        _startButton = BuildButton("START TUTORIAL", new Vector2(-0.19f, -0.165f),
            new Color(0.08f, 0.38f, 0.64f, 1f));
        _skipButton = BuildButton("SKIP", new Vector2(0.19f, -0.165f),
            new Color(0.16f, 0.19f, 0.25f, 1f));
    }

    private static string IntroCopy(string status) =>
        "Learn hand steering, main and secondary fire,\nand collecting items in a safe practice level.\n\n" +
        "If the view is off-centre: face forward, then\npress the LEFT controller Menu button to recenter.\n\n" +
        (status.Length == 0 ? "Point either controller and squeeze Trigger to choose." : status);

    private RayButton BuildButton(string text, Vector2 center, Color color)
    {
        var material = UiMaterial(color, 114);
        _introPanel.AddChild(new MeshInstance3D
        {
            Name = text.Replace(" ", ""), Mesh = new QuadMesh { Size = new Vector2(0.30f, 0.075f) },
            Position = new Vector3(center.X, center.Y, -0.001f), MaterialOverride = material,
        });
        var label = MakeLabel(text, 26, new Vector3(center.X, center.Y, 0.001f));
        _introPanel.AddChild(label);
        return new RayButton { Center = center, Size = new Vector2(0.30f, 0.075f), Label = label, Material = material };
    }

    private void BuildPracticeScene()
    {
        _popup = new Node3D
        {
            Name = "TutorialPopup", Position = new Vector3(0f, 1.28f, -0.52f),
            RotationDegrees = new Vector3(-10f, 0f, 0f),
        };
        AddChild(_popup);
        _popup.AddChild(PanelQuad("PopupBacking", new Vector2(0.72f, 0.19f),
            new Color(0.012f, 0.025f, 0.065f, 0.96f), -0.003f, 116));
        _popupText = MakeLabel("", 28, Vector3.Zero);
        _popupText.RenderPriority = 117;
        _popup.AddChild(_popupText);

        _practiceBoard = new Node3D
        {
            Name = "TutorialLevel", Position = new Vector3(0f, 1.05f, -0.90f),
            RotationDegrees = new Vector3(-42f, 0f, 0f),
        };
        AddChild(_practiceBoard);
        var terrain = PanelQuad("PracticeSpace", new Vector2(0.66f, 0.42f),
            Colors.White, -0.010f, 104);
        terrain.MaterialOverride = SpriteMaterial(TutorialAssets.Terrain(), 104);
        _practiceBoard.AddChild(terrain);
        AddBoardLine(-0.31f); AddBoardLine(0.31f);
        _target = SpriteQuad("SteeringTarget", new Vector2(0.027f, 0.027f), new Color(0.15f, 0.68f, 1f, 0.82f), 106);
        _practiceBoard.AddChild(_target);
        _ship = TexturedSprite("PracticeShip", new Vector2(0.105f, 0.061f), TutorialAssets.PlayerShip(), 107);
        _practiceBoard.AddChild(_ship);
        _pickup = TexturedSprite("PracticePickup", new Vector2(0.050f, 0.058f), TutorialAssets.Powerup(), 108);
        _practiceBoard.AddChild(_pickup);
        _practiceBoard.Visible = false;
        _popup.Visible = false;
    }

    private void AddBoardLine(float x)
    {
        var line = new ImmediateMesh();
        line.SurfaceBegin(Mesh.PrimitiveType.Lines, UiMaterial(new Color(0.12f, 0.35f, 0.52f), 105));
        line.SurfaceAddVertex(new Vector3(x, -0.18f, 0f));
        line.SurfaceAddVertex(new Vector3(x, 0.18f, 0f));
        line.SurfaceEnd();
        _practiceBoard.AddChild(new MeshInstance3D { Mesh = line });
    }

    private void BuildLasers()
    {
        _laserMesh = new ImmediateMesh();
        _laserVisual = new MeshInstance3D { Name = "TutorialLasers", Mesh = _laserMesh };
        AddChild(_laserVisual);
    }

    private void UpdateLasers(XRController3D? left, bool leftTracking,
                              XRController3D? right, bool rightTracking,
                              out RayButton? leftHovered, out RayButton? rightHovered)
    {
        _laserMesh.ClearSurfaces();
        leftHovered = null;
        rightHovered = null;
        if (!leftTracking && !rightTracking)
            return;
        _laserMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, UiMaterial(new Color(0.22f, 0.78f, 1f), 120));
        DrawControllerRay(left, leftTracking, ref leftHovered);
        DrawControllerRay(right, rightTracking, ref rightHovered);
        _laserMesh.SurfaceEnd();
    }

    private void DrawControllerRay(XRController3D? controller, bool tracking, ref RayButton? hovered)
    {
        if (!tracking || controller == null) return;
        Vector3 origin = controller.GlobalPosition;
        Vector3 direction = -(controller.GlobalTransform.Basis.Z).Normalized();
        Vector3 localOrigin = _introPanel.ToLocal(origin);
        Vector3 localDirection = (_introPanel.GlobalTransform.Basis.Inverse() * direction).Normalized();
        Vector3 end = origin + direction * 1.5f;
        if (Mathf.Abs(localDirection.Z) > 0.0001f)
        {
            float t = -localOrigin.Z / localDirection.Z;
            if (t > 0f && t < 2.5f)
            {
                Vector3 hit = localOrigin + localDirection * t;
                end = _introPanel.ToGlobal(hit);
                if (Contains(_startButton, hit.X, hit.Y)) hovered = _startButton;
                else if (Contains(_skipButton, hit.X, hit.Y)) hovered = _skipButton;
            }
        }
        _laserMesh.SurfaceAddVertex(ToLocal(origin));
        _laserMesh.SurfaceAddVertex(ToLocal(end));
    }

    private static bool Contains(RayButton b, float x, float y) =>
        Mathf.Abs(x - b.Center.X) <= b.Size.X * 0.5f && Mathf.Abs(y - b.Center.Y) <= b.Size.Y * 0.5f;

    private static void SetHover(RayButton b, bool hover)
    {
        b.Material.EmissionEnabled = hover;
        b.Material.Emission = new Color(0.25f, 0.72f, 1f);
        b.Material.EmissionEnergyMultiplier = hover ? 1.4f : 0f;
        b.Label.Modulate = hover ? Colors.White : new Color(0.86f, 0.92f, 1f);
    }

    private void StartPractice()
    {
        _step = Step.Corners;
        _introPanel.Visible = false; _laserVisual.Visible = false;
        _popup.Visible = true; _practiceBoard.Visible = true; _pickup.Visible = false;
        _corners = 0;
        _playerSim = _targetSim = new Vector2(148f, 85f);
        _targetRampX = _targetRampY = 0;
        _pursuitAccumulator = 0;
        RefreshPopup();
        GD.Print("OpenTyrianVR: tutorial -> Corners");
    }

    private void UpdatePractice(double delta, Vector2 hand, bool tracking, bool triggerEdge, bool gripEdge)
    {
        _targetSim = new Vector2(Mathf.Remap(hand.X, -1f, 1f, 16f, 280f),
                                 Mathf.Remap(hand.Y, -1f, 1f, 160f, 10f));
        _pursuitAccumulator += delta;
        while (_pursuitAccumulator >= 1.0 / 35.0)
        {
            _pursuitAccumulator -= 1.0 / 35.0;
            _playerSim.X += TargetEaseStep(Mathf.RoundToInt(_targetSim.X - _playerSim.X), ref _targetRampX);
            _playerSim.Y += TargetEaseStep(Mathf.RoundToInt(_targetSim.Y - _playerSim.Y), ref _targetRampY);
        }
        _playerPosition = SimToBoard(_playerSim);
        Vector2 targetPosition = SimToBoard(_targetSim);
        _target.Position = new Vector3(targetPosition.X, targetPosition.Y, 0.004f);
        _ship.Position = new Vector3(_playerPosition.X, _playerPosition.Y, 0.004f);
        UpdateShots(delta);
        if (!tracking)
        {
            _popupText.Text = "WAKE YOUR LEFT CONTROLLER\nTraining resumes when hand tracking returns.";
            return;
        }
        switch (_step)
        {
            case Step.Corners:
                if (Mathf.Abs(hand.X) >= 0.78f && Mathf.Abs(hand.Y) >= 0.72f)
                {
                    int corner = (hand.X > 0 ? 1 : 0) | (hand.Y > 0 ? 2 : 0);
                    byte before = _corners;
                    _corners |= (byte)(1 << corner);
                    if (_corners != before) RefreshPopup();
                    if (_corners == 0x0f) Advance(Step.MainWeapon);
                }
                break;
            case Step.MainWeapon when triggerEdge:
                SpawnShot(_playerPosition, new Vector2(0f, 0.48f), new Color(0.35f, 0.9f, 1f));
                Advance(Step.SecondaryWeapon);
                break;
            case Step.SecondaryWeapon when gripEdge:
                SpawnShot(_playerPosition + new Vector2(-0.035f, 0f), new Vector2(-0.08f, 0.42f), new Color(1f, 0.45f, 0.20f));
                SpawnShot(_playerPosition + new Vector2(0.035f, 0f), new Vector2(0.08f, 0.42f), new Color(1f, 0.45f, 0.20f));
                Advance(Step.Pickup); RespawnPickup();
                break;
            case Step.Pickup:
                _pickupPosition.Y -= (float)delta * 0.105f;
                _pickup.Position = new Vector3(_pickupPosition.X, _pickupPosition.Y, 0.006f);
                if (_pickupPosition.DistanceTo(_playerPosition) < 0.055f) Finish(skipped: false);
                else if (_pickupPosition.Y < -0.19f) RespawnPickup();
                break;
        }
    }

    private static Vector2 SimToBoard(Vector2 sim) => new(
        Mathf.Remap(sim.X, 16f, 280f, -0.27f, 0.27f),
        Mathf.Remap(sim.Y, 160f, 10f, -0.15f, 0.15f));

    // Matches mainint.c target_ease_step: 1 px/tick acceleration, distance-
    // based slow arrival, and the game's default 5 px/tick maximum.
    private static int TargetEaseStep(int distance, ref int ramp)
    {
        if (distance == 0) { ramp = 0; return 0; }
        int direction = distance > 0 ? 1 : -1;
        int remaining = Math.Abs(distance);
        int desired = Math.Min((remaining + 2) / 3, 5);
        int speed = ramp * direction;
        if (speed < 0) speed = 0;
        speed = Math.Min(speed + 1, desired);
        int step = Math.Min(speed, remaining);
        ramp = direction * speed;
        return direction * step;
    }

    private void Advance(Step next) { _step = next; RefreshPopup(); GD.Print($"OpenTyrianVR: tutorial -> {_step}"); }

    private void RefreshPopup() => _popupText.Text = _step switch
    {
        Step.Corners => $"1 / 4   HAND STEERING\nMove the blue hand marker to all four corners.   {BitCount(_corners)} / 4",
        Step.MainWeapon => "2 / 4   MAIN WEAPON\nSqueeze either Trigger to fire.",
        Step.SecondaryWeapon => "3 / 4   SECONDARY FIRE\nSqueeze either Grip to fire that sidekick.",
        Step.Pickup => "4 / 4   COLLECT THE ITEM\nMove into the falling gold pickup. It retries until collected.",
        Step.Complete => "TRAINING COMPLETE\nLaunching OpenTyrianVR…",
        _ => _popupText.Text,
    };

    private static int BitCount(byte value)
    {
        int count = 0;
        for (; value != 0; value >>= 1) count += value & 1;
        return count;
    }

    private void RespawnPickup()
    {
        float[] lanes = { -0.19f, 0.17f, -0.06f, 0.08f, 0f };
        _pickupPosition = new Vector2(lanes[_pickupAttempt++ % lanes.Length], 0.18f);
        _pickup.Position = new Vector3(_pickupPosition.X, _pickupPosition.Y, 0.006f);
        _pickup.Visible = true;
        GD.Print($"OpenTyrianVR: tutorial pickup attempt {_pickupAttempt}");
    }

    private void SpawnShot(Vector2 position, Vector2 velocity, Color color)
    {
        var mesh = SpriteQuad("TutorialShot", new Vector2(0.012f, 0.035f), color, 109);
        mesh.Position = new Vector3(position.X, position.Y, 0.008f);
        _practiceBoard.AddChild(mesh);
        _shots.Add((mesh, velocity, 1.2));
    }

    private void UpdateShots(double delta)
    {
        for (int i = _shots.Count - 1; i >= 0; --i)
        {
            var shot = _shots[i];
            shot.Life -= delta;
            Vector3 p = shot.Mesh.Position;
            p.X += shot.Velocity.X * (float)delta; p.Y += shot.Velocity.Y * (float)delta;
            shot.Mesh.Position = p;
            if (shot.Life <= 0) { shot.Mesh.QueueFree(); _shots.RemoveAt(i); }
            else _shots[i] = shot;
        }
    }

    private void Finish(bool skipped)
    {
        using var file = Godot.FileAccess.Open(CompletionPath, Godot.FileAccess.ModeFlags.Write);
        if (file == null) GD.PushWarning($"OpenTyrianVR: could not persist tutorial completion ({Godot.FileAccess.GetOpenError()})");
        else file.StoreString(skipped ? "skipped\n" : "complete\n");
        _step = Step.Complete;
        _introPanel.Visible = false; _laserVisual.Visible = false;
        if (skipped) { _popup.Visible = false; _practiceBoard.Visible = false; _completeDelay = 0.05; }
        else { _pickup.Visible = false; _popup.Visible = true; RefreshPopup(); _completeDelay = 1.8; }
        _launchPending = true;
        GD.Print($"OpenTyrianVR: pre-game tutorial {(skipped ? "skipped" : "complete")}");
    }

    private static MeshInstance3D PanelQuad(string name, Vector2 size, Color color, float z, int priority) => new()
    {
        Name = name, Mesh = new QuadMesh { Size = size }, Position = new Vector3(0f, 0f, z),
        MaterialOverride = UiMaterial(color, priority),
    };
    private static MeshInstance3D SpriteQuad(string name, Vector2 size, Color color, int priority) => new()
    {
        Name = name, Mesh = new QuadMesh { Size = size }, MaterialOverride = UiMaterial(color, priority),
    };
    private static MeshInstance3D TexturedSprite(string name, Vector2 size, Texture2D texture, int priority) => new()
    {
        Name = name, Mesh = new QuadMesh { Size = size }, MaterialOverride = SpriteMaterial(texture, priority),
    };
    private static StandardMaterial3D SpriteMaterial(Texture2D texture, int priority) => new()
    {
        AlbedoTexture = texture, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = true, RenderPriority = priority,
    };
    private static StandardMaterial3D UiMaterial(Color color, int priority) => new()
    {
        AlbedoColor = color,
        Transparency = color.A < 0.999f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true, RenderPriority = priority,
    };
    private static Label3D MakeLabel(string text, int fontSize, Vector3 position) => new()
    {
        Text = text, Position = position, PixelSize = 0.00062f, FontSize = fontSize, OutlineSize = 7,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Modulate = new Color(0.86f, 0.92f, 1f), DoubleSided = true, NoDepthTest = true, RenderPriority = 119,
    };
}
