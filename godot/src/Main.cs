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

    private OtyrNative.Buttons _lastButtons;
    private double _statusAccumulator;

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

        _image = Image.CreateEmpty(OtyrNative.FrameWidth, OtyrNative.FrameHeight, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);

        var material = new StandardMaterial3D
        {
            AlbedoTexture = _texture,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };

        var lane = new MeshInstance3D
        {
            Name = "Lane",
            Mesh = new QuadMesh { Size = new Vector2(1.28f, 0.8f) },  // 320:200
            MaterialOverride = material,
        };
        _playfieldRoot.AddChild(lane);

        _diagnostics = new Label3D
        {
            Name = "Diagnostics",
            Text = "starting...",
            PixelSize = 0.0008f,
            Position = new Vector3(0f, -0.47f, 0.02f),
            Modulate = new Color(0.6f, 1.0f, 0.6f),
        };
        _playfieldRoot.AddChild(_diagnostics);

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

    private void StartGame()
    {
        uint nativeAbi = OtyrNative.GetAbiVersion();
        if (nativeAbi != OtyrNative.AbiVersion)
            throw new InvalidOperationException($"native ABI {nativeAbi}, expected {OtyrNative.AbiVersion}");

        string dataDir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "tyrian21"));
        var config = OtyrNative.Config.Create(dataDir);

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
        SubmitInput();
        UpdateDiagnostics(delta);
    }

    private unsafe void PollFrame()
    {
        int rc = OtyrNative.AcquireFrame(_session, ref _frame, _frame.StructSize, 0);
        if (rc != OtyrNative.Ok)
            return;  // Timeout: no new legacy frame this render frame.

        fixed (OtyrNative.Frame* frame = &_frame)
        {
            for (int i = 0; i < OtyrNative.FrameWidth * OtyrNative.FrameHeight; ++i)
            {
                uint argb = frame->Palette[frame->Pixels[i]];
                _rgba[i * 4 + 0] = (byte)(argb >> 16);
                _rgba[i * 4 + 1] = (byte)(argb >> 8);
                _rgba[i * 4 + 2] = (byte)argb;
                _rgba[i * 4 + 3] = 0xff;
            }
        }

        _image.SetData(OtyrNative.FrameWidth, OtyrNative.FrameHeight, false, Image.Format.Rgba8, _rgba);
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
            Vector2 stick = _leftHand.GetVector2("primary");
            const float deadzone = 0.4f;
            if (stick.Y > deadzone) buttons |= OtyrNative.Buttons.Up;
            if (stick.Y < -deadzone) buttons |= OtyrNative.Buttons.Down;
            if (stick.X < -deadzone) buttons |= OtyrNative.Buttons.Left;
            if (stick.X > deadzone) buttons |= OtyrNative.Buttons.Right;

            if (_rightHand.GetFloat("trigger") > 0.55f) buttons |= OtyrNative.Buttons.Fire;
            if (_rightHand.GetFloat("grip") > 0.6f) buttons |= OtyrNative.Buttons.RightSidekick;
            if (_leftHand.GetFloat("grip") > 0.6f) buttons |= OtyrNative.Buttons.LeftSidekick;

            if (_rightHand.IsButtonPressed("ax_button")) buttons |= OtyrNative.Buttons.UiConfirm;
            if (_rightHand.IsButtonPressed("by_button")) buttons |= OtyrNative.Buttons.UiCancel;
            if (_leftHand.IsButtonPressed("by_button")) buttons |= OtyrNative.Buttons.ChangeFire;

            // Left menu button: recenter, and pause the game.
            if (_leftHand.IsButtonPressed("menu_button"))
            {
                buttons |= OtyrNative.Buttons.UiPause;
                XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
            }
        }

        if (buttons == _lastButtons)
            return;
        _lastButtons = buttons;

        var input = OtyrNative.InputFrame.Create(buttons);
        OtyrNative.SubmitInput(_session, in input, input.StructSize);
    }

    private void UpdateDiagnostics(double delta)
    {
        _statusAccumulator += delta;
        if (_statusAccumulator < 0.25)
            return;
        _statusAccumulator = 0;

        var state = new OtyrNative.PlayerState
        {
            StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.PlayerState>(),
        };
        OtyrNative.GetPlayerState(_session, ref state, state.StructSize);

        _diagnostics.Text =
            $"{(_xrActive ? "XR" : "FLAT")}  render {Engine.GetFramesPerSecond():0} fps  " +
            $"game frame {_frame.FrameNumber}\n" +
            $"pos ({state.X},{state.Y})  shield {state.Shield}  armor {state.Armor}  " +
            $"cash {state.Cash}  lives {state.Lives}";
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
