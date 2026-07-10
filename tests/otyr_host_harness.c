/* Test harness for the thread-hosted OpenTyrian core DLL.
 *
 * Loads the DLL dynamically (as Godot's .NET host will), runs the game
 * headless, injects menu input, samples frames and player state, and dumps
 * BMP screenshots for visual verification.
 *
 * Build (VS developer prompt or via tests/build_harness.ps1):
 *   cl /nologo /W4 otyr_host_harness.c /Fe:otyr_host_harness.exe
 *
 * Run from the repo root:
 *   tests\otyr_host_harness.exe
 */
#include "../src/otyr_host.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <windows.h>

typedef uint32_t (*fn_abi_version)(void);
typedef int32_t (*fn_last_error)(char *, uint32_t);
typedef int32_t (*fn_session_create)(const OtyrConfig *, uint32_t, uint64_t *);
typedef int32_t (*fn_session_destroy)(uint64_t);
typedef int32_t (*fn_submit_input)(uint64_t, const OtyrInputFrame *, uint32_t);
typedef int32_t (*fn_acquire_frame)(uint64_t, OtyrFrame *, uint32_t, uint32_t);
typedef int32_t (*fn_player_state)(uint64_t, OtyrPlayerState *, uint32_t);
typedef int32_t (*fn_snapshot)(uint64_t, OtyrSnapshot *, uint32_t, uint32_t);
typedef int32_t (*fn_sprite_sheet)(uint64_t, uint32_t, OtyrSpriteSheet *, uint32_t);
typedef int32_t (*fn_background_map)(uint64_t, uint32_t, OtyrBackgroundMap *, uint32_t);
typedef int32_t (*fn_old_sprite)(uint64_t, uint32_t, uint32_t, OtyrOldSprite *, uint32_t);

static fn_abi_version p_abi_version;
static fn_last_error p_last_error;
static fn_session_create p_session_create;
static fn_session_destroy p_session_destroy;
static fn_submit_input p_submit_input;
static fn_acquire_frame p_acquire_frame;
static fn_player_state p_player_state;
static fn_snapshot p_snapshot;
static fn_sprite_sheet p_sprite_sheet;
static fn_background_map p_background_map;
static fn_old_sprite p_old_sprite;

static uint64_t g_session;

static void die(const char *what)
{
	char message[256] = "";
	if (p_last_error != NULL)
		p_last_error(message, sizeof(message));
	fprintf(stderr, "FAIL: %s (%s)\n", what, message);
	exit(1);
}

static void write_bmp(const char *path, const OtyrFrame *frame)
{
	const uint32_t w = frame->width, h = frame->height;
	const uint32_t image_size = w * h * 4;

	uint8_t header[54] = { 'B', 'M' };
	*(uint32_t *)(header + 2) = 54 + image_size;
	*(uint32_t *)(header + 10) = 54;
	*(uint32_t *)(header + 14) = 40;
	*(int32_t *)(header + 18) = (int32_t)w;
	*(int32_t *)(header + 22) = (int32_t)h;
	*(uint16_t *)(header + 26) = 1;
	*(uint16_t *)(header + 28) = 32;
	*(uint32_t *)(header + 34) = image_size;

	FILE *f = fopen(path, "wb");
	if (f == NULL)
		die("failed to open BMP for writing");
	fwrite(header, 1, sizeof(header), f);
	for (int y = (int)h - 1; y >= 0; --y)  /* BMP rows are bottom-up */
	{
		for (uint32_t x = 0; x < w; ++x)
		{
			uint32_t argb = frame->palette[frame->pixels[y * w + x]];
			fwrite(&argb, 4, 1, f);
		}
	}
	fclose(f);
	printf("wrote %s\n", path);
}

/* Replays blit_background_row (backgrnd.c) from exported map data onto a flat
 * 320-pitch buffer, including its flat-address clipping quirks, so the raster
 * hash can be compared bit-for-bit with the native standalone capture. */
