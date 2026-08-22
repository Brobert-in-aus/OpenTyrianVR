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

    // The authored maps include one side tile beyond each reachable edge.
    // Structures legitimately occupy those columns (for example E1 types
    // 522-527 at x=258..306). Render the whole continuous map span; using a
    // separate centre crop exposed a hard seam at x=0/264 and cut those
    // structures in half.
    public const float TerrainMinX = -24f;
    public const float TerrainMaxX = 288f;
    public const float TerrainWidth = TerrainMaxX - TerrainMinX;

    public const float HudSidebarWidth = 56f;
    public const float HudSidebarCenterX = MaxX + HudSidebarWidth / 2f;
}
