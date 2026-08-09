#include <jni.h>
#include <android/log.h>

/* SDL2 is loaded as opentyrian_core's dependency, so Android does not call
 * SDL2's own JNI_OnLoad. This host entry point is invoked because Java loads
 * opentyrian_core directly; forward the VM before SDL creates any threads. */
extern void SDL_AndroidSetJavaVMForForeignActivity(JavaVM *vm);

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void)reserved;
    SDL_AndroidSetJavaVMForForeignActivity(vm);
    __android_log_print(ANDROID_LOG_INFO, "OpenTyrianVR",
                        "JNI_OnLoad: SDL JavaVM and main-ready bridge installed");
    return JNI_VERSION_1_6;
}
