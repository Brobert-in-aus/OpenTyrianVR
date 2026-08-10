#include <jni.h>
#include <android/log.h>

/* SDL2 is loaded as opentyrian_core's dependency, so Android does not call
 * SDL2's own JNI_OnLoad. This host entry point is invoked because Java loads
 * opentyrian_core directly; forward the VM before SDL creates any threads. */
extern void SDL_AndroidSetJavaVMForForeignActivity(JavaVM *vm);
extern void Java_org_libsdl_app_SDLAudioManager_nativeSetupJNI(JNIEnv *env, jclass cls);
extern void Java_org_libsdl_app_SDLAudioManager_addAudioDevice(
    JNIEnv *env, jclass cls, jboolean is_capture, jint device_id);
extern void Java_org_libsdl_app_SDLAudioManager_removeAudioDevice(
    JNIEnv *env, jclass cls, jboolean is_capture, jint device_id);

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void)reserved;
    SDL_AndroidSetJavaVMForForeignActivity(vm);
    __android_log_print(ANDROID_LOG_INFO, "OpenTyrianVR",
                        "JNI_OnLoad: SDL JavaVM and main-ready bridge installed");
    return JNI_VERSION_1_6;
}

JNIEXPORT jboolean JNICALL
Java_com_godot_game_GodotApp_nativeSetupSDLAudio(JNIEnv *env, jclass host_class)
{
    (void)host_class;
    jclass audio_class = (*env)->FindClass(env, "org/libsdl/app/SDLAudioManager");
    if (!audio_class) {
        __android_log_print(ANDROID_LOG_ERROR, "OpenTyrianVR",
                            "SDL audio bridge: SDLAudioManager class not found");
        return JNI_FALSE;
    }

    const JNINativeMethod device_methods[] = {
        { "addAudioDevice", "(ZI)V",
          (void *)Java_org_libsdl_app_SDLAudioManager_addAudioDevice },
        { "removeAudioDevice", "(ZI)V",
          (void *)Java_org_libsdl_app_SDLAudioManager_removeAudioDevice },
    };
    if ((*env)->RegisterNatives(env, audio_class, device_methods,
                                sizeof(device_methods) / sizeof(device_methods[0])) != 0) {
        __android_log_print(ANDROID_LOG_ERROR, "OpenTyrianVR",
                            "SDL audio bridge: native device registration failed");
        (*env)->DeleteLocalRef(env, audio_class);
        return JNI_FALSE;
    }

    Java_org_libsdl_app_SDLAudioManager_nativeSetupJNI(env, audio_class);
    (*env)->DeleteLocalRef(env, audio_class);
    __android_log_print(ANDROID_LOG_INFO, "OpenTyrianVR",
                        "SDL audio bridge: Java callbacks installed");
    return JNI_TRUE;
}
