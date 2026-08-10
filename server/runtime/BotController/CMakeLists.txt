cmake_minimum_required(VERSION 3.20)
project(BotController CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "BotController requires x64 build (use -A x64).")
endif()

set(HL2SDK "$ENV{HL2SDKCS2}")
set(MMS    "$ENV{MMSOURCE_DEV}")
set(PROTO_DIR "$ENV{CSGO_PROTO}")
foreach(v HL2SDK MMS)
    if(NOT ${v} OR NOT EXISTS "${${v}}")
        message(FATAL_ERROR "${v} env var not set or path missing: '${${v}}'")
    endif()
endforeach()

if(NOT PROTO_DIR OR NOT EXISTS "${PROTO_DIR}/network_connection.proto" OR
   NOT EXISTS "${PROTO_DIR}/networkbasetypes.proto" OR
   NOT EXISTS "${PROTO_DIR}/netmessages.proto" OR
   NOT EXISTS "${PROTO_DIR}/source2_steam_stats.proto" OR
   NOT EXISTS "${PROTO_DIR}/usercmd.proto" OR
   NOT EXISTS "${PROTO_DIR}/cs_usercmd.proto" OR
   NOT EXISTS "${PROTO_DIR}/valveextensions.proto")
    set(PROTO_STAGE "${CMAKE_BINARY_DIR}/proto_stage")
    file(MAKE_DIRECTORY "${PROTO_STAGE}")
    set(PROTO_SOURCE_FILES
        "${HL2SDK}/common/network_connection.proto"
        "${HL2SDK}/common/networkbasetypes.proto"
        "${HL2SDK}/common/netmessages.proto"
        "${HL2SDK}/common/source2_steam_stats.proto"
        "${HL2SDK}/common/valveextensions.proto"
        "${HL2SDK}/game/shared/usercmd.proto"
        "${HL2SDK}/game/shared/cs/cs_usercmd.proto")
    foreach(src ${PROTO_SOURCE_FILES})
        if(NOT EXISTS "${src}")
            message(FATAL_ERROR "required CS2 proto missing: ${src}")
        endif()
        get_filename_component(proto_name "${src}" NAME)
        configure_file("${src}" "${PROTO_STAGE}/${proto_name}" COPYONLY)
    endforeach()
    set(PROTO_DIR "${PROTO_STAGE}")
endif()

# eiface.h needs network_connection.pb.h; the usercmd protos give us
# CSGOUserCmdPB / CSubtickMoveStep for subtick record + replay.
if(DEFINED ENV{PROTOC} AND EXISTS "$ENV{PROTOC}")
    set(PROTOC_EXE "$ENV{PROTOC}")
else()
    find_program(PROTOC_EXE protoc REQUIRED)
endif()
execute_process(COMMAND "${PROTOC_EXE}" --version OUTPUT_VARIABLE PROTOC_VER OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT PROTOC_VER MATCHES "libprotoc 3\\.21\\.")
    message(FATAL_ERROR "protoc must be 3.21.x (got '${PROTOC_VER}')")
endif()
message(STATUS "protoc: ${PROTOC_EXE} (${PROTOC_VER})")

set(PROTO_FILES network_connection.proto networkbasetypes.proto netmessages.proto source2_steam_stats.proto valveextensions.proto usercmd.proto cs_usercmd.proto)
set(GENERATED_DIR "${CMAKE_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${GENERATED_DIR}")
set(PROTO_SRCS "")
set(PROTO_INPUTS "")
foreach(p ${PROTO_FILES})
    string(REGEX REPLACE "\\.proto$" ".pb.cc" cc ${p})
    string(REGEX REPLACE "\\.proto$" ".pb.h"  hh ${p})
    list(APPEND PROTO_SRCS   "${GENERATED_DIR}/${cc}" "${GENERATED_DIR}/${hh}")
    list(APPEND PROTO_INPUTS "${PROTO_DIR}/${p}")
