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
#ifndef OTYR_HOST_H
#define OTYR_HOST_H

/* Public C ABI for hosting the OpenTyrian core as a native library.
 *
 * Spike scope (VR_CONVERSION_PLAN.md section 6): the unmodified game loop runs
 * on a dedicated thread inside the library ("thread-hosted" mode).  The legacy
 * present call is the synchronization point: each JE_showVGA() publishes the
 * completed 320x200 indexed frame and applies pending host input.  There is no
 * otyr_session_tick() yet -- re-entrant ticking is Phase 2.
 *
 * Conventions: all structs are fixed-width and append-only; every call taking
 * a struct also takes its size and fails on mismatch with older/larger sizes
 * handled by struct_size gating.  Only one session may exist per process (the
 * game core is a global-state singleton).
 */

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(OTYR_BUILD_DLL)
#define OTYR_API __declspec(dllexport)
#else
#define OTYR_API
#endif

#define OTYR_ABI_VERSION 8u

#define OTYR_FRAME_WIDTH  320u
#define OTYR_FRAME_HEIGHT 200u

/* Return codes. */
#define OTYR_OK                  0
#define OTYR_ERROR              -1
#define OTYR_INVALID_ARGUMENT   -2
#define OTYR_INVALID_SESSION    -3
#define OTYR_TIMEOUT            -4
#define OTYR_UNSUPPORTED        -5
#define OTYR_ALREADY_EXISTS     -6

/* OtyrInputFrame.buttons bits.  0-7 map to the game's eight configurable
 * actions; the rest are fixed UI keys for menu navigation. */
#define OTYR_BUTTON_UP             (1u << 0)
#define OTYR_BUTTON_DOWN           (1u << 1)
#define OTYR_BUTTON_LEFT           (1u << 2)
#define OTYR_BUTTON_RIGHT          (1u << 3)
#define OTYR_BUTTON_FIRE           (1u << 4)
#define OTYR_BUTTON_CHANGE_FIRE    (1u << 5)
#define OTYR_BUTTON_LEFT_SIDEKICK  (1u << 6)
#define OTYR_BUTTON_RIGHT_SIDEKICK (1u << 7)
#define OTYR_BUTTON_UI_CONFIRM     (1u << 8)   /* Enter */
#define OTYR_BUTTON_UI_CANCEL      (1u << 9)   /* Escape */
#define OTYR_BUTTON_UI_SPACE       (1u << 10)  /* Space */
#define OTYR_BUTTON_UI_PAUSE       (1u << 11)  /* P */

/* OtyrConfig.flags bits. */
#define OTYR_CONFIG_ENABLE_AUDIO           (1u << 0)
#define OTYR_CONFIG_SUPPRESS_ENTITY_DRAW   (1u << 1) /* legacy frame shows only
                                                        backgrounds/HUD; host
                                                        renders entities from
                                                        the snapshot (v6) */
#define OTYR_CONFIG_SUPPRESS_BACKGROUND    (1u << 2) /* skip the background
                                                        tile blits (scroll
                                                        state still advances);
                                                        host renders the map
                                                        layers itself (v8) */
#define OTYR_CONFIG_BACKGROUND_HASHES      (1u << 3) /* publish per-layer
                                                        standalone raster
                                                        hashes for export
                                                        verification (v8) */

typedef struct OtyrConfig
{
	uint32_t struct_size;   /* sizeof(OtyrConfig) */
	uint32_t abi_version;   /* must be OTYR_ABI_VERSION */
	uint32_t flags;
	uint32_t reserved;
	char     data_dir[260]; /* UTF-8, NUL-terminated; Tyrian data directory */
	char     hash_log[260]; /* optional --hash-log path; empty to disable */
	char     user_dir[260]; /* config/save directory; empty = process cwd (v2) */
} OtyrConfig;

typedef struct OtyrInputFrame
{
	uint32_t struct_size;   /* sizeof(OtyrInputFrame) */
	uint32_t buttons;       /* OTYR_BUTTON_* bits; level-triggered (held) */
	int16_t  analog_dx;     /* analog movement, mouse-accumulator units:
	                           consumed once per gameplay tick while set;
	                           steady-state ship speed ~= value/4 px per tick,
	                           useful range about -30..30 (v4) */
	int16_t  analog_dy;
	uint8_t  use_target;    /* absolute target following: the ship pursues
	                           (target_x, target_y) in sim coordinates at up
	                           to target_speed px per tick, with the feedback
	                           loop inside the simulation tick (v5) */
	uint8_t  target_speed;  /* 0 = default (2 px per tick) */
	int16_t  target_x;
	int16_t  target_y;
} OtyrInputFrame;

typedef struct OtyrFrame
{
	uint32_t struct_size;   /* sizeof(OtyrFrame) */
	uint32_t frame_number;  /* increments once per legacy present */
	uint32_t width;         /* OTYR_FRAME_WIDTH */
	uint32_t height;        /* OTYR_FRAME_HEIGHT */
	uint8_t  pixels[OTYR_FRAME_WIDTH * OTYR_FRAME_HEIGHT]; /* palette indices */
	uint32_t palette[256];  /* 0xAARRGGBB, A always 0xFF */
	uint32_t level_tick;    /* gameplay tick this present belongs to (v6) */
} OtyrFrame;

