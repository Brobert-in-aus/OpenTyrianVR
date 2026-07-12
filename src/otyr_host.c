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
#include "otyr_host.h"
#include "otyr_host_internal.h"

#include "config.h"
#include "game_input.h"
#include "keyboard.h"
#include "palette.h"
#include "player.h"
#include "present_frame.h"
#include "sprite.h"
#include "varz.h"
#include "video.h"

#include "SDL.h"

#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")

#ifndef PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
#define PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION 0x4
#endif

/* Defensive timer hygiene for the sleep-paced game thread: request 1 ms
 * resolution and opt out of Windows 11's background timer coarsening (the
 * XR host's desktop window is typically an occluded mirror).  Note: an
 * apparent low-fps collapse that motivated this turned out to be the game's
 * own "Slower" speed setting persisted in tyrian.cfg -- if pacing looks
 * wrong, check gameSpeed (OTYR_TRACE shows the requested frame period). */
static void win32_keep_timer_resolution(void)
{
	PROCESS_POWER_THROTTLING_STATE state = {
		.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
		.ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
		.StateMask = 0,
	};
	SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
	                      &state, sizeof(state));
	timeBeginPeriod(1);
}
#endif

bool otyr_hosted = false;

/* ABI layout guards: a host reading these structs assumes exactly these
 * sizes (foundation rule: size asserts on both sides of the boundary). */
typedef char otyr_assert_sprite_size[sizeof(OtyrSnapshotSprite) == 16 ? 1 : -1];
typedef char otyr_assert_bg_draw_size[sizeof(OtyrBackgroundDraw) == 16 ? 1 : -1];
typedef char otyr_assert_snapshot_size[sizeof(OtyrSnapshot) == 36 + 1024 * 16 + 3 * 16 ? 1 : -1];
typedef char otyr_assert_sheet_size[sizeof(OtyrSpriteSheet) == 12 + 2 * 1024 * 12 * 14 ? 1 : -1];
typedef char otyr_assert_frame_size[sizeof(OtyrFrame) == 16 + 320 * 200 + 1024 + 4 + 4 ? 1 : -1];
typedef char otyr_assert_bg_map_size[sizeof(OtyrBackgroundMap) == 16 + 600 * 15 + 72 * 24 * 28 ? 1 : -1];
typedef char otyr_assert_old_sprite_size[sizeof(OtyrOldSprite) == 8 + 2 * 64 * 64 ? 1 : -1];

/* The game core is a global-state singleton, so exactly one session. */
#define SESSION_HANDLE 1ull

/* 32 MiB: JE_main and friends have deep stacks; host runtimes (.NET) default
 * to ~1 MiB, so the thread we create carries its own generous stack. */
#define GAME_THREAD_STACK_SIZE (32 * 1024 * 1024)

enum session_state
{
	SESSION_NONE,
	SESSION_RUNNING,
	SESSION_HALTED,
};

static struct
{
	SDL_mutex *mutex;       /* guards everything below */
	SDL_cond *frame_ready;

	enum session_state state;
	SDL_Thread *thread;

	/* argv storage for the game thread */
	char args[5][300];
	char *argv[6];
	int argc;

	/* latest published frame */
	uint32_t frame_number;
	uint32_t frame_level_tick;
	uint8_t frame_in_level;
	uint8_t frame_legacy_fallback;
	uint8_t frame_menu_present;
	uint8_t pixels[OTYR_FRAME_WIDTH * OTYR_FRAME_HEIGHT];
	uint32_t palette_argb[256];

	/* latest published player state */
	OtyrPlayerState player_state;

	/* host-desired button state; applied on the game thread each present */
	uint32_t pending_buttons;
	uint32_t applied_buttons;
	int16_t pending_analog_dx;
	int16_t pending_analog_dy;
	uint8_t pending_use_target;
	uint8_t pending_target_speed;
	int16_t pending_target_x;
	int16_t pending_target_y;

	uint32_t level_tick;
	char user_dir[260];

	/* latest published presentation snapshot */
	OtyrSnapshot snapshot;

	/* rasterized sprite-sheet cache, filled on the game thread at level load */
	uint32_t sheet_epoch;
	uint32_t sheet_cell_count[OTYR_SHEET_COUNT];
	uint8_t (*sheet_cells)[OTYR_SHEET_CELL_MAX * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H];
	uint8_t (*sheet_opacity)[OTYR_SHEET_CELL_MAX * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H];

	/* per-cell "fully opaque" flags (game-thread only): baked terrain art */
	uint8_t sheet_cell_opaque[OTYR_SHEET_COUNT][OTYR_SHEET_CELL_MAX];

	/* background map layers, captured with the sheets at level load */
	OtyrBackgroundMap bg_maps[OTYR_BG_LAYER_COUNT];

	/* rasterized old variable-size tables, captured with the sheets: slot 0
	   OPTION_SHAPES (blend shots), slots 1-3 the font tables (text records,
	   v13); see old_table_slot() */
	OtyrOldSprite old_sprites[4][OTYR_OLD_SPRITE_MAX];
} session;

