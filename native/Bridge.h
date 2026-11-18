// Shared between the OpenXR and OpenVR back ends of the DearEstherVR native bridge.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>

namespace bridge {

struct Pose { float px, py, pz, qx, qy, qz, qw; };

// Shared with C#. Keep in sync with Native.cs. Poses and tangents use OpenXR's right-handed convention with -Z
// forward, which OpenVR shares.
struct Frame {
    int running;       // the session is running, so this frame must be submitted
    int shouldRender;  // the runtime wants images this frame
    int viewsValid;    // the eye poses below are tracked
    int pad;
    Pose eye[2];
    float tangents[2][4];  // per eye, the tangents of the view the game should render: left, right, up, down
    float periodSeconds;
    float rawTangents[2][4];  // the headset's own views, before any widening
    float floorY;             // height of the real floor in the tracking space the eyes are given in
    int floorValid;           // the runtime knows where the floor is (OpenXR STAGE, SteamVR standing)
    int positionTracked;      // the head position is really tracked, not a placeholder from before tracking started
};

struct Quad { Pose pose; float width, height; int visible; };
struct Source { ID3D11Texture2D* tex = nullptr; int w = 0, h = 0; };

void Log(const char* fmt, ...);
void InstallCrashStacks(const char* path);

// Widens a view to a frustum that is symmetric about the eye's forward axis and contains the original.
// The game's image effects (fog, ambient occlusion, volumetric light) rebuild view rays from the camera's
// field of view and aspect ratio alone, which only describe symmetric frustums.
inline void Symmetric(const float raw[4], float out[4]) {
    float h = raw[0] < 0 ? -raw[0] : raw[0];
    if (raw[1] > h) h = raw[1];
    else if (-raw[1] > h) h = -raw[1];
    float v = raw[2] < 0 ? -raw[2] : raw[2];
    if (raw[3] > v) v = raw[3];
    else if (-raw[3] > v) v = -raw[3];
    out[0] = -h; out[1] = h; out[2] = v; out[3] = -v;
}

}  // namespace bridge

// The OpenVR back end, used for SteamVR.
namespace ovr {
int Create(char* runtimeName, int nameSize, char* systemName, int systemSize, int* recW, int* recH);
void BeginFrame(bridge::Frame* out, bool symmetric);
void Submit(const bridge::Source eyes[2], const bridge::Source& ui, const bridge::Quad& quad, ID3D11Device* device);
void Shutdown();
bool Running();
bool ExitRequested();
}  // namespace ovr
