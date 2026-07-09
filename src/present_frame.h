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
	PRESENT_PLAYER_SHADOW,      /* decoration on terrain */
	PRESENT_SIDEKICK,           /* low band */
	PRESENT_EXPLOSION,          /* band of source entity */
	PRESENT_SUPERPIXEL,         /* debris */
} PresentCategory;

typedef enum PresentBlitKind
{
	PRESENT_BLIT_SPRITE2 = 0,       /* blit_sprite2; filter/blend variants by flags */
	PRESENT_BLIT_SPRITE2X2,         /* blit_sprite2x2 and variants */
} PresentBlitKind;

enum
{
	PRESENT_FLAG_FILTER = 1,        /* use filter_color */
	PRESENT_FLAG_BLEND = 2,         /* additive blend variant */
	PRESENT_FLAG_DARKEN = 4,        /* darken variant (shadows) */
};

typedef struct PresentSprite
{
	Uint8 category;                 /* PresentCategory */
	Uint8 kind;                     /* PresentBlitKind */
	Uint8 flags;
	Uint8 filter_color;
	Sint16 x, y;                    /* frame coordinates at record time */
	Uint16 index;                   /* sprite index within the sheet */
	Sprite2_array *sheet;
} PresentSprite;

#define PRESENT_SPRITE_MAX 1024

extern PresentSprite present_sprites[PRESENT_SPRITE_MAX];
extern unsigned int present_sprite_count;

/* Clears the record list; called once at the top of each gameplay tick. */
void present_frame_reset(void);

/* Appends a record and returns its index (or drops it silently when full,
 * returning PRESENT_SPRITE_MAX; the assert catches overflow in debug). */
unsigned int present_record(PresentCategory category, PresentBlitKind kind,
                            Uint8 flags, Uint8 filter_color,
                            Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index);

/* Replays records [from, count) onto the surface; each subsystem calls this
 * for its own slice at its original draw point in the tick. */
void present_draw_from(SDL_Surface *surface, unsigned int from);

#endif /* PRESENT_FRAME_H */
