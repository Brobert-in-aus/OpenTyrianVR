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

static fn_abi_version p_abi_version;
static fn_last_error p_last_error;
static fn_session_create p_session_create;
static fn_session_destroy p_session_destroy;
static fn_submit_input p_submit_input;
static fn_acquire_frame p_acquire_frame;
static fn_player_state p_player_state;
static fn_snapshot p_snapshot;
static fn_sprite_sheet p_sprite_sheet;

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
	if (!p_abi_version || !p_last_error || !p_session_create || !p_session_destroy ||
	    !p_submit_input || !p_acquire_frame || !p_player_state || !p_snapshot || !p_sprite_sheet)
		die("missing export");

	if (p_abi_version() != OTYR_ABI_VERSION)
		die("ABI version mismatch");
	printf("ABI version %u\n", p_abi_version());

	OtyrConfig config = { .struct_size = sizeof(OtyrConfig), .abi_version = OTYR_ABI_VERSION };
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