endforeach()
add_custom_command(
    OUTPUT ${PROTO_SRCS}
    COMMAND "${PROTOC_EXE}" --cpp_out=${GENERATED_DIR} --proto_path=${PROTO_DIR} --proto_path=${HL2SDK}/thirdparty/protobuf-3.21.8/src ${PROTO_FILES}
    # protoc 3.21 emits `class X final : public Message`; strip the class-def
    # `final` so PlayerCommand (playercommand.h) can inherit CSGOUserCmdPB.
    # final is only a devirtualization hint -- no layout/ABI impact.
    COMMAND "${CMAKE_COMMAND}"
        -DGENERATED_DIR=${GENERATED_DIR}
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/StripProtoFinal.cmake"
    DEPENDS ${PROTO_INPUTS}
    WORKING_DIRECTORY "${PROTO_DIR}"
    COMMENT "protoc ${PROTO_FILES} (+ strip class final)"
    VERBATIM
)

if(DEFINED ENV{FUNCHOOK_ROOT} AND EXISTS "$ENV{FUNCHOOK_ROOT}/include/funchook.h")
    add_library(funchook-static STATIC IMPORTED GLOBAL)
    add_library(distorm-static STATIC IMPORTED GLOBAL)
    set_target_properties(funchook-static PROPERTIES
        INTERFACE_INCLUDE_DIRECTORIES "$ENV{FUNCHOOK_ROOT}/include"
        INTERFACE_LINK_LIBRARIES distorm-static)
    if(WIN32)
        set_target_properties(funchook-static PROPERTIES
            IMPORTED_LOCATION_RELEASE "$ENV{FUNCHOOK_ROOT}/lib/Release/funchook.lib"
            IMPORTED_LOCATION_RELWITHDEBINFO "$ENV{FUNCHOOK_ROOT}/lib/Release/funchook.lib"
            IMPORTED_LOCATION_MINSIZEREL "$ENV{FUNCHOOK_ROOT}/lib/Release/funchook.lib"
            IMPORTED_LOCATION_DEBUG "$ENV{FUNCHOOK_ROOT}/lib/Debug/funchook.lib")
        set_target_properties(distorm-static PROPERTIES
            IMPORTED_LOCATION_RELEASE "$ENV{FUNCHOOK_ROOT}/lib/Release/distorm.lib"
            IMPORTED_LOCATION_RELWITHDEBINFO "$ENV{FUNCHOOK_ROOT}/lib/Release/distorm.lib"
            IMPORTED_LOCATION_MINSIZEREL "$ENV{FUNCHOOK_ROOT}/lib/Release/distorm.lib"
            IMPORTED_LOCATION_DEBUG "$ENV{FUNCHOOK_ROOT}/lib/Debug/distorm.lib")
    else()
        set_target_properties(funchook-static PROPERTIES
            IMPORTED_LOCATION_RELEASE "$ENV{FUNCHOOK_ROOT}/lib/Release/libfunchook.a"
            IMPORTED_LOCATION_RELWITHDEBINFO "$ENV{FUNCHOOK_ROOT}/lib/Release/libfunchook.a"
            IMPORTED_LOCATION_MINSIZEREL "$ENV{FUNCHOOK_ROOT}/lib/Release/libfunchook.a"
            IMPORTED_LOCATION_DEBUG "$ENV{FUNCHOOK_ROOT}/lib/Debug/libfunchook.a")
        set_target_properties(distorm-static PROPERTIES
            IMPORTED_LOCATION_RELEASE "$ENV{FUNCHOOK_ROOT}/lib/Release/libdistorm.a"
            IMPORTED_LOCATION_RELWITHDEBINFO "$ENV{FUNCHOOK_ROOT}/lib/Release/libdistorm.a"
            IMPORTED_LOCATION_MINSIZEREL "$ENV{FUNCHOOK_ROOT}/lib/Release/libdistorm.a"
            IMPORTED_LOCATION_DEBUG "$ENV{FUNCHOOK_ROOT}/lib/Debug/libdistorm.a")
    endif()