static void bg_blit_row(uint8_t *surf, int x, int y, const OtyrBackgroundMap *map,
                        int32_t tile_offset, int blend)
{
	uint8_t *pixels = surf + (y * 320) + x;
	uint8_t *pixels_ll = surf;
	uint8_t *pixels_ul = surf + 200 * 320;

	for (int row = 0; row < 28; row++)
	{
		if ((pixels + (12 * 24)) < pixels_ll)
		{
			pixels += 320;
			continue;
		}

		for (int tile = 0; tile < 12; tile++)
		{
			int32_t off = tile_offset + tile;
			const uint8_t *data = NULL;
			if (off >= 0 && off < (int32_t)(map->width * map->height) &&
			    map->tiles[off] != OTYR_BG_TILE_EMPTY)
				data = map->shapes + map->tiles[off] * OTYR_BG_TILE_W * OTYR_BG_TILE_H;

			if (data == NULL)
			{
				pixels += 24;
				continue;
			}

			data += row * 24;

			for (int i = 24; i; i--)
			{
				if (pixels >= pixels_ul)
					return;
				if (pixels >= pixels_ll && *data != 0)
					*pixels = blend ? (uint8_t)((*data & 0xf0) | (((*pixels & 0x0f) + (*data & 0x0f)) / 2))
					                : *data;

				pixels++;
				data++;
			}
		}

		pixels += 320 - 12 * 24;
	}
}

static uint32_t bg_reconstruct_hash(const OtyrBackgroundMap *map, const OtyrBackgroundDraw *draw)
{
	/* padding above the frame: the first blit row starts at negative y */
	static uint8_t buffer[320 * 29 + 320 * 200];
	uint8_t *surf = buffer + 320 * 29;
	memset(buffer, 0, sizeof(buffer));

	for (int i = 0; i < 8; i++)
		bg_blit_row(surf, draw->x, draw->y + i * 28, map,
		            draw->tile_offset + i * map->width, draw->blend);

	uint32_t hash = 2166136261u;
	for (int y = 0; y < 200; y++)
		for (int x = 0; x < 320; x++)
		{
			hash ^= surf[y * 320 + x];
			hash *= 16777619u;
		}
	return hash;
}

static void pulse_button(uint32_t buttons, DWORD hold_ms)
{
	OtyrInputFrame input = { .struct_size = sizeof(OtyrInputFrame), .buttons = buttons };
	if (p_submit_input(g_session, &input, sizeof(input)) != OTYR_OK)
		die("submit_input(press)");
	Sleep(hold_ms);
	input.buttons = 0;
	if (p_submit_input(g_session, &input, sizeof(input)) != OTYR_OK)
		die("submit_input(release)");
}

