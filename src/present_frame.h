/*
 * OpenTyrian: A modern cross-platform port of Tyrian
 * Copyright (C) The OpenTyrian Development Team
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
#ifndef PRESENT_FRAME_H
#define PRESENT_FRAME_H

/* The per-tick presentation frame (VR_CONVERSION_PLAN.md Phase 2 steps 3-7).
 *
 * Simulation code RECORDS what it would have drawn (at the exact moment the
 * legacy code drew it) instead of blitting directly; each subsystem then
 * REPLAYS its slice of the record list at its original point in the tick, so
 * the legacy framebuffer stays pixel-identical (enforced by frame hashes).
 * The record list, tagged by semantic category (ENTITY_TAXONOMY.md height
 * bands), is the payload of the presentation snapshot exported to the host.
 */

#include "opentyr.h"
#include "sprite.h"

#include "SDL.h"

typedef enum PresentCategory
{
	PRESENT_ENEMY_SKY = 0,      /* slots 0-24; mid band */
	PRESENT_ENEMY_GROUND_A,     /* slots 25-49; ground band */
	PRESENT_ENEMY_TOP,          /* slots 50-74; high band */
	PRESENT_ENEMY_GROUND_B,     /* slots 75-99; ground band */
	PRESENT_ENEMY_SHOT,         /* mid band */
	PRESENT_PLAYER_SHOT,        /* mid band */
	PRESENT_PLAYER,             /* low band */
	PRESENT_SHADOW,             /* decoration cast onto the terrain */
	PRESENT_SIDEKICK,           /* low band */
	PRESENT_EXPLOSION,          /* band of source entity */
	PRESENT_SUPERPIXEL,         /* debris */
	PRESENT_TEXT,               /* in-play overlay text and HUD icons; proud
	                               band, above everything */
} PresentCategory;

typedef enum PresentBlitKind
{
	PRESENT_BLIT_SPRITE2 = 0,       /* blit_sprite2; filter/blend/darken by flags */
	PRESENT_BLIT_SPRITE2X2,         /* blit_sprite2x2 and variants */
	PRESENT_BLIT_SPRITE_BLEND,      /* blit_sprite_blend on the old sprite table
	                                   system; sheet is NULL, index is the sprite
	                                   id, filter_color is the table id */
	PRESENT_PIXEL_GLOW,             /* superpixel read-modify-write glow; sheet
	                                   is NULL, filter_color is the intensity z,
	                                   index is the palette color base */
	PRESENT_BLIT_SPRITE_HV,         /* old-table glyph with hue/value shading
	                                   (fonthand.c); sheet is NULL, index is
	                                   the sprite id, filter_color packs
	                                   (table << 4) | hue, aux is the signed
	                                   value shift */
} PresentBlitKind;

enum
{
	PRESENT_FLAG_FILTER = 1,        /* use filter_color */
	PRESENT_FLAG_BLEND = 2,         /* additive blend variant */
	PRESENT_FLAG_DARKEN = 4,        /* darken variant (shadows) */
	/* SPRITE_HV only: */
	PRESENT_FLAG_BLACK = 8,         /* solid black glyph (outline passes) */
	PRESENT_FLAG_CLAMP = 16,        /* blit_sprite_hv value clamping (vs the
	                                   _unsafe wrap-around) */
	/* Enemy records only: */
	PRESENT_FLAG_COLLIDER = 64,     /* contact with the player deals damage
	                                   (JE_playerCollide: evalue <= 0 and
	                                   explosiontype bit 0 clear); drives the
	                                   height editor's hazard markers */
};

typedef struct PresentSprite
{
	Uint8 category;                 /* PresentCategory */
	Uint8 kind;                     /* PresentBlitKind */
	Uint8 flags;
	Uint8 filter_color;
	Uint8 aux;                      /* per-category metadata; for enemies the
	                                   enemyground flag (terrain-baked art) */
	Sint16 x, y;                    /* frame coordinates at record time */
	Uint16 index;                   /* sprite index within the sheet */
	Uint16 source_id;               /* stable entity identity across ticks
	                                   (slot-based) for host interpolation;
	                                   PRESENT_NO_SOURCE for transient art */
	Uint16 entity_type;             /* enemies: eDat index (enemytype); 0 for
	                                   everything else.  Keys the authored
	                                   hover-height metadata (Stage B). */
	Sprite2_array *sheet;
} PresentSprite;

#define PRESENT_NO_SOURCE 0xffffu

#define PRESENT_SPRITE_MAX 1024
#define PRESENT_SOUND_MAX 8

extern PresentSprite present_sprites[PRESENT_SPRITE_MAX];
extern unsigned int present_sprite_count;

/* Sound events dispatched this tick: (mixer channel, sample id) pairs. */
extern Uint8 present_sound_channel[PRESENT_SOUND_MAX];
extern Uint8 present_sound_sample[PRESENT_SOUND_MAX];
extern unsigned int present_sound_count;

void present_sound(Uint8 channel, Uint8 sample);

/* When set (hosted VR mode), present_draw_from becomes a no-op: entities are
 * rendered by the host from the snapshot instead, and the legacy framebuffer
 * carries only backgrounds, HUD, and text. */
extern bool present_suppress_entity_draw;

