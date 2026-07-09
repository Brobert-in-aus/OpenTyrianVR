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
#include "statehash.h"

#include "mtrand.h"
#include "player.h"
#include "shots.h"
#include "varz.h"
#include "video.h"

#include <stdio.h>

bool statehash_enabled = false;

static FILE *log_file = NULL;
static Uint64 hash_tick = 0;

#define FNV64_OFFSET 14695981039346656037ULL
#define FNV64_PRIME  1099511628211ULL

static Uint64 fnv64_u8(Uint64 h, Uint8 v)
{
	return (h ^ v) * FNV64_PRIME;
}

static Uint64 fnv64_u32(Uint64 h, Uint32 v)
{
	h = fnv64_u8(h, (Uint8)(v));
	h = fnv64_u8(h, (Uint8)(v >> 8));
	h = fnv64_u8(h, (Uint8)(v >> 16));
	h = fnv64_u8(h, (Uint8)(v >> 24));
	return h;
}

bool statehash_open(const char *path)
{
	log_file = fopen(path, "w");
	if (log_file == NULL)
	{
		fprintf(stderr, "error: failed to open hash log '%s'\n", path);
		return false;
	}
	statehash_enabled = true;
	return true;
}

void statehash_close(void)
{
	if (log_file != NULL)
	{
		fclose(log_file);
		log_file = NULL;
	}
	statehash_enabled = false;
}

void statehash_note(const char *note)
{
	if (log_file == NULL)
		return;
	fprintf(log_file, "# %s\n", note);
	fflush(log_file);
}

/* Fields are hashed individually (not whole structs) because several structs
 * contain pointers and padding, which are not run-to-run deterministic. */
static Uint64 hash_players(Uint64 h)
{
	for (uint i = 0; i < COUNTOF(player); ++i)
	{
		const Player *p = &player[i];

		h = fnv64_u32(h, (Uint32)p->cash);
		h = fnv64_u8(h, p->items.ship);
		h = fnv64_u8(h, p->items.generator);
		h = fnv64_u8(h, p->items.shield);
		for (uint w = 0; w < 2; ++w)
		{
			h = fnv64_u8(h, p->items.weapon[w].id);
			h = fnv64_u8(h, p->items.weapon[w].power);
			h = fnv64_u8(h, p->items.sidekick[w]);
		}
		h = fnv64_u8(h, p->items.special);
		h = fnv64_u8(h, p->items.sidekick_series);
		h = fnv64_u8(h, p->items.sidekick_level);
		h = fnv64_u8(h, p->items.super_arcade_mode);

		h = fnv64_u8(h, p->lives != NULL ? *p->lives : 0);

		h = fnv64_u8(h, p->is_alive);
		h = fnv64_u32(h, p->invulnerable_ticks);
		h = fnv64_u32(h, p->exploding_ticks);
		h = fnv64_u32(h, p->shield);
		h = fnv64_u32(h, p->armor);
		h = fnv64_u32(h, p->weapon_mode);
		h = fnv64_u32(h, p->superbombs);
		h = fnv64_u32(h, p->purple_balls_needed);
		h = fnv64_u32(h, (Uint32)p->x);
		h = fnv64_u32(h, (Uint32)p->y);
		h = fnv64_u32(h, (Uint32)p->x_velocity);
		h = fnv64_u32(h, (Uint32)p->y_velocity);
		h = fnv64_u32(h, (Uint32)p->delta_x_shot_move);
		h = fnv64_u32(h, (Uint32)p->delta_y_shot_move);

		for (uint s = 0; s < 2; ++s)
		{
			h = fnv64_u32(h, (Uint32)p->sidekick[s].x);
			h = fnv64_u32(h, (Uint32)p->sidekick[s].y);
			h = fnv64_u32(h, (Uint32)p->sidekick[s].ammo);
		}
	}
	return h;
}

