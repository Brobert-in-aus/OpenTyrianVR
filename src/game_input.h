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
#ifndef GAME_INPUT_H
#define GAME_INPUT_H

#include "opentyr.h"
#include "player.h"

/* The abstract per-tick gameplay input frame (VR_CONVERSION_PLAN.md Phase 2
 * step 1).  Exactly one frame is consumed per player per gameplay tick, from
 * one of three producers: local device sampling (desktop), demo playback, or
 * the native host boundary. */
typedef struct GameInput
{
	/* Digital movement at keyboard speed (CURRENT_KEY_SPEED px per tick). */
	bool up, down, left, right;

	/* Analog movement in mouse-accumulator units: contributed every tick
	 * while nonzero, applied as (accumulator +-3)/4 px per tick with the
	 * accumulator clamped to +-30.  Same semantics as an analog joystick
	 * axis or relative mouse motion. */
	Sint16 analog_dx, analog_dy;

	/* Actions. */
	bool fire, left_sidekick, right_sidekick, change_fire;

	/* Dragonwing linked-mode analog gun aim (analog joystick only). */
	bool link_gun_analog;
	float link_gun_angle;
} GameInput;

/* Samples keyboard/mouse/joystick into an input frame (the desktop producer).
 * Mirrors the legacy behavior exactly: also feeds the in-game-menu/pause
 * globals from joystick buttons, applies the constantPlay cheat's direct
 * position effects, and writes demo-recording bytes when recording. */
void game_input_sample_local(GameInput *input, Player *this_player, JE_byte inputDevice);

#endif /* GAME_INPUT_H */