typedef struct OtyrPlayerState
{
	uint32_t struct_size;   /* sizeof(OtyrPlayerState) */
	int32_t  x, y;          /* Tyrian sim coordinates (see ENTITY_TAXONOMY.md) */
	uint32_t shield;
	uint32_t armor;
	uint32_t cash;
	uint32_t lives;
	uint32_t is_alive;
	uint32_t level_tick;    /* increments once per gameplay tick; static in
	                           menus -- gates gameplay-only host features (v2) */
	int32_t  x_velocity;    /* sim units per tick; for predictive steering (v3) */
	int32_t  y_velocity;
} OtyrPlayerState;

/* --- Presentation snapshot (v6) --------------------------------------- */

/* Semantic sprite categories; mirrors PresentCategory (present_frame.h) and
 * maps onto the ENTITY_TAXONOMY.md height bands. */
#define OTYR_CAT_ENEMY_SKY       0u
#define OTYR_CAT_ENEMY_GROUND_A  1u
#define OTYR_CAT_ENEMY_TOP       2u
#define OTYR_CAT_ENEMY_GROUND_B  3u
#define OTYR_CAT_ENEMY_SHOT      4u
#define OTYR_CAT_PLAYER_SHOT     5u
#define OTYR_CAT_PLAYER          6u
#define OTYR_CAT_SHADOW          7u
#define OTYR_CAT_SIDEKICK        8u
#define OTYR_CAT_EXPLOSION       9u
#define OTYR_CAT_SUPERPIXEL      10u

#define OTYR_KIND_SPRITE2        0u  /* 12x14 sprite cell */
#define OTYR_KIND_SPRITE2X2      1u  /* four cells forming 24x28 */
#define OTYR_KIND_SPRITE_BLEND   2u  /* old-table blend; no sheet */
#define OTYR_KIND_PIXEL_GLOW     3u  /* debris pixel; filter_color=intensity, index=color */

#define OTYR_SHEET_INVALID       0xffu

#define OTYR_SNAPSHOT_SPRITE_MAX 1024u
#define OTYR_SNAPSHOT_SOUND_MAX  8u

typedef struct OtyrSnapshotSprite
{
	uint8_t  category;      /* OTYR_CAT_* */
	uint8_t  kind;          /* OTYR_KIND_* */
	uint8_t  flags;         /* 1 filter, 2 blend, 4 darken (matches present_frame) */
	uint8_t  filter_color;
	int16_t  x, y;          /* legacy frame coordinates at draw time */
	uint16_t index;         /* sprite index within the sheet (1-based) */
	uint8_t  sheet_id;      /* OTYR_SHEET_* or OTYR_SHEET_INVALID */
	uint8_t  aux;           /* per-category metadata (enemies: terrain art) */
	uint16_t source_id;     /* stable entity id across ticks (0xffff none);
	                           same-id records pair by emit order (v7) */
	uint8_t  reserved[2];   /* pads the struct to exactly 16 bytes */
} OtyrSnapshotSprite;

/* Per-tick scroll record for one background map layer (v8).  The map data
 * itself is static per level (otyr_background_map); this pins where the
 * legacy blit placed it this tick. */
#define OTYR_BG_LAYER_COUNT 3u

typedef struct OtyrBackgroundDraw
{
	int32_t  tile_offset;   /* element index of the first blit row's first
	                           tile within the flattened map (may be < 0) */
	int16_t  x, y;          /* frame position of that tile; the layer covers
	                           8 rows of 12 tiles from here (24x28 each) */
	uint8_t  drawn;         /* 0 = layer not blitted this tick */
	uint8_t  blend;         /* 50/50 value-nibble blend variant */
	uint16_t reserved;
	uint32_t hash;          /* standalone-raster FNV-1a; only filled when
	                           OTYR_CONFIG_BACKGROUND_HASHES is set */
} OtyrBackgroundDraw;

typedef struct OtyrSnapshot
{
	uint32_t struct_size;   /* sizeof(OtyrSnapshot); doubles as last-seen key
	                           via level_tick, like OtyrFrame */
	uint32_t level_tick;    /* gameplay tick this snapshot belongs to */
	uint32_t sheet_epoch;   /* increments when sprite sheets are (re)loaded */
	uint32_t sprite_count;
	uint32_t sound_count;
	uint8_t  sound_channel[OTYR_SNAPSHOT_SOUND_MAX];
	uint8_t  sound_sample[OTYR_SNAPSHOT_SOUND_MAX];
	OtyrSnapshotSprite sprites[OTYR_SNAPSHOT_SPRITE_MAX];
	OtyrBackgroundDraw background[OTYR_BG_LAYER_COUNT]; /* (v8) */
} OtyrSnapshot;

