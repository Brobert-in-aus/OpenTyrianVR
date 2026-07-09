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
#ifndef OTYR_HOST_INTERNAL_H
#define OTYR_HOST_INTERNAL_H

/* Hooks the game code calls when running thread-hosted inside the library.
 * When otyr_hosted is false (the normal desktop executable), every hook is a
 * no-op and the legacy paths run unchanged. */

#include "opentyr.h"

#include "SDL.h"

extern bool otyr_hosted;

/* Called from JE_showVGA in place of presenting to a window: publishes the
 * frame to the host and applies pending host input. */
void otyr_host_present(SDL_Surface *screen);

/* Called at the end of JE_tyrianHalt in place of exit(): unwinds the game
 * thread back to its entry point so the host process survives. */
void otyr_host_thread_exit(int code);

/* Called once per gameplay tick (top of the level loop). */
void otyr_host_level_tick(void);

/* Called on the game thread after a level's sprite sheets are loaded;
 * rasterizes all sheets into the session cache and bumps the epoch. */
void otyr_host_capture_sheets(void);

/* True when the cell is (near-)fully opaque: baked terrain art that must
 * stay in the legacy frame rather than float as a 3D sprite.  sheet is a
 * const Sprite2_array *. */
bool otyr_host_cell_is_opaque(const void *sheet, Uint16 index);

/* Fills the gameplay input frame from the host's submitted state; consumed
 * by the player-movement tick in place of local device sampling. */
struct GameInput;
void otyr_host_game_input(struct GameInput *input);

/* Hosted config/save directory; empty string means "use the default". */
const char *otyr_host_user_dir(void);

/* Timing diagnostics: appends "tag a b" lines to the file named by the
 * OTYR_TRACE environment variable; no-op when unset. */
void otyr_trace(const char *tag, Uint32 a, Uint32 b);

/* The real program entry, shared by main() and the hosted game thread. */
int opentyrian_main(int argc, char *argv[]);

#endif /* OTYR_HOST_INTERNAL_H */
