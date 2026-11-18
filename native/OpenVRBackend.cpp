// OpenVR back end, for SteamVR. SteamVR's OpenXR runtime is 64-bit only, and Dear Esther is a 32-bit game,
// so SteamVR is reached through OpenVR, which still serves 32-bit applications.
//
// The world goes to the compositor as one texture per eye. The interface goes to an overlay, which SteamVR always
// draws over the scene, just as the OpenXR back end's quad layer is.
#include "Bridge.h"
#include <openvr.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>

namespace {

std::atomic<bool> g_running{false}, g_exit{false};
vr::VROverlayHandle_t g_overlay = vr::k_ulOverlayHandleInvalid;
bool g_overlayShown = false;
float g_overlayWidth = 0;
std::mutex g_boundsLock;
vr::VRTextureBounds_t g_bounds[2] = {{0, 0, 1, 1}, {0, 0, 1, 1}};
vr::HmdMatrix34_t g_renderPose{};
bool g_havePose = false;
std::atomic<vr::ETrackingUniverseOrigin> g_universe{vr::TrackingUniverseSeated};
int g_invalidSeated = 0;

void ToPose(const vr::HmdMatrix34_t& m, bridge::Pose& p) {
    p.px = m.m[0][3]; p.py = m.m[1][3]; p.pz = m.m[2][3];
    float tr = m.m[0][0] + m.m[1][1] + m.m[2][2];
    float qw, qx, qy, qz;
    if (tr > 0) {
        float s = std::sqrt(tr + 1.0f) * 2;
        qw = 0.25f * s; qx = (m.m[2][1] - m.m[1][2]) / s; qy = (m.m[0][2] - m.m[2][0]) / s; qz = (m.m[1][0] - m.m[0][1]) / s;
    } else if (m.m[0][0] > m.m[1][1] && m.m[0][0] > m.m[2][2]) {
        float s = std::sqrt(1.0f + m.m[0][0] - m.m[1][1] - m.m[2][2]) * 2;
        qw = (m.m[2][1] - m.m[1][2]) / s; qx = 0.25f * s; qy = (m.m[0][1] + m.m[1][0]) / s; qz = (m.m[0][2] + m.m[2][0]) / s;
    } else if (m.m[1][1] > m.m[2][2]) {
        float s = std::sqrt(1.0f + m.m[1][1] - m.m[0][0] - m.m[2][2]) * 2;
        qw = (m.m[0][2] - m.m[2][0]) / s; qx = (m.m[0][1] + m.m[1][0]) / s; qy = 0.25f * s; qz = (m.m[1][2] + m.m[2][1]) / s;
    } else {
        float s = std::sqrt(1.0f + m.m[2][2] - m.m[0][0] - m.m[1][1]) * 2;
        qw = (m.m[1][0] - m.m[0][1]) / s; qx = (m.m[0][2] + m.m[2][0]) / s; qy = (m.m[1][2] + m.m[2][1]) / s; qz = 0.25f * s;
    }
    p.qx = qx; p.qy = qy; p.qz = qz; p.qw = qw;
}

vr::HmdMatrix34_t Mul(const vr::HmdMatrix34_t& a, const vr::HmdMatrix34_t& b) {
    vr::HmdMatrix34_t r{};
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 4; j++) {
            r.m[i][j] = a.m[i][0] * b.m[0][j] + a.m[i][1] * b.m[1][j] + a.m[i][2] * b.m[2][j];
            if (j == 3) r.m[i][j] += a.m[i][3];
        }
    return r;
}

