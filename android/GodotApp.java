/**************************************************************************/
/*  GodotApp.java                                                         */
/**************************************************************************/
/* Copyright (c) 2014-present Godot Engine contributors.                  */
/* SPDX-License-Identifier: MIT                                           */
/**************************************************************************/

package com.godot.game;

import org.godotengine.godot.Godot;
import org.godotengine.godot.GodotActivity;
import org.libsdl.app.SDLAudioManager;

import android.os.Bundle;
import android.util.Log;

import androidx.activity.EdgeToEdge;
import androidx.core.splashscreen.SplashScreen;

/** Godot activity with the OpenTyrian native host preloaded by the JVM. */
public class GodotApp extends GodotActivity {
	private static boolean sdlAudioReady = false;

	private static native boolean nativeSetupSDLAudio();

	static {
		if (BuildConfig.FLAVOR.equals("mono")) {
			try {
				Log.v("GODOT", "Loading System.Security.Cryptography.Native.Android library");
				System.loadLibrary("System.Security.Cryptography.Native.Android");
			} catch (UnsatisfiedLinkError e) {
				Log.e("GODOT", "Unable to load System.Security.Cryptography.Native.Android library");
			}
			try {
				Log.v("GODOT", "Loading OpenTyrian native host library");
				System.loadLibrary("opentyrian_core");
				SDLAudioManager.initialize();
				sdlAudioReady = nativeSetupSDLAudio();
				Log.i("OpenTyrianVR", "SDL audio bridge ready=" + sdlAudioReady);
			} catch (UnsatisfiedLinkError e) {
				Log.e("GODOT", "Unable to load OpenTyrian native host library", e);
			}
		}
	}

	private final Runnable updateWindowAppearance = () -> {
		Godot godot = getGodot();
		if (godot != null) {
			godot.enableImmersiveMode(godot.isInImmersiveMode(), true);
			godot.enableEdgeToEdge(godot.isInEdgeToEdgeMode(), true);
			godot.setSystemBarsAppearance();
		}
	};

	@Override
	public void onCreate(Bundle savedInstanceState) {
		SplashScreen splashScreen = SplashScreen.installSplashScreen(this);
		EdgeToEdge.enable(this);
		super.onCreate(savedInstanceState);
		if (sdlAudioReady) {
			SDLAudioManager.setContext(this);
		}

		Godot godot = getGodot();
		if (godot != null && godot.getDisableGodotSplash()) {
			splashScreen.setKeepOnScreenCondition(() -> godot.getRunStatus() != Godot.RunStatus.STARTED);
		}
	}

	@Override
	protected void onDestroy() {
		if (sdlAudioReady) {
			SDLAudioManager.release(this);
		}
		super.onDestroy();
	}

	@Override
	public void onResume() {
		super.onResume();
		updateWindowAppearance.run();
	}

	@Override
	public void onGodotMainLoopStarted() {
		super.onGodotMainLoopStarted();
		runOnUiThread(updateWindowAppearance);
	}

	@Override
	public void onGodotForceQuit(Godot instance) {
		if (!BuildConfig.FLAVOR.equals("instrumented")) {
			super.onGodotForceQuit(instance);
		}
	}

	@Override
	protected boolean isPiPEnabled() {
		return true;
	}
}