/* Maps an old-table id (sprite.h table constants) to its cache slot. */
static int old_table_slot(uint32_t table)
{
	switch (table)
	{
	case OTYR_OLD_TABLE_OPTION:     return 0;
	case OTYR_OLD_TABLE_FONT_BIG:   return 1;
	case OTYR_OLD_TABLE_FONT_SMALL: return 2;
	case OTYR_OLD_TABLE_FONT_TINY:  return 3;
	default: return -1;
	}
}

/* Registry mapping sheet ids to the game's sheet globals. */
static Sprite2_array *sheet_registry(uint32_t sheet_id)
{
	switch (sheet_id)
	{
	case 0: return &spriteSheet8;
	case 1: return &spriteSheet9;
	case 2: return &spriteSheet10;
	case 3: return &spriteSheet12;
	case 4: return &explosionSpriteSheet;
	case 5: return &enemySpriteSheets[0];
	case 6: return &enemySpriteSheets[1];
	case 7: return &enemySpriteSheets[2];
	case 8: return &enemySpriteSheets[3];
	default: return NULL;
	}
}

static uint8_t sheet_id_for(const Sprite2_array *sheet)
{
	for (uint32_t id = 0; id < OTYR_SHEET_COUNT; ++id)
		if (sheet_registry(id) == sheet)
			return (uint8_t)id;
	return OTYR_SHEET_INVALID;
}

static char last_error[256] = "";

static jmp_buf thread_exit_env;

static void set_error(const char *message)
{
	snprintf(last_error, sizeof(last_error), "%s", message);
}

uint32_t otyr_abi_version(void)
{
	return OTYR_ABI_VERSION;
}

int32_t otyr_last_error(char *buffer, uint32_t buffer_size)
{
	if (buffer == NULL || buffer_size == 0)
		return OTYR_INVALID_ARGUMENT;
	snprintf(buffer, buffer_size, "%s", last_error);
	return (int32_t)strlen(last_error);
}

static int game_thread_main(void *data)
{
	(void)data;

	int code = setjmp(thread_exit_env);
	if (code == 0)
	{
		opentyrian_main(session.argc, session.argv);
		/* opentyrian_main only returns on early SDL_Init failure; normal
		   shutdown longjmps from JE_tyrianHalt. */
	}

	SDL_LockMutex(session.mutex);
	session.state = SESSION_HALTED;
	SDL_CondBroadcast(session.frame_ready);
	SDL_UnlockMutex(session.mutex);
	return code - 1;
}

void otyr_host_thread_exit(int code)
{
	longjmp(thread_exit_env, code + 1);
}

bool otyr_in_level = false;
bool otyr_tick_present = false;

void otyr_host_level_tick(void)
{
	/* Game-thread write; published to the host via the state snapshot taken
	   under the mutex at present time. */
	++session.level_tick;
	otyr_in_level = true;
}

void otyr_host_level_end(void)
{
	otyr_in_level = false;
}

/* Decodes one RLE sprite cell (see blit_sprite2) into a 12x14 indexed
 * bitmap plus an opacity mask.  Draw runs can legitimately write index 0
 * (black), so the pixel value alone cannot mark transparency. */
static void rasterize_cell(const Sprite2_array *sheet, uint32_t index,
                           uint8_t *cell, uint8_t *opacity)
{
	memset(cell, 0, OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H);
	memset(opacity, 0, OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H);

	const uint8_t *base = sheet->data;
	const uint8_t *end = base + sheet->size;

	uint16_t offset = (uint16_t)(base[2 * index] | (base[2 * index + 1] << 8));
	if (offset >= sheet->size)
		return;

	const uint8_t *data = base + offset;
	int x = 0, y = 0;

	for (; data < end && *data != 0x0f; ++data)
	{
		x += *data & 0x0f;
		unsigned int count = (*data & 0xf0) >> 4;

		if (count == 0)  /* next pixel row */
		{
			++y;
			x -= OTYR_SHEET_CELL_W;
			if (y >= (int)OTYR_SHEET_CELL_H)
				return;
		}
		else
		{
			while (count-- && ++data < end)
			{
				if (x >= 0 && x < (int)OTYR_SHEET_CELL_W && y >= 0 && y < (int)OTYR_SHEET_CELL_H)
				{
					cell[y * OTYR_SHEET_CELL_W + x] = *data;
					opacity[y * OTYR_SHEET_CELL_W + x] = 1;
				}
				++x;
			}
		}
	}
}

/* Recovers a mainmap tile pointer's shape index (the loader stores pointers
 * into megaDataN.shapes[i].sh; NULL and out-of-table become "empty"). */
static uint8_t bg_tile_index(const JE_byte *tile, const uint8_t *shape0,
                             size_t stride, unsigned int count)
{
	if (tile == NULL)
		return OTYR_BG_TILE_EMPTY;
	ptrdiff_t delta = (const uint8_t *)tile - shape0;
	if (delta < 0 || (size_t)delta % stride != 0 || (size_t)delta / stride >= count)
		return OTYR_BG_TILE_EMPTY;
	return (uint8_t)((size_t)delta / stride);
}

