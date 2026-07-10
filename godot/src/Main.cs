using Godot;
using System;
using System.IO;

namespace OpenTyrianVR;

/// <summary>
/// Spike host (VR_CONVERSION_PLAN.md section 6): boots OpenXR with a flat
/// fallback, hosts the OpenTyrian core on its internal game thread, shows the
/// legacy 320x200 frame on a tilted lane, and maps VR-controller / keyboard
/// input into the game's abstract actions.  The whole scene is built in code.
/// </summary>
public partial class Main : Node3D
{
    private bool _xrActive;
    private ulong _session;
    private bool _sessionLive;

    private Node3D _playfieldRoot = null!;
    private Label3D _diagnostics = null!;
    private XRController3D? _leftHand;
    private XRController3D? _rightHand;

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private OtyrNative.Frame _frame;
    private readonly byte[] _rgba = new byte[OtyrNative.FrameWidth * OtyrNative.FrameHeight * 4];
    private readonly uint[] _palette = new uint[256];
    private SnapshotLayer _snapshotLayer = null!;

    private OtyrNative.Buttons _lastButtons;
    private double _statusAccumulator;
    private uint _fpsWindowFrame;
    private double _fpsWindowTime;
    private double _gameFps;
    private double _fpsLogAccumulator;

    // Hand-rectangle steering: the left hand's position within a floating
    // control rectangle maps 1:1 onto the gameplay rectangle; the ship is
    // steered toward that target each frame (the game only accepts 8-way
    // input, so this is bang-bang until Phase 2 gives us analog axes).
    private Node3D _controlRect = null!;
    private MeshInstance3D _handMarker = null!;
    private MeshInstance3D _targetReticle = null!;
    private TestChecklist _checklist = null!;
    private bool _lastCheckPressed, _lastSkipPressed;
    private OtyrNative.PlayerState _playerState;
    private uint _lastLevelTick;
    private ulong _lastLevelTickMs;
    private bool _inGameplay;

    private const float ControlRectWidth = 0.36f;
    private const float ControlRectHeight = 0.25f;
    // Player movement clamp in Tyrian sim coordinates (ENTITY_TAXONOMY.md).
    private const float GameMinX = 40f, GameMaxX = 256f;
    private const float GameMinY = 10f, GameMaxY = 160f;
    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    // 0 = the sim's default max (5 px/tick, the original sustained keyboard
    // speed); the sim applies its own distance-based ease profile.
    private const byte HandTargetSpeed = 0;

    // Render the background map layers as 3D geometry (ground/structures
    // pixel-locked to the lane, clouds elevated and scroll-interpolated); the
    // legacy frame's tile blits are suppressed and palette index 0 becomes
    // transparent so the frame overlays the tile layers.
    private const bool Render3DBackground = true;
    private bool _handTargetActive;
    private short _handTargetX, _handTargetY;
    private bool _lastTargetActive;
    private short _lastTargetX, _lastTargetY;

    public override void _Ready()
    {
        OtyrNative.RegisterResolver();

        InitXr();
        BuildScene();
        StartGame();
    }

    private void InitXr()
    {
        var xr = XRServer.FindInterface("OpenXR");
        if (xr != null && xr.IsInitialized())
        {
            _xrActive = true;
        }
        else if (xr != null && xr.Initialize())
        {
            _xrActive = true;
        }

        if (_xrActive)
        {
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            GD.Print("OpenTyrianVR: OpenXR active");
        }
        else
        {
            GD.Print("OpenTyrianVR: OpenXR unavailable, running flat");
        }
    }

