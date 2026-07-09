using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenTyrianVR;

/// <summary>
/// P/Invoke bindings for the opentyrian-core native library (src/otyr_host.h).
/// Struct layouts mirror the C ABI exactly; all structs are fixed-width and
/// append-only, guarded by struct_size and the ABI version handshake.
/// </summary>
public static unsafe class OtyrNative
{
    public const uint AbiVersion = 6;

    public const int FrameWidth = 320;
    public const int FrameHeight = 200;

    public const int Ok = 0;
    public const int InvalidSession = -3;
    public const int Timeout = -4;

    // OtyrInputFrame.buttons bits.
    [Flags]
    public enum Buttons : uint
    {
        None = 0,
        Up = 1 << 0,
        Down = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        Fire = 1 << 4,
        ChangeFire = 1 << 5,
        LeftSidekick = 1 << 6,
        RightSidekick = 1 << 7,
        UiConfirm = 1 << 8,
        UiCancel = 1 << 9,
        UiSpace = 1 << 10,
        UiPause = 1 << 11,
    }

    [Flags]
    public enum ConfigFlags : uint
    {
        None = 0,
        EnableAudio = 1 << 0,
        // Legacy frame shows only backgrounds/HUD; entities come from the snapshot.
        SuppressEntityDraw = 1 << 1,
    }

    // Presentation snapshot (ABI v6).
    public const int SnapshotSpriteMax = 1024;
    public const int SnapshotSoundMax = 8;
    public const int SheetCount = 9;
    public const int SheetCellW = 12;
    public const int SheetCellH = 14;
    public const int SheetCellMax = 1024;
    public const byte SheetInvalid = 0xff;