static void capture_background_maps_locked(void)
{
	const struct
	{
		JE_byte *const *map;
		unsigned int width, height;
		const uint8_t *shape0;
		size_t stride;
		unsigned int shape_count;
	} layers[OTYR_BG_LAYER_COUNT] = {
		{ &megaData1.mainmap[0][0], 14, 300,
		  (const uint8_t *)megaData1.shapes[0].sh, sizeof(megaData1.shapes[0]), 72 },
		{ &megaData2.mainmap[0][0], 14, 600,
		  (const uint8_t *)megaData2.shapes[0].sh, sizeof(megaData2.shapes[0]), 71 },
		{ &megaData3.mainmap[0][0], 15, 600,
		  (const uint8_t *)megaData3.shapes[0].sh, sizeof(megaData3.shapes[0]), 70 },
	};

	for (unsigned int l = 0; l < OTYR_BG_LAYER_COUNT; ++l)
	{
		OtyrBackgroundMap *out = &session.bg_maps[l];
		out->struct_size = sizeof(OtyrBackgroundMap);
		out->width = (uint16_t)layers[l].width;
		out->height = (uint16_t)layers[l].height;
		out->shape_count = (uint16_t)layers[l].shape_count;
		out->reserved = 0;

		const unsigned int cells = layers[l].width * layers[l].height;
		for (unsigned int i = 0; i < cells; ++i)
			out->tiles[i] = bg_tile_index(layers[l].map[i], layers[l].shape0,
			                              layers[l].stride, layers[l].shape_count);
		memset(out->tiles + cells, OTYR_BG_TILE_EMPTY, sizeof(out->tiles) - cells);

		for (unsigned int s = 0; s < layers[l].shape_count; ++s)
			memcpy(out->shapes + s * OTYR_BG_TILE_W * OTYR_BG_TILE_H,
			       layers[l].shape0 + s * layers[l].stride,
			       OTYR_BG_TILE_W * OTYR_BG_TILE_H);
	}
}

/* Decodes one old-table sprite (blit_sprite run encoding: 255 = skip run,
 * 254 = next row, 253 = skip one, else literal pixel) into a fixed-stride
 * bitmap. */
static void capture_old_sprites_locked(void)
{
	static const uint32_t tables[4] = {
		OTYR_OLD_TABLE_OPTION, OTYR_OLD_TABLE_FONT_BIG,
		OTYR_OLD_TABLE_FONT_SMALL, OTYR_OLD_TABLE_FONT_TINY,
	};

	for (uint32_t t = 0; t < 4; ++t)
	{
		const uint32_t table = tables[t];
		const int slot = old_table_slot(table);

		for (uint32_t i = 0; i < OTYR_OLD_SPRITE_MAX; ++i)
		{
			OtyrOldSprite *out = &session.old_sprites[slot][i];
			out->struct_size = sizeof(OtyrOldSprite);
			out->width = 0;
			out->height = 0;
			memset(out->pixels, 0, sizeof(out->pixels));
			memset(out->opacity, 0, sizeof(out->opacity));

			if (i >= sprite_table[table].count ||
			    !sprite_exists(table, i))
				continue;

			const Sprite *spr = sprite(table, i);
			if (spr->width > OTYR_OLD_SPRITE_W_MAX || spr->height > OTYR_OLD_SPRITE_H_MAX)
				continue;  /* stays 0x0: host skips it (none expected this big) */

			out->width = spr->width;
			out->height = spr->height;

			const Uint8 *data = spr->data;
			const Uint8 *const data_ul = data + spr->size;
			unsigned int px = 0, py = 0;

			for (; data < data_ul && py < spr->height; ++data)
			{
				switch (*data)
				{
				case 255:
					++data;
					px += *data;
					break;
				case 254:
					px = spr->width;
					break;
				case 253:
					++px;
					break;
				default:
					if (px < spr->width)
					{
						out->pixels[py * OTYR_OLD_SPRITE_W_MAX + px] = *data;
						out->opacity[py * OTYR_OLD_SPRITE_W_MAX + px] = 1;
					}
					++px;
					break;
				}
				if (px >= spr->width)
				{
					px = 0;
					++py;
				}
			}
		}
	}
}