static Uint64 hash_enemies(Uint64 h)
{
	for (uint i = 0; i < COUNTOF(enemy); ++i)
	{
		const struct JE_SingleEnemyType *e = &enemy[i];

		h = fnv64_u8(h, enemyAvail[i]);
		h = fnv64_u32(h, (Uint32)e->ex);
		h = fnv64_u32(h, (Uint32)e->ey);
		h = fnv64_u8(h, (Uint8)e->exc);
		h = fnv64_u8(h, (Uint8)e->eyc);
		h = fnv64_u8(h, (Uint8)e->exca);
		h = fnv64_u8(h, (Uint8)e->eyca);
		h = fnv64_u8(h, (Uint8)e->excc);
		h = fnv64_u8(h, (Uint8)e->eycc);
		h = fnv64_u8(h, e->armorleft);
		h = fnv64_u8(h, e->enemycycle);
		h = fnv64_u8(h, e->ani);
		h = fnv64_u8(h, e->aniactive);
		h = fnv64_u8(h, e->linknum);
		h = fnv64_u32(h, e->enemytype);
		h = fnv64_u8(h, e->launchwait);
		h = fnv64_u32(h, e->launchtype);
		h = fnv64_u8(h, e->iced);
		h = fnv64_u8(h, e->edamaged);
		h = fnv64_u32(h, (Uint32)e->evalue);
		h = fnv64_u32(h, e->mapoffset);
	}
	return h;
}

static Uint64 hash_shots(Uint64 h)
{
	for (uint i = 0; i < ENEMY_SHOT_MAX; ++i)
	{
		const EnemyShotType *s = &enemyShot[i];

		h = fnv64_u8(h, enemyShotAvail[i]);
		h = fnv64_u32(h, (Uint32)s->sx);
		h = fnv64_u32(h, (Uint32)s->sy);
		h = fnv64_u32(h, (Uint32)s->sxm);
		h = fnv64_u32(h, (Uint32)s->sym);
		h = fnv64_u8(h, (Uint8)s->sxc);
		h = fnv64_u8(h, (Uint8)s->syc);
		h = fnv64_u32(h, s->sgr);
		h = fnv64_u8(h, s->sdmg);
		h = fnv64_u8(h, s->duration);
		h = fnv64_u32(h, s->animate);
	}

	for (uint i = 0; i < MAX_PWEAPON; ++i)
	{
		const PlayerShotDataType *s = &playerShotData[i];

		h = fnv64_u8(h, shotAvail[i]);
		h = fnv64_u32(h, (Uint32)s->shotX);
		h = fnv64_u32(h, (Uint32)s->shotY);
		h = fnv64_u32(h, (Uint32)s->shotXM);
		h = fnv64_u32(h, (Uint32)s->shotYM);
		h = fnv64_u32(h, (Uint32)s->shotXC);
		h = fnv64_u32(h, (Uint32)s->shotYC);
		h = fnv64_u8(h, s->shotDmg);
		h = fnv64_u32(h, s->shotGr);
		h = fnv64_u32(h, s->shotAni);
		h = fnv64_u8(h, s->playerNumber);
		h = fnv64_u8(h, s->aimAtEnemy);
		h = fnv64_u8(h, s->aimDelay);
	}
	return h;
}

static Uint64 hash_level_state(Uint64 h)
{
	h = fnv64_u32(h, eventLoc);
	h = fnv64_u32(h, maxEvent);
	h = fnv64_u32(h, curLoc);
	h = fnv64_u8(h, levelEnd);
	h = fnv64_u32(h, levelEndFxWait);
	h = fnv64_u8(h, (Uint8)levelEndWarp);
	return h;
}

static Uint64 hash_rng(Uint64 h)
{
	const unsigned long *state;
	unsigned int count;
	unsigned int index = mt_state_snapshot(&state, &count);

	h = fnv64_u32(h, index);
	for (unsigned int i = 0; i < count; ++i)
		h = fnv64_u32(h, (Uint32)(state[i] & 0xffffffffUL));
	return h;
}

static Uint64 hash_frame(void)
{
	Uint64 h = FNV64_OFFSET;
	const Uint8 *pixels = (const Uint8 *)VGAScreen->pixels;

	for (int y = 0; y < VGAScreen->h; ++y)
	{
		const Uint8 *row = pixels + y * VGAScreen->pitch;
		for (int x = 0; x < VGAScreen->w; ++x)
			h = fnv64_u8(h, row[x]);
	}
	return h;
}

void statehash_tick(void)
{
	if (log_file == NULL)
		return;

	Uint64 h = FNV64_OFFSET;
	h = hash_players(h);
	h = hash_enemies(h);
	h = hash_shots(h);
	h = hash_level_state(h);
	h = hash_rng(h);

	fprintf(log_file, "%llu %016llx %016llx\n",
	        (unsigned long long)hash_tick,
	        (unsigned long long)h,
	        (unsigned long long)hash_frame());
	fflush(log_file);

	++hash_tick;
}
