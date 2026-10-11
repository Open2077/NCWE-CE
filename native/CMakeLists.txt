cmake_minimum_required(VERSION 3.24)
project(NcweGizmo LANGUAGES CXX)
include(FetchContent)
FetchContent_Declare(imgui URL https://github.com/ocornut/imgui/archive/947aa9c9722bd6ff740dd757da609ff41f4d3ba3.zip URL_HASH SHA256=091b70d972347108ccde50b099c4688859b5d402b085fca82732c7d93c0d4676 DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_Declare(imguizmo URL https://github.com/CedricGuillemet/ImGuizmo/archive/b10e91756d32395f5c1fefd417899b657ed7cb88.zip URL_HASH SHA256=f945d54fc3ba48e07af61c3be4f3fad606ed24693ce2682ba73d7b8a60228220 DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_Declare(cimgui URL https://github.com/cimgui/cimgui/archive/970c614802935f51f451aa21ae06e838bdcf9349.zip URL_HASH SHA256=90d54602a0950ab0ea6c6c17aea361cef0dd570e615660769f9a1e4d5b0698e3 DOWNLOAD_EXTRACT_TIMESTAMP TRUE SOURCE_SUBDIR ncwe-unused)
FetchContent_MakeAvailable(imgui imguizmo cimgui)
# Match the native build shipped by ImGui.NET 1.91.6.1. One library owns both
# cimgui and ImGuizmo, so contexts, allocators and internal layouts cannot drift.
file(COPY ${imgui_SOURCE_DIR}/ DESTINATION ${cimgui_SOURCE_DIR}/imgui)
add_library(ncwe-gizmo SHARED GizmoBridge.cpp ${imguizmo_SOURCE_DIR}/ImGuizmo.cpp ${cimgui_SOURCE_DIR}/cimgui.cpp
  ${cimgui_SOURCE_DIR}/imgui/imgui.cpp ${cimgui_SOURCE_DIR}/imgui/imgui_draw.cpp
  ${cimgui_SOURCE_DIR}/imgui/imgui_tables.cpp ${cimgui_SOURCE_DIR}/imgui/imgui_widgets.cpp ${cimgui_SOURCE_DIR}/imgui/imgui_demo.cpp)
target_include_directories(ncwe-gizmo PRIVATE ${cimgui_SOURCE_DIR}/imgui ${cimgui_SOURCE_DIR} ${imguizmo_SOURCE_DIR})
target_compile_definitions(ncwe-gizmo PRIVATE IMGUI_DISABLE_OBSOLETE_FUNCTIONS=1 "IMGUI_USER_CONFIG=\"${cimgui_SOURCE_DIR}/cimconfig.h\"")
target_compile_features(ncwe-gizmo PRIVATE cxx_std_17)
set_target_properties(ncwe-gizmo PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../artifacts/native")
