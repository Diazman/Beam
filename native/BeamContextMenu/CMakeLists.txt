# Built by packaging/build-msix.ps1 for each architecture: cmake -A x64|ARM64
cmake_minimum_required(VERSION 3.20)
project(BeamContextMenu CXX)
set(CMAKE_CXX_STANDARD 17)
# Static C++ runtime: the Store package doesn't depend on the VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_library(BeamContextMenu SHARED BeamContextMenu.cpp BeamContextMenu.def)
target_compile_definitions(BeamContextMenu PRIVATE UNICODE _UNICODE NOMINMAX)
target_compile_options(BeamContextMenu PRIVATE /W4 /utf-8)
target_link_libraries(BeamContextMenu PRIVATE shlwapi ole32 shell32)
