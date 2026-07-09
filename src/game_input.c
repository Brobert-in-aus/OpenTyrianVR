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
#include "game_input.h"

#include "config.h"
#include "file.h"
#include "joystick.h"
#include "keyboard.h"
#include "mainint.h"
#include "mouse.h"
#include "params.h"
#include "varz.h"

/* Local-device input sampling, extracted verbatim from JE_playerMovement.
 * inputDevice: 0 = any, 1 = keyboard, 2 = mouse, >= 3 = specific joystick. */
void game_input_sample_local(GameInput *input, Player *this_player, JE_byte inputDevice)
{
	/* joystick input */
	if ((inputDevice == 0 || inputDevice >= 3) && joysticks > 0)
	{
		int j = inputDevice == 0 ? 0 : inputDevice - 3;
		int j_max = inputDevice == 0 ? joysticks : inputDevice - 3 + 1;
		for (; j < j_max; j++)
		{
			poll_joystick(j);

			if (joystick[j].analog)
			{
				input->analog_dx += joystick_axis_reduce(j, joystick[j].x);
				input->analog_dy += joystick_axis_reduce(j, joystick[j].y);

				input->link_gun_analog = joystick_analog_angle(j, &input->link_gun_angle);
			}
			else
			{
				input->left |= joystick[j].direction[3];
				input->right |= joystick[j].direction[1];
				input->up |= joystick[j].direction[0];
				input->down |= joystick[j].direction[2];
			}

			input->fire |= joystick[j].action[0];
			input->left_sidekick |= joystick[j].action[2];
			input->right_sidekick |= joystick[j].action[3];
			input->change_fire |= joystick[j].action_pressed[1];

			ingamemenu_pressed |= joystick[j].action_pressed[4];
			pause_pressed |= joystick[j].action_pressed[5];
		}
	}

	/* mouse input */
	if ((inputDevice == 0 || inputDevice == 2) && has_mouse)
	{
		input->fire |= (mouseButtonsDown & SDL_BUTTON_LMASK) != 0;
		input->left_sidekick |= (mouseButtonsDown & SDL_BUTTON_RMASK) != 0;
		input->right_sidekick |= (mouseButtonsDown & (mouse_has_three_buttons ? SDL_BUTTON_MMASK : SDL_BUTTON_RMASK)) != 0;

		Sint32 mouseXR;
		Sint32 mouseYR;
		mouseGetRelativePosition(&mouseXR, &mouseYR);
		input->analog_dx += mouseXR;
		input->analog_dy += mouseYR;
	}

	/* keyboard input */
	if (inputDevice == 0 || inputDevice == 1)
	{
		input->up |= keysactive[keySettings[KEY_SETTING_UP]];
		input->down |= keysactive[keySettings[KEY_SETTING_DOWN]];
		input->left |= keysactive[keySettings[KEY_SETTING_LEFT]];
		input->right |= keysactive[keySettings[KEY_SETTING_RIGHT]];

		input->fire |= keysactive[keySettings[KEY_SETTING_FIRE]];
		input->change_fire |= keysactive[keySettings[KEY_SETTING_CHANGE_FIRE]];
		input->left_sidekick |= keysactive[keySettings[KEY_SETTING_LEFT_SIDEKICK]];
		input->right_sidekick |= keysactive[keySettings[KEY_SETTING_RIGHT_SIDEKICK]];

		if (constantPlay)
		{
			input->fire = true;
			input->left_sidekick = true;
			input->right_sidekick = true;
			input->change_fire = true;

			++this_player->y;
			this_player->x += constantLastX;
		}

		// TODO: check if demo recording still works
		if (record_demo)
		{
			bool new_input = false;

			for (unsigned int i = 0; i < 8; i++)
			{
				bool temp = demo_keys & (1 << i);
				if (temp != keysactive[keySettings[i]])
					new_input = true;
			}

			demo_keys_wait++;

			if (new_input)
			{
				Uint8 temp2[2] = { demo_keys_wait >> 8, demo_keys_wait };
				fwrite_u8(temp2, 2, demo_file);

				demo_keys = 0;
				for (unsigned int i = 0; i < 8; i++)
					demo_keys |= keysactive[keySettings[i]] ? (1 << i) : 0;

				fwrite_u8(&demo_keys, 1, demo_file);

				demo_keys_wait = 0;
			}
		}
	}
}