void otyr_host_capture_sheets(void)
{
	SDL_LockMutex(session.mutex);
	capture_background_maps_locked();
	capture_old_sprites_locked();
	for (uint32_t id = 0; id < OTYR_SHEET_COUNT; ++id)
	{
		const Sprite2_array *sheet = sheet_registry(id);
		uint32_t count = 0;

		if (sheet->data != NULL && sheet->size >= 2)
		{
			uint16_t first = (uint16_t)(sheet->data[0] | (sheet->data[1] << 8));
			count = first / 2;
			if (count > OTYR_SHEET_CELL_MAX)
				count = OTYR_SHEET_CELL_MAX;

			for (uint32_t i = 0; i < count; ++i)
			{
				uint8_t *cell = session.sheet_cells[id] + i * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H;
				uint8_t *opa = session.sheet_opacity[id] + i * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H;
				rasterize_cell(sheet, i, cell, opa);

				unsigned int opaque = 0;
				for (unsigned int p = 0; p < OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H; ++p)
					if (opa[p] != 0)
						++opaque;
				/* ~98%+: baked terrain backdrops; real flyers always have
				   transparent corners. */
				session.sheet_cell_opaque[id][i] =
					opaque >= OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H - 4;
			}
		}

		session.sheet_cell_count[id] = count;
	}
	++session.sheet_epoch;
	SDL_UnlockMutex(session.mutex);
}

void otyr_host_game_input(struct GameInput *input)
{
	SDL_LockMutex(session.mutex);
	const uint32_t buttons = session.pending_buttons;

	input->up = (buttons & OTYR_BUTTON_UP) != 0;
	input->down = (buttons & OTYR_BUTTON_DOWN) != 0;
	input->left = (buttons & OTYR_BUTTON_LEFT) != 0;
	input->right = (buttons & OTYR_BUTTON_RIGHT) != 0;

	input->fire = (buttons & OTYR_BUTTON_FIRE) != 0;
	input->change_fire = (buttons & OTYR_BUTTON_CHANGE_FIRE) != 0;
	input->left_sidekick = (buttons & OTYR_BUTTON_LEFT_SIDEKICK) != 0;
	input->right_sidekick = (buttons & OTYR_BUTTON_RIGHT_SIDEKICK) != 0;

	input->analog_dx = session.pending_analog_dx;
	input->analog_dy = session.pending_analog_dy;

	input->has_target = session.pending_use_target != 0;
	input->target_x = session.pending_target_x;
	input->target_y = session.pending_target_y;
	input->target_speed = session.pending_target_speed;
	SDL_UnlockMutex(session.mutex);
}

const char *otyr_host_user_dir(void)
{
	return session.user_dir;
}

bool otyr_host_cell_is_opaque(const void *sheet, Uint16 index)
{
	if (session.state != SESSION_RUNNING || index == 0)
		return false;
	uint8_t id = sheet_id_for((const Sprite2_array *)sheet);
	if (id >= OTYR_SHEET_COUNT || index - 1u >= session.sheet_cell_count[id])
		return false;
	return session.sheet_cell_opaque[id][index - 1] != 0;
}

int32_t otyr_session_create(const OtyrConfig *config, uint32_t config_size,
                            uint64_t *out_session)
{
	if (config == NULL || out_session == NULL ||
	    config_size < sizeof(OtyrConfig) ||
	    config->struct_size < sizeof(OtyrConfig))
	{
		set_error("invalid config");
		return OTYR_INVALID_ARGUMENT;
	}
	if (config->abi_version != OTYR_ABI_VERSION)
	{
		set_error("ABI version mismatch");
		return OTYR_UNSUPPORTED;
	}
	if (session.state != SESSION_NONE)
	{
		set_error("session already exists");
		return OTYR_ALREADY_EXISTS;
	}
	if (config->data_dir[0] == '\0')
	{
		set_error("data_dir is required");
		return OTYR_INVALID_ARGUMENT;
	}

	session.mutex = SDL_CreateMutex();
	session.frame_ready = SDL_CreateCond();
	if (session.mutex == NULL || session.frame_ready == NULL)
	{
		set_error("failed to create synchronization primitives");
		return OTYR_ERROR;
	}

	/* No window in hosted mode; must be set before SDL video init. */
	SDL_setenv("SDL_VIDEODRIVER", "dummy", 1);

#ifdef _WIN32
	win32_keep_timer_resolution();
#endif

	session.argc = 0;
	snprintf(session.args[0], sizeof(session.args[0]), "opentyrian");
	session.argv[session.argc] = session.args[0];
	++session.argc;
	snprintf(session.args[1], sizeof(session.args[1]), "--data=%s", config->data_dir);
	session.argv[session.argc] = session.args[1];
	++session.argc;
	if (!(config->flags & OTYR_CONFIG_ENABLE_AUDIO))
	{
		snprintf(session.args[2], sizeof(session.args[2]), "--no-sound");
		session.argv[session.argc] = session.args[2];
		++session.argc;
	}
	if (config->hash_log[0] != '\0')
	{
		snprintf(session.args[3], sizeof(session.args[3]), "--hash-log=%s", config->hash_log);
		session.argv[session.argc] = session.args[3];
		++session.argc;
	}
	/* The host owns input mapping; hosted joystick access would double up. */
	snprintf(session.args[4], sizeof(session.args[4]), "--no-joystick");
	session.argv[session.argc] = session.args[4];
	++session.argc;
	session.argv[session.argc] = NULL;

	snprintf(session.user_dir, sizeof(session.user_dir), "%s", config->user_dir);

	session.sheet_cells = calloc(OTYR_SHEET_COUNT, sizeof(*session.sheet_cells));
	session.sheet_opacity = calloc(OTYR_SHEET_COUNT, sizeof(*session.sheet_opacity));
	if (session.sheet_cells == NULL || session.sheet_opacity == NULL)
	{
		set_error("failed to allocate sheet cache");
		return OTYR_ERROR;
	}
	memset(session.sheet_cell_count, 0, sizeof(session.sheet_cell_count));
	session.sheet_epoch = 0;
	memset(&session.snapshot, 0, sizeof(session.snapshot));

	session.frame_number = 0;
	session.pending_buttons = 0;
	session.applied_buttons = 0;
	memset(&session.player_state, 0, sizeof(session.player_state));
	session.player_state.struct_size = sizeof(OtyrPlayerState);

	otyr_hosted = true;
	windowHasFocus = true;  /* no window: the host always "has focus" */
	/* Configured wishes; the effective flags are recomputed each gameplay
	   tick (present_frame_reset) so smoothie levels can fall back. */
	present_config_suppress_entity = (config->flags & OTYR_CONFIG_SUPPRESS_ENTITY_DRAW) != 0;
	present_config_suppress_background = (config->flags & OTYR_CONFIG_SUPPRESS_BACKGROUND) != 0;
	present_config_suppress_text = (config->flags & OTYR_CONFIG_SUPPRESS_TEXT) != 0;
	present_suppress_entity_draw = present_config_suppress_entity;
	present_suppress_background = present_config_suppress_background;
	present_suppress_text = present_config_suppress_text;
	present_background_hash = (config->flags & OTYR_CONFIG_BACKGROUND_HASHES) != 0;
	memset(session.bg_maps, 0, sizeof(session.bg_maps));

	session.state = SESSION_RUNNING;
	session.thread = SDL_CreateThreadWithStackSize(game_thread_main,
		"otyr-game", GAME_THREAD_STACK_SIZE, NULL);
	if (session.thread == NULL)
	{
		session.state = SESSION_NONE;
		otyr_hosted = false;
		set_error("failed to create game thread");
		return OTYR_ERROR;
	}

	*out_session = SESSION_HANDLE;
	return OTYR_OK;
}