    private void BuildScene()
    {
        // Rig: origin + camera + hands.  Added in both modes; in flat mode the
        // XRCamera3D behaves as a normal camera at the origin, so we park it
        // at a seated head height.
        var origin = new XROrigin3D { Name = "XROrigin" };
        AddChild(origin);

        var camera = new XRCamera3D { Name = "XRCamera" };
        if (!_xrActive)
        {
            // Approximate a seated player looking down at the board.
            camera.Position = new Vector3(0f, 1.6f, 0f);
            camera.RotationDegrees = new Vector3(-25f, 0f, 0f);
        }
        origin.AddChild(camera);
        camera.MakeCurrent();

        _leftHand = new XRController3D { Name = "LeftHand", Tracker = "left_hand", Pose = "aim" };
        _rightHand = new XRController3D { Name = "RightHand", Tracker = "right_hand", Pose = "aim" };
        origin.AddChild(_leftHand);
        origin.AddChild(_rightHand);

        // The arcade board: a lane in front of and below the head, top edge
        // leaning away (Guitar Hero).  Dimensions/tilt are the spike's main
        // tunables.
        _playfieldRoot = new Node3D { Name = "PlayfieldRoot" };
        _playfieldRoot.Position = new Vector3(0f, 1.05f, -0.9f);
        _playfieldRoot.RotationDegrees = new Vector3(-42f, 0f, 0f);
        AddChild(_playfieldRoot);

        _image = Image.CreateEmpty(OtyrNative.FrameWidth, OtyrNative.FrameHeight, true, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);

        // Anti-aliased point sampling: linear filtering with the UVs snapped
        // to texel centers except in a fwidth-wide band at texel edges.
        // Crisp pixel art without the shimmer of raw nearest at an angle.
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, depth_prepass_alpha;

                uniform sampler2D frame : source_color, filter_linear_mipmap_anisotropic;

                void fragment() {
                    vec2 size = vec2(textureSize(frame, 0));
                    vec2 pixel = UV * size;
                    vec2 seam = floor(pixel + 0.5);
                    vec2 dudv = fwidth(pixel);
                    pixel = seam + clamp((pixel - seam) / dudv, -0.5, 0.5);
                    vec4 c = texture(frame, pixel / size);
                    // Alpha carries the background color key (the suppressed
                    // fill index); the tile layers render just behind this
                    // plane.  Texture data is premultiplied: un-premultiply
                    // after filtering so keyed texels can't tint art edges.
                    ALBEDO = c.rgb / max(c.a, 0.004);
                    ALPHA = c.a;
                }
                """,
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("frame", _texture);

        var lane = new MeshInstance3D
        {
            Name = "Lane",
            Mesh = new QuadMesh { Size = new Vector2(LaneWidth, LaneHeight) },  // 320:200
            MaterialOverride = material,
        };
        _playfieldRoot.AddChild(lane);

        BuildHandSteering();

        _snapshotLayer = new SnapshotLayer { Name = "SnapshotLayer", EnableBackground = Render3DBackground };
        _playfieldRoot.AddChild(_snapshotLayer);

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        GetViewport().Scaling3DScale = 1.4f;

        _diagnostics = new Label3D
        {
            Name = "Diagnostics",
            Text = "starting...",
            PixelSize = 0.0008f,
            Position = new Vector3(0f, -0.47f, 0.02f),
            Modulate = new Color(0.6f, 1.0f, 0.6f),
        };
        _playfieldRoot.AddChild(_diagnostics);

        // In-headset test checklist: a panel directly to the player's left at
        // head height, facing them -- turn your head 90 degrees to read it.
        // World-space (not under PlayfieldRoot), so it ignores the lane tilt.
        _checklist = new TestChecklist
        {
            Name = "TestChecklist",
            Position = new Vector3(-0.85f, 1.35f, -0.25f),
            RotationDegrees = new Vector3(0f, 90f, 0f),
        };
        AddChild(_checklist);

        var environment = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.02f, 0.05f),
            },
        };
        AddChild(environment);
    }

    private void BuildHandSteering()
    {
        // The control rectangle floats in front of and below the lane, tilted
        // to match it, at a comfortable seated arm position.
        _controlRect = new Node3D { Name = "ControlRect" };
        _controlRect.Position = new Vector3(0f, 0.95f, -0.45f);
        _controlRect.RotationDegrees = new Vector3(-42f, 0f, 0f);
        AddChild(_controlRect);

        var rectVisual = new MeshInstance3D
        {
            Name = "Bounds",
            Mesh = new QuadMesh { Size = new Vector2(ControlRectWidth, ControlRectHeight) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.3f, 0.6f, 1.0f, 0.08f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        _controlRect.AddChild(rectVisual);

        _handMarker = new MeshInstance3D
        {
            Name = "HandMarker",
            Mesh = new SphereMesh { Radius = 0.012f, Height = 0.024f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.2f, 0.5f, 1.0f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        _controlRect.AddChild(_handMarker);

        // Where the ship is being told to go, shown on the lane itself.
        _targetReticle = new MeshInstance3D
        {
            Name = "TargetReticle",
            Mesh = new QuadMesh { Size = new Vector2(0.025f, 0.025f) },
            Position = new Vector3(0f, 0f, 0.006f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.2f, 0.8f, 1.0f, 0.65f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        _playfieldRoot.AddChild(_targetReticle);

        _controlRect.Visible = false;
        _targetReticle.Visible = false;
    }

    private void StartGame()
    {
        uint nativeAbi = OtyrNative.GetAbiVersion();
        if (nativeAbi != OtyrNative.AbiVersion)
            throw new InvalidOperationException($"native ABI {nativeAbi}, expected {OtyrNative.AbiVersion}");

        string dataDir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "tyrian21"));
        string userDir = ProjectSettings.GlobalizePath("user://");
        var flags = OtyrNative.ConfigFlags.EnableAudio | OtyrNative.ConfigFlags.SuppressEntityDraw;
        if (Render3DBackground)
            flags |= OtyrNative.ConfigFlags.SuppressBackground;
        var config = OtyrNative.Config.Create(dataDir, flags, userDir: userDir);

        int rc = OtyrNative.SessionCreate(in config, (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Config>(), out _session);
        if (rc != OtyrNative.Ok)
            throw new InvalidOperationException($"session_create failed: {rc} ({OtyrNative.LastError()})");
        _sessionLive = true;

        _frame.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Frame>();
        GD.Print($"OpenTyrianVR: session up, data={dataDir}");
    }

    public override void _Process(double delta)
    {
        if (!_sessionLive)
            return;

        PollFrame();
        PollPlayerState();
        _snapshotLayer.Poll(_session, _palette);
        // Menus, pause, and quit-to-title stop gameplay ticks; the 3D scene
        // (sprites AND background layers) must not linger over them.
        _snapshotLayer.Visible = _inGameplay;
        UpdateChecklistInput();
        SubmitInput();
        UpdateDiagnostics(delta);
    }

    private void UpdateChecklistInput()
    {
        // Stick clicks are unused by the game, so they drive the checklist;
        // C/V cover flat testing.  Edge-triggered.
        bool check = Input.IsKeyPressed(Key.C) ||
            (_xrActive && _rightHand != null && _rightHand.IsButtonPressed("primary_click"));
        bool skip = Input.IsKeyPressed(Key.V) ||
            (_xrActive && _leftHand != null && _leftHand.IsButtonPressed("primary_click"));

        if (check && !_lastCheckPressed)
            _checklist.ToggleCurrent();
        if (skip && !_lastSkipPressed)
            _checklist.MoveCursor();
        _lastCheckPressed = check;
        _lastSkipPressed = skip;
    }

    private void PollPlayerState()
    {
        _playerState.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.PlayerState>();
        if (OtyrNative.GetPlayerState(_session, ref _playerState, _playerState.StructSize) != OtyrNative.Ok)
            return;

        ulong now = Time.GetTicksMsec();
        if (_playerState.LevelTick != _lastLevelTick)
        {
            _lastLevelTick = _playerState.LevelTick;
            _lastLevelTickMs = now;
        }
        // Gameplay ticks run at ~35 Hz; if none arrived recently we are in a
        // menu (or paused) and hand steering must not emit direction input.
        _inGameplay = _lastLevelTickMs != 0 && now - _lastLevelTickMs < 250;
    }

    private unsafe void PollFrame()
    {
        int rc = OtyrNative.AcquireFrame(_session, ref _frame, _frame.StructSize, 0);
        if (rc == OtyrNative.InvalidSession)
        {
            // The player quit from inside the game; the game thread halted.
            GD.Print("OpenTyrianVR: game quit, closing host");
            ShutDown();
            GetTree().Quit();
            return;
        }
        if (rc != OtyrNative.Ok)
            return;  // Timeout: no new legacy frame this render frame.

        fixed (OtyrNative.Frame* frame = &_frame)
        {
            for (int p = 0; p < 256; ++p)
                _palette[p] = frame->Palette[p];

            for (int i = 0; i < OtyrNative.FrameWidth * OtyrNative.FrameHeight; ++i)
            {
                byte index = frame->Pixels[i];
                // Color key: with the native background suppressed, the fill
                // index means "nothing drawn here" and the 3D tile layers
                // show through.  Index-0 black in art stays opaque.  Data is
                // premultiplied (keyed pixels fully zero) so linear/mipmap
                // filtering can't bleed key-colored fringes into the art.
                if (Render3DBackground && index == OtyrNative.BgKeyIndex)
                {
                    _rgba[i * 4 + 0] = 0;
                    _rgba[i * 4 + 1] = 0;
                    _rgba[i * 4 + 2] = 0;
                    _rgba[i * 4 + 3] = 0;
                }
                else
                {
                    uint argb = frame->Palette[index];
                    _rgba[i * 4 + 0] = (byte)(argb >> 16);
                    _rgba[i * 4 + 1] = (byte)(argb >> 8);
                    _rgba[i * 4 + 2] = (byte)argb;
                    _rgba[i * 4 + 3] = 0xff;
                }
            }
        }

        _image.SetData(OtyrNative.FrameWidth, OtyrNative.FrameHeight, false, Image.Format.Rgba8, _rgba);
        _image.GenerateMipmaps();
        _texture.Update(_image);
    }

    private void SubmitInput()
    {
        var buttons = OtyrNative.Buttons.None;

        // Keyboard (flat testing and desk-side debugging).
        if (Input.IsKeyPressed(Key.Up)) buttons |= OtyrNative.Buttons.Up;
        if (Input.IsKeyPressed(Key.Down)) buttons |= OtyrNative.Buttons.Down;
        if (Input.IsKeyPressed(Key.Left)) buttons |= OtyrNative.Buttons.Left;
        if (Input.IsKeyPressed(Key.Right)) buttons |= OtyrNative.Buttons.Right;
        if (Input.IsKeyPressed(Key.Space)) buttons |= OtyrNative.Buttons.Fire;
        if (Input.IsKeyPressed(Key.Enter)) buttons |= OtyrNative.Buttons.UiConfirm;
        if (Input.IsKeyPressed(Key.Escape)) buttons |= OtyrNative.Buttons.UiCancel;
        if (Input.IsKeyPressed(Key.Ctrl)) buttons |= OtyrNative.Buttons.LeftSidekick;
        if (Input.IsKeyPressed(Key.Alt)) buttons |= OtyrNative.Buttons.RightSidekick;
        if (Input.IsKeyPressed(Key.P)) buttons |= OtyrNative.Buttons.UiPause;

        // VR controllers (Godot's default OpenXR action map).
        if (_xrActive && _leftHand != null && _rightHand != null)
        {
            // Either stick moves, either trigger fires.
            Vector2 stick = _leftHand.GetVector2("primary") + _rightHand.GetVector2("primary");
            const float deadzone = 0.4f;
            if (stick.Y > deadzone) buttons |= OtyrNative.Buttons.Up;
            if (stick.Y < -deadzone) buttons |= OtyrNative.Buttons.Down;
            if (stick.X < -deadzone) buttons |= OtyrNative.Buttons.Left;
            if (stick.X > deadzone) buttons |= OtyrNative.Buttons.Right;

            if (Math.Max(_rightHand.GetFloat("trigger"), _leftHand.GetFloat("trigger")) > 0.55f)
                buttons |= OtyrNative.Buttons.Fire;
            if (_rightHand.GetFloat("grip") > 0.6f) buttons |= OtyrNative.Buttons.RightSidekick;
            if (_leftHand.GetFloat("grip") > 0.6f) buttons |= OtyrNative.Buttons.LeftSidekick;

            if (_rightHand.IsButtonPressed("ax_button")) buttons |= OtyrNative.Buttons.UiConfirm;
            if (_rightHand.IsButtonPressed("by_button")) buttons |= OtyrNative.Buttons.UiCancel;
            if (_leftHand.IsButtonPressed("ax_button")) buttons |= OtyrNative.Buttons.UiSpace;
            if (_leftHand.IsButtonPressed("by_button")) buttons |= OtyrNative.Buttons.ChangeFire;

            // Left menu button: recenter, and pause the game.
            if (_leftHand.IsButtonPressed("menu_button"))
            {
                buttons |= OtyrNative.Buttons.UiPause;
                XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
            }

            HandSteering();
        }

        if (buttons == _lastButtons && _handTargetActive == _lastTargetActive &&
            _handTargetX == _lastTargetX && _handTargetY == _lastTargetY)
            return;
        if (buttons != _lastButtons)
            GD.Print($"OpenTyrianVR: input -> 0x{(uint)buttons:x3}");
        _lastButtons = buttons;
        _lastTargetActive = _handTargetActive;
        _lastTargetX = _handTargetX;
        _lastTargetY = _handTargetY;

        var input = _handTargetActive
            ? OtyrNative.InputFrame.CreateWithTarget(buttons, _handTargetX, _handTargetY, HandTargetSpeed)
            : OtyrNative.InputFrame.Create(buttons);
        OtyrNative.SubmitInput(_session, in input, input.StructSize);
    }

    /// <summary>
    /// Maps the left hand's position within the control rectangle to a target
    /// point in the gameplay rectangle and returns the direction bits that
    /// steer the ship toward it.  Shows the hand marker on the rectangle and
    /// the target reticle on the lane.
    /// </summary>
    private void HandSteering()
    {
        // The rectangle stays visible whenever the hand tracks (TODO in plan:
        // user setting to hide it or fade it out after level start); the lane
        // reticle and steering only engage during gameplay.
        bool tracking = _leftHand != null && _leftHand.GetHasTrackingData();
        _controlRect.Visible = tracking;
        _targetReticle.Visible = tracking && _inGameplay;
        _handTargetActive = false;
        if (!tracking)
            return;

        // Project the hand onto the rectangle's plane (drop local Z), clamp
        // to the bounds, and show the marker at the clamped point.
        Vector3 local = _controlRect.ToLocal(_leftHand!.GlobalPosition);
        float lx = Mathf.Clamp(local.X, -ControlRectWidth / 2f, ControlRectWidth / 2f);
        float ly = Mathf.Clamp(local.Y, -ControlRectHeight / 2f, ControlRectHeight / 2f);
        _handMarker.Position = new Vector3(lx, ly, 0.002f);

        if (!_inGameplay)
            return;

        // 1:1 map to the gameplay rectangle.  Rectangle-up = screen-up = smaller
        // Tyrian y (sim y grows downward).
        float targetX = Mathf.Remap(lx, -ControlRectWidth / 2f, ControlRectWidth / 2f, GameMinX, GameMaxX);
        float targetY = Mathf.Remap(ly, -ControlRectHeight / 2f, ControlRectHeight / 2f, GameMaxY, GameMinY);

        // Reticle on the lane, placed where the ship's VISUAL CENTER will sit
        // when it reaches the target: the two 24x28 sprite blocks are drawn at
        // (x-17, y-7), so the visual center is (x+6.5, y+6.5); the play area
        // is composited shifted left by 24 px.
        float frameU = (targetX + 6.5f - 24f) / 320f;
        float frameV = (targetY + 6.5f) / 200f;
        _targetReticle.Position = new Vector3(
            (frameU - 0.5f) * LaneWidth, (0.5f - frameV) * LaneHeight, 0.006f);

        // Hand the target to the simulation: the pursuit loop runs inside the
        // tick against fresh position, so it cannot oscillate regardless of
        // host-side latency.  Hysteresis: the commanded target only moves
        // when the hand moves meaningfully, so sensor jitter never twitches
        // the ship.
        const float targetHysteresisPx = 3f;
        _handTargetActive = true;
        if (!_lastTargetActive ||
            Mathf.Abs(targetX - _handTargetX) > targetHysteresisPx ||
            Mathf.Abs(targetY - _handTargetY) > targetHysteresisPx)
        {
            _handTargetX = (short)Mathf.RoundToInt(targetX);
            _handTargetY = (short)Mathf.RoundToInt(targetY);
        }
    }

    private void UpdateDiagnostics(double delta)
    {
        // Measured legacy-frame rate over a 1 s window (should be ~35).
        _fpsWindowTime += delta;
        if (_fpsWindowTime >= 1.0)
        {
            _gameFps = (_frame.FrameNumber - _fpsWindowFrame) / _fpsWindowTime;
            _fpsWindowFrame = _frame.FrameNumber;
            _fpsWindowTime = 0;

            _fpsLogAccumulator += 1.0;
            if (_fpsLogAccumulator >= 5.0)
            {
                _fpsLogAccumulator = 0;
                GD.Print($"OpenTyrianVR: game {_gameFps:0.0} fps, render {Engine.GetFramesPerSecond():0} fps");
            }
        }

        _statusAccumulator += delta;
        if (_statusAccumulator < 0.25)
            return;
        _statusAccumulator = 0;

        OtyrNative.PlayerState state = _playerState;

        string inputProbe = "";
        if (_xrActive && _leftHand != null && _rightHand != null)
        {
            Vector2 stick = _leftHand.GetVector2("primary");
            inputProbe =
                $"\nstick ({stick.X:0.00},{stick.Y:0.00})  trig {_rightHand.GetFloat("trigger"):0.00}  " +
                $"A:{(_rightHand.IsButtonPressed("ax_button") ? 1 : 0)} B:{(_rightHand.IsButtonPressed("by_button") ? 1 : 0)}  " +
                $"buttons {(uint)_lastButtons:x3}";
        }

        _diagnostics.Text =
            $"{(_xrActive ? "XR" : "FLAT")}  render {Engine.GetFramesPerSecond():0} fps  " +
            $"game {_gameFps:0.0} fps (frame {_frame.FrameNumber})\n" +
            $"pos ({state.X},{state.Y})  shield {state.Shield}  armor {state.Armor}  " +
            $"cash {state.Cash}  lives {state.Lives}" + inputProbe;
    }

    public override void _ExitTree()
    {
        ShutDown();
    }

    private void ShutDown()
    {
        if (!_sessionLive)
            return;
        _sessionLive = false;
        int rc = OtyrNative.SessionDestroy(_session);
        GD.Print($"OpenTyrianVR: session destroy -> {rc}");
    }
}
