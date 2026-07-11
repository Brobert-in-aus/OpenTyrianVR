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

#include "backgrnd.h"
#include "config.h"
#include "otyr_host_internal.h"
#include "video.h"

#include <assert.h>
#include <string.h>

PresentSprite present_sprites[PRESENT_SPRITE_MAX];
unsigned int present_sprite_count = 0;

Uint8 present_sound_channel[PRESENT_SOUND_MAX];
Uint8 present_sound_sample[PRESENT_SOUND_MAX];
unsigned int present_sound_count = 0;

PresentBackground present_backgrounds[PRESENT_BACKGROUND_LAYERS];
bool present_suppress_background = false;
bool present_background_hash = false;

void present_frame_reset(void)
{
	present_sprite_count = 0;
	present_sound_count = 0;
	memset(present_backgrounds, 0, sizeof(present_backgrounds));
	present_text_window = true;

	/* Smoothie filters (lava/water/iced blur, the starShowVGA warp) read
	   and rewrite the composited frame mid-draw-order; with suppression
	   they would warp a key-filled buffer.  Fall back to full legacy
	   drawing while any is active (one-tick lag on transitions: both
	   values are last tick's -- acceptable).  Unhosted, the config flags
	   are all false and this is a no-op. */
	present_legacy_fallback = anySmoothies || starShowVGASpecialCode != 0;
	present_suppress_entity_draw = present_config_suppress_entity && !present_legacy_fallback;
	present_suppress_background = present_config_suppress_background && !present_legacy_fallback;
	present_suppress_text = present_config_suppress_text && !present_legacy_fallback;
}

void present_background(int layer, Sint32 tile_offset, Sint16 x, Sint16 y,
                        bool blend, Uint8 over, Uint32 hash)
{
	if (layer < 0 || layer >= PRESENT_BACKGROUND_LAYERS)
		return;
	present_backgrounds[layer].tile_offset = tile_offset;
	present_backgrounds[layer].x = x;
	present_backgrounds[layer].y = y;
	present_backgrounds[layer].drawn = 1;
	present_backgrounds[layer].blend = blend ? 1 : 0;
	present_backgrounds[layer].over = over;
	present_backgrounds[layer].hash = hash;
}

void present_sound(Uint8 channel, Uint8 sample)
{
	if (present_sound_count < PRESENT_SOUND_MAX)
	{
		present_sound_channel[present_sound_count] = channel;
		present_sound_sample[present_sound_count] = sample;
		++present_sound_count;
	}
}

unsigned int present_record_id(PresentCategory category, PresentBlitKind kind,
                               Uint8 flags, Uint8 filter_color, Uint8 aux,
                               Uint16 source_id,
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
	sprite->aux = aux;
	sprite->x = x;
	sprite->y = y;
	sprite->index = index;
	sprite->source_id = source_id;
	sprite->sheet = sheet;
	return present_sprite_count++;
}

unsigned int present_record_aux(PresentCategory category, PresentBlitKind kind,
                                Uint8 flags, Uint8 filter_color, Uint8 aux,
                                Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index)
{
	return present_record_id(category, kind, flags, filter_color, aux,
	                         PRESENT_NO_SOURCE, sheet, x, y, index);
}

unsigned int present_record(PresentCategory category, PresentBlitKind kind,
                            Uint8 flags, Uint8 filter_color,
                            Sprite2_array *sheet, Sint16 x, Sint16 y, Uint16 index)
{
	return present_record_aux(category, kind, flags, filter_color, 0,
	                          sheet, x, y, index);
}

bool present_suppress_entity_draw = false;

bool present_config_suppress_entity = false;
bool present_config_suppress_background = false;
bool present_config_suppress_text = false;
bool present_legacy_fallback = false;

bool present_text_window = false;
bool present_suppress_text = false;

/* In-play overlay text: the tick's record window is open, the draw targets
 * the gameplay surface (pause/menu screens draw to VGAScreenSeg), and the
 * position is inside the play region (sidebar and bottom text bar stay in
 * the frame). */