int32_t otyr_session_destroy(uint64_t handle)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;

	if (session.state == SESSION_RUNNING)
	{
		/* Ask the game to quit as if the window were closed. */
		SDL_Event quit_event = { .type = SDL_QUIT };
		SDL_PushEvent(&quit_event);
	}

	/* The game only notices the event at its next input poll; give it a
	   moment.  If it is stuck (e.g. an animation that ignores quit), we
	   detach rather than block the host forever. */
	const Uint32 deadline = SDL_GetTicks() + 5000;
	SDL_LockMutex(session.mutex);
	while (session.state != SESSION_HALTED && SDL_GetTicks() < deadline)
	{
		SDL_CondWaitTimeout(session.frame_ready, session.mutex, 100);
	}
	bool halted = (session.state == SESSION_HALTED);
	SDL_UnlockMutex(session.mutex);

	if (halted)
	{
		SDL_WaitThread(session.thread, NULL);
	}
	else
	{
		SDL_DetachThread(session.thread);
		set_error("game thread did not halt within timeout; detached");
	}

	session.thread = NULL;
	session.state = SESSION_NONE;
	otyr_hosted = false;

	if (halted)
	{
		free(session.sheet_cells);
		session.sheet_cells = NULL;
		free(session.sheet_opacity);
		session.sheet_opacity = NULL;
	}
	/* (detached thread: leak the cache rather than free under its feet) */

	return halted ? OTYR_OK : OTYR_TIMEOUT;
}

static void apply_pending_input_locked(void);

int32_t otyr_session_submit_input(uint64_t handle, const OtyrInputFrame *input,
                                  uint32_t input_size)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (input == NULL || input_size < sizeof(OtyrInputFrame) ||
	    input->struct_size < sizeof(OtyrInputFrame))
		return OTYR_INVALID_ARGUMENT;

	/* Push the key events immediately (SDL_PushEvent is thread-safe).
	   Applying them lazily at present time deadlocks in menus: they block
	   waiting for input without redrawing, so no present ever happens. */
	SDL_LockMutex(session.mutex);
	session.pending_buttons = input->buttons;
	session.pending_analog_dx = input->analog_dx;
	session.pending_analog_dy = input->analog_dy;
	session.pending_use_target = input->use_target;
	session.pending_target_speed = input->target_speed;
	session.pending_target_x = input->target_x;
	session.pending_target_y = input->target_y;
	apply_pending_input_locked();
	SDL_UnlockMutex(session.mutex);
	return OTYR_OK;
}

