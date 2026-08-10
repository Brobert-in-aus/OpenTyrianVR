using Godot;

namespace OpenTyrianVR;

/// <summary>
/// Post-composites Tyrian's legacy full-playfield filters over the finished
/// 3D scene. Sampling the screen texture here keeps the result stereo-correct:
/// each eye distorts and shades its own already-rendered geometry.
/// </summary>
public partial class PresentationEffects : MeshInstance3D
{
    private readonly ShaderMaterial _material;

    public PresentationEffects()
    {
        Name = "PresentationEffects";
        float width = PlayfieldGeometry.Width / 320f;
        float height = PlayfieldGeometry.Height / 200f * 0.625f;
        Mesh = new QuadMesh { Size = new Vector2(width, height) };
        Position = new Vector3(
            ((PlayfieldGeometry.MinX + PlayfieldGeometry.MaxX) * 0.5f / 320f - 0.5f),
            (0.5f - (PlayfieldGeometry.MinY + PlayfieldGeometry.MaxY) * 0.5f / 200f) * 0.625f,
            0.12f);

        _material = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                    shader_type spatial;
                    render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never;

                    uniform sampler2D screen : hint_screen_texture, repeat_disable, filter_linear;
                    uniform int effect_mask = 0;
                    uniform float effect_time = 0.0;
                    uniform vec2 player_px = vec2(132.0, 140.0);
                    uniform vec4 play_rect_px;

                    varying vec2 play_uv;

                    void vertex() {
                        play_uv = UV;
                    }

                    vec3 nearby_blur(vec2 uv, float radius) {
                        vec2 px = 1.0 / vec2(textureSize(screen, 0));
                        vec3 c = textureLod(screen, uv, 0.0).rgb * 0.40;
                        c += textureLod(screen, uv + vec2(px.x * radius, 0.0), 0.0).rgb * 0.15;
                        c += textureLod(screen, uv - vec2(px.x * radius, 0.0), 0.0).rgb * 0.15;
                        c += textureLod(screen, uv + vec2(0.0, px.y * radius), 0.0).rgb * 0.15;
                        c += textureLod(screen, uv - vec2(0.0, px.y * radius), 0.0).rgb * 0.15;
                        return c;
                    }

                    void fragment() {
                        vec2 sample_uv = SCREEN_UV;

                        // Lava: a deterministic heat shimmer plus the legacy
                        // filter's strong red bias. The distortion is in
                        // screen pixels, so it is stable across eye targets.
                        if ((effect_mask & 1) != 0) {
                            vec2 px = 1.0 / vec2(textureSize(screen, 0));
                            sample_uv.x += sin(play_uv.y * 115.0 + effect_time * 8.0) * px.x * 2.0;
                            sample_uv.y += sin(play_uv.x * 83.0 - effect_time * 5.0) * px.y;
                        }

                        vec3 color = textureLod(screen, sample_uv, 0.0).rgb;
                        if ((effect_mask & 1) != 0)
                            color = mix(color, vec3(max(color.r, dot(color, vec3(0.30, 0.59, 0.11))),
                                                    color.g * 0.35, color.b * 0.22), 0.62);

                        // Smoothies 3 and 5 use the same iced blur family;
                        // smoothie 4 is the neutral motion blur.
                        if ((effect_mask & 8) != 0)
                            color = mix(color, nearby_blur(sample_uv, 2.0), 0.62);
                        if ((effect_mask & (4 | 16)) != 0) {
                            color = mix(color, nearby_blur(sample_uv, 2.5), 0.70);
                            float value = max(color.r, max(color.g, color.b));
                            color = mix(color, vec3(value * 0.32, value * 0.68, value), 0.58);
                        }

                        // Special code 2: the original upward Manhattan cone,
                        // with a five-pixel feather and quarter brightness
                        // outside it. player_px is in the cropped frame space.
                        if ((effect_mask & 32) != 0) {
                            vec2 p = mix(play_rect_px.xy, play_rect_px.zw, play_uv);
                            float cone = (player_px.y - p.y) - abs(p.x - player_px.x);
                            float light = smoothstep(-5.0, 0.0, cone);
                            color *= mix(0.25, 1.0, light);
                        }

                        ALBEDO = color;
                        ALPHA = 1.0;
                    }
                    """,
            },
            RenderPriority = 120,
        };
        _material.SetShaderParameter("play_rect_px", new Vector4(
            PlayfieldGeometry.MinX, PlayfieldGeometry.MinY,
            PlayfieldGeometry.MaxX, PlayfieldGeometry.MaxY));
        MaterialOverride = _material;
        Visible = false;
    }

    public void Update(byte effectMask, uint levelTick, int playerX, int playerY, bool show)
    {
        // Water has its dedicated background ripple and needs no full-scene
        // screen copy when it is the only active effect.
        Visible = show && (effectMask & ~(byte)OtyrNative.Effects.Water) != 0;
        if (!Visible)
            return;
        _material.SetShaderParameter("effect_mask", (int)effectMask);
        _material.SetShaderParameter("effect_time", levelTick / 35f);
        _material.SetShaderParameter("player_px", new Vector2(playerX - 17f, playerY + 12f));
    }
}