vr::HmdMatrix34_t FromPose(const bridge::Pose& p) {
    float x = p.qx, y = p.qy, z = p.qz, w = p.qw;
    vr::HmdMatrix34_t m{};
    m.m[0][0] = 1 - 2 * (y * y + z * z); m.m[0][1] = 2 * (x * y - z * w);     m.m[0][2] = 2 * (x * z + y * w);     m.m[0][3] = p.px;
    m.m[1][0] = 2 * (x * y + z * w);     m.m[1][1] = 1 - 2 * (x * x + z * z); m.m[1][2] = 2 * (y * z - x * w);     m.m[1][3] = p.py;
    m.m[2][0] = 2 * (x * z - y * w);     m.m[2][1] = 2 * (y * z + x * w);     m.m[2][2] = 1 - 2 * (x * x + y * y); m.m[2][3] = p.pz;
    return m;
}

void PollEvents() {
    vr::VREvent_t e;
    while (vr::VRSystem() && vr::VRSystem()->PollNextEvent(&e, sizeof e)) {
        if (e.eventType == vr::VREvent_Quit) {
            // SteamVR is closing, or another scene app is taking over. The game itself carries on flat, so this
            // only lets go of the connection, which the main loop does at once.
            bridge::Log("SteamVR asked the game to let go of the headset");
            g_running = false;
            g_exit = true;
        }
    }
}

}  // namespace