/* --- Per-tick legacy fallback ------------------------------------------ */

/* The host's configured suppression wishes (set once at session create).
 * The EFFECTIVE flags above are recomputed at the top of every gameplay
 * tick: levels whose presentation the 3D path cannot reproduce -- the
 * smoothie warp/blur filters read and rewrite the composited frame -- fall
 * back to full legacy drawing for the duration (present_legacy_fallback
 * set; the host shows the flat frame and hides its 3D layers). */
extern bool present_config_suppress_entity;
extern bool present_config_suppress_background;
extern bool present_config_suppress_text;
extern bool present_legacy_fallback;

/* --- In-play text and HUD icons ---------------------------------------- */

/* The recording window for in-play overlay text: opened by
 * present_frame_reset (top of a gameplay tick), closed by JE_showVGA.  Text
 * drawn to game_screen inside the play region while the window is open is
 * in-play overlay (cash, lives, WARNING, timer, game over, insert coin) and
 * gets recorded; everything else -- pause/menu screens (drawn to
 * VGAScreenSeg or after the present), the sidebar, the bottom text bar --
 * stays in the frame. */
extern bool present_text_window;

/* When set (hosted VR mode), recorded text/icon draws are also skipped in
 * the frame: the host renders them proud of the playfield. */
extern bool present_suppress_text;

/* Records one glyph draw when the text gate passes.  Returns true when the
 * frame blit should be SKIPPED (recorded and suppression is on). */
bool present_text_glyph(SDL_Surface *screen, int x, int y,
                        unsigned int table, unsigned int sprite_id,
                        Uint8 flags, Uint8 hue, Sint8 value);

/* As above for the in-play HUD icon blits (lives, superbombs, special). */
bool present_hud_blit(SDL_Surface *screen, Sprite2_array *sheet,
                      int x, int y, unsigned int index, bool two_by_two);

/* --- Background layers ------------------------------------------------ */

/* Per-tick record of one scrolling map layer draw (backgrnd.c).  The maps
 * themselves are static per level and exported separately; this captures the
 * exact scroll position the legacy blit used so the host can reproduce the
 * layer pixel-for-pixel. */
typedef struct PresentBackground
{
	Sint32 tile_offset;   /* element index of the first blit row's first tile
	                         within the layer's flattened map (may be < 0:
	                         the row above the map, clipped offscreen) */
	Sint16 x, y;          /* frame position of that tile; y = backPos - 28 */
	Uint8 drawn;          /* layer was blitted this tick */
	Uint8 blend;          /* 50/50 value-nibble blend variant (wild detail) */
	Uint8 over;           /* the layer's *over mode at draw time: where the
	                         draw sat in the tick relative to entities (layer
	                         1: background2over -- 1 draws over ground
	                         enemies; layer 2: background3over -- 2 draws
	                         before sky enemies) */
	Uint32 hash;          /* standalone-raster FNV-1a when capture enabled */
} PresentBackground;

#define PRESENT_BACKGROUND_LAYERS 3

extern PresentBackground present_backgrounds[PRESENT_BACKGROUND_LAYERS];

/* When set (hosted VR mode), the background tile blits are skipped -- scroll
 * state still advances -- and the host renders the layers from the exported
 * maps plus the per-tick scroll records. */
extern bool present_suppress_background;

/* Palette index the suppressed background fill uses so the host can key it
 * out.  Deliberately NOT 0: art (pit interiors, baked slabs) uses index-0
 * black that must stay opaque in the frame overlay. */
#define PRESENT_BG_KEY_INDEX 254

/* Blackens the key fill in the seg play region before a pause/menu freezes
 * it as a backdrop (the host stops keying during menu presents so menu art
 * on index 254 -- volume slider borders -- can render). */
void present_blacken_key_fill(SDL_Surface *seg);

/* When set, each background draw also rasters its layer standalone (against
 * a zeroed scratch frame) and publishes an FNV-1a hash so a host-side
 * reconstruction can be verified mechanically. */
extern bool present_background_hash;

void present_background(int layer, Sint32 tile_offset, Sint16 x, Sint16 y,
                        bool blend, Uint8 over, Uint32 hash);

/* Clears the record list; called once at the top of each gameplay tick. */
void present_frame_reset(void);

/* Appends a record and returns its index (or drops it silently when full,
 * returning PRESENT_SPRITE_MAX; the assert catches overflow in debug). */
unsigned int present_record(PresentCategory category, PresentBlitKind kind,
                            Uint8 flags, Uint8 filter_color,
                            Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index);

/* As present_record, with per-category aux metadata. */
unsigned int present_record_aux(PresentCategory category, PresentBlitKind kind,
                                Uint8 flags, Uint8 filter_color, Uint8 aux,
                                Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index);

/* Full-parameter variant with a stable source identity for interpolation. */
unsigned int present_record_id(PresentCategory category, PresentBlitKind kind,
                               Uint8 flags, Uint8 filter_color, Uint8 aux,
                               Uint16 source_id,
                               Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index);

/* Replays records [from, count) onto the surface; each subsystem calls this
 * for its own slice at its original draw point in the tick. */
void present_draw_from(SDL_Surface *surface, unsigned int from);

#endif /* PRESENT_FRAME_H */
