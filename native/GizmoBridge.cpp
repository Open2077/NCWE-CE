// SPDX-License-Identifier: GPL-3.0-only
#include "imgui.h"
#include "ImGuizmo.h"
#define API extern "C" __declspec(dllexport)
API const char* ncwe_gizmo_version() { return IMGUI_VERSION; }
API void ncwe_gizmo_begin(void* context) {
    ImGuizmo::SetImGuiContext(static_cast<ImGuiContext*>(context));
    ImGuizmo::BeginFrame();
}
static ImGuizmo::OPERATION Operation(int operation) {
    return operation == 1 ? ImGuizmo::ROTATE : operation == 2 ? ImGuizmo::SCALE : ImGuizmo::TRANSLATE;
}
API int ncwe_gizmo_manipulate(const float* view, const float* projection, float* model,
    float x, float y, float width, float height, int operation) {
    ImGuizmo::SetOrthographic(false);
    ImGuizmo::SetDrawlist();
    ImGuizmo::SetRect(x, y, width, height);
    return ImGuizmo::Manipulate(view, projection, Operation(operation), ImGuizmo::LOCAL, model) ? 1 : 0;
}
// mode: 0 local, 1 world. snap: null or three per-axis steps (degrees for rotation).
API int ncwe_gizmo_manipulate_ex(const float* view, const float* projection, float* model,
    float x, float y, float width, float height, int operation, int mode, const float* snap, float size) {
    ImGuizmo::SetOrthographic(false);
    ImGuizmo::SetDrawlist();
    ImGuizmo::SetRect(x, y, width, height);
    if (size > 0) ImGuizmo::SetGizmoSizeClipSpace(size);
    return ImGuizmo::Manipulate(view, projection, Operation(operation), mode == 1 ? ImGuizmo::WORLD : ImGuizmo::LOCAL, model, nullptr, snap) ? 1 : 0;
}
API int ncwe_gizmo_using() { return ImGuizmo::IsUsing() ? 1 : 0; }
API int ncwe_gizmo_over() { return ImGuizmo::IsOver() ? 1 : 0; }
