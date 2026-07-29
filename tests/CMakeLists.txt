cmake_minimum_required(VERSION 3.20)
project(BotControllerReplaySafety LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

include(CTest)

set(REPLAY_LAYOUT_SOURCE
    "${CMAKE_CURRENT_LIST_DIR}/../src/BotRecorder/ReplaySubtickLayout.cpp")
set(REPLAY_INCLUDE_DIR
    "${CMAKE_CURRENT_LIST_DIR}/../src/BotRecorder")

add_executable(BotControllerReplaySubtickTests
    replay_subtick_layout_tests.cpp
    ${REPLAY_LAYOUT_SOURCE})
target_include_directories(BotControllerReplaySubtickTests PRIVATE ${REPLAY_INCLUDE_DIR})
if(MSVC)
    target_compile_options(BotControllerReplaySubtickTests PRIVATE /W4 /permissive-)
else()
    target_compile_options(BotControllerReplaySubtickTests PRIVATE
        -Wall -Wextra -Wpedantic)
endif()
add_test(NAME BotControllerReplaySubtickTests
    COMMAND BotControllerReplaySubtickTests)

add_executable(BotControllerSafeReadTests
    safe_read_tests.cpp)
target_include_directories(BotControllerSafeReadTests PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/../src/common")
if(MSVC)
    target_compile_options(BotControllerSafeReadTests PRIVATE /W4 /permissive-)
else()
    target_compile_options(BotControllerSafeReadTests PRIVATE
        -Wall -Wextra -Wpedantic)
endif()
add_test(NAME BotControllerSafeReadTests
    COMMAND BotControllerSafeReadTests)

add_executable(BotControllerBuyPolicyTests
    buy_controller_policy_tests.cpp)
target_include_directories(BotControllerBuyPolicyTests PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/../src/BuyController")
if(MSVC)
    target_compile_options(BotControllerBuyPolicyTests PRIVATE /W4 /permissive-)
else()
    target_compile_options(BotControllerBuyPolicyTests PRIVATE
        -Wall -Wextra -Wpedantic)
endif()
add_test(NAME BotControllerBuyPolicyTests
    COMMAND BotControllerBuyPolicyTests)

option(BOTCONTROLLER_BUILD_FUZZER
    "Build the libFuzzer target for the production replay ABI validator" OFF)
if(BOTCONTROLLER_BUILD_FUZZER)
    if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        message(FATAL_ERROR
            "BOTCONTROLLER_BUILD_FUZZER requires a Clang toolchain with libFuzzer")
    endif()
    if(WIN32)
        message(FATAL_ERROR
            "The supported fuzz configuration is Linux Clang; Windows unit tests remain supported")
    endif()

    add_executable(BotControllerReplayAbiFuzz
        replay_load_abi_fuzz.cpp
        ${REPLAY_LAYOUT_SOURCE})
    target_include_directories(BotControllerReplayAbiFuzz PRIVATE ${REPLAY_INCLUDE_DIR})
    target_compile_options(BotControllerReplayAbiFuzz PRIVATE
        -fsanitize=fuzzer,address,undefined
        -fno-omit-frame-pointer)
    target_link_options(BotControllerReplayAbiFuzz PRIVATE
        -fsanitize=fuzzer,address,undefined
        -fno-omit-frame-pointer)
endif()