int32_t otyr_session_acquire_frame(uint64_t handle, OtyrFrame *frame,
                                   uint32_t frame_size, uint32_t timeout_ms)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (frame == NULL || frame_size < sizeof(OtyrFrame) ||
	    frame->struct_size < sizeof(OtyrFrame))
		return OTYR_INVALID_ARGUMENT;

	/* frame->frame_number doubles as "the last frame this caller saw". */
	const uint32_t last_seen = frame->frame_number;

	int32_t result = OTYR_TIMEOUT;
	const Uint32 deadline = SDL_GetTicks() + timeout_ms;

	SDL_LockMutex(session.mutex);
	for (;;)
	{
		if (session.frame_number != last_seen && session.frame_number != 0)
		{
			frame->frame_number = session.frame_number;
			frame->width = OTYR_FRAME_WIDTH;
			frame->height = OTYR_FRAME_HEIGHT;
			memcpy(frame->pixels, session.pixels, sizeof(session.pixels));
			memcpy(frame->palette, session.palette_argb, sizeof(session.palette_argb));
			frame->level_tick = session.frame_level_tick;
			frame->in_level = session.frame_in_level;
			frame->legacy_fallback = session.frame_legacy_fallback;
			frame->menu_present = session.frame_menu_present;
			memset(frame->reserved, 0, sizeof(frame->reserved));
			result = OTYR_OK;
			break;
		}
		if (session.state == SESSION_HALTED)
		{
			result = OTYR_INVALID_SESSION;
			break;
		}
		Uint32 now = SDL_GetTicks();
		if (now >= deadline)
			break;
		SDL_CondWaitTimeout(session.frame_ready, session.mutex, deadline - now);
	}
	SDL_UnlockMutex(session.mutex);
	return result;
}

int32_t otyr_session_player_state(uint64_t handle, OtyrPlayerState *state,
                                  uint32_t state_size)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (state == NULL || state_size < sizeof(OtyrPlayerState) ||
	    state->struct_size < sizeof(OtyrPlayerState))
		return OTYR_INVALID_ARGUMENT;

	SDL_LockMutex(session.mutex);
	*state = session.player_state;
	SDL_UnlockMutex(session.mutex);
	return OTYR_OK;
}

int32_t otyr_session_snapshot(uint64_t handle, OtyrSnapshot *snapshot,
                              uint32_t snapshot_size, uint32_t timeout_ms)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (snapshot == NULL || snapshot_size < sizeof(OtyrSnapshot) ||
	    snapshot->struct_size < sizeof(OtyrSnapshot))
		return OTYR_INVALID_ARGUMENT;

	const uint32_t last_seen = snapshot->level_tick;

	int32_t result = OTYR_TIMEOUT;
	const Uint32 deadline = SDL_GetTicks() + timeout_ms;

	SDL_LockMutex(session.mutex);
	for (;;)
	{
		if (session.snapshot.level_tick != last_seen && session.snapshot.level_tick != 0)
		{
			memcpy(snapshot, &session.snapshot, sizeof(OtyrSnapshot));
			result = OTYR_OK;
			break;
		}
		if (session.state == SESSION_HALTED)
		{
			result = OTYR_INVALID_SESSION;
			break;
		}
		Uint32 now = SDL_GetTicks();
		if (now >= deadline)
			break;
		SDL_CondWaitTimeout(session.frame_ready, session.mutex, deadline - now);
	}
	SDL_UnlockMutex(session.mutex);
	return result;
}

int32_t otyr_sprite_sheet(uint64_t handle, uint32_t sheet_id,
                          OtyrSpriteSheet *sheet, uint32_t sheet_size)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (sheet == NULL || sheet_size < sizeof(OtyrSpriteSheet) ||
	    sheet->struct_size < sizeof(OtyrSpriteSheet) ||
	    sheet_id >= OTYR_SHEET_COUNT)
		return OTYR_INVALID_ARGUMENT;

	SDL_LockMutex(session.mutex);
	sheet->sheet_epoch = session.sheet_epoch;
	sheet->cell_count = session.sheet_cell_count[sheet_id];
	memcpy(sheet->pixels, session.sheet_cells[sheet_id],
	       sheet->cell_count * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H);
	memcpy(sheet->opacity, session.sheet_opacity[sheet_id],
	       sheet->cell_count * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H);
	SDL_UnlockMutex(session.mutex);
	return OTYR_OK;
}

int32_t otyr_background_map(uint64_t handle, uint32_t layer,
                            OtyrBackgroundMap *map, uint32_t map_size)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (map == NULL || map_size < sizeof(OtyrBackgroundMap) ||
	    map->struct_size < sizeof(OtyrBackgroundMap) ||
	    layer >= OTYR_BG_LAYER_COUNT)
		return OTYR_INVALID_ARGUMENT;

	SDL_LockMutex(session.mutex);
	memcpy(map, &session.bg_maps[layer], sizeof(OtyrBackgroundMap));
	map->struct_size = sizeof(OtyrBackgroundMap);
	map->sheet_epoch = session.sheet_epoch;
	SDL_UnlockMutex(session.mutex);
	return OTYR_OK;
}

