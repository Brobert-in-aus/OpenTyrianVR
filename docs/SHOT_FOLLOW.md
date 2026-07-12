# Player-follow shots (the "curving" weapons under de-parallax)

## Mechanism

Weapon sub-shot fields `sx[]`/`sy[]` above 100 are **magic follow codes**,
not velocities (`shots.c` `player_shot_move_and_draw`, ~line 179):

- `101` — the shot inherits the player's per-tick movement in **both axes**
  (`delta_x_shot_move` / `delta_y_shot_move`, updated in `mainint.c` ~4182).
- `120` — the shot inherits the player's movement on **that axis only**
  (a 120 in `sx` follows X; a 120 in `sy` follows Y).

In legacy, strafing shifts the whole world opposite the player (faux
parallax), so follow shots stay visually pinned to the terrain column they
were fired at — that's the point of the mechanic. Under the E2 presentational
de-parallax the world no longer shifts, so the same tracking reads as the
shot CURVING with the player.

## Affected weapons (episode-1 data; `OTYR_DUMP_SHOTFOLLOW=1` regenerates)

Primary front/rear port weapons:

| Port | Modes/powers | Code |
| --- | --- | --- |
| Laser (port 4) | all powers | 101 (both axes) |
| Zica Laser (port 5) | all powers | 120 (X only) |
| Protron (port 12) | mode 2, all powers | 120/120 (both axes) |
| Mega Pulse (port 19) | powers 7-11 | 120 (X) |
| Rear Heavy Missile (port 21) | powers 3/6/8 | 120-ish slots |
| Rear Mega Pulse (port 22) | several powers | mixed |
| Banana Blast Rear (port 24) | powers 3/6 | mixed |
| HotDog Rear (port 26) | powers 1/5 | mixed |
| Hyper Pulse (port 27) | powers 4/5/7 | mixed |
| The Orange Juicer (port 35) | powers 6-11 | 120/120 |
| Missile Launcher (17) / Fireball (18) | isolated powers | data noise* |
| Sidekick weapons 83/85/87/88/104 ("Miscellaneous Option Weapons") | — | 120 |

*Some rows (Missile Launcher 9-10, Fireball, and most unported weapon ids)
trip the >100 test on sub-shot slots beyond the weapon's active `multi`
count — junk data in unused slots, not real follow behavior. The clean
signals are exact 101/120 values in active slots.

## Why we are NOT fixing it visually (yet)

The follow is applied in the SIM: the shot's real hitbox tracks the
parallax-shifted world, and collision happens there. Removing the curve
presentationally (rebasing shot records like enemy records) would make the
VISUAL shot diverge from its REAL hitbox — trading a cosmetic curve for
invisible hits/misses on the very weapons players aim most precisely.

The correct fix is the sim-side E2 milestone (remove parallax IN the
simulation): the follow codes then add zero delta on a still world and the
curve disappears at the source, together with the hitbox-vs-visual
divergence and the clamped player travel width. Until then the curve stays
as an honest artifact.
