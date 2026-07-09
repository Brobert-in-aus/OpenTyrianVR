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
#include "video.h"

#include "SDL.h"

#include <setjmp.h>
#include <stdio.h>
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
} session;

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

void otyr_host_level_tick(void)
{
	/* Game-thread write; published to the host via the state snapshot taken
	   under the mutex at present time. */
	++session.level_tick;
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

	session.frame_number = 0;
	session.pending_buttons = 0;
	session.applied_buttons = 0;
	memset(&session.player_state, 0, sizeof(session.player_state));
	session.player_state.struct_size = sizeof(OtyrPlayerState);

	otyr_hosted = true;
	windowHasFocus = true;  /* no window: the host always "has focus" */

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

	SDL_CondBroadcast(session.frame_ready);
	SDL_UnlockMutex(session.mutex);
}
