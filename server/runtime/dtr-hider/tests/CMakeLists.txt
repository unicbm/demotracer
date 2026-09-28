cmake_minimum_required(VERSION 3.20)
project(BotHiderTests LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
include(CTest)

set(DTR_COMMON_DIR "${CMAKE_CURRENT_LIST_DIR}/../../common" CACHE PATH "DemoTracer shared runtime sources")
if(NOT EXISTS "${DTR_COMMON_DIR}/third_party/nlohmann/json.hpp")
    message(FATAL_ERROR "Shared runtime sources are missing; use the full DemoTracer checkout or set DTR_COMMON_DIR.")
endif()

add_executable(BotHiderPresentationTests hider_presentation_tests.cpp ../src/presentation_state.cpp)
target_include_directories(BotHiderPresentationTests PRIVATE ../src)
add_test(NAME BotHiderPresentationTests COMMAND BotHiderPresentationTests)

add_executable(BotHiderIdentityTests hider_identity_tests.cpp
    ../src/bot_info.cpp
    ../src/fake_client_manager.cpp
    ../src/ping_display.cpp
    ../src/presentation_state.cpp)
target_include_directories(BotHiderIdentityTests PRIVATE ../src "${DTR_COMMON_DIR}" "${DTR_COMMON_DIR}/third_party")
add_test(NAME BotHiderIdentityTests COMMAND BotHiderIdentityTests)

if(MSVC)
    foreach(target BotHiderPresentationTests BotHiderIdentityTests)
        target_compile_definitions(${target} PRIVATE _CRT_SECURE_NO_WARNINGS)
        target_compile_options(${target} PRIVATE /utf-8 /EHsc)
    endforeach()
endif()