else()
    include(FetchContent)
    set(FUNCHOOK_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(FUNCHOOK_BUILD_SHARED OFF CACHE BOOL "" FORCE)
    if(DEFINED ENV{FUNCHOOK_SOURCE} AND EXISTS "$ENV{FUNCHOOK_SOURCE}/CMakeLists.txt")
        set(FETCHCONTENT_SOURCE_DIR_FUNCHOOK "$ENV{FUNCHOOK_SOURCE}" CACHE PATH "")
    endif()
    FetchContent_Declare(funchook
        GIT_REPOSITORY https://github.com/kubo/funchook.git
        GIT_TAG v2.0.0 GIT_SHALLOW TRUE
        UPDATE_DISCONNECTED TRUE)
    FetchContent_MakeAvailable(funchook)
endif()

set(SDK_SOURCES
    ${HL2SDK}/public/tier0/memoverride.cpp
    ${HL2SDK}/tier1/generichash.cpp
    ${HL2SDK}/tier1/keyvalues3.cpp
    ${HL2SDK}/tier1/convar.cpp
    ${MMS}/core/sourcehook/sourcehook.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_chookidman.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_chookmaninfo.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_cvfnptr.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_cproto.cpp
)

add_library(BotController SHARED
    src/plugin.cpp
    src/WeaponLocker/WeaponLocker.cpp
    src/WeaponLocker/WeaponLockerState.cpp
    src/BuyController/BuyController.cpp
    src/BuyController/BuyControllerState.cpp
    src/BotController/BotController.cpp
    src/BotController/BotControllerState.cpp
    src/BotRecorder/InputInjector.cpp
    src/BotRecorder/MotionRecorder.cpp
    src/BotRecorder/ReplayPawnEquipment.cpp
    src/BotRecorder/ReplaySubtickLayout.cpp
    src/VoiceSender/VoiceSender.cpp
    src/common/sig_scan.cpp
    src/common/schema_resolver.cpp
    src/common/version_targets.cpp
    src/common/platform.cpp
    src/common/ccsbot_slot.cpp
    src/common/dispatch.cpp
    src/common/commands.cpp
    src/common/projectile_birth_align.cpp
    src/common/exports.cpp
    ${PROTO_SRCS}
    ${SDK_SOURCES}
)

target_include_directories(BotController PRIVATE
    src
    src/common
    src/WeaponLocker
    src/BuyController
    src/BotController
    src/BotRecorder
    src/VoiceSender
    ${CMAKE_CURRENT_SOURCE_DIR}/../third_party
    ${GENERATED_DIR}
    ${HL2SDK}/thirdparty/protobuf-3.21.8/src
    ${MMS}/core ${MMS}/core/sourcehook ${MMS}/core/sourcemm
    ${HL2SDK}/public
    ${HL2SDK}/public/engine
    ${HL2SDK}/public/game/server
    ${HL2SDK}/public/game/shared
    ${HL2SDK}/public/mathlib
    ${HL2SDK}/public/tier0
    ${HL2SDK}/public/entity2
    ${HL2SDK}/game/shared
    ${HL2SDK}/common
)

target_link_libraries(BotController PRIVATE funchook-static)

target_compile_definitions(BotController PRIVATE
    META_IS_SOURCE2 PLATFORM_64BITS X64BITS GOOGLE_PROTOBUF_NO_RTTI)

if(WIN32)
    set(BC_PLATFORM_DIR win64)
    set(BC_SUFFIX ".dll")
    set(HL2SDK_LIBDIR ${HL2SDK}/lib/public/win64)
    # MSVC has no <byteswap.h> clash, so tier1 goes on the normal include path
    target_include_directories(BotController PRIVATE ${HL2SDK}/public/tier1)
    target_link_libraries(BotController PRIVATE
        psapi
        ${HL2SDK_LIBDIR}/tier0.lib
        ${HL2SDK_LIBDIR}/tier1.lib
        ${HL2SDK_LIBDIR}/mathlib.lib
        ${HL2SDK_LIBDIR}/interfaces.lib
        ${HL2SDK_LIBDIR}/2015/libprotobuf.lib
    )
    target_link_options(BotController PRIVATE /NODEFAULTLIB:LIBCMTD)
    target_compile_definitions(BotController PRIVATE
        _CRT_SECURE_NO_WARNINGS
        WIN32 _WIN32 _WINDOWS
        COMPILER_MSVC COMPILER_MSVC64
        NOMINMAX _ALLOW_KEYWORD_MACROS
    )
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(BC_PLATFORM_DIR linuxsteamrt64)
    set(BC_SUFFIX ".so")
    set(HL2SDK_LIBDIR ${HL2SDK}/lib/linux64)
    target_link_libraries(BotController PRIVATE
        ${HL2SDK_LIBDIR}/libtier0.so
        ${HL2SDK_LIBDIR}/tier1.a
        ${HL2SDK_LIBDIR}/mathlib.a
        ${HL2SDK_LIBDIR}/interfaces.a
        ${HL2SDK_LIBDIR}/release/libprotobuf.a
        pthread
        dl
    )
    target_compile_definitions(BotController PRIVATE
        POSIX _LINUX LINUX
        COMPILER_GCC
        stricmp=strcasecmp
        _stricmp=strcasecmp
        _strnicmp=strncasecmp
        strnicmp=strncasecmp
        _snprintf=snprintf
        _vsnprintf=vsnprintf
        _alloca=alloca
        strcmpi=strcasecmp
    )
    target_compile_options(BotController PRIVATE
        -fPIC
        -fno-rtti
        -fvisibility=hidden
        # tier1 last on the search path: glibc's <byteswap.h> wins over SDK's same-named header
        -idirafter ${HL2SDK}/public/tier1
        -Wno-non-virtual-dtor
        -Wno-overloaded-virtual
        -Wno-register
        -Wno-narrowing
        -Wno-deprecated-copy
    )
    set_target_properties(BotController PROPERTIES
        SKIP_BUILD_RPATH TRUE
        BUILD_RPATH ""
        INSTALL_RPATH ""
    )
else()
    message(FATAL_ERROR "Unsupported platform: ${CMAKE_SYSTEM_NAME}")
endif()
set(BC_PACKAGE_BIN_DIR "${CMAKE_BINARY_DIR}/package/addons/BotController/bin/${BC_PLATFORM_DIR}")
set(BC_VDF_FILE "addons/BotController/bin/${BC_PLATFORM_DIR}/BotController")
configure_file(
    "${CMAKE_SOURCE_DIR}/cmake/BotController.vdf.in"
    "${CMAKE_BINARY_DIR}/BotController.vdf"
    @ONLY
)

set_target_properties(BotController PROPERTIES
    OUTPUT_NAME "BotController"
    PREFIX ""
    SUFFIX "${BC_SUFFIX}"
    RUNTIME_OUTPUT_DIRECTORY         "${BC_PACKAGE_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${BC_PACKAGE_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${BC_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY         "${BC_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${BC_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${BC_PACKAGE_BIN_DIR}"
)

# Stage vdf into addons/metamod + gamedata.json into addons/BotController
add_custom_command(TARGET BotController POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${CMAKE_BINARY_DIR}/package/addons/metamod"
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${CMAKE_BINARY_DIR}/package/addons/BotController"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_BINARY_DIR}/BotController.vdf"
        "${CMAKE_BINARY_DIR}/package/addons/metamod/BotController.vdf"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/configs/addons/BotController/gamedata.json"
        "${CMAKE_BINARY_DIR}/package/addons/BotController/gamedata.json"
)

if(MSVC)
    target_compile_options(BotController PRIVATE
        /utf-8 /W3 /permissive- /wd4819 /wd4267 /wd4244 /wd4146 /wd4996 /wd4661)
endif()
