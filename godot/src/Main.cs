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
    private bool _xrStartupCentered;
    private int _xrStartupFrames;
    private int _xrStereoLogCountdown;
    private int _xrRefreshLogCountdown;
    private XRInterface? _openXr;
    private XROrigin3D _xrOrigin = null!;
    private XRCamera3D _xrCamera = null!;
    private ulong _session;
    private bool _sessionLive;

    private Node3D _playfieldRoot = null!;
    private Label3D _diagnostics = null!;
    private XRController3D? _leftHand;
    private XRController3D? _rightHand;

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private ShaderMaterial _laneMaterial = null!;
    private MeshInstance3D _hudSidebar = null!;
    private MeshInstance3D _hudBottomBar = null!;
    private Node3D _flipRoot = null!;
    private float _flipScale = 1f;  // animated +1 (normal) .. -1 (mirrored)
    private OtyrNative.Frame _frame;
    private bool _loggedFirstFrame;
    private bool _loggedPresentationMode;
    private bool _lastLegacyFallback;
    private int _presentationTransitions;
    private bool _regressionHybridSeen;
    private bool _regressionLegacySeen;
    private bool _regressionFlipSeen;
    private bool _regressionStormSeen;
    private int _regressionMaxShadows;
    private int _regressionMaxMapShadows;
    private int _regressionMaxReceiverLayers;
    private int _regressionMaxCells;
    private bool _regressionFinished;
    private readonly byte[] _rgba = new byte[OtyrNative.FrameWidth * OtyrNative.FrameHeight * 4];
    private readonly uint[] _palette = new uint[256];
    private SnapshotLayer _snapshotLayer = null!;

    private OtyrNative.Buttons _lastButtons;
    private double _statusAccumulator;
    private uint _fpsWindowFrame;
    private double _fpsWindowTime;
    private double _gameFps;
    private double _fpsLogAccumulator;
    private ulong _perfWindowStartUsec;
    private double _perfHostTotalMs;
    private double _perfHostMaxMs;
    private int _perfHostSamples;
    private double _perfFrameMaxMs;
    private int _perfLongFrames;
    private float _perfPlayerMinX = float.PositiveInfinity;
    private float _perfPlayerMaxX = float.NegativeInfinity;
    private float _perfHandMinX = float.PositiveInfinity;
    private float _perfHandMaxX = float.NegativeInfinity;
    private float _perfTargetMinX = float.PositiveInfinity;
    private float _perfTargetMaxX = float.NegativeInfinity;

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
    // Preserve the playable simulation area's 264:150 aspect ratio so one
    // centimetre of hand travel has the same meaning on both axes.
    private const float ControlRectHeight = ControlRectWidth * 150f / 264f;
    // E2-full player travel in Tyrian sim coordinates.  Keep this host-side
    // absolute target range identical to mainint.c's de-parallax clamp;
    // keyboard input reaches these bounds directly, so a stale 40..256 here
    // made Quest hand steering narrower than the flat editor.
    private const float GameMinX = 16f, GameMaxX = 280f;
    private const float GameMinY = 10f, GameMaxY = 160f;
    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    private const double QuestTargetRefreshRate = 90.0;
    // 0 = the sim's default max (5 px/tick, the original sustained keyboard
    // speed); the sim applies its own distance-based ease profile.
    private const byte HandTargetSpeed = 0;

    // Render the background map layers as 3D geometry (ground/structures
    // pixel-locked to the lane, clouds elevated and scroll-interpolated); the
    // legacy frame's tile blits are suppressed and palette index 0 becomes
    // transparent so the frame overlays the tile layers.
    private const bool Render3DBackground = true;

    // Render in-play overlay text and HUD icons (cash, lives, WARNING,
    // timer, game over, insert coin) proud of the playfield instead of flat
    // in the frame (v13).
    private const bool RenderProudText = true;
    private bool _handTargetActive;
    private short _handTargetX, _handTargetY;
    private bool _lastTargetActive;
    private short _lastTargetX, _lastTargetY;

    public override void _Ready()
    {
        OtyrNative.RegisterResolver();

        MoveWindowToSideMonitor();
        InitXr();
        BuildScene();
        StartGame();
    }

    // Park the desktop window on the rightmost monitor (the tester's side
    // ultrawide) so it neither occludes nor gets occluded by their work.
    // Godot's screen API is DPI-aware; CLI --position is not reliable here.
    private void MoveWindowToSideMonitor()
    {
        int screens = DisplayServer.GetScreenCount();
        if (screens < 2)
            return;
        int rightmost = 0, bestX = int.MinValue;
        for (int i = 0; i < screens; i++)
        {
            int x = DisplayServer.ScreenGetPosition(i).X;
            if (x > bestX)
            {
                bestX = x;
                rightmost = i;
            }
        }
        GetWindow().CurrentScreen = rightmost;
    }

    private void InitXr()
    {
        // Debug override: force flat mode even when an OpenXR runtime is
        // reachable (Virtual Desktop grabs the app whenever it streams,
        // which blackens the desktop window and blocks solo flat testing).
        if (System.Environment.GetEnvironmentVariable("OTYR_FLAT") == "1")
        {
            GD.Print("OpenTyrianVR: OTYR_FLAT=1, forcing flat mode");
            return;
        }

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
            _openXr = xr;
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            GD.Print("OpenTyrianVR: OpenXR active");
            if (OS.GetName() == "Android")
            {
                ConfigureQuestRefresh("startup");
                // Re-apply once the mobile session has rendered a few frames;
                // the settled log proves the runtime's actual selected rate.
                _xrRefreshLogCountdown = 3;
            }
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
        _xrOrigin = new XROrigin3D { Name = "XROrigin" };
        AddChild(_xrOrigin);

        _xrCamera = new XRCamera3D { Name = "XRCamera" };
        if (!_xrActive)
        {
            // Approximate a seated player looking down at the board.  The
            // height editor wants the OPPOSITE of steep: a low grazing view
            // (the lane is tilted -42, so a steep camera pitch approaches
            // perpendicular and flattens all height separation) from close
            // in, so hover heights read as clear vertical offsets.  In
            // editor mode this is just the orbit's starting pose.
            _xrCamera.Position = HeightEditor ? new Vector3(0f, 1.26f, -0.05f) : new Vector3(0f, 1.6f, 0f);
            _xrCamera.RotationDegrees = new Vector3(HeightEditor ? -15f : -25f, 0f, 0f);
        }
        if (HeightEditor)
            _editorCamera = _xrCamera;
        _xrOrigin.AddChild(_xrCamera);
        _xrCamera.MakeCurrent();

        // XR swapchains are not readable through the main viewport texture
        // (run captures came back black), so captures in XR render a
        // spectator SubViewport.  The camera is a FIXED seat view framing
        // the whole lane: head-pose mirroring proved unreliable (captures
        // aimed at the void), and a stable framing is better for debriefs
        // anyway -- every capture shows the same composition.
        if (_xrActive && (CaptureCount > 0 || CaptureRun || CaptureAt.Length > 0))
        {
            _spectator = new SubViewport
            {
                Size = new Vector2I(1280, 720),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            _spectatorCamera = new Camera3D
            {
                Fov = 65f,
                Position = new Vector3(0f, 1.6f, 0.1f),
                RotationDegrees = new Vector3(-28f, 0f, 0f),
            };
            _spectator.AddChild(_spectatorCamera);
            AddChild(_spectator);
        }

        _leftHand = new XRController3D { Name = "LeftHand", Tracker = "left_hand", Pose = "aim" };
        _rightHand = new XRController3D { Name = "RightHand", Tracker = "right_hand", Pose = "aim" };
        _xrOrigin.AddChild(_leftHand);
        _xrOrigin.AddChild(_rightHand);

        // The arcade board: a lane in front of and below the head, top edge
        // leaning away (Guitar Hero).  Dimensions/tilt are the spike's main
        // tunables.
        _playfieldRoot = new Node3D { Name = "PlayfieldRoot" };
        _playfieldRoot.Position = new Vector3(0f, 1.05f, -0.9f);
        _playfieldRoot.RotationDegrees = new Vector3(-42f, 0f, 0f);
        AddChild(_playfieldRoot);

        // OTYR_TOPDOWN=1 (flat, diagnostics): an orthographic camera looking
        // straight down the lane normal, so captures are linearly mappable
        // to legacy frame pixels -- used with the harness OTYR_DUMP_TICK to
        // measure sprite-vs-tile alignment mechanically.
        if (!_xrActive &&
            System.Environment.GetEnvironmentVariable("OTYR_TOPDOWN") == "1")
        {
            var ortho = new Camera3D
            {
                Name = "TopdownProbe",
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 0.625f,  // full lane height; width follows aspect
                Position = new Vector3(0f, 0f, 1.0f),
            };
            _playfieldRoot.AddChild(ortho);
            ortho.MakeCurrent();
            GD.Print("OpenTyrianVR: TOPDOWN probe camera active");
        }

        if (HeightEditor)
        {
            // Selection readout: a fixed line between the lane's bottom edge
            // and the diagnostics text, matching its size (a floating label
            // over the game world was unreadable against the art).
            _editorLabel = new Label3D
            {
                Name = "EditorSelection",
                PixelSize = 0.0008f,
                Position = new Vector3(0f, -0.40f, 0.02f),
                Modulate = new Color(1f, 0.9f, 0.3f),
                Visible = false,
            };
            _playfieldRoot.AddChild(_editorLabel);

            // Height-band legend, height-ordered, to the right of the lane.
            _editorLegend = new Label3D
            {
                Name = "EditorLegend",
                PixelSize = 0.0008f,
                Position = new Vector3(0.72f, 0f, 0.02f),
                Modulate = new Color(0.7f, 0.9f, 1f),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _playfieldRoot.AddChild(_editorLegend);
            GD.Print("OpenTyrianVR: HEIGHT EDITOR (ctrl+click select, drag orbit, RMB-drag pan, wheel zoom, Up/Down nudge, Shift coarse, 1..= bands, numpad +/- step, [/] rewind/forward, Shift fine, Backspace live, S save, P pause, N skip)");
        }

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
                // UV sub-rect of the frame this quad shows (the HUD split
                // renders sidebar/bottom-bar regions on their own quads).
                uniform vec2 uv0 = vec2(0.0, 0.0);
                uniform vec2 uv1 = vec2(1.0, 1.0);
                // E1 HUD split: 1 = discard the sidebar (x >= 264) and the
                // bottom bar (y >= 184) here; they render relocated.
                uniform int hud_split = 0;

                void fragment() {
                    vec2 size = vec2(textureSize(frame, 0));
                    vec2 pixel = (uv0 + UV * (uv1 - uv0)) * size;
                    vec2 seam = floor(pixel + 0.5);
                    vec2 dudv = fwidth(pixel);
                    pixel = seam + clamp((pixel - seam) / dudv, -0.5, 0.5);
                    vec4 c = texture(frame, pixel / size);
                    // HUD split: alpha-zero (NOT discard) so the depth
                    // prepass also skips these texels and the tile canvas
                    // shows through where the sidebar/bottom bar sat.
                    if (hud_split == 1 && (pixel.x >= 264.0 || pixel.y >= 184.0))
                        c = vec4(0.0);
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
        _laneMaterial = material;

        // Flip root (v23 card-flip): the play CONTENT -- lane overlay, 3D
        // layers, target reticle -- mirrors vertically on levels running
        // the legacy vflip (special code 1).  Scale.Y animates +1 -> -1
        // through 0 (the squash-flip: heights never invert, nothing is
        // ever seen from behind); HUD quads and the room stay upright.
        _flipRoot = new Node3D { Name = "FlipRoot" };
        _playfieldRoot.AddChild(_flipRoot);

        var lane = new MeshInstance3D
        {
            Name = "Lane",
            Mesh = new QuadMesh { Size = new Vector2(LaneWidth, LaneHeight) },  // 320:200
            MaterialOverride = material,
        };
        _flipRoot.AddChild(lane);

        // E1 HUD split: the sidebar (frame x 264..320) and bottom bar
        // (y 184..200 under the play area) render on their own quads,
        // floated clear of the widened diorama; the lane discards those
        // regions while the split is live (in-level, 3D showing).
        _hudSidebar = BuildHudQuad(shader, "HudSidebar",
            new Vector2(264f / 320f, 0f), new Vector2(1f, 1f),
            PlayfieldGeometry.HudSidebarWidth, 200f,
            frameCenterX: PlayfieldGeometry.HudSidebarCenterX,
            frameCenterY: 100f, yawDeg: -18f);
        // Bottom bar returns directly beneath the original y=184 play edge.
        _hudBottomBar = BuildHudQuad(shader, "HudBottomBar",
            new Vector2(0f, 184f / 200f), new Vector2(264f / 320f, 1f),
            264f, 16f, frameCenterX: 132f, frameCenterY: 192f, yawDeg: 0f);

        BuildHandSteering();

        _snapshotLayer = new SnapshotLayer { Name = "SnapshotLayer", EnableBackground = Render3DBackground };
        _flipRoot.AddChild(_snapshotLayer);

        MeshInstance3D BuildHudQuad(Shader laneShader, string name, Vector2 uv0, Vector2 uv1,
                                    float widthPx, float heightPx,
                                    float frameCenterX, float frameCenterY, float yawDeg)
        {
            var m = new ShaderMaterial { Shader = laneShader };
            m.SetShaderParameter("frame", _texture);
            m.SetShaderParameter("uv0", uv0);
            m.SetShaderParameter("uv1", uv1);
            var quad = new MeshInstance3D
            {
                Name = name,
                Mesh = new QuadMesh { Size = new Vector2(widthPx / 320f * LaneWidth, heightPx / 200f * LaneHeight) },
                MaterialOverride = m,
                Position = new Vector3((frameCenterX / 320f - 0.5f) * LaneWidth,
                                       (0.5f - frameCenterY / 200f) * LaneHeight, 0.03f),
                Rotation = new Vector3(0f, Mathf.DegToRad(yawDeg), 0f),
                Visible = false,
            };
            _playfieldRoot.AddChild(quad);
            return quad;
        }

        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        // Keep Quest at the OpenXR-recommended render-target scale while
        // validating stereo.  Supersampling the intermediate viewport here
        // can desynchronize multiview subimage extents from the runtime's
        // per-eye projections on some mobile Vulkan paths.
        GetViewport().Scaling3DScale = _xrActive ? 1.0f : 1.4f;

        _diagnostics = new Label3D
        {
            Name = "Diagnostics",
            Text = "starting...",
            PixelSize = 0.0008f,
            // Below the relocated bottom bar (E1 HUD split).
            Position = new Vector3(0f, -0.56f, 0.02f),
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
        _flipRoot.AddChild(_targetReticle);  // mirrors with the play content

        _controlRect.Visible = false;
        _targetReticle.Visible = false;
    }

    private void StartGame()
    {
        uint nativeAbi = OtyrNative.GetAbiVersion();
        if (nativeAbi != OtyrNative.AbiVersion)
            throw new InvalidOperationException($"native ABI {nativeAbi}, expected {OtyrNative.AbiVersion}");

        string dataDir = OS.GetName() == "Android"
            ? ExtractAndroidData()
            : Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "tyrian21"));
        string userDir = ProjectSettings.GlobalizePath("user://");
        // E2-full is the VR product's sim: pinned offsets (hitbox truth),
        // full-width travel, wide active windows.
        var flags = OtyrNative.ConfigFlags.SuppressEntityDraw | OtyrNative.ConfigFlags.SimDeparallax;
        // The hosted SDL2 build does not ship SDLActivity's Java audio bridge.
        // Keep Quest silent until audio is routed through Godot; enabling SDL's
        // Android backend here aborts inside AudioTrack during startup.
        if (OS.GetName() != "Android")
            flags |= OtyrNative.ConfigFlags.EnableAudio;
        // OTYR_MUTE=1: no game audio (solo test runs; the attract demo is
        // loud and the tester may be doing something else entirely).
        if (System.Environment.GetEnvironmentVariable("OTYR_MUTE") == "1")
        {
            GD.Print("OpenTyrianVR: OTYR_MUTE=1, audio disabled");
            flags &= ~OtyrNative.ConfigFlags.EnableAudio;
        }
        if (Render3DBackground)
            flags |= OtyrNative.ConfigFlags.SuppressBackground;
        if (RenderProudText)
            flags |= OtyrNative.ConfigFlags.SuppressText;
        var config = OtyrNative.Config.Create(dataDir, flags, userDir: userDir);

        int rc = OtyrNative.SessionCreate(in config, (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Config>(), out _session);
        if (rc != OtyrNative.Ok)
            throw new InvalidOperationException($"session_create failed: {rc} ({OtyrNative.LastError()})");
        _sessionLive = true;

        _frame.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Frame>();
        GD.Print($"OpenTyrianVR: session up, data={dataDir}");

        if (CaptureRun)
        {
            using var dir = DirAccess.Open("user://");
            foreach (string file in dir.GetFiles())
                if (file.StartsWith("cap_f") && file.EndsWith(".jpg"))
                    dir.Remove(file);
            GD.Print("OpenTyrianVR: run capture on (every 2 s, named by frame)");
        }
    }

    private void ConfigureQuestRefresh(string phase)
    {
        if (_openXr == null || !_openXr.HasMethod("set_display_refresh_rate"))
        {
            GD.Print($"OpenTyrianVR: XR refresh {phase}: runtime extension unavailable; target=90Hz");
            return;
        }

        try
        {
            string available = _openXr.HasMethod("get_available_display_refresh_rates")
                ? _openXr.Call("get_available_display_refresh_rates").ToString()
                : "unknown";
            _openXr.Call("set_display_refresh_rate", QuestTargetRefreshRate);
            string actual = _openXr.HasMethod("get_display_refresh_rate")
                ? $"{_openXr.Call("get_display_refresh_rate").AsDouble():0.##}Hz"
                : "unknown";
            GD.Print($"OpenTyrianVR: XR refresh {phase}: available={available} requested=90Hz actual={actual}");
        }
        catch (Exception exception)
        {
            GD.PrintErr($"OpenTyrianVR: XR refresh {phase} failed: {exception.Message}");
        }
    }

    // The C core reads Tyrian assets with stdio, while Android res:// files
    // live inside Godot's PCK. Materialize the flat freeware data directory in
    // user:// on first launch (and refresh files whose packaged size changes).
    private static string ExtractAndroidData()
    {
        const string sourceDir = "res://tyrian21";
        const string targetDir = "user://tyrian21";
        string[] files = DirAccess.GetFilesAt(sourceDir);
        if (files.Length == 0)
            throw new InvalidOperationException("Quest package contains no Tyrian data files");

        Error mkdir = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(targetDir));
        if (mkdir != Error.Ok && mkdir != Error.AlreadyExists)
            throw new IOException($"cannot create Quest data directory: {mkdir}");

        int copied = 0;
        foreach (string file in files)
        {
            string source = $"{sourceDir}/{file}";
            string target = $"{targetDir}/{file}";
            using var input = Godot.FileAccess.Open(source, Godot.FileAccess.ModeFlags.Read);
            if (input == null)
                throw new IOException($"cannot read packaged Tyrian data: {source}");

            bool needsCopy = !Godot.FileAccess.FileExists(target);
            if (!needsCopy)
            {
                using var existing = Godot.FileAccess.Open(target, Godot.FileAccess.ModeFlags.Read);
                needsCopy = existing == null || existing.GetLength() != input.GetLength();
            }
            if (!needsCopy)
                continue;

            byte[] bytes = input.GetBuffer(checked((long)input.GetLength()));
            using var output = Godot.FileAccess.Open(target, Godot.FileAccess.ModeFlags.Write);
            if (output == null)
                throw new IOException($"cannot write extracted Tyrian data: {target}");
            output.StoreBuffer(bytes);
            copied++;
        }

        GD.Print($"OpenTyrianVR: Quest data ready ({files.Length} files, {copied} refreshed)");
        return ProjectSettings.GlobalizePath(targetDir);
    }

    // OTYR_CAPTURE=N: save the viewport to user://cap_N.png every ~2 s,
    // N captures total (OTYR_CAPTURE=1 keeps the historical 40); for
    // self-service visual verification -- window capture is unreliable on
    // multi-monitor setups and XR grabs the window entirely.
    private static readonly int CaptureCount = ParseCaptureCount();
    private static readonly uint RegressionFrameLimit = ParseUnsignedEnvironment("OTYR_TEST_FRAMES");
    private static readonly string CaptureDirectory =
        System.Environment.GetEnvironmentVariable("OTYR_CAPTURE_DIR")?.Trim() ?? "";
    private double _captureAccumulator;
    private int _captureIndex;

    // OTYR_CAPTURE_AT=n1,n2,...: capture the viewport the moment the frame
    // counter (the "(frame N)" in the diagnostics overlay) crosses each
    // target, as user://cap_at_N.png.  Demos are deterministic, so a frame
    // number quoted from an overlay screenshot is directly addressable.
    private static readonly uint[] CaptureAt = ParseCaptureAt();
    private int _captureAtIndex;

    // OTYR_CAPTURE_RUN=1: during user test runs, capture every 2 s named by
    // the frame counter (user://cap_f<frame>.jpg) so a frame number quoted
    // from the diagnostics overlay maps to the nearest file.  JPEG encode
    // runs off the main thread (a PNG encode stalls the headset).  Stale
    // captures are wiped at launch so quotes are unambiguous per session.
    private static readonly bool CaptureRun =
        System.Environment.GetEnvironmentVariable("OTYR_CAPTURE_RUN") == "1";
    private double _captureRunAccumulator;
    private int _captureRunCount;
    private const int CaptureRunMax = 1800;  // ~60 min runaway guard

    private SubViewport? _spectator;
    private Camera3D? _spectatorCamera;

    // OTYR_HEIGHT_EDITOR=1: flat leaned-camera hover-height editor.  Click an
    // enemy to select its type; Up/Down nudge height (Shift = coarse), 1-5
    // assign classes (ground/pickup/air-low/air-mid/air-high), S saves to
    // hover_heights.json, [/] scrub the retained timeline (Shift = one tick),
    // Backspace returns live, P pauses the game, N skips the level. Pair with
    // OTYR_INVULN=1 so the parked ghost player survives the level.
    private static readonly bool HeightEditor =
        System.Environment.GetEnvironmentVariable("OTYR_HEIGHT_EDITOR") == "1";
    // Ghost-mode testing outside the editor (invulnerable VR level sweeps):
    // the debug keys (N skip, K kill-all, PageUp/Down level jump) arm with
    // the same OTYR_INVULN the native side requires for them.
    private static readonly bool InvulnSession =
        System.Environment.GetEnvironmentVariable("OTYR_INVULN") == "1";
    // The unified band table drives the legend, the assignment keys, and
    // numpad +/- stepping.  Cls != null assigns a JSON class ("ground" =
    // surface-following); otherwise the band's height is set explicitly.
    // Key != '\0' binds the band to the 1..= row (12 keys); key-less bands
    // (air-low/high, UI/text) are reachable via numpad +/- stepping.
    private struct EditorBand
    {
        public float Z; public string? Cls; public string Label; public char Key;
    }
    private EditorBand[]? _editorBands;  // ascending by height; [0] = ground class

    private EditorBand[] BuildEditorBands()
    {
        var c = _snapshotLayer.ClassHeights;
        float H(string name, float fallback) => c.TryGetValue(name, out float v) ? v : fallback;
        return new[]
        {
            new EditorBand { Z = -0.005f, Label = "below ground (probe)", Key = '\0' },
            new EditorBand { Z = float.NaN, Cls = "ground", Label = "ground (+surf)", Key = '1' },
            new EditorBand { Z = 0.0006f, Label = "ground objects", Key = '2' },
            new EditorBand { Z = H("mid-under", 0.012f), Cls = "mid-under", Label = "mid-under", Key = '3' },
            new EditorBand { Z = 0.020f, Label = "clouds (lo)", Key = '4' },
            new EditorBand { Z = 0.025f, Label = "clouds (hi)", Key = '5' },
            new EditorBand { Z = H("platform-under", 0.0285f), Cls = "platform-under", Label = "platform-under", Key = '6' },
            new EditorBand { Z = 0.030f, Label = "platforms", Key = '7' },
            new EditorBand { Z = 0.0315f, Label = "platform objects", Key = '8' },
            new EditorBand { Z = H("air-low", 0.033f), Cls = "air-low", Label = "air-low", Key = '\0' },
            new EditorBand { Z = H("air-mid", 0.0355f), Cls = "air-mid", Label = "air-mid", Key = '9' },
            new EditorBand { Z = H("air-high", 0.038f), Cls = "air-high", Label = "air-high", Key = '\0' },
            new EditorBand { Z = H("pickup", 0.040f), Cls = "pickup", Label = "player/pickup", Key = '0' },
            new EditorBand { Z = 0.041f, Label = "shots", Key = '-' },
            new EditorBand { Z = H("over-top", 0.075f), Cls = "over-top", Label = "over-top", Key = '=' },
            new EditorBand { Z = 0.090f, Label = "UI/text", Key = '\0' },
        };
    }

    private void EditorAssignBand(in EditorBand band)
    {
        if (band.Cls != null)
            _snapshotLayer.EditorSetClass(_editorSelected, band.Cls,
                float.IsNaN(band.Z) ? 0f : band.Z);
        else
            _snapshotLayer.EditorSetHeight(_editorSelected, band.Z);
    }

    // Editor level select: PageUp/PageDown cycle the episode-1 playable
    // sections via the v18 debug_section input (native honors it only in
    // OTYR_INVULN ghost mode).
    private static readonly (ushort Section, string Name)[] Ep1Levels =
    {
        (4, "TYRIAN"), (6, "ASTEROID1"), (7, "ASTEROID2"), (11, "SAVARA"),
        (14, "MINES"), (17, "BUBBLES"), (20, "DELIANI"), (22, "ASTEROID?"),
        (24, "MINEMAZE"), (26, "BONUS"), (29, "HOLES"), (30, "SAVARA2"),
        (32, "SOH JIN"), (34, "WINDY"), (37, "ASSASSIN"), (39, "SAVARA V"),
        (42, "ALE"),
    };
    private int _editorLevelIndex = -1;
    private bool _editorHistoryOwnsPause;

    private void EditorPulsePause()
    {
        var pause = OtyrNative.InputFrame.Create(OtyrNative.Buttons.UiPause);
        OtyrNative.SubmitInput(_session, in pause, pause.StructSize);
        var release = OtyrNative.InputFrame.Create(OtyrNative.Buttons.None);
        OtyrNative.SubmitInput(_session, in release, release.StructSize);
        _lastButtons = OtyrNative.Buttons.None;
    }

    private void EditorStepTimeline(int direction)
    {
        if (_frame.InLevel == 0)
            return;
        bool entering = direction < 0 && !_snapshotLayer.EditorHistoryActive;
        if (entering && _frame.MenuPresent == 0)
        {
            EditorPulsePause();
            _editorHistoryOwnsPause = true;
        }
        int ticks = Input.IsKeyPressed(Key.Shift) ? 1 : 35;
        bool changed = _snapshotLayer.EditorStepHistory(_session, direction * ticks);
        if (!changed && entering && _editorHistoryOwnsPause)
        {
            EditorPulsePause();
            _editorHistoryOwnsPause = false;
        }
        if (!_snapshotLayer.EditorHistoryActive && _editorHistoryOwnsPause)
        {
            EditorPulsePause();
            _editorHistoryOwnsPause = false;
        }
    }

    private void EditorResumeTimeline()
    {
        _snapshotLayer.EditorResumeHistory(_session);
        if (_editorHistoryOwnsPause)
        {
            EditorPulsePause();
            _editorHistoryOwnsPause = false;
        }
    }

    private void EditorJumpLevel(int direction)
    {
        EditorResumeTimeline();
        _snapshotLayer.EditorClearHistory();
        if (_editorLevelIndex < 0)
        {
            // Seed from OTYR_START_SECTION so the first PageDown continues
            // from wherever the session booted.
            string env = System.Environment.GetEnvironmentVariable("OTYR_START_SECTION") ?? "";
            for (int i = 0; i < Ep1Levels.Length; i++)
                if (Ep1Levels[i].Section.ToString() == env)
                    _editorLevelIndex = i;
        }
        _editorLevelIndex = ((_editorLevelIndex + direction) % Ep1Levels.Length + Ep1Levels.Length) % Ep1Levels.Length;
        var (section, name) = Ep1Levels[_editorLevelIndex];
        GD.Print($"OpenTyrianVR: editor level jump -> section {section} ({name})");
        var jump = OtyrNative.InputFrame.Create(OtyrNative.Buttons.None);
        jump.DebugSection = section;
        OtyrNative.SubmitInput(_session, in jump, jump.StructSize);
        jump.DebugSection = 0;
        OtyrNative.SubmitInput(_session, in jump, jump.StructSize);
        _lastButtons = OtyrNative.Buttons.None;
        _editorSelected = 0;
        _editorSelectedLayer = -1;
    }

    private ushort _editorSelected;
    private int _editorSelectedLayer = -1;
    private string _editorSelectedLayerName = "";
    private float _editorSelectedLayerZ;
    private Label3D _editorLabel = null!;
    private Label3D _editorLegend = null!;
    private bool _editorLastClick;
    private readonly System.Collections.Generic.HashSet<Key> _editorHeld = new();

    // Editor orbit camera (Ctrl+LMB orbit, Ctrl+RMB pan, Ctrl+wheel zoom;
    // bare clicks stay selection).  Pivot starts at the lane center.
    private Camera3D _editorCamera = null!;
    private Vector3 _editorPivot = new(0f, 1.05f, -0.9f);
    private float _editorYaw;            // degrees
    private float _editorPitch = -14f;   // degrees
    private float _editorDist = 0.88f;

    private bool EditorKeyPressed(Key k)
    {
        bool down = Input.IsKeyPressed(k);
        if (down && _editorHeld.Add(k))
            return true;
        if (!down)
            _editorHeld.Remove(k);
        return false;
    }

    // The viewport captures read from: the spectator in XR, the window flat.
    private Viewport CaptureViewport => _spectator ?? GetViewport();

    private static int ParseCaptureCount()
    {
        string value = System.Environment.GetEnvironmentVariable("OTYR_CAPTURE") ?? "";
        if (!int.TryParse(value, out int count) || count <= 0)
            return 0;
        return count == 1 ? 40 : count;
    }

    private static uint[] ParseCaptureAt()
    {
        string value = System.Environment.GetEnvironmentVariable("OTYR_CAPTURE_AT") ?? "";
        var targets = new System.Collections.Generic.List<uint>();
        foreach (string part in value.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(part.Trim(), out uint frame))
                targets.Add(frame);
        targets.Sort();
        return targets.ToArray();
    }

    private static uint ParseUnsignedEnvironment(string name)
    {
        string value = System.Environment.GetEnvironmentVariable(name) ?? "";
        return uint.TryParse(value, out uint parsed) ? parsed : 0;
    }

    private static string CapturePath(string fileName)
    {
        if (CaptureDirectory.Length == 0)
            return $"user://{fileName}";
        Directory.CreateDirectory(CaptureDirectory);
        return Path.Combine(CaptureDirectory, fileName);
    }

    public override void _Process(double delta)
    {
        if (_xrRefreshLogCountdown > 0 && --_xrRefreshLogCountdown == 0)
            ConfigureQuestRefresh("settled");

        // Quest preserves the runtime/Guardian heading across launches.  The
        // scene is authored around a seated head at the origin facing -Z, so
        // center that authored space on the live HMD once tracking has had a
        // few rendered frames to settle.  Without this, the board can appear
        // far off to one side depending on the saved Guardian forward.
        if (_xrActive && !_xrStartupCentered && ++_xrStartupFrames >= 3)
        {
            XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
            _xrStartupCentered = true;
            _xrStereoLogCountdown = 2;
            GD.Print("OpenTyrianVR: XR presentation centered on startup HMD pose");
        }
        if (_xrStereoLogCountdown > 0 && --_xrStereoLogCountdown == 0)
            LogXrStereo("recentered");

        if (!_sessionLive)
            return;

        ulong hostWorkStartUsec = Time.GetTicksUsec();

        if (CaptureCount > 0)
        {
            _captureAccumulator += delta;
            if (_captureAccumulator >= 2.0 && _captureIndex < CaptureCount)
            {
                _captureAccumulator = 0;
                CaptureViewport.GetTexture().GetImage().SavePng(CapturePath($"cap_{_captureIndex++:D3}.png"));
            }
        }

        // Captures run BEFORE any polling: the viewport texture is the
        // PREVIOUS render frame, which corresponds to the pre-poll frame
        // and snapshot state -- so the logged tick matches the image's
        // actual content (capturing after PollFrame mixed tick N pixels
        // with a tick N-1 scene and faked a 1-px tile offset in the
        // alignment probe).
        while (_captureAtIndex < CaptureAt.Length && _frame.FrameNumber >= CaptureAt[_captureAtIndex])
        {
            CaptureViewport.GetTexture().GetImage().SavePng(
                CapturePath($"cap_at_{CaptureAt[_captureAtIndex]}.png"));
            GD.Print($"OpenTyrianVR: cap_at_{CaptureAt[_captureAtIndex]} level_tick={_frame.LevelTick}");
            ++_captureAtIndex;
        }

        if (CaptureRun)
        {
            _captureRunAccumulator += delta;
            if (_captureRunAccumulator >= 2.0 && _captureRunCount < CaptureRunMax)
            {
                _captureRunAccumulator = 0;
                ++_captureRunCount;
                var image = CaptureViewport.GetTexture().GetImage();
                string path = CapturePath($"cap_f{_frame.FrameNumber:D6}.jpg");
                System.Threading.Tasks.Task.Run(() => image.SaveJpg(path, 0.8f));
            }
        }

        PollFrame();
        bool legacyFallback = _frame.LegacyFallback != 0;
        if (!_loggedPresentationMode || legacyFallback != _lastLegacyFallback)
        {
            _loggedPresentationMode = true;
            _lastLegacyFallback = legacyFallback;
            _presentationTransitions++;
            GD.Print($"OpenTyrianVR: presentation -> {(legacyFallback ? "complete legacy fallback" : "hybrid 3D")}" +
                     $" tick={_frame.LevelTick} storm={_frame.StormWater} flip={_frame.FlipCode}");
        }
        _regressionHybridSeen |= !legacyFallback && _frame.InLevel != 0;
        _regressionLegacySeen |= legacyFallback && _frame.InLevel != 0;
        _regressionFlipSeen |= _frame.FlipCode == 1 && _frame.InLevel != 0;
        _regressionStormSeen |= _frame.StormWater != 0 && _frame.InLevel != 0;
        PollPlayerState();
        _snapshotLayer.Poll(_session, _palette);
        _regressionMaxShadows = Math.Max(_regressionMaxShadows, _snapshotLayer.VirtualShadowCount);
        _regressionMaxMapShadows = Math.Max(_regressionMaxMapShadows, _snapshotLayer.MapCastShadowCount);
        _regressionMaxReceiverLayers = Math.Max(_regressionMaxReceiverLayers,
                                                _snapshotLayer.ElevatedReceiverLayerCount);
        _regressionMaxCells = Math.Max(_regressionMaxCells, _snapshotLayer.CellCount);
        // Menus, pause, and quit-to-title stop gameplay ticks; the 3D scene
        // (sprites AND background layers) must not linger over them.  A
        // legacy-fallback level (smoothie warp) draws its complete frame
        // flat instead -- the 3D layers would double every entity.
        // MenuPresent hides the scene the INSTANT a pause/menu present
        // arrives; the tick-staleness check (250 ms) alone left the whole
        // 3D scene floating over the menu for a beat.  The height editor
        // works ON the frozen paused scene, so it keeps it visible.
        _snapshotLayer.Visible = HeightEditor
            ? _frame.InLevel != 0 && _frame.LegacyFallback == 0
            : _inGameplay && _frame.LegacyFallback == 0 && _frame.MenuPresent == 0;
        _snapshotLayer.SetStorm(_frame.StormWater);
        // E1 HUD split follows the 3D scene: in-level the lane drops the
        // sidebar/bottom-bar regions and the floating quads show them;
        // menus/fallback restore the whole flat frame.
        bool hudSplit = _snapshotLayer.Visible;
        _laneMaterial.SetShaderParameter("hud_split", hudSplit ? 1 : 0);
        _hudSidebar.Visible = hudSplit;
        _hudBottomBar.Visible = hudSplit;

        // Card-flip (v23): squash-flip the play content toward the level's
        // mirror state -- Scale.Y through zero, ~0.5 s, heights unaffected.
        float flipTarget = _frame.FlipCode == 1 && _frame.InLevel != 0 ? -1f : 1f;
        _flipScale = Mathf.MoveToward(_flipScale, flipTarget, 4f * (float)GetProcessDeltaTime());
        _flipRoot.Scale = new Vector3(1f, Mathf.Abs(_flipScale) < 0.02f ? 0.02f : _flipScale, 1f);
        if (HeightEditor)
            UpdateHeightEditor();
        else if (InvulnSession)
        {
            // Invulnerable VR sweep: level jump on the keyboard.
            if (EditorKeyPressed(Key.Pagedown))
                EditorJumpLevel(1);
            if (EditorKeyPressed(Key.Pageup))
                EditorJumpLevel(-1);
        }
        UpdateChecklistInput();
        SubmitInput();
        UpdateDiagnostics(delta);
        RecordPerformance(hostWorkStartUsec, delta);
    }

    private void RecordPerformance(ulong hostWorkStartUsec, double delta)
    {
        ulong now = Time.GetTicksUsec();
        double hostMs = (now - hostWorkStartUsec) / 1000.0;
        _perfHostTotalMs += hostMs;
        _perfHostMaxMs = Math.Max(_perfHostMaxMs, hostMs);
        _perfHostSamples++;
        double frameMs = delta * 1000.0;
        _perfFrameMaxMs = Math.Max(_perfFrameMaxMs, frameMs);
        // Quest targets 90 Hz (11.11 ms); allow a small scheduler tolerance.
        // Flat diagnostics retain their traditional 60 Hz-oriented threshold.
        double longFrameThresholdMs = _xrActive ? 12.0 : 16.67;
        if (frameMs > longFrameThresholdMs)
            _perfLongFrames++;

        if (_inGameplay)
        {
            _perfPlayerMinX = Math.Min(_perfPlayerMinX, _playerState.X);
            _perfPlayerMaxX = Math.Max(_perfPlayerMaxX, _playerState.X);
        }

        if (!_regressionFinished && RegressionFrameLimit > 0 &&
            _frame.FrameNumber > RegressionFrameLimit && _captureAtIndex >= CaptureAt.Length)
        {
            _regressionFinished = true;
            GD.Print("OpenTyrianVR: REGRESSION " +
                     $"frames={_frame.FrameNumber} hybrid={(_regressionHybridSeen ? 1 : 0)} " +
                     $"legacy={(_regressionLegacySeen ? 1 : 0)} " +
                     $"flip={(_regressionFlipSeen ? 1 : 0)} storm={(_regressionStormSeen ? 1 : 0)} " +
                     $"transitions={_presentationTransitions} max_cells={_regressionMaxCells} " +
                     $"max_cast_shadows={_regressionMaxShadows} " +
                     $"max_map_cast_shadows={_regressionMaxMapShadows} " +
                     $"max_receiver_layers={_regressionMaxReceiverLayers} captures={_captureAtIndex}");
            ShutDown();
            GetTree().Quit();
            return;
        }

        if (_perfWindowStartUsec == 0)
        {
            _perfWindowStartUsec = now;
            return;
        }
        double windowSeconds = (now - _perfWindowStartUsec) / 1_000_000.0;
        if (windowSeconds < 5.0)
            return;

        double hostAverageMs = _perfHostSamples > 0 ? _perfHostTotalMs / _perfHostSamples : 0.0;
        double engineProcessMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double videoMiB = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);
        double managedMiB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        string rangeProbe = float.IsFinite(_perfPlayerMinX)
            ? $" x=player[{_perfPlayerMinX:0},{_perfPlayerMaxX:0}] hand[{_perfHandMinX:0.000},{_perfHandMaxX:0.000}] target[{_perfTargetMinX:0},{_perfTargetMaxX:0}]"
            : "";

        GD.Print($"OpenTyrianVR: PERF render={Engine.GetFramesPerSecond()}Hz game={_gameFps:0.0}Hz " +
                 $"engine_cpu={engineProcessMs:0.00}ms host_cpu={hostAverageMs:0.00}/{_perfHostMaxMs:0.00}ms(avg/max) " +
                 $"frame_max={_perfFrameMaxMs:0.00}ms long_frames={_perfLongFrames} " +
                 $"draws={drawCalls:0} objects={objects:0} prims={primitives:0} " +
                 $"video={videoMiB:0.0}MiB managed={managedMiB:0.0}MiB " +
                 $"cells={_snapshotLayer.CellCount} visible={_snapshotLayer.VisibleInstanceCount} " +
                 $"cast_shadows={_snapshotLayer.VirtualShadowCount} " +
                 $"assemblies={_snapshotLayer.RigidAssemblyCount}/{_snapshotLayer.SeamGuardCellCount}(groups/cells) " +
                 $"snapshot_gap={_snapshotLayer.LastTickGap}/{_snapshotLayer.MaxTickGap}(last/max) " +
                 $"missed={_snapshotLayer.SkippedTicksTotal}{rangeProbe}");

        _perfWindowStartUsec = now;
        _perfHostTotalMs = 0.0;
        _perfHostMaxMs = 0.0;
        _perfHostSamples = 0;
        _perfFrameMaxMs = 0.0;
        _perfLongFrames = 0;
        _perfPlayerMinX = float.PositiveInfinity;
        _perfPlayerMaxX = float.NegativeInfinity;
        _perfHandMinX = float.PositiveInfinity;
        _perfHandMaxX = float.NegativeInfinity;
        _perfTargetMinX = float.PositiveInfinity;
        _perfTargetMaxX = float.NegativeInfinity;
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

    /// <summary>Editor camera controls: Ctrl+LMB drag orbits, Ctrl+RMB drag
    /// pans the pivot, Ctrl+wheel zooms.  Godot input events (not polling)
    /// so drags accumulate per motion event.</summary>
    public override void _Input(InputEvent ev)
    {
        // Bare mouse = camera; Ctrl (objects) and Alt (layers) reserve the
        // mouse for selection.
        if (!HeightEditor || Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Alt))
            return;

        if (ev is InputEventMouseMotion motion)
        {
            if (motion.ButtonMask.HasFlag(MouseButtonMask.Left))
            {
                _editorYaw -= motion.Relative.X * 0.35f;
                _editorPitch = Mathf.Clamp(_editorPitch - motion.Relative.Y * 0.35f, -85f, 40f);
            }
            else if (motion.ButtonMask.HasFlag(MouseButtonMask.Right))
            {
                Basis basis = _editorCamera.GlobalTransform.Basis;
                float k = _editorDist * 0.0011f;
                _editorPivot += basis.X * (-motion.Relative.X * k) + basis.Y * (motion.Relative.Y * k);
            }
        }
        else if (ev is InputEventMouseButton { Pressed: true } wheel)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp)
                _editorDist = Mathf.Max(0.12f, _editorDist * 0.88f);
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
                _editorDist = Mathf.Min(3.5f, _editorDist / 0.88f);
        }
    }

    private void UpdateEditorCamera()
    {
        // Orbit pose from yaw/pitch/distance around the pivot.
        Basis rot = Basis.FromEuler(new Vector3(
            Mathf.DegToRad(_editorPitch), Mathf.DegToRad(_editorYaw), 0f));
        Vector3 back = rot * Vector3.Back;  // camera sits behind the pivot
        _editorCamera.GlobalTransform = new Transform3D(rot, _editorPivot + back * _editorDist);
    }

    private void UpdateHeightEditor()
    {
        UpdateEditorCamera();
        // A level/asset boundary can invalidate the retained presentation
        // segment. If that happens during the pause handshake, return the
        // game to the state we found it in instead of leaving it paused.
        if (_editorHistoryOwnsPause && !_snapshotLayer.EditorHistoryActive)
        {
            EditorPulsePause();
            _editorHistoryOwnsPause = false;
        }

        // Ctrl+click selects OBJECTS only; Alt+click selects fixed LAYERS
        // (clouds, platforms, ground).  Falling from object-pick through to
        // layers grabbed the layer whenever the zoom put cell centers out
        // of reach, making close-up object selection impossible.
        bool click = Input.IsMouseButtonPressed(MouseButton.Left) &&
                     (Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Alt));
        if (click && !_editorLastClick)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam != null)
            {
                Vector2 mouse = GetViewport().GetMousePosition();
                if (Input.IsKeyPressed(Key.Alt))
                {
                    if (_snapshotLayer.TryPickLayer(cam, mouse, out int layer, out float layerZ, out string layerName))
                    {
                        _editorSelected = 0;
                        _editorSelectedLayer = layer;
                        _editorSelectedLayerZ = layerZ;
                        _editorSelectedLayerName = layerName;
                    }
                }
                else if (_snapshotLayer.TryPick(cam, mouse, out ushort picked, out _))
                {
                    _editorSelected = picked;
                    _editorSelectedLayer = -1;
                }
            }
        }
        _editorLastClick = click;

        _editorBands ??= BuildEditorBands();

        // Home: snap the orbit to lane-perpendicular.  The lane is tilted
        // -42 about X, which rotates its NORMAL to 42 degrees elevation --
        // so the zero-parallax view is pitch -42 exactly (user-corrected;
        // and straight down at -90 is 48 degrees oblique to the lane).
        if (EditorKeyPressed(Key.Home))
        {
            _editorYaw = 0f;
            _editorPitch = -42f;
        }

        // PageUp/PageDown cycle levels; [/] scrub one second (Shift = one
        // simulation tick), Backspace returns to the live edge; B toggles
        // the red hazard (collider) halos.
        if (EditorKeyPressed(Key.Pagedown))
            EditorJumpLevel(1);
        if (EditorKeyPressed(Key.Pageup))
            EditorJumpLevel(-1);
        if (EditorKeyPressed(Key.Bracketleft))
            EditorStepTimeline(-1);
        if (EditorKeyPressed(Key.Bracketright))
            EditorStepTimeline(1);
        if (EditorKeyPressed(Key.Backspace))
            EditorResumeTimeline();
        if (EditorKeyPressed(Key.B))
            _snapshotLayer.HazardMarkersEnabled = !_snapshotLayer.HazardMarkersEnabled;
        _snapshotLayer.EditorHazardMarkers();
        _snapshotLayer.EditorReviewMarkers();

        if (_editorSelected != 0 && _frame.InLevel != 0)
        {
            float current = _snapshotLayer.EditorHeightOf(_editorSelected);
            float step = Input.IsKeyPressed(Key.Shift) ? 0.01f : 0.002f;
            if (EditorKeyPressed(Key.Up))
                _snapshotLayer.EditorSetHeight(_editorSelected,
                    (float.IsNaN(current) ? 0.004f : current) + step);
            // No floor: nudging below the tile plane is a legitimate probe
            // (sink an object through the ground to verify alignment).
            if (EditorKeyPressed(Key.Down))
                _snapshotLayer.EditorSetHeight(_editorSelected,
                    (float.IsNaN(current) ? 0.004f : current) - step);

            // Band assignment keys (1..9, 0, -, = -- height-ordered).
            foreach (ref readonly EditorBand band in _editorBands.AsSpan())
            {
                if (band.Key == '\0')
                    continue;
                Key key = band.Key switch
                {
                    '0' => Key.Key0,
                    '-' => Key.Minus,
                    '=' => Key.Equal,
                    _ => Key.Key1 + (band.Key - '1'),
                };
                if (EditorKeyPressed(key))
                    EditorAssignBand(in band);
            }

            // Numpad +/-: step the selection through ALL bands, including
            // the key-less ones (air-low/high, UI/text).
            int dir = (EditorKeyPressed(Key.KpAdd) ? 1 : 0) - (EditorKeyPressed(Key.KpSubtract) ? 1 : 0);
            if (dir != 0)
            {
                int index = EditorBandIndex(current);
                int next = Math.Clamp(index + dir, 0, _editorBands.Length - 1);
                if (next != index)
                    EditorAssignBand(in _editorBands[next]);
            }
        }
        else if (_editorSelectedLayer == 1 && _snapshotLayer.WaterCloudsSelectable &&
                 _frame.InLevel != 0)
        {
            // The lifted water-cloud layer is height-adjustable like an
            // object: Up/Down (or numpad +/-) nudge, S persists it as
            // classes["water-clouds"].
            float step = Input.IsKeyPressed(Key.Shift) ? 0.01f : 0.002f;
            int dir = ((EditorKeyPressed(Key.Up) || EditorKeyPressed(Key.KpAdd)) ? 1 : 0) -
                      ((EditorKeyPressed(Key.Down) || EditorKeyPressed(Key.KpSubtract)) ? 1 : 0);
            if (dir != 0)
            {
                _snapshotLayer.EditorSetWaterCloudHeight(
                    _snapshotLayer.EditorWaterCloudHeight + dir * step);
                _editorSelectedLayerZ = _snapshotLayer.EditorWaterCloudHeight;
            }
        }

        if (EditorKeyPressed(Key.S))
            GD.Print($"OpenTyrianVR: editor saved {_snapshotLayer.EditorSave()} type edit(s)");

        _snapshotLayer.EditorHighlight(_editorSelected);
        _snapshotLayer.EditorHighlightLayer(_editorSelectedLayer);

        float selectedHeight = 0f;
        bool hasSelection = false;
        if (_editorSelected != 0)
        {
            hasSelection = true;
            selectedHeight = _snapshotLayer.EditorHeightOf(_editorSelected);
            _editorLabel.Visible = true;
            bool onScreen = _snapshotLayer.TryLocateType(_editorSelected, out _);
            string heightText = float.IsNaN(selectedHeight)
                ? $"surface-following [live {_snapshotLayer.EditorLiveZOf(_editorSelected)}]"
                : selectedHeight.ToString("0.####");
            _editorLabel.Text =
                $"selected type {_editorSelected}  h={heightText}  " +
                _snapshotLayer.EditorDescribe(_editorSelected) +
                (_snapshotLayer.IsReviewType(_editorSelected) ? "  [REVIEW]" : "") +
                (onScreen ? "" : "  (not on screen)");
        }
        else if (_editorSelectedLayer >= 0)
        {
            hasSelection = true;
            bool cloudLayer = _editorSelectedLayer == 1 && _snapshotLayer.WaterCloudsSelectable;
            selectedHeight = cloudLayer ? _snapshotLayer.EditorWaterCloudHeight : _editorSelectedLayerZ;
            _editorLabel.Visible = true;
            _editorLabel.Text =
                $"selected {_editorSelectedLayerName}  h={selectedHeight:0.####}  " +
                (cloudLayer ? "(Up/Down adjusts, S saves)"
                            : "(layer heights are structural -- not editable here)");
        }
        else
            _editorLabel.Visible = false;

        _editorLegend.Text = BuildEditorLegend(hasSelection, selectedHeight);
    }

    /// <summary>Index of the band closest to the given height (NaN = the
    /// ground class at index 0); used for legend marking and stepping.</summary>
    private int EditorBandIndex(float height)
    {
        int best = 0;
        float bestDelta = float.MaxValue;
        for (int i = 0; i < _editorBands!.Length; i++)
        {
            if (float.IsNaN(_editorBands[i].Z))
            {
                if (float.IsNaN(height))
                    return i;  // ground class matches the ground band
                continue;
            }
            float d = Math.Abs(_editorBands[i].Z - height);
            if (d < bestDelta)
            {
                bestDelta = d;
                best = i;
            }
        }
        return best;
    }

    private string BuildEditorLegend(bool hasSelection, float selectedHeight)
    {
        _editorBands ??= BuildEditorBands();
        int marked = hasSelection ? EditorBandIndex(selectedHeight) : -1;
        // Only mark when actually AT the band (not merely nearest).
        if (marked >= 0 && !float.IsNaN(_editorBands[marked].Z) &&
            Math.Abs(_editorBands[marked].Z - selectedHeight) > 0.0008f)
            marked = -1;

        string timeline = _snapshotLayer.EditorHistoryActive
            ? $"REWIND  -{_snapshotLayer.EditorHistorySecondsBack:0.0}s / {_snapshotLayer.EditorHistorySecondsAvailable:0.0}s  [/] Shift=fine  Backspace=live\n"
            : $"LIVE  history {_snapshotLayer.EditorHistorySecondsAvailable:0.0}s  [/] rewind/forward  Shift=1 tick\n";
        var sb = new System.Text.StringBuilder(timeline + "HEIGHT BANDS   key\n");
        for (int i = _editorBands.Length - 1; i >= 0; i--)
        {
            ref readonly EditorBand band = ref _editorBands[i];
            sb.Append(i == marked ? "> " : "  ")
              .Append((float.IsNaN(band.Z) ? "+surf" : band.Z.ToString("0.####")).PadRight(7))
              .Append(band.Label.PadRight(17))
              .Append(band.Key == '\0' ? "+/-" : band.Key.ToString())
              .Append('\n');
        }
        return sb.ToString();
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
        int rc;
        fixed (OtyrNative.Frame* frame = &_frame)
            rc = OtyrNative.AcquireFrame(_session, frame, _frame.StructSize, 0);
        if (rc == OtyrNative.InvalidSession)
        {
            // The player quit from inside the game; the game thread halted.
            GD.Print("OpenTyrianVR: game quit, closing host");
            ShutDown();
            GetTree().Quit();
            return;
        }
        if (rc != OtyrNative.Ok)
        {
            if (rc != OtyrNative.Timeout)
                GD.PushError($"OpenTyrianVR: acquire_frame failed: {rc}, managed size={_frame.StructSize}");
            return;  // Timeout: no new legacy frame this render frame.
        }
        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            GD.Print($"OpenTyrianVR: managed received frame {_frame.FrameNumber} ({_frame.Width}x{_frame.Height})");
        }

        fixed (OtyrNative.Frame* frame = &_frame)
        {
            for (int p = 0; p < 256; ++p)
                _palette[p] = frame->Palette[p];

            for (int i = 0; i < OtyrNative.FrameWidth * OtyrNative.FrameHeight; ++i)
            {
                byte index = frame->Pixels[i];
                // Color key: with the native background suppressed, the fill
                // index means "nothing drawn here" and the 3D tile layers
                // show through.  Only while a level is active: menus redraw
                // the frame fully and legitimately use the key index in art
                // (keying there punched black holes in the title logo).
                // Data is premultiplied (keyed pixels fully zero) so
                // linear/mipmap filtering can't bleed key-colored fringes.
                // Keying pauses during menu presents: menu art legitimately
                // uses the key index (the volume slider borders), and the
                // native side blackens the frozen backdrop fill at menu
                // entry so nothing magenta shows through.
                if (Render3DBackground && _frame.InLevel != 0 && _frame.LegacyFallback == 0 &&
                    (_frame.MenuPresent == 0 || HeightEditor) && index == OtyrNative.BgKeyIndex)
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

        if (HeightEditor)
        {
            // Editor: menu navigation + pause + skip only.  The ghost player
            // never moves or fires.  Arrows navigate menus while OUT of a
            // level; in-level they belong to the editor's height nudge.
            if (Input.IsKeyPressed(Key.Enter)) buttons |= OtyrNative.Buttons.UiConfirm;
            if (Input.IsKeyPressed(Key.Escape)) buttons |= OtyrNative.Buttons.UiCancel;
            if (Input.IsKeyPressed(Key.Space)) buttons |= OtyrNative.Buttons.UiSpace;
            if (Input.IsKeyPressed(Key.P) && !_snapshotLayer.EditorHistoryActive)
                buttons |= OtyrNative.Buttons.UiPause;
            if (Input.IsKeyPressed(Key.N)) buttons |= OtyrNative.Buttons.DebugSkip;
            if (Input.IsKeyPressed(Key.K)) buttons |= OtyrNative.Buttons.DebugKill;
            if (_frame.InLevel == 0)
            {
                if (Input.IsKeyPressed(Key.Up)) buttons |= OtyrNative.Buttons.Up;
                if (Input.IsKeyPressed(Key.Down)) buttons |= OtyrNative.Buttons.Down;
                if (Input.IsKeyPressed(Key.Left)) buttons |= OtyrNative.Buttons.Left;
                if (Input.IsKeyPressed(Key.Right)) buttons |= OtyrNative.Buttons.Right;
            }

            if (buttons == _lastButtons)
                return;
            _lastButtons = buttons;
            var editorInput = OtyrNative.InputFrame.Create(buttons);
            OtyrNative.SubmitInput(_session, in editorInput, editorInput.StructSize);
            return;
        }

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
        if (InvulnSession)
        {
            // Ghost sweep: past bosses / kill-gates from the keyboard
            // (native side ignores these without OTYR_INVULN anyway).
            if (Input.IsKeyPressed(Key.N)) buttons |= OtyrNative.Buttons.DebugSkip;
            if (Input.IsKeyPressed(Key.K)) buttons |= OtyrNative.Buttons.DebugKill;
        }

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
                _xrStereoLogCountdown = 2;
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

    private void LogXrStereo(string reason)
    {
        XRInterface? xr = XRServer.PrimaryInterface;
        if (xr == null)
        {
            GD.PushError($"OpenTyrianVR: XR stereo diagnostic ({reason}): no primary interface");
            return;
        }

        uint views = xr.GetViewCount();
        Vector2 target = xr.GetRenderTargetSize();
        GD.Print($"OpenTyrianVR: XR stereo diagnostic ({reason}): views={views}, target={target.X:0}x{target.Y:0}, " +
                 $"tracking={xr.GetTrackingStatus()}, viewport_scale={GetViewport().Scaling3DScale:0.00}");
        GD.Print($"OpenTyrianVR: XR HMD={XRServer.GetHmdTransform()}");
        GD.Print($"OpenTyrianVR: XR reference={XRServer.GetReferenceFrame()}");
        GD.Print($"OpenTyrianVR: XR origin={_xrOrigin.GlobalTransform}, camera={_xrCamera.GlobalTransform}");

        Transform3D[] eyes = new Transform3D[views];
        for (uint view = 0; view < views; ++view)
        {
            eyes[view] = xr.GetTransformForView(view, _xrOrigin.GlobalTransform);
            Projection projection = xr.GetProjectionForView(view, 1.0, _xrCamera.Near, _xrCamera.Far);
            GD.Print($"OpenTyrianVR: XR eye[{view}]={eyes[view]}");
            GD.Print($"OpenTyrianVR: XR projection[{view}]={projection}");
        }
        if (views == 2)
            GD.Print($"OpenTyrianVR: XR calculated IPD={eyes[0].Origin.DistanceTo(eyes[1].Origin):0.000000} m");
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

        // Exact endpoint-to-endpoint map: the hand rectangle and playable
        // simulation area are the same normalized coordinate space.  Do not
        // add comfort saturation/deadzones here; those break the 1:1 contract.
        // Rectangle-up = screen-up = smaller
        // Tyrian y (sim y grows downward).  On a MIRRORED level (card-flip),
        // screen-up is sim-down: the hand box flips with the view so hand-up
        // stays visually up (the sim's own mouse compensation is bypassed in
        // target mode -- no double inversion).
        bool mirrored = _flipScale < 0f;
        float targetX = Mathf.Remap(lx, -ControlRectWidth / 2f,
            ControlRectWidth / 2f, GameMinX, GameMaxX);
        float targetY = mirrored
            ? Mathf.Remap(ly, -ControlRectHeight / 2f, ControlRectHeight / 2f, GameMinY, GameMaxY)
            : Mathf.Remap(ly, -ControlRectHeight / 2f, ControlRectHeight / 2f, GameMaxY, GameMinY);

        _perfHandMinX = Math.Min(_perfHandMinX, local.X);
        _perfHandMaxX = Math.Max(_perfHandMaxX, local.X);
        _perfTargetMinX = Math.Min(_perfTargetMinX, targetX);
        _perfTargetMaxX = Math.Max(_perfTargetMaxX, targetX);

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
        bool exactEndpointChanged =
            ((lx <= -ControlRectWidth / 2f || lx >= ControlRectWidth / 2f) && targetX != _handTargetX) ||
            ((ly <= -ControlRectHeight / 2f || ly >= ControlRectHeight / 2f) && targetY != _handTargetY);
        _handTargetActive = true;
        if (!_lastTargetActive ||
            exactEndpointChanged ||
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
