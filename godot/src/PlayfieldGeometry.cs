namespace OpenTyrianVR;

/// <summary>
/// Authoritative presentation bounds in the legacy 320x200 frame coordinate
/// system. Keep terrain, entity clipping/culling, picking, and detached HUD
/// placement derived from this single definition.
/// </summary>
public static class PlayfieldGeometry
{
    public const float MinX = -24f;
    public const float MaxX = 288f;
    public const float MinY = 0f;
    public const float MaxY = 184f;
    public const float Width = MaxX - MinX;
    public const float Height = MaxY - MinY;

    public const float HudSidebarWidth = 56f;
    public const float HudSidebarCenterX = MaxX + HudSidebarWidth / 2f;
}