    public enum Category : byte
    {
        EnemySky = 0, EnemyGroundA, EnemyTop, EnemyGroundB,
        EnemyShot, PlayerShot, Player, Shadow, Sidekick, Explosion, Superpixel,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SnapshotSprite
    {
        public byte Category, Kind, Flags, FilterColor;
        public short X, Y;
        public ushort Index;   // 1-based cell index within the sheet
        public byte SheetId;
        public byte Aux;  // per-category metadata; enemies: enemyground flag
        public byte R1, R2, R3, R4;  // pads to exactly 16 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Snapshot
    {
        public uint StructSize;
        public uint LevelTick;
        public uint SheetEpoch;
        public uint SpriteCount;
        public uint SoundCount;
        public fixed byte SoundChannel[SnapshotSoundMax];
        public fixed byte SoundSample[SnapshotSoundMax];
        public fixed byte SpritesRaw[SnapshotSpriteMax * 16];  // SnapshotSprite[1024]
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SpriteSheet
    {
        public uint StructSize;
        public uint SheetEpoch;
        public uint CellCount;
        public fixed byte Pixels[SheetCellMax * SheetCellW * SheetCellH];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Flags;
        public uint Reserved;
        public fixed byte DataDir[260];
        public fixed byte HashLog[260];
        public fixed byte UserDir[260];

        public static Config Create(string dataDir, ConfigFlags flags = ConfigFlags.None,
                                     string hashLog = "", string userDir = "")
        {
            var config = new Config
            {
                StructSize = (uint)sizeof(Config),
                AbiVersion = OtyrNative.AbiVersion,
                Flags = (uint)flags,
            };
            WriteUtf8(config.DataDir, 260, dataDir);
            WriteUtf8(config.HashLog, 260, hashLog);
            WriteUtf8(config.UserDir, 260, userDir);
            return config;
        }

        private static void WriteUtf8(byte* destination, int capacity, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length >= capacity)
                throw new ArgumentException($"string too long for ABI field: {value}");
            for (int i = 0; i < bytes.Length; ++i)
                destination[i] = bytes[i];
            destination[bytes.Length] = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputFrame
    {
        public uint StructSize;
        public uint ButtonBits;
        // Mouse-accumulator units, consumed once per gameplay tick while set;
        // steady-state ship speed ~= value/4 px per tick, useful range +-30.
        public short AnalogDx;
        public short AnalogDy;
        // Absolute target following: the ship pursues (TargetX, TargetY) in
        // sim coordinates at up to TargetSpeed px/tick; the feedback loop
        // runs inside the simulation, so it is latency-immune.
        public byte UseTarget;
        public byte TargetSpeed;  // 0 = default (2 px/tick)
        public short TargetX;
        public short TargetY;

        public static InputFrame Create(Buttons buttons) => new()
        {
            StructSize = (uint)sizeof(InputFrame),
            ButtonBits = (uint)buttons,
        };

        public static InputFrame CreateWithTarget(Buttons buttons, short targetX, short targetY, byte speed = 0) => new()
        {
            StructSize = (uint)sizeof(InputFrame),
            ButtonBits = (uint)buttons,
            UseTarget = 1,
            TargetSpeed = speed,
            TargetX = targetX,
            TargetY = targetY,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public uint StructSize;
        public uint FrameNumber;
        public uint Width;
        public uint Height;
        public fixed byte Pixels[FrameWidth * FrameHeight];
        public fixed uint Palette[256];  // 0xAARRGGBB
        public uint LevelTick;  // gameplay tick this present belongs to (v6)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlayerState
    {
        public uint StructSize;
        public int X, Y;
        public uint Shield;
        public uint Armor;
        public uint Cash;
        public uint Lives;
        public uint IsAlive;
        public uint LevelTick;
        public int XVelocity;
        public int YVelocity;
    }

    private const string Dll = "opentyrian-core-x64-Release";

    [DllImport(Dll, EntryPoint = "otyr_abi_version")]
    public static extern uint GetAbiVersion();

    [DllImport(Dll, EntryPoint = "otyr_last_error")]
    public static extern int GetLastError(byte* buffer, uint bufferSize);

    [DllImport(Dll, EntryPoint = "otyr_session_create")]
    public static extern int SessionCreate(in Config config, uint configSize, out ulong session);

    [DllImport(Dll, EntryPoint = "otyr_session_destroy")]
    public static extern int SessionDestroy(ulong session);

    [DllImport(Dll, EntryPoint = "otyr_session_submit_input")]
    public static extern int SubmitInput(ulong session, in InputFrame input, uint inputSize);

    [DllImport(Dll, EntryPoint = "otyr_session_acquire_frame")]
    public static extern int AcquireFrame(ulong session, ref Frame frame, uint frameSize, uint timeoutMs);

    [DllImport(Dll, EntryPoint = "otyr_session_player_state")]
    public static extern int GetPlayerState(ulong session, ref PlayerState state, uint stateSize);

    // Pointer signatures: these structs exceed the interop marshaller's size
    // limit for by-ref parameters; raw pointers bypass marshalling entirely.
    [DllImport(Dll, EntryPoint = "otyr_session_snapshot")]
    public static extern int GetSnapshot(ulong session, Snapshot* snapshot, uint snapshotSize, uint timeoutMs);

    [DllImport(Dll, EntryPoint = "otyr_sprite_sheet")]
    public static extern int GetSpriteSheet(ulong session, uint sheetId, SpriteSheet* sheet, uint sheetSize);

    public static string LastError()
    {
        var buffer = stackalloc byte[256];
        int length = GetLastError(buffer, 256);
        return length > 0 ? Encoding.UTF8.GetString(buffer, Math.Min(length, 255)) : "";
    }

    /// <summary>
    /// Registers a resolver that loads the native core (and its SDL2
    /// dependencies) from res://native/&lt;rid&gt;/ so the library is found both
    /// in the editor and in exported builds.
    /// </summary>
    public static void RegisterResolver()
    {
        // ABI layout guards (mirrors the native static asserts).
        if (sizeof(SnapshotSprite) != 16 ||
            sizeof(Snapshot) != 36 + SnapshotSpriteMax * 16 ||
            sizeof(SpriteSheet) != 12 + SheetCellMax * SheetCellW * SheetCellH ||
            sizeof(Frame) != 16 + FrameWidth * FrameHeight + 1024 + 4)
            throw new InvalidOperationException("ABI struct layout mismatch");

        NativeLibrary.SetDllImportResolver(typeof(OtyrNative).Assembly, (name, assembly, searchPath) =>
        {
            if (name != Dll)
                return IntPtr.Zero;

            string nativeDir = Godot.ProjectSettings.GlobalizePath("res://native/win-x64/");

            // SDL2 must be loadable before the core; load it from the same
            // directory explicitly so the OS loader doesn't search elsewhere.
            foreach (var dep in new[] { "SDL2.dll", "SDL2_net.dll" })
                NativeLibrary.TryLoad(System.IO.Path.Combine(nativeDir, dep), out _);

            if (NativeLibrary.TryLoad(System.IO.Path.Combine(nativeDir, Dll + ".dll"), out var handle))
                return handle;

            return IntPtr.Zero;
        });
    }
}