int32_t otyr_old_sprite(uint64_t handle, uint32_t table, uint32_t index,
                        OtyrOldSprite *out, uint32_t out_size)
{
	if (handle != SESSION_HANDLE || session.state == SESSION_NONE)
		return OTYR_INVALID_SESSION;
	if (out == NULL || out_size < sizeof(OtyrOldSprite) ||
	    out->struct_size < sizeof(OtyrOldSprite) ||
	    index >= OTYR_OLD_SPRITE_MAX)
		return OTYR_INVALID_ARGUMENT;
	const int slot = old_table_slot(table);
	if (slot < 0)
		return OTYR_UNSUPPORTED;  /* only OPTION_SHAPES and fonts are cached */

	SDL_LockMutex(session.mutex);
	memcpy(out, &session.old_sprites[slot][index], sizeof(OtyrOldSprite));
	out->struct_size = sizeof(OtyrOldSprite);
	SDL_UnlockMutex(session.mutex);
	return OTYR_OK;
}

/* ---- game-thread side ---------------------------------------------- */

static void push_key_event(SDL_Scancode scancode, bool down)
{
	SDL_Event ev;
	memset(&ev, 0, sizeof(ev));
	ev.type = down ? SDL_KEYDOWN : SDL_KEYUP;
	ev.key.state = down ? SDL_PRESSED : SDL_RELEASED;
	ev.key.keysym.scancode = scancode;
	ev.key.keysym.sym = SDL_GetKeyFromScancode(scancode);
	SDL_PushEvent(&ev);
}

static SDL_Scancode button_scancode(unsigned int bit)
{
	/* Bits 0-7 are the eight configurable game actions, in keySettings
	   order (up, down, left, right, fire, change fire, sidekicks). */
	if (bit < 8)
		return keySettings[bit];

	switch (1u << bit)
	{
	case OTYR_BUTTON_UI_CONFIRM: return SDL_SCANCODE_RETURN;
	case OTYR_BUTTON_UI_CANCEL:  return SDL_SCANCODE_ESCAPE;
	case OTYR_BUTTON_UI_SPACE:   return SDL_SCANCODE_SPACE;
	case OTYR_BUTTON_UI_PAUSE:   return SDL_SCANCODE_P;
	default:                     return SDL_SCANCODE_UNKNOWN;
	}
}

static void apply_pending_input_locked(void)
{
	const uint32_t pending = session.pending_buttons;
	const uint32_t changed = pending ^ session.applied_buttons;
	if (changed == 0)
		return;

	for (unsigned int bit = 0; bit < 12; ++bit)
	{
		if (!(changed & (1u << bit)))
			continue;
		SDL_Scancode scancode = button_scancode(bit);
		if (scancode != SDL_SCANCODE_UNKNOWN)
			push_key_event(scancode, (pending & (1u << bit)) != 0);
	}
	session.applied_buttons = pending;
}

void otyr_trace(const char *tag, Uint32 a, Uint32 b)
{
	static FILE *trace_file = NULL;
	static bool checked = false;

	if (!checked)
	{
		checked = true;
		const char *path = SDL_getenv("OTYR_TRACE");
		if (path != NULL && path[0] != '\0')
			trace_file = fopen(path, "w");
	}
	if (trace_file == NULL)
		return;
	fprintf(trace_file, "%s %lu %lu\n", tag, (unsigned long)a, (unsigned long)b);
	fflush(trace_file);
}

