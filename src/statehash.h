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
#ifndef STATEHASH_H
#define STATEHASH_H

#include "opentyr.h"

/* Deterministic per-tick hashing of gameplay state, used to verify that
 * replays and refactors preserve simulation behavior.  Enabled with the
 * --hash-log=FILE command-line option.  Each gameplay tick appends a line:
 *
 *   <tick> <state hash> <frame hash>
 *
 * where the state hash covers players, enemies, shots, level-event progress,
 * and RNG state, and the frame hash covers the legacy 320x200 framebuffer as
 * of the start of the tick. */

extern bool statehash_enabled;

bool statehash_open(const char *path);
void statehash_close(void);

/* Writes a comment line, e.g. at level start. */
void statehash_note(const char *note);

/* Hashes simulation state and the current VGAScreen; called once per
 * gameplay tick at the top of the level loop. */
void statehash_tick(void);

#endif /* STATEHASH_H */