/* Sprite sheet export: every cell is a 12x14 indexed-color bitmap (0 =
 * transparent); palette comes from the frame.  Sheets 0-3 are the static
 * main tables (8, 9, 10, 12), 4 the explosion sheet, 5-8 the per-level
 * enemy sheets.  Re-fetch when snapshot.sheet_epoch changes. */
#define OTYR_SHEET_COUNT   9u
#define OTYR_SHEET_CELL_W  12u
#define OTYR_SHEET_CELL_H  14u
#define OTYR_SHEET_CELL_MAX 1024u

typedef struct OtyrSpriteSheet
{
	uint32_t struct_size;
	uint32_t sheet_epoch;
	uint32_t cell_count;
	uint8_t  pixels[OTYR_SHEET_CELL_MAX * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H];
} OtyrSpriteSheet;

/* Returns OTYR_ABI_VERSION of this library. */
OTYR_API uint32_t otyr_abi_version(void);

/* Copies the last error message (UTF-8, NUL-terminated) into buffer.
 * Returns the full message length, or OTYR_INVALID_ARGUMENT. */
OTYR_API int32_t otyr_last_error(char *buffer, uint32_t buffer_size);

/* Creates the (single) session and starts the game thread.  The game runs
 * under SDL's dummy video driver; no window is created.  Returns OTYR_OK and
 * a nonzero handle, or a negative error. */
OTYR_API int32_t otyr_session_create(const OtyrConfig *config,
                                     uint32_t config_size,
                                     uint64_t *out_session);

/* Requests shutdown (as if the window were closed), waits briefly for the
 * game thread to finish, and frees the session. */
OTYR_API int32_t otyr_session_destroy(uint64_t session);

/* Replaces the currently-held button state.  Applied atomically between
 * legacy frames. */
OTYR_API int32_t otyr_session_submit_input(uint64_t session,
                                           const OtyrInputFrame *input,
                                           uint32_t input_size);

/* Blocks until a frame newer than the last one delivered to the caller is
 * available (or timeout_ms elapses; 0 polls).  On OTYR_OK the frame, palette,
 * and frame_number are filled in. */
OTYR_API int32_t otyr_session_acquire_frame(uint64_t session,
                                            OtyrFrame *frame,
                                            uint32_t frame_size,
                                            uint32_t timeout_ms);

/* Snapshots player 1 state.  Valid whenever the session lives; values are
 * stale while in menus. */
OTYR_API int32_t otyr_session_player_state(uint64_t session,
                                           OtyrPlayerState *state,
                                           uint32_t state_size);

/* Blocks until a presentation snapshot for a gameplay tick newer than
 * snapshot->level_tick is available (0 polls).  Menus produce none. */
OTYR_API int32_t otyr_session_snapshot(uint64_t session,
                                       OtyrSnapshot *snapshot,
                                       uint32_t snapshot_size,
                                       uint32_t timeout_ms);

/* Copies the rasterized cells of one sprite sheet.  Cheap (memcpy from a
 * cache filled at level load); call for all sheets whenever sheet_epoch
 * changes. */
OTYR_API int32_t otyr_sprite_sheet(uint64_t session,
                                   uint32_t sheet_id,
                                   OtyrSpriteSheet *sheet,
                                   uint32_t sheet_size);

/* --- Background map export (v8) ---------------------------------------- */

/* Each level has three scrolling map layers built from 24x28 indexed-color
 * tiles (0 = transparent): layer 0 the ground (14x300 map, parallax 1x),
 * layer 1 structures/water (14x600, 2x), layer 2 clouds/top (15x600, 3x).
 * Static per level; re-fetch when snapshot.sheet_epoch changes.  Per-tick
 * placement comes from OtyrSnapshot.background[]. */
#define OTYR_BG_TILE_W      24u
#define OTYR_BG_TILE_H      28u
#define OTYR_BG_SHAPE_MAX   72u
#define OTYR_BG_MAP_CELL_MAX (600u * 15u)
#define OTYR_BG_TILE_EMPTY  0xffu

typedef struct OtyrBackgroundMap
{
	uint32_t struct_size;
	uint32_t sheet_epoch;
	uint16_t width, height; /* map dimensions in tiles */
	uint16_t shape_count;
	uint16_t reserved;
	uint8_t  tiles[OTYR_BG_MAP_CELL_MAX]; /* row-major shape indices;
	                                         OTYR_BG_TILE_EMPTY = no tile */
	uint8_t  shapes[OTYR_BG_SHAPE_MAX * OTYR_BG_TILE_W * OTYR_BG_TILE_H];
} OtyrBackgroundMap;

/* Copies one background map layer (0..2) from the cache filled at level
 * load. */
OTYR_API int32_t otyr_background_map(uint64_t session,
                                     uint32_t layer,
                                     OtyrBackgroundMap *map,
                                     uint32_t map_size);

#ifdef __cplusplus
}
#endif

#endif /* OTYR_HOST_H */
