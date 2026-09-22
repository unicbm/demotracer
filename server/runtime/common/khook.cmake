# Both runtimes consume Metamod's interface; never link a private KHook engine.
if(NOT EXISTS "${MMS}/third_party/khook/include/khook.hpp")
    message(FATAL_ERROR "Metamod 2.0 build 1469+ with its KHook submodule is required. See server/README.md.")
endif()
file(READ "${MMS}/core/ISmmAPI.h" DEMOTRACER_MMS_API)
if(NOT DEMOTRACER_MMS_API MATCHES "GetDetourInterface")
    message(FATAL_ERROR "MMSOURCE_DEV must provide the KHook Metamod API.")
endif()
include_directories("${MMS}/third_party/khook/include")