static bool text_gate(SDL_Surface *screen, int x, int y)
{
	bool pass = present_text_window && screen == game_screen &&
	            x > -64 && x < 264 && y > -64 && y < 184;
	/* Tripwire (OTYR_TRACE): an in-play-looking HUD/text draw that misses
	   the gate mid-level lands FLAT in the frame -- log who and where, so
	   intermittent leaks (the pause-backdrop "pip") identify themselves. */
	if (!pass && otyr_hosted && otyr_in_level &&
	    x >= 0 && x < 264 && y >= 0 && y < 184)
		otyr_trace(present_text_window ? "hudleak-surface" : "hudleak-window",
		           (Uint32)x, (Uint32)y);
	return pass;
}

bool present_text_glyph(SDL_Surface *screen, int x, int y,
                        unsigned int table, unsigned int sprite_id,
                        Uint8 flags, Uint8 hue, Sint8 value)
{
	if (!text_gate(screen, x, y))
		return false;
	present_record_aux(PRESENT_TEXT, PRESENT_BLIT_SPRITE_HV, flags,
	                   (Uint8)((table << 4) | (hue & 0x0f)), (Uint8)value,
	                   NULL, (Sint16)x, (Sint16)y, (Uint16)sprite_id);
	return present_suppress_text;
}

bool present_hud_blit(SDL_Surface *screen, Sprite2_array *sheet,
                      int x, int y, unsigned int index, bool two_by_two)
{
	if (!text_gate(screen, x, y))
		return false;
	present_record(PRESENT_TEXT,
	               two_by_two ? PRESENT_BLIT_SPRITE2X2 : PRESENT_BLIT_SPRITE2,
	               0, 0, sheet, (Sint16)x, (Sint16)y, (Uint16)index);
	return present_suppress_text;
}

void present_draw_from(SDL_Surface *surface, unsigned int from)
{
	for (unsigned int i = from; i < present_sprite_count; i++)
	{
		/* In suppress mode the host renders entities in 3D -- except art
		   that is really terrain paint: ground-flagged enemy sprites (baked
		   tile backdrops, must layer under clouds/text) and shadows (depth
		   cues cast onto the terrain).  Those keep drawing into the frame. */
		if (present_suppress_entity_draw)
			continue;  /* the host renders every record in 3D -- including
			              shadows (translucent quads over the terrain) and
			              baked structures (map-locked coplanar cells); the
			              frame keeps only backgrounds, HUD, and text */
		const PresentSprite *sprite = &present_sprites[i];

		/* Text/HUD records blit inline at their call sites (fonthand.c,
		   JE_inGameDisplays); they are export metadata only. */
		if (sprite->category == PRESENT_TEXT)
			continue;

		if (sprite->kind == PRESENT_BLIT_SPRITE2)
		{
			if (sprite->flags & PRESENT_FLAG_FILTER)
				blit_sprite2_filter(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index, sprite->filter_color);
			else if (sprite->flags & PRESENT_FLAG_BLEND)
				blit_sprite2_blend(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
			else if (sprite->flags & PRESENT_FLAG_DARKEN)
				blit_sprite2_darken(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
			else
				blit_sprite2(surface, sprite->x, sprite->y, *sprite->sheet, sprite->index);
		}
		else if (sprite->kind == PRESENT_BLIT_SPRITE_BLEND)
		{
			blit_sprite_blend(surface, sprite->x, sprite->y, sprite->filter_color, sprite->index);
		}
		else if (sprite->kind == PRESENT_PIXEL_GLOW)
		{
			const Uint16 x = (Uint16)sprite->x, y = (Uint16)sprite->y;
			const Uint8 z = sprite->filter_color;
			const Uint8 color = (Uint8)sprite->index;

			if (x < (unsigned)surface->w && y < (unsigned)surface->h)
			{
				Uint8 *s = (Uint8 *)surface->pixels; /* screen pointer, 8-bit specific */
				s += y * surface->pitch;
				s += x;

				*s = (((*s & 0x0f) + z) >> 1) + color;
				if (x > 0)
					*(s - 1) = (((*(s - 1) & 0x0f) + (z >> 1)) >> 1) + color;
				if (x < surface->w - 1u)
					*(s + 1) = (((*(s + 1) & 0x0f) + (z >> 1)) >> 1) + color;
				if (y > 0)
					*(s - surface->pitch) = (((*(s - surface->pitch) & 0x0f) + (z >> 1)) >> 1) + color;
				if (y < surface->h - 1u)
					*(s + surface->pitch) = (((*(s + surface->pitch) & 0x0f) + (z >> 1)) >> 1) + color;
			}
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