int main(void)
{
	HMODULE dll = LoadLibraryA("opentyrian-core-x64-Release.dll");
	if (dll == NULL)
	{
		fprintf(stderr, "FAIL: could not load opentyrian-core-x64-Release.dll "
		                "(run from the repo root)\n");
		return 1;
	}

	p_abi_version = (fn_abi_version)GetProcAddress(dll, "otyr_abi_version");
	p_last_error = (fn_last_error)GetProcAddress(dll, "otyr_last_error");
	p_session_create = (fn_session_create)GetProcAddress(dll, "otyr_session_create");
	p_session_destroy = (fn_session_destroy)GetProcAddress(dll, "otyr_session_destroy");
	p_submit_input = (fn_submit_input)GetProcAddress(dll, "otyr_session_submit_input");
	p_acquire_frame = (fn_acquire_frame)GetProcAddress(dll, "otyr_session_acquire_frame");
	p_player_state = (fn_player_state)GetProcAddress(dll, "otyr_session_player_state");
	p_snapshot = (fn_snapshot)GetProcAddress(dll, "otyr_session_snapshot");
	p_sprite_sheet = (fn_sprite_sheet)GetProcAddress(dll, "otyr_sprite_sheet");
	p_background_map = (fn_background_map)GetProcAddress(dll, "otyr_background_map");
	p_old_sprite = (fn_old_sprite)GetProcAddress(dll, "otyr_old_sprite");
	if (!p_abi_version || !p_last_error || !p_session_create || !p_session_destroy ||
	    !p_submit_input || !p_acquire_frame || !p_player_state || !p_snapshot || !p_sprite_sheet ||
	    !p_background_map || !p_old_sprite)
		die("missing export");

	if (p_abi_version() != OTYR_ABI_VERSION)
		die("ABI version mismatch");
	printf("ABI version %u\n", p_abi_version());

	OtyrConfig config = { .struct_size = sizeof(OtyrConfig), .abi_version = OTYR_ABI_VERSION,
	                      .flags = OTYR_CONFIG_BACKGROUND_HASHES };
	snprintf(config.data_dir, sizeof(config.data_dir), "tyrian21");
	snprintf(config.hash_log, sizeof(config.hash_log), "captures\\hash-hosted.log");

	if (p_session_create(&config, sizeof(config), &g_session) != OTYR_OK)
		die("session_create");
	printf("session created\n");

	OtyrFrame *frame = calloc(1, sizeof(OtyrFrame));
	frame->struct_size = sizeof(OtyrFrame);

	OtyrPlayerState state = { .struct_size = sizeof(OtyrPlayerState) };

	/* Phase 1: wait out the intro logos until presents go quiet (the title
	 * menu only redraws on change, so "no new frame for a while" means the
	 * menu is idle and ready). */
	LARGE_INTEGER qpf;
	QueryPerformanceFrequency(&qpf);

	printf("waiting for title menu to go idle...\n");
	DWORD wait_start = GetTickCount();
	while (GetTickCount() - wait_start < 20000)
	{
		if (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 1500) == OTYR_TIMEOUT)
			break;  /* 1.5 s with no present: menu is idle */
	}

	/* Phase 2: input-to-frame latency.  From an idle menu, the next present
	 * only happens in response to input, so submit-to-frame is the native
	 * input latency.  SPACE advances a menu, ESC backs out; alternate. */
	printf("input-to-frame latency (menu response):\n");
	double latencies[6];
	for (int i = 0; i < 6; ++i)
	{
		uint32_t button = (i % 2 == 0) ? OTYR_BUTTON_UI_SPACE : OTYR_BUTTON_UI_CANCEL;

		LARGE_INTEGER t0, t1;
		QueryPerformanceCounter(&t0);

		OtyrInputFrame input = { .struct_size = sizeof(OtyrInputFrame), .buttons = button };
		if (p_submit_input(g_session, &input, sizeof(input)) != OTYR_OK)
			die("submit_input(press)");

		if (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 3000) != OTYR_OK)
			die("no frame within 3s of menu input");
		QueryPerformanceCounter(&t1);

		latencies[i] = (t1.QuadPart - t0.QuadPart) * 1000.0 / qpf.QuadPart;
		printf("  %s -> %.1f ms\n", (i % 2 == 0) ? "SPACE" : "ESC  ", latencies[i]);

		input.buttons = 0;
		if (p_submit_input(g_session, &input, sizeof(input)) != OTYR_OK)
			die("submit_input(release)");

		/* Drain fade/transition frames until quiet before the next probe. */
		while (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 700) == OTYR_OK)
			;
	}
	double latency_sum = 0;
	for (int i = 0; i < 6; ++i)
		latency_sum += latencies[i];
	printf("  average %.1f ms\n", latency_sum / 6);

	/* Phase 3: navigate title -> Demo (DOWN x5, ENTER) and measure frame
	 * cadence during demo gameplay. */
	printf("starting demo playback...\n");
	for (int i = 0; i < 5; ++i)
	{
		pulse_button(OTYR_BUTTON_DOWN, 80);
		Sleep(150);
	}
	pulse_button(OTYR_BUTTON_UI_CONFIRM, 100);

	/* Give the fade + level load a moment, then sample. */
	Sleep(4000);
	while (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 500) == OTYR_OK)
		break;

	printf("sampling frame cadence for 20s...\n");
	LARGE_INTEGER prev = { 0 }, now;
	double min_period = 1e9, max_period = 0, period_sum = 0;
	uint32_t periods = 0;
	DWORD cadence_start = GetTickCount();
	while (GetTickCount() - cadence_start < 20000)
	{
		if (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 500) != OTYR_OK)
			continue;
		QueryPerformanceCounter(&now);
		if (prev.QuadPart != 0)
		{
			double period = (now.QuadPart - prev.QuadPart) * 1000.0 / qpf.QuadPart;
			if (period < min_period) min_period = period;
			if (period > max_period) max_period = period;
			period_sum += period;
			++periods;
		}
		prev = now;
	}
	if (periods > 0)
	{
		double avg = period_sum / periods;
		printf("cadence: %u frames, avg %.2f ms (%.1f fps), min %.2f ms, max %.2f ms\n",
		       periods, avg, 1000.0 / avg, min_period, max_period);
	}

	/* Phase 4: presentation snapshot + sprite sheets (ABI v6). */
	OtyrSnapshot *snapshot = calloc(1, sizeof(OtyrSnapshot));
	snapshot->struct_size = sizeof(OtyrSnapshot);
	if (p_snapshot(g_session, snapshot, sizeof(OtyrSnapshot), 2000) != OTYR_OK)
		die("session_snapshot");

	unsigned int by_category[16] = { 0 };
	for (uint32_t i = 0; i < snapshot->sprite_count; ++i)
		if (snapshot->sprites[i].category < 16)
			by_category[snapshot->sprites[i].category]++;
	printf("snapshot: tick %u, %u sprites (sky %u gndA %u top %u gndB %u eshot %u pshot %u player %u shadow %u sk %u expl %u px %u), %u sounds\n",
	       snapshot->level_tick, snapshot->sprite_count,
	       by_category[0], by_category[1], by_category[2], by_category[3],
	       by_category[4], by_category[5], by_category[6], by_category[7],
	       by_category[8], by_category[9], by_category[10],
	       snapshot->sound_count);
	if (snapshot->sprite_count == 0)
		die("snapshot has no sprites during demo gameplay");

	/* Phase 4b: mechanically verify snapshot records against the legacy
	 * frame: reconstruct every record's pixels from the rasterized sheets
	 * and compare with what the legacy renderer actually drew at that
	 * position in the same tick.  Any record whose opaque pixels mismatch
	 * badly points at a decode/index bug. */
	OtyrSpriteSheet *sheets = calloc(OTYR_SHEET_COUNT, sizeof(OtyrSpriteSheet));
	for (uint32_t id = 0; id < OTYR_SHEET_COUNT; ++id)
	{
		sheets[id].struct_size = sizeof(OtyrSpriteSheet);
		if (p_sprite_sheet(g_session, id, &sheets[id], sizeof(OtyrSpriteSheet)) != OTYR_OK)
			die("sprite_sheet fetch for verify");
	}

	/* Pair a frame and snapshot from the same tick.  With smooth scroll two
	 * presents share a tick; entity pixels are identical in both. */
	for (int attempt = 0; ; ++attempt)
	{
		if (attempt > 200)
			die("could not pair frame and snapshot ticks");
		if (p_acquire_frame(g_session, frame, sizeof(OtyrFrame), 1000) != OTYR_OK)
			die("frame for verify");
		if (p_snapshot(g_session, snapshot, sizeof(OtyrSnapshot), 1000) != OTYR_OK)
			die("snapshot for verify");
		if (frame->level_tick == snapshot->level_tick)
			break;
		snapshot->level_tick = 0;  /* re-read until they land on one tick */
	}

	unsigned int bad_records = 0, checked_records = 0;
	for (uint32_t i = 0; i < snapshot->sprite_count; ++i)
	{
		const OtyrSnapshotSprite *rec = &snapshot->sprites[i];
		if (rec->sheet_id >= OTYR_SHEET_COUNT || rec->index == 0 || rec->flags != 0)
			continue;  /* only plain blits are directly comparable */
		if (rec->kind != 0 && rec->kind != 1)
			continue;

		unsigned int pieces = rec->kind == 1 ? 4 : 1;
		static const int piece_dx[4] = { 0, 12, 0, 12 };
		static const int piece_dy[4] = { 0, 0, 14, 14 };
		static const int piece_di[4] = { 0, 1, 19, 20 };

		unsigned int opaque = 0, match = 0;
		for (unsigned int p = 0; p < pieces; ++p)
		{
			uint32_t cell = rec->index + piece_di[p] - 1;
			if (cell >= sheets[rec->sheet_id].cell_count)
				continue;
			const uint8_t *cell_px = sheets[rec->sheet_id].pixels + cell * 12 * 14;

			for (int y = 0; y < 14; ++y)
				for (int x = 0; x < 12; ++x)
				{
					uint8_t v = cell_px[y * 12 + x];
					if (v == 0)
						continue;
					int fx = rec->x + piece_dx[p] + x;
					int fy = rec->y + piece_dy[p] + y;
					if (fx < 0 || fx >= 320 || fy < 0 || fy >= 200)
						continue;
					++opaque;
					/* Frame pixels are game_screen composited -24; but the
					 * exported frame is the composited VGAScreenSeg, so
					 * compare at fx-24 within the 264-wide play area. */
					int cx = fx - 24;
					if (cx < 0 || cx >= 264)
						continue;
					if (frame->pixels[fy * 320 + cx] == v)
						++match;
				}
		}

		if (opaque >= 20)
		{
			++checked_records;
			if (match * 100 < opaque * 60)  /* under 60% agreement */
			{
				if (++bad_records <= 10)
					printf("  MISMATCH cat %u kind %u sheet %u index %u at (%d,%d): %u/%u px\n",
					       rec->category, rec->kind, rec->sheet_id, rec->index,
					       rec->x, rec->y, match, opaque);
			}
		}
	}
	printf("record verify: %u checked, %u bad\n", checked_records, bad_records);
	free(sheets);

	/* Phase 4c: background map export (ABI v8).  Fetch the three map layers
	 * and, across a stretch of live snapshots, reconstruct each drawn layer
	 * from exported data alone; the hash must match the native standalone
	 * raster bit-for-bit. */
	OtyrBackgroundMap *bg_maps = calloc(OTYR_BG_LAYER_COUNT, sizeof(OtyrBackgroundMap));
	for (uint32_t l = 0; l < OTYR_BG_LAYER_COUNT; ++l)
	{
		bg_maps[l].struct_size = sizeof(OtyrBackgroundMap);
		if (p_background_map(g_session, l, &bg_maps[l], sizeof(OtyrBackgroundMap)) != OTYR_OK)
			die("background_map");
		unsigned int used = 0;
		for (uint32_t i = 0; i < (uint32_t)(bg_maps[l].width * bg_maps[l].height); ++i)
			if (bg_maps[l].tiles[i] != OTYR_BG_TILE_EMPTY)
				++used;
		printf("bg map %u: %ux%u tiles (%u placed), %u shapes\n",
		       l, bg_maps[l].width, bg_maps[l].height, used, bg_maps[l].shape_count);
	}

	{
		unsigned int bg_checked[OTYR_BG_LAYER_COUNT] = { 0 };
		unsigned int bg_bad[OTYR_BG_LAYER_COUNT] = { 0 };
		DWORD bg_start = GetTickCount();
		while (GetTickCount() - bg_start < 15000)
		{
			if (p_snapshot(g_session, snapshot, sizeof(OtyrSnapshot), 1000) != OTYR_OK)
				continue;
			for (uint32_t l = 0; l < OTYR_BG_LAYER_COUNT; ++l)
			{
				const OtyrBackgroundDraw *draw = &snapshot->background[l];
				if (!draw->drawn)
					continue;
				++bg_checked[l];
				if (bg_reconstruct_hash(&bg_maps[l], draw) != draw->hash)
				{
					if (++bg_bad[l] <= 4)
						printf("  BG MISMATCH layer %u tick %u offset %d at (%d,%d) blend %u\n",
						       l, snapshot->level_tick, draw->tile_offset,
						       draw->x, draw->y, draw->blend);
				}
			}
		}
		printf("bg verify: layer ticks checked %u/%u/%u, mismatches %u/%u/%u, over modes %u/%u/%u\n",
		       bg_checked[0], bg_checked[1], bg_checked[2],
		       bg_bad[0], bg_bad[1], bg_bad[2],
		       snapshot->background[0].over_mode,
		       snapshot->background[1].over_mode,
		       snapshot->background[2].over_mode);
		if (bg_checked[0] == 0)
			die("background layer 1 never drawn during demo verify window");
		if (bg_bad[0] + bg_bad[1] + bg_bad[2] != 0)
			die("background reconstruction mismatch");
	}
	free(bg_maps);

	/* Phase 4d: old-table sprite export (ABI v9).  OPTION_SHAPES carries the
	 * special blend shots; verify the cache answers and reports plausible
	 * dimensions. */
	{
		OtyrOldSprite *old = calloc(1, sizeof(OtyrOldSprite));
		old->struct_size = sizeof(OtyrOldSprite);
		unsigned int present = 0, max_w = 0, max_h = 0;
		for (uint32_t i = 0; i < OTYR_OLD_SPRITE_MAX; ++i)
		{
			old->struct_size = sizeof(OtyrOldSprite);
			if (p_old_sprite(g_session, OTYR_OLD_TABLE_OPTION, i, old, sizeof(OtyrOldSprite)) != OTYR_OK)
				die("old_sprite");
			if (old->width == 0)
				continue;
			++present;
			if (old->width > max_w) max_w = old->width;
			if (old->height > max_h) max_h = old->height;
		}
		printf("old sprites: %u present, max %ux%u\n", present, max_w, max_h);
		if (present == 0)
			die("no OPTION_SHAPES sprites cached");
		free(old);
	}

	OtyrSpriteSheet *sheet = calloc(1, sizeof(OtyrSpriteSheet));
	sheet->struct_size = sizeof(OtyrSpriteSheet);
	printf("sheets:");
	for (uint32_t id = 0; id < OTYR_SHEET_COUNT; ++id)
	{
		if (p_sprite_sheet(g_session, id, sheet, sizeof(OtyrSpriteSheet)) != OTYR_OK)
			die("sprite_sheet");
		unsigned int opaque = 0;
		for (uint32_t px = 0; px < sheet->cell_count * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H; ++px)
			if (sheet->pixels[px] != 0)
				++opaque;
		printf(" [%u]=%u/%u%%", id, sheet->cell_count,
		       sheet->cell_count ? opaque * 100 / (sheet->cell_count * OTYR_SHEET_CELL_W * OTYR_SHEET_CELL_H) : 0);
	}
	printf(" (epoch %u)\n", sheet->sheet_epoch);
	free(sheet);
	free(snapshot);

	write_bmp("captures\\hosted-demo.bmp", frame);
	if (p_player_state(g_session, &state, sizeof(state)) != OTYR_OK)
		die("player_state");
	printf("player: x=%d y=%d shield=%u armor=%u cash=%u lives=%u alive=%u\n",
	       state.x, state.y, state.shield, state.armor,
	       state.cash, state.lives, state.is_alive);

	printf("destroying session...\n");
	int32_t rc = p_session_destroy(g_session);
	printf("destroy: %s\n", rc == OTYR_OK ? "clean" : "TIMEOUT (thread detached)");

	free(frame);
	FreeLibrary(dll);
	printf("PASS\n");
	return rc == OTYR_OK ? 0 : 2;
}