void otyr_host_present(SDL_Surface *screen)
{
	static Uint32 last_present = 0;
	Uint32 now_present = SDL_GetTicks();
	if (last_present != 0)
		otyr_trace("present", now_present - last_present, now_present);
	last_present = now_present;

	SDL_LockMutex(session.mutex);

	const Uint8 *pixels = (const Uint8 *)screen->pixels;
	for (int y = 0; y < (int)OTYR_FRAME_HEIGHT; ++y)
		memcpy(session.pixels + y * OTYR_FRAME_WIDTH,
		       pixels + y * screen->pitch, OTYR_FRAME_WIDTH);

	/* rgb_palette is SDL_PIXELFORMAT_RGB888 (0x00RRGGBB); force alpha. */
	for (int i = 0; i < 256; ++i)
		session.palette_argb[i] = rgb_palette[i] | 0xff000000u;

	session.player_state.x = player[0].x;
	session.player_state.y = player[0].y;
	session.player_state.shield = player[0].shield;
	session.player_state.armor = player[0].armor;
	session.player_state.cash = (uint32_t)player[0].cash;
	session.player_state.lives = player[0].lives != NULL ? *player[0].lives : 0;
	session.player_state.is_alive = player[0].is_alive;
	session.player_state.level_tick = session.level_tick;
	session.player_state.x_velocity = player[0].x_velocity;
	session.player_state.y_velocity = player[0].y_velocity;

	++session.frame_number;
	session.frame_level_tick = session.level_tick;
	session.frame_in_level = otyr_in_level;
	session.frame_legacy_fallback = present_legacy_fallback && otyr_in_level;
	session.frame_menu_present = otyr_in_level && !otyr_tick_present;

	OtyrSnapshot *snapshot = &session.snapshot;

	/* Only the tick-completing present (JE_starShowVGA) publishes sprite
	   records.  Pause and the in-game menu present MID-TICK: the record
	   buffer is partial, and publishing it froze whichever fragments the
	   interrupted tick had recorded over the menu backdrop for the whole
	   pause (the round-4 "pip" -- a piece of the special-weapon HUD icon).
	   Mid-tick presents keep the last complete tick's records, minus the
	   in-play text/HUD layer (frozen HUD would float over the menu box),
	   and fire no sounds. */
	if (otyr_in_level && !otyr_tick_present)
	{
		snapshot->struct_size = sizeof(OtyrSnapshot);
		unsigned int kept = 0;
		for (unsigned int i = 0; i < snapshot->sprite_count; ++i)
			if (snapshot->sprites[i].category != OTYR_CAT_TEXT)
			{
				if (kept != i)
					snapshot->sprites[kept] = snapshot->sprites[i];
				++kept;
			}
		snapshot->sprite_count = kept;
		snapshot->sound_count = 0;
		snapshot->level_tick = session.level_tick;

		SDL_CondBroadcast(session.frame_ready);
		SDL_UnlockMutex(session.mutex);
		return;
	}
	otyr_tick_present = false;

	/* Publish the presentation snapshot for this tick (records complete by
	   present time; sheet pointers resolve to stable ids). */
	snapshot->struct_size = sizeof(OtyrSnapshot);
	snapshot->level_tick = session.level_tick;
	snapshot->sheet_epoch = session.sheet_epoch;

	unsigned int sprite_count = present_sprite_count;
	if (sprite_count > OTYR_SNAPSHOT_SPRITE_MAX)
		sprite_count = OTYR_SNAPSHOT_SPRITE_MAX;
	snapshot->sprite_count = sprite_count;
	for (unsigned int i = 0; i < sprite_count; ++i)
	{
		const PresentSprite *in = &present_sprites[i];
		OtyrSnapshotSprite *out = &snapshot->sprites[i];
		out->category = in->category;
		out->kind = in->kind;
		out->flags = in->flags;
		out->filter_color = in->filter_color;
		out->x = in->x;
		out->y = in->y;
		out->index = in->index;
		out->sheet_id = in->sheet != NULL ? sheet_id_for(in->sheet) : OTYR_SHEET_INVALID;
		out->aux = in->aux;
		out->source_id = in->source_id;
		out->entity_type = in->entity_type;
	}

	/* Stacked statics recorded BEFORE their base (e.g. a dome crown whose
	   slot draws earlier than the dome body) miss the in-tick surface-rider
	   check; re-run it over the complete record list so never-moved enemies
	   whose center sits on terrain-baked art export as aux 2. */
	for (unsigned int i = 0; i < sprite_count; ++i)
	{
		OtyrSnapshotSprite *rec = &snapshot->sprites[i];
		if (rec->category > OTYR_CAT_ENEMY_GROUND_B || rec->aux != 0)
			continue;
		if ((rec->source_id & 0xff00) != 0x1000 || (rec->source_id & 0xff) >= 100)
			continue;  /* only enemy-slot records carry the latch */
		if (otyr_enemy_moved[rec->source_id & 0xff])
			continue;

		const int16_t cx = rec->x + (rec->kind == 1 ? 12 : 6);
		const int16_t cy = rec->y + (rec->kind == 1 ? 14 : 7);
		for (unsigned int r = 0; r < sprite_count; ++r)
		{
			const OtyrSnapshotSprite *other = &snapshot->sprites[r];
			if (other->aux != 1 || other->category > OTYR_CAT_ENEMY_GROUND_B)
				continue;
			const int16_t w = other->kind == 1 ? 24 : 12;
			const int16_t h = other->kind == 1 ? 28 : 14;
			if (cx >= other->x && cx < other->x + w &&
			    cy >= other->y && cy < other->y + h)
			{
				rec->aux = 2;
				break;
			}
		}
	}

	unsigned int sound_count = present_sound_count;
	if (sound_count > OTYR_SNAPSHOT_SOUND_MAX)
		sound_count = OTYR_SNAPSHOT_SOUND_MAX;
	snapshot->sound_count = sound_count;
	memcpy(snapshot->sound_channel, present_sound_channel, sizeof(snapshot->sound_channel));
	memcpy(snapshot->sound_sample, present_sound_sample, sizeof(snapshot->sound_sample));

	for (unsigned int l = 0; l < OTYR_BG_LAYER_COUNT; ++l)
	{
		const PresentBackground *in = &present_backgrounds[l];
		OtyrBackgroundDraw *out = &snapshot->background[l];
		out->tile_offset = in->tile_offset;
		out->x = in->x;
		out->y = in->y;
		out->drawn = in->drawn;
		out->blend = in->blend;
		out->over_mode = in->over;
		out->reserved = 0;
		out->hash = in->hash;
	}

	SDL_CondBroadcast(session.frame_ready);
	SDL_UnlockMutex(session.mutex);
}
