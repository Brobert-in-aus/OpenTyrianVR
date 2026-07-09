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
#include "present_frame.h"

#include <assert.h>

PresentSprite present_sprites[PRESENT_SPRITE_MAX];
unsigned int present_sprite_count = 0;

void present_frame_reset(void)
{
	present_sprite_count = 0;
}

unsigned int present_record(PresentCategory category, PresentBlitKind kind,
                            Uint8 flags, Uint8 filter_color,
                            Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index)
{
	assert(present_sprite_count < PRESENT_SPRITE_MAX);
	if (present_sprite_count >= PRESENT_SPRITE_MAX)
		return PRESENT_SPRITE_MAX;

	PresentSprite *sprite = &present_sprites[present_sprite_count];
	sprite->category = (Uint8)category;
	sprite->kind = (Uint8)kind;
	sprite->flags = flags;
	sprite->filter_color = filter_color;
	sprite->x = x;
	sprite->y = y;
	sprite->index = index;
	sprite->sheet = sheet;
	return present_sprite_count++;
}

void present_draw_from(SDL_Surface *surface, unsigned int from)
{
	for (unsigned int i = from; i < present_sprite_count; i++)
	{
		const PresentSprite *sprite = &present_sprites[i];

		if (sprite->kind == PRESENT_BLIT_SPRITE2)
		{
			if (sprite->flags & PRESENT_FLAG_FILTER)
				blit_sprite2_filter(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index, sprite->filter_color);
			else if (sprite->flags & PRESENT_FLAG_BLEND)
				blit_sprite2_blend(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
			else
				blit_sprite2(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
		}
		else  /* PRESENT_BLIT_SPRITE2X2 */
		{
			if (sprite->flags & PRESENT_FLAG_DARKEN)
				blit_sprite2x2_darken(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
			else if (sprite->flags & PRESENT_FLAG_BLEND)
				blit_sprite2x2_blend(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
			else
				blit_sprite2x2(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
		}
	}
}
