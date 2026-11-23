# The native background traffic engine, src.traffic._traffic_native.
# Build it with Tools/build_native.py, which configures this with the backend's Python and copies the
# module into backend/src/traffic. Without it the backend runs the same traffic in Python.
cmake_minimum_required(VERSION 3.18...3.31)
project(formula9_traffic LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()
# The C++ runtime is linked in, so the module loads in the bundled build's portable Python on a machine
# without the Visual C++ redistributable. The C runtime (UCRT) stays the system's DLL: its pow, atan2 and
# acos must be the very ones CPython calls, for the two engines to round alike.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

find_package(Python 3.12 COMPONENTS Interpreter Development.Module REQUIRED)
execute_process(COMMAND "${Python_EXECUTABLE}" -m nanobind --cmake_dir
                OUTPUT_STRIP_TRAILING_WHITESPACE OUTPUT_VARIABLE nanobind_ROOT)
find_package(nanobind CONFIG REQUIRED)

# FREE_THREADED: on a free-threaded Python (3.14t) the module declares it needs no GIL; on a normal one
# this changes nothing
nanobind_add_module(_traffic_native NB_STATIC FREE_THREADED traffic_core.cpp)

# The module reports the source it was built from, so the backend never runs a stale build
file(SHA256 "${CMAKE_CURRENT_SOURCE_DIR}/traffic_core.cpp" F9_TRAFFIC_SOURCE_HASH)
target_compile_definitions(_traffic_native PRIVATE F9_TRAFFIC_SOURCE_HASH="${F9_TRAFFIC_SOURCE_HASH}")

# Rounding must be CPython's: no fused multiply-adds, no reassociation
if(MSVC)
  target_compile_options(_traffic_native PRIVATE /fp:precise /W3 /permissive- /utf-8)
  # The static C++ runtime with the shared UCRT (Microsoft's "hybrid" linking)
  target_link_options(_traffic_native PRIVATE /NODEFAULTLIB:libucrt.lib)
  target_link_libraries(_traffic_native PRIVATE ucrt.lib)
else()
  target_compile_options(_traffic_native PRIVATE -ffp-contract=off -fno-fast-math -Wall)
endif()