namespace ovr {

// Returns 0 on success, 1 when no headset is available yet (worth retrying), or 2 on a hard failure.
int Create(char* runtimeName, int nameSize, char* systemName, int systemSize, int* recW, int* recH) {
    if (!vr::VR_IsRuntimeInstalled()) { bridge::Log("SteamVR is not installed"); return 2; }
    if (!vr::VR_IsHmdPresent()) { bridge::Log("SteamVR reports no headset"); return 1; }
    vr::EVRInitError err = vr::VRInitError_None;
    // Connecting as a scene application quits whichever one SteamVR is showing. So while SteamVR is already
    // running, a background connection (which never starts SteamVR) checks that no other app has the scene.
    // That keeps the game from taking SteamVR back from a VR app, or Room Setup, the player opened since.
    vr::VR_Init(&err, vr::VRApplication_Background);
    if (err == vr::VRInitError_None) {
        uint32_t scene = vr::VRApplications() ? vr::VRApplications()->GetCurrentSceneProcessId() : 0;
        vr::VR_Shutdown();
        if (scene != 0 && scene != GetCurrentProcessId()) {
            static uint32_t reported = 0;
            if (reported != scene) { bridge::Log("Another SteamVR app (process %u) has the headset, waiting for it to close", scene); reported = scene; }
            return 1;
        }
    }
    err = vr::VRInitError_None;
    vr::VR_Init(&err, vr::VRApplication_Scene);
    if (err != vr::VRInitError_None) {
        bridge::Log("VR_Init failed: %s", vr::VR_GetVRInitErrorAsEnglishDescription(err));
        return 1;
    }
    if (!vr::VRCompositor()) { bridge::Log("SteamVR compositor unavailable"); vr::VR_Shutdown(); return 1; }
    g_universe = vr::TrackingUniverseSeated;
    g_invalidSeated = 0;
    vr::VRCompositor()->SetTrackingSpace(g_universe);
    strncpy_s(runtimeName, nameSize, "SteamVR (OpenVR)", _TRUNCATE);
    vr::VRSystem()->GetStringTrackedDeviceProperty(vr::k_unTrackedDeviceIndex_Hmd, vr::Prop_ModelNumber_String, systemName, systemSize);
    uint32_t w = 0, h = 0;
    vr::VRSystem()->GetRecommendedRenderTargetSize(&w, &h);
    *recW = (int)w;
    *recH = (int)h;
    bridge::Log("SteamVR headset: %s, recommended eye size %ux%u", systemName, w, h);
    if (vr::VROverlay()) {
        auto oe = vr::VROverlay()->CreateOverlay("dearesthervr.interface", "Dear Esther VR", &g_overlay);
        if (oe != vr::VROverlayError_None) { bridge::Log("CreateOverlay failed: %d", (int)oe); g_overlay = vr::k_ulOverlayHandleInvalid; }
        else vr::VROverlay()->SetOverlayFlag(g_overlay, vr::VROverlayFlags_IsPremultiplied, true);
    }
    g_running = true;
    g_exit = false;
    return 0;
}

// Threading: the compositor is only used from Unity's render thread, as Unity's own OpenVR support does.
// WaitGetPoses there paces the game to the headset. Calling it on the main thread instead made SteamVR's client
// create its D3D11 device there, and that deadlocked in the graphics driver against the render thread.
// The main thread reads predicted poses with GetDeviceToAbsoluteTrackingPose, which never blocks, and each
// submit carries the pose the eyes were rendered from, so the compositor reprojects from the right place.
void BeginFrame(bridge::Frame* out, bool symmetric) {
    PollEvents();
    if (!g_running) return;
    out->running = 1;
    out->shouldRender = 1;
    auto* sys = vr::VRSystem();
    float hz = sys->GetFloatTrackedDeviceProperty(vr::k_unTrackedDeviceIndex_Hmd, vr::Prop_DisplayFrequency_Float);
    float frame = hz > 1 ? 1.0f / hz : 1.0f / 90.0f;
    out->periodSeconds = frame;
    // These eyes reach the display a frame after this one's vsync: the render thread submits them during the
    // next frame. The rest of the current frame, one more frame, and the panel's own vsync-to-photons delay.
    float sinceVsync = 0;
    uint64_t counter = 0;
    sys->GetTimeSinceLastVsync(&sinceVsync, &counter);
    float toPhotons = sys->GetFloatTrackedDeviceProperty(vr::k_unTrackedDeviceIndex_Hmd, vr::Prop_SecondsFromVsyncToPhotons_Float);
    float ahead = std::max(0.0f, frame - sinceVsync) + frame + toPhotons;
    vr::TrackedDevicePose_t poses[1];
    sys->GetDeviceToAbsoluteTrackingPose(g_universe, ahead, poses, 1);
    const auto& head = poses[0];
    if (!head.bPoseIsValid) {
        // The seated space needs a room set up. Without one, SteamVR tracks the headset but calls every seated
        // pose invalid. The mod recenters by itself, so the raw space works just as well.
        if (head.eTrackingResult == vr::TrackingResult_Running_OK && g_universe == vr::TrackingUniverseSeated && ++g_invalidSeated > 30) {
            g_universe = vr::TrackingUniverseRawAndUncalibrated;
            vr::VRCompositor()->SetTrackingSpace(g_universe);
            bridge::Log("SteamVR has no seated calibration (no room set up), using its raw tracking space");
        }
        return;
    }
    out->viewsValid = 1;
    out->positionTracked = head.eTrackingResult == vr::TrackingResult_Running_OK ? 1 : 0;
    // The seated zero pose, seen from the standing (floor-level) space: its height is how far the seated
    // origin is above the real floor. SteamVR knows it once a room is set up.
    if (g_universe == vr::TrackingUniverseSeated) {
        auto standing = sys->GetSeatedZeroPoseToStandingAbsoluteTrackingPose();
        float h = standing.m[1][3];
        if (h > 0.05f && h < 3.0f) { out->floorY = -h; out->floorValid = 1; }
    }
    {
        std::lock_guard<std::mutex> lk(g_boundsLock);
        g_renderPose = head.mDeviceToAbsoluteTracking;
        g_havePose = true;
    }
    for (int i = 0; i < 2; i++) {
        auto eye = (vr::EVREye)i;
        ToPose(Mul(head.mDeviceToAbsoluteTracking, vr::VRSystem()->GetEyeToHeadTransform(eye)), out->eye[i]);
        float l, r, t, b;
        vr::VRSystem()->GetProjectionRaw(eye, &l, &r, &t, &b);
        // OpenVR's top and bottom follow the image's downward rows, so up is -top.
        float raw[4] = {l, r, -t, -b};
        for (int k = 0; k < 4; k++) out->rawTangents[i][k] = raw[k];
        float use[4];
        if (symmetric) bridge::Symmetric(raw, use); else for (int k = 0; k < 4; k++) use[k] = raw[k];
        for (int k = 0; k < 4; k++) out->tangents[i][k] = use[k];
        // The game renders the wider view. The compositor only takes the headset's own view, so the
        // submitted bounds pick that region out of the texture.
        vr::VRTextureBounds_t bb;
        bb.uMin = (raw[0] - use[0]) / (use[1] - use[0]);
        bb.uMax = (raw[1] - use[0]) / (use[1] - use[0]);
        bb.vMin = (use[2] - raw[2]) / (use[2] - use[3]);
        bb.vMax = (use[2] - raw[3]) / (use[2] - use[3]);
        std::lock_guard<std::mutex> lk(g_boundsLock);
        g_bounds[i] = bb;
    }
}

void Submit(const bridge::Source eyes[2], const bridge::Source& ui, const bridge::Quad& quad, ID3D11Device*) {
    if (!g_running) return;
    vr::VRTextureBounds_t bounds[2];
    vr::HmdMatrix34_t pose;
    bool havePose;
    {
        std::lock_guard<std::mutex> lk(g_boundsLock);
        bounds[0] = g_bounds[0];
        bounds[1] = g_bounds[1];
        pose = g_renderPose;
        havePose = g_havePose;
    }
    // Each frame's submits must follow a WaitGetPoses, which also paces the game to the headset.
    auto we = vr::VRCompositor()->WaitGetPoses(nullptr, 0, nullptr, 0);
    if (we != vr::VRCompositorError_None) {
        static int reported = 0;
        if (reported++ < 5) bridge::Log("WaitGetPoses failed: %d", (int)we);
    }
    for (int i = 0; i < 2 && havePose; i++) {
        if (!eyes[i].tex) return;
        vr::VRTextureWithPose_t t{};
        t.handle = eyes[i].tex;
        t.eType = vr::TextureType_DirectX;
        t.eColorSpace = vr::ColorSpace_Auto;
        t.mDeviceToAbsoluteTracking = pose;
        auto e = vr::VRCompositor()->Submit((vr::EVREye)i, &t, &bounds[i], vr::Submit_TextureWithPose);
        if (e != vr::VRCompositorError_None) {
            static int reported = 0;
            if (reported++ < 5) bridge::Log("Submit failed: %d", (int)e);
        }
    }
    vr::VRCompositor()->PostPresentHandoff();
    if (g_overlay == vr::k_ulOverlayHandleInvalid || !vr::VROverlay()) return;
    bool show = quad.visible && ui.tex;
    if (show) {
        vr::Texture_t t{ui.tex, vr::TextureType_DirectX, vr::ColorSpace_Auto};
        vr::VROverlay()->SetOverlayTexture(g_overlay, &t);
        if (quad.width != g_overlayWidth) { vr::VROverlay()->SetOverlayWidthInMeters(g_overlay, quad.width); g_overlayWidth = quad.width; }
        auto m = FromPose(quad.pose);
        vr::VROverlay()->SetOverlayTransformAbsolute(g_overlay, g_universe.load(), &m);
    }
    if (show != g_overlayShown) {
        if (show) vr::VROverlay()->ShowOverlay(g_overlay); else vr::VROverlay()->HideOverlay(g_overlay);
        g_overlayShown = show;
    }
}

void Shutdown() {
    if (g_overlay != vr::k_ulOverlayHandleInvalid && vr::VROverlay()) vr::VROverlay()->DestroyOverlay(g_overlay);
    g_overlay = vr::k_ulOverlayHandleInvalid;
    g_overlayShown = false;
    g_overlayWidth = 0;
    vr::VR_Shutdown();
    g_running = false;
    g_exit = false;
    bridge::Log("OpenVR shut down");
}

bool Running() { return g_running; }
bool ExitRequested() { return g_exit; }

}  // namespace ovr
