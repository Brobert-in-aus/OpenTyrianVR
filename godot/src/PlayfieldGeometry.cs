namespace OpenTyrianVR;

/// <summary>
/// Authoritative presentation bounds in the legacy 320x200 frame coordinate
/// system. Terrain stops at the reachable surface, while sprites retain a
/// wider envelope for legitimate ship/enemy overhang.
/// </summary>
public static class PlayfieldGeometry
{
    // The legal ship centres are 16..280. Split-wing variants emit a 24 px
    // left section at playerX-17, then frame rebasing subtracts 24: the
    // leftmost quad begins at -25. On the right the last covered pixel is
    // 287, so the exclusive boundary remains 288.
    public const float MinX = -25f;
    public const float MaxX = 288f;
    public const float MinY = 0f;
    public const float MaxY = 184f;
    public const float Width = MaxX - MinX;
    public const float Height = MaxY - MinY;

    public const float TerrainMinX = 0f;
    public const float TerrainMaxX = 264f;
    public const float TerrainWidth = TerrainMaxX - TerrainMinX;

    public const float HudSidebarWidth = 56f;
    public const float HudSidebarCenterX = MaxX + HudSidebarWidth / 2f;
}
