cmake_minimum_required(VERSION 3.20)
project(DEVRNative CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(XR "${CMAKE_CURRENT_SOURCE_DIR}/../tools/openxr")
set(OVR "${CMAKE_CURRENT_SOURCE_DIR}/../tools/openvr")

# Dear Esther: Landmark Edition is a 32-bit game, so everything here is built for Win32.
add_library(DEVRNative SHARED DEVRNative.cpp OpenVRBackend.cpp CrashStacks.cpp)
target_include_directories(DEVRNative PRIVATE "${XR}/include" "${OVR}/include")
target_link_libraries(DEVRNative PRIVATE "${XR}/native/Win32/release/lib/openxr_loader.lib" "${OVR}/win32/openvr_api.lib" d3d11 dxgi)

# The virtual headset used by the tests: a minimal 32-bit OpenXR runtime with a scripted head.
add_library(DEVRMockXR SHARED mock/MockRuntime.cpp)
target_include_directories(DEVRMockXR PRIVATE "${XR}/include")
target_link_libraries(DEVRMockXR PRIVATE d3d11 dxgi winmm)

# Symbols for the bridge, so crash stacks name functions and lines. The PDB is not shipped to players.
target_compile_options(DEVRNative PRIVATE /Zi)
target_link_options(DEVRNative PRIVATE /DEBUG /OPT:REF /OPT:ICF)
