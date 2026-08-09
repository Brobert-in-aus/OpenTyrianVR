cmake_minimum_required(VERSION 3.22)
project(opentyrian_vr_android C)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED SDL2_SOURCE_DIR)
    message(FATAL_ERROR "Pass -DSDL2_SOURCE_DIR=<SDL 2.32.10 source checkout>")
endif()

set(SDL_SHARED ON CACHE BOOL "Build SDL2 shared" FORCE)
set(SDL_STATIC OFF CACHE BOOL "Do not build SDL2 static" FORCE)
set(SDL_TEST OFF CACHE BOOL "Do not build SDL2 tests" FORCE)
set(SDL_TESTS OFF CACHE BOOL "Do not build SDL2 tests" FORCE)
set(SDL_DISABLE_INSTALL ON CACHE BOOL "Do not install SDL2" FORCE)
add_subdirectory("${SDL2_SOURCE_DIR}" sdl2)

set(OTYR_SOURCES
    animlib.c arg_parse.c backgrnd.c config.c config_file.c destruct.c
    editship.c episodes.c file.c font.c fonthand.c game_input.c game_menu.c
    helptext.c joystick.c jukebox.c keyboard.c lds_play.c loudness.c lvllib.c
    lvlmast.c mainint.c memreader.c memwriter.c menus.c mouse.c mtrand.c
    musmast.c network.c nortsong.c nortvars.c opentyr.c opl.c otyr_host.c
    palette.c params.c pcxload.c pcxmast.c picload.c player.c present_frame.c
    shots.c sndmast.c sprite.c starlib.c statehash.c tyrian2.c varz.c
    vga256d.c vga_palette.c video.c video_scale.c video_scale_hqNx.c xmas.c
    ../android/otyr_android_jni.c
)
list(TRANSFORM OTYR_SOURCES PREPEND "${CMAKE_CURRENT_LIST_DIR}/../src/")

add_library(opentyrian_core SHARED ${OTYR_SOURCES})
target_include_directories(opentyrian_core PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/../src"
    "${SDL2_SOURCE_DIR}/include"
)
target_compile_definitions(opentyrian_core PRIVATE
    OTYR_BUILD_DLL
    NDEBUG
    SDL_MAIN_HANDLED
    TYRIAN_DIR="data"
)
target_compile_options(opentyrian_core PRIVATE
    -Wall
    -Wextra
    -Wno-sign-compare
    -Wno-unused-parameter
)
target_link_libraries(opentyrian_core PRIVATE SDL2::SDL2 log android)

set_target_properties(opentyrian_core PROPERTIES
    OUTPUT_NAME "opentyrian_core"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
)
