// DearEstherVR native bridge. With OpenXR (VDXR, and the mock headset used by the tests) it owns the instance and
// session, and submits a stereo projection layer for the game world plus a quad layer for the game's UI. With
// OpenVR (SteamVR) it hands the same work to OpenVRBackend.cpp.
//
// Threading: DVR_Create and DVR_BeginFrame (xrWaitFrame, xrLocateViews, event polling) run on Unity's main
// thread. Session creation, swapchain work, xrBeginFrame and xrEndFrame run on Unity's render thread through
// GL.IssuePluginEvent, because they touch the D3D11 immediate context.
//
// The UI quad is composited by the runtime on top of the world layer, so menus can never be hidden by scenery.
#include "Bridge.h"
#include <tlhelp32.h>
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

#define API extern "C" __declspec(dllexport)

namespace {

using bridge::Frame;
using bridge::Log;
using bridge::Pose;
using bridge::Quad;
using bridge::Source;

// Which API reaches the headset. Keep in sync with Native.cs.
const int kOpenXR = 1, kOpenVR = 2;
std::atomic<int> g_backend{0};
int g_ovrRecW = 0, g_ovrRecH = 0;

struct Pending { XrTime displayTime; bool shouldRender; bool viewsValid; XrView views[2]; Quad ui; };

std::mutex g_logLock;
std::deque<std::string> g_logs;

XrInstance g_instance = XR_NULL_HANDLE;
XrSystemId g_system = XR_NULL_SYSTEM_ID;
XrSession g_session = XR_NULL_HANDLE;
XrSpace g_local = XR_NULL_HANDLE;
XrSpace g_stage = XR_NULL_HANDLE;  // floor-level space, if the runtime has one; used only to find the floor
std::atomic<int> g_state{0};
std::atomic<bool> g_running{false};
std::atomic<bool> g_exitRequested{false};
XrViewConfigurationView g_viewCfg[2]{{XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}};
char g_runtimeName[XR_MAX_RUNTIME_NAME_SIZE] = "";
char g_systemName[XR_MAX_SYSTEM_NAME_SIZE] = "";

ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_ctx = nullptr;

struct Chain { XrSwapchain handle = XR_NULL_HANDLE; std::vector<XrSwapchainImageD3D11KHR> images; int w = 0, h = 0; };
Chain g_eyeChain[2], g_uiChain;
Source g_eyeSrc[2], g_uiSrc;

// Unity 5.3 replaces the D3D11 texture behind a render texture when the game changes resolution or window mode
// (and around some level loads), and frees the old one. The bridge therefore holds its own reference on every
// texture it copies from: a texture Unity has dropped stays valid, just stale, until the mod hands over the new
// one. Old textures are released on the render thread, the only thread that copies from them.
std::mutex g_srcLock;
std::vector<ID3D11Texture2D*> g_releaseQueue;

void Hold(Source& slot, ID3D11Texture2D* t, int w, int h) {
    std::lock_guard<std::mutex> l(g_srcLock);
    if (t == slot.tex) { slot.w = w; slot.h = h; return; }
    if (t) t->AddRef();
    if (slot.tex) g_releaseQueue.push_back(slot.tex);
    slot = {t, w, h};
}

void DrainReleases() {
    std::vector<ID3D11Texture2D*> q;
    {
        std::lock_guard<std::mutex> l(g_srcLock);
        q.swap(g_releaseQueue);
    }
    for (auto* t : q) t->Release();
}

void Snapshot(Source eyes[2], Source& ui) {
    std::lock_guard<std::mutex> l(g_srcLock);
    eyes[0] = g_eyeSrc[0];
    eyes[1] = g_eyeSrc[1];
    ui = g_uiSrc;
}

int64_t g_format = 0;
std::atomic<bool> g_symmetric{true};
std::atomic<int> g_debug{0};  // troubleshooting: 1 = skip the copy into swapchains, 2 = skip xrBeginFrame/xrEndFrame

std::mutex g_quadLock;
Quad g_ui{{0, 0, -1.5f, 0, 0, 0, 1}, 1.6f, 0.9f, 0};

std::mutex g_queueLock;
std::deque<Pending> g_queue;
std::atomic<uint64_t> g_framesSubmitted{0};

bool Check(XrResult r, const char* what) {
    if (XR_SUCCEEDED(r)) return true;
    char s[XR_MAX_RESULT_STRING_SIZE] = "";
    if (g_instance) xrResultToString(g_instance, r, s);
    Log("%s failed: %d %s", what, (int)r, s);
    return false;
}

void DestroyChain(Chain& c) {
    if (c.handle) xrDestroySwapchain(c.handle);
    c = Chain{};
}

bool PickFormat() {
    uint32_t n = 0;
    xrEnumerateSwapchainFormats(g_session, 0, &n, nullptr);
    std::vector<int64_t> formats(n);
    if (!Check(xrEnumerateSwapchainFormats(g_session, n, &n, formats.data()), "xrEnumerateSwapchainFormats")) return false;
    // Unity's ARGB32 render textures are R8G8B8A8, so a straight copy needs a swapchain of the same family.
    // The game renders in gamma space, so its bytes are already sRGB encoded: an sRGB swapchain makes the
    // compositor decode them correctly.
    g_format = 0;
    for (int64_t want : {(int64_t)DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, (int64_t)DXGI_FORMAT_R8G8B8A8_UNORM})
        for (auto f : formats)
            if (f == want && !g_format) g_format = f;
    if (!g_format) { Log("the runtime offers no RGBA8 swapchain format"); return false; }
    return true;
}

bool CreateChain(Chain& c, int w, int h, const char* what) {
    DestroyChain(c);
    XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    ci.format = g_format;
    ci.sampleCount = 1;
    ci.width = (uint32_t)w;
    ci.height = (uint32_t)h;
    ci.faceCount = 1;
    ci.arraySize = 1;
    ci.mipCount = 1;
    if (!Check(xrCreateSwapchain(g_session, &ci, &c.handle), what)) return false;
    uint32_t count = 0;
    xrEnumerateSwapchainImages(c.handle, 0, &count, nullptr);
    c.images.assign(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    if (!Check(xrEnumerateSwapchainImages(c.handle, count, &count, (XrSwapchainImageBaseHeader*)c.images.data()), "xrEnumerateSwapchainImages")) return false;
    c.w = w;
    c.h = h;
    Log("%s swapchain %dx%d format %d images %u", what, w, h, (int)g_format, count);
    return true;
}

void SyncChains() {
    if (!g_session || !g_format) return;
    Source eyes[2], ui;
    Snapshot(eyes, ui);
    for (int i = 0; i < 2; i++)
        if (eyes[i].tex && (g_eyeChain[i].w != eyes[i].w || g_eyeChain[i].h != eyes[i].h)) CreateChain(g_eyeChain[i], eyes[i].w, eyes[i].h, i == 0 ? "left eye" : "right eye");
    if (ui.tex && (g_uiChain.w != ui.w || g_uiChain.h != ui.h)) CreateChain(g_uiChain, ui.w, ui.h, "ui");
}

// Copies a Unity texture into the next image of a swapchain. Returns false if the layer must be left out.
bool CopyToChain(Chain& c, const Source& s) {
    if (!c.handle || !s.tex || c.w != s.w || c.h != s.h) return false;
    // The texture itself must match too, not just the size the mod said it has.
    D3D11_TEXTURE2D_DESC sd{};
    s.tex->GetDesc(&sd);
    if ((int)sd.Width != c.w || (int)sd.Height != c.h || sd.SampleDesc.Count != 1) {
        static int reported = 0;
        if (reported++ < 5) Log("skipped a copy: source texture is %ux%u x%u, swapchain is %dx%d", sd.Width, sd.Height, sd.SampleDesc.Count, c.w, c.h);
        return false;
    }
    uint32_t idx = 0;
    XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    if (!Check(xrAcquireSwapchainImage(c.handle, &ai, &idx), "xrAcquireSwapchainImage")) return false;
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = 100000000;
    bool ok = Check(xrWaitSwapchainImage(c.handle, &wi), "xrWaitSwapchainImage");
    if (ok && (g_debug & 32)) {
        static FILE* trace = nullptr;
        static int n = 0;
        if (!trace) {
            char p[MAX_PATH];
            GetEnvironmentVariableA("USERPROFILE", p, MAX_PATH);
            fopen_s(&trace, (std::string(p) + "\\AppData\\LocalLow\\DearEstherVR\\lab\\copy_trace.txt").c_str(), "w");
        }
        if (trace && n++ < 5000) {
            D3D11_TEXTURE2D_DESC sd{}, dd{};
            s.tex->GetDesc(&sd);
            c.images[idx].texture->GetDesc(&dd);
            ID3D11Device* sdev = nullptr;
            s.tex->GetDevice(&sdev);
            fprintf(trace, "%d chain=%p img=%u src=%p %ux%u f%d s%u bind%x usage%d misc%x dev=%p | dst=%p %ux%u f%d bind%x | thread %lu\n", n, (void*)c.handle, idx, (void*)s.tex,
                    sd.Width, sd.Height, (int)sd.Format, sd.SampleDesc.Count, sd.BindFlags, (int)sd.Usage, sd.MiscFlags, (void*)sdev,
                    (void*)c.images[idx].texture, dd.Width, dd.Height, (int)dd.Format, dd.BindFlags, GetCurrentThreadId());
            fflush(trace);
            if (sdev) sdev->Release();
        }
    }
    if (ok && !(g_debug & 1)) {
        g_ctx->CopySubresourceRegion(c.images[idx].texture, 0, 0, 0, 0, s.tex, 0, nullptr);
        if (g_debug & 8) g_ctx->Flush();
    }
    XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    Check(xrReleaseSwapchainImage(c.handle, &ri), "xrReleaseSwapchainImage");
    return ok;
}

void RT_CreateSession() {
    Source eyes[2], uiSrc;
    Snapshot(eyes, uiSrc);
    if (g_session || !g_instance || !eyes[0].tex) return;
    eyes[0].tex->GetDevice(&g_device);
    g_device->GetImmediateContext(&g_ctx);
    Log("D3D11 device flags 0x%x, feature level 0x%x", g_device->GetCreationFlags(), (unsigned)g_device->GetFeatureLevel());
    if (g_debug & 4) {
        ID3D11Multithread* mt = nullptr;
        if (SUCCEEDED(g_ctx->QueryInterface(__uuidof(ID3D11Multithread), (void**)&mt))) {
            BOOL was = mt->SetMultithreadProtected(TRUE);
            Log("D3D11 multithread protection on (was %d)", was);
            mt->Release();
        }
    }

    D3D11_TEXTURE2D_DESC td{};
    eyes[0].tex->GetDesc(&td);
    Log("eye texture %ux%u dxgi=%d samples=%u", td.Width, td.Height, (int)td.Format, td.SampleDesc.Count);

    PFN_xrGetD3D11GraphicsRequirementsKHR getReq = nullptr;
    xrGetInstanceProcAddr(g_instance, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&getReq);
    XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    if (!getReq || !Check(getReq(g_instance, g_system, &req), "xrGetD3D11GraphicsRequirementsKHR")) return;

    IDXGIDevice* dxgi = nullptr;
    if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi))) {
        IDXGIAdapter* ad = nullptr;
        if (SUCCEEDED(dxgi->GetAdapter(&ad))) {
            DXGI_ADAPTER_DESC d{};
            ad->GetDesc(&d);
            char game[128] = "";
            WideCharToMultiByte(CP_UTF8, 0, d.Description, -1, game, sizeof game, nullptr, nullptr);
            if (memcmp(&d.AdapterLuid, &req.adapterLuid, sizeof(LUID)) != 0)
                Log("WARNING: the game runs on '%s' but the headset runtime asked for a different graphics adapter", game);
            else
                Log("graphics adapter: %s", game);
            ad->Release();
        }
        dxgi->Release();
    }

    XrGraphicsBindingD3D11KHR bind{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    bind.device = g_device;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &bind;
    sci.systemId = g_system;
    if (!Check(xrCreateSession(g_instance, &sci, &g_session), "xrCreateSession")) return;

    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.poseInReferenceSpace.orientation.w = 1;
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    Check(xrCreateReferenceSpace(g_session, &rs, &g_local), "local space");
    // The STAGE space has its origin on the real floor. Locating it in LOCAL gives the floor height, which
    // lets the mod put the player's real floor on the game's floor.
    uint32_t n = 0;
    xrEnumerateReferenceSpaces(g_session, 0, &n, nullptr);
    std::vector<XrReferenceSpaceType> types(n);
    if (n && XR_SUCCEEDED(xrEnumerateReferenceSpaces(g_session, n, &n, types.data())) &&
        std::find(types.begin(), types.end(), XR_REFERENCE_SPACE_TYPE_STAGE) != types.end()) {
        rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        if (Check(xrCreateReferenceSpace(g_session, &rs, &g_stage), "stage space")) Log("the runtime reports a floor (STAGE space)");
    } else {
        Log("the runtime has no STAGE space, so the floor height is unknown");
    }
    if (PickFormat()) SyncChains();
    Log("session created");
}

void RT_Submit() {
    Pending p;
    {
        std::lock_guard<std::mutex> l(g_queueLock);
        if (g_queue.empty()) return;
        p = g_queue.front();
        g_queue.pop_front();
    }
    XrFrameBeginInfo bi{XR_TYPE_FRAME_BEGIN_INFO};
    XrResult br = xrBeginFrame(g_session, &bi);
    if (XR_FAILED(br)) { Check(br, "xrBeginFrame"); return; }

    XrCompositionLayerProjection world{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    XrCompositionLayerProjectionView views[2]{{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
    XrCompositionLayerQuad ui{XR_TYPE_COMPOSITION_LAYER_QUAD};
    const XrCompositionLayerBaseHeader* layers[2];
    uint32_t layerCount = 0;

    Source eyes[2], uiSrc;
    Snapshot(eyes, uiSrc);
    if (p.shouldRender && p.viewsValid) {
        bool ok = true;
        for (int i = 0; i < 2 && ok; i++) {
            ok = CopyToChain(g_eyeChain[i], eyes[i]);
            views[i].pose = p.views[i].pose;
            views[i].fov = p.views[i].fov;
            views[i].subImage.swapchain = g_eyeChain[i].handle;
            views[i].subImage.imageRect = {{0, 0}, {g_eyeChain[i].w, g_eyeChain[i].h}};
            views[i].subImage.imageArrayIndex = 0;
        }
        if (ok) {
            world.space = g_local;
            world.viewCount = 2;
            world.views = views;
            layers[layerCount++] = (const XrCompositionLayerBaseHeader*)&world;
        }
    }
    // The first layer is the rearmost, so the UI always draws over the world.
    if (p.shouldRender && p.ui.visible && CopyToChain(g_uiChain, uiSrc)) {
        ui.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
        ui.space = g_local;
        ui.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
        ui.subImage.swapchain = g_uiChain.handle;
        ui.subImage.imageRect = {{0, 0}, {g_uiChain.w, g_uiChain.h}};
        ui.pose.position = {p.ui.pose.px, p.ui.pose.py, p.ui.pose.pz};
        ui.pose.orientation = {p.ui.pose.qx, p.ui.pose.qy, p.ui.pose.qz, p.ui.pose.qw};
        ui.size = {p.ui.width, p.ui.height};
        layers[layerCount++] = (const XrCompositionLayerBaseHeader*)&ui;
    }

    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = p.displayTime;
    ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    ei.layerCount = layerCount;
    ei.layers = layers;
    if (Check(xrEndFrame(g_session, &ei), "xrEndFrame")) g_framesSubmitted++;
}

void RT_Shutdown() {
    for (auto& c : g_eyeChain) DestroyChain(c);
    DestroyChain(g_uiChain);
    Hold(g_eyeSrc[0], nullptr, 0, 0);
    Hold(g_eyeSrc[1], nullptr, 0, 0);
    Hold(g_uiSrc, nullptr, 0, 0);
    DrainReleases();
    if (g_stage) { xrDestroySpace(g_stage); g_stage = XR_NULL_HANDLE; }
    if (g_local) { xrDestroySpace(g_local); g_local = XR_NULL_HANDLE; }
    if (g_session) { xrDestroySession(g_session); g_session = XR_NULL_HANDLE; }
    if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
    if (g_device) { g_device->Release(); g_device = nullptr; }
    if (g_instance) { xrDestroyInstance(g_instance); g_instance = XR_NULL_HANDLE; }
    {
        std::lock_guard<std::mutex> l(g_queueLock);
        g_queue.clear();
    }
    g_system = XR_NULL_SYSTEM_ID;
    g_format = 0;
    g_running = false;
    g_exitRequested = false;
    g_state = 0;
    Log("OpenXR shut down");
}

void OVR_Submit() {
    Quad q;
    {
        std::lock_guard<std::mutex> l(g_quadLock);
        q = g_ui;
    }
    Source eyes[2], uiSrc;
    Snapshot(eyes, uiSrc);
    ovr::Submit(eyes, uiSrc, q, nullptr);
    g_framesSubmitted++;
}

void OVR_Shutdown() {
    ovr::Shutdown();
    Hold(g_eyeSrc[0], nullptr, 0, 0);
    Hold(g_eyeSrc[1], nullptr, 0, 0);
    Hold(g_uiSrc, nullptr, 0, 0);
    DrainReleases();
    g_backend = 0;
}

void __stdcall OnRenderEvent(int id) {
    static DWORD lastThread = 0;
    if (GetCurrentThreadId() != lastThread) { lastThread = GetCurrentThreadId(); Log("render event %d on thread %lu", id, lastThread); }
    __try {
        DrainReleases();
        bool vr = g_backend == kOpenVR;
        switch (id) {
            case 1: if (!vr) RT_CreateSession(); break;
            case 2: if (vr) OVR_Submit(); else RT_Submit(); break;
            case 3: if (!vr) SyncChains(); break;
            case 4: if (vr) OVR_Shutdown(); else { RT_Shutdown(); g_backend = 0; } break;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("render event %d raised exception 0x%08X", id, GetExceptionCode());
    }
}

void PollEvents() {
    for (;;) {
        XrEventDataBuffer e{XR_TYPE_EVENT_DATA_BUFFER};
        if (xrPollEvent(g_instance, &e) != XR_SUCCESS) return;
        if (e.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* s = (XrEventDataSessionStateChanged*)&e;
            g_state = (int)s->state;
            Log("session state %d", (int)s->state);
            if (s->state == XR_SESSION_STATE_READY) {
                XrSessionBeginInfo b{XR_TYPE_SESSION_BEGIN_INFO};
                b.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                if (Check(xrBeginSession(g_session, &b), "xrBeginSession")) g_running = true;
            } else if (s->state == XR_SESSION_STATE_STOPPING) {
                g_running = false;
                Check(xrEndSession(g_session), "xrEndSession");
            } else if (s->state == XR_SESSION_STATE_EXITING || s->state == XR_SESSION_STATE_LOSS_PENDING) {
                g_running = false;
                g_exitRequested = true;
            }
        } else if (e.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
            g_running = false;
            g_exitRequested = true;
            Log("instance loss pending");
        }
    }
}

// Widens a view to a frustum that is symmetric about the eye's own forward axis and contains the original.
// The game's image effects (fog, ambient occlusion, volumetric light) rebuild view rays from the camera's
// field of view and aspect ratio alone, which only describe symmetric frustums. Rendering this wider view,
// and telling the runtime that is what was rendered, keeps every one of them correct per eye.
XrFovf Symmetric(const XrFovf& f) {
    float h = std::max(std::fabs(f.angleLeft), std::fabs(f.angleRight));
    float v = std::max(std::fabs(f.angleUp), std::fabs(f.angleDown));
    return {-h, h, v, -v};
}

}  // namespace

void bridge::Log(const char* fmt, ...) {
    char b[1024];
    va_list a; va_start(a, fmt); vsnprintf(b, sizeof b, fmt, a); va_end(a);
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_logs.size() < 512) g_logs.emplace_back(b);
}

// Returns 0 on success, 1 when no headset is available yet (worth retrying), 2 on a hard failure, or 3 when the
// OpenXR runtime itself can't be started (its manifest or DLL can't be loaded): worth trying another way instead.
API int DVR_Create(const char* runtimeJson, int backend) {
    Log("DVR_Create on thread %lu", GetCurrentThreadId());
    if (g_backend != 0) return 0;
    if (backend == kOpenVR) {
        int r = ovr::Create(g_runtimeName, sizeof g_runtimeName, g_systemName, sizeof g_systemName, &g_ovrRecW, &g_ovrRecH);
        if (r == 0) g_backend = kOpenVR;
        return r;
    }
    if (g_instance) return 0;
    // An empty manifest means the system's active runtime, so an override left from an earlier attempt goes.
    if (!runtimeJson || !runtimeJson[0]) SetEnvironmentVariableA("XR_RUNTIME_JSON", nullptr);
    if (runtimeJson && runtimeJson[0]) {
        SetEnvironmentVariableA("XR_RUNTIME_JSON", runtimeJson);
        Log("runtime override: %s", runtimeJson);
    }
    const char* exts[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ci.applicationInfo.applicationName, "Dear Esther VR");
    ci.applicationInfo.applicationVersion = 1;
    strcpy_s(ci.applicationInfo.engineName, "Unity 5.3");
    ci.applicationInfo.engineVersion = 53;
    ci.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ci.enabledExtensionCount = 1;
    ci.enabledExtensionNames = exts;
    XrResult r = xrCreateInstance(&ci, &g_instance);
    if (XR_FAILED(r)) {
        const char* what = r == XR_ERROR_FILE_ACCESS_ERROR ? "XR_ERROR_FILE_ACCESS_ERROR: the runtime's manifest or DLL couldn't be loaded (often a 64-bit-only runtime, or a leftover registration)"
                         : r == XR_ERROR_RUNTIME_UNAVAILABLE ? "XR_ERROR_RUNTIME_UNAVAILABLE: no OpenXR runtime is installed for 32-bit programs"
                         : r == XR_ERROR_FILE_CONTENTS_INVALID ? "XR_ERROR_FILE_CONTENTS_INVALID: the runtime's manifest is damaged"
                         : r == XR_ERROR_EXTENSION_NOT_PRESENT ? "XR_ERROR_EXTENSION_NOT_PRESENT: the runtime has no Direct3D 11 support"
                         : r == XR_ERROR_INSTANCE_LOST ? "XR_ERROR_INSTANCE_LOST" : "";
        Log("xrCreateInstance failed: %d %s", (int)r, what);
        g_instance = XR_NULL_HANDLE;
        return r == XR_ERROR_INSTANCE_LOST ? 1 : 3;
    }

    XrInstanceProperties ip{XR_TYPE_INSTANCE_PROPERTIES};
    if (XR_SUCCEEDED(xrGetInstanceProperties(g_instance, &ip))) {
        strcpy_s(g_runtimeName, ip.runtimeName);
        Log("runtime: %s %d.%d.%d", ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion));
    }

    XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO};
    si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    r = xrGetSystem(g_instance, &si, &g_system);
    if (XR_FAILED(r)) {
        Log("xrGetSystem failed: %d (is the headset connected?)", (int)r);
        xrDestroyInstance(g_instance);
        g_instance = XR_NULL_HANDLE;
        return r == XR_ERROR_FORM_FACTOR_UNAVAILABLE ? 1 : 2;
    }
    XrSystemProperties sp{XR_TYPE_SYSTEM_PROPERTIES};
    if (XR_SUCCEEDED(xrGetSystemProperties(g_instance, g_system, &sp))) {
        strcpy_s(g_systemName, sp.systemName);
        Log("headset: %s", sp.systemName);
    }
    uint32_t n = 2;
    if (!Check(xrEnumerateViewConfigurationViews(g_instance, g_system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &n, g_viewCfg), "view config")) return 2;
    Log("recommended eye size %ux%u", g_viewCfg[0].recommendedImageRectWidth, g_viewCfg[0].recommendedImageRectHeight);
    g_backend = kOpenXR;
    return 0;
}

API int DVR_Backend() { return g_backend.load(); }

API void DVR_GetRecommended(int* w, int* h) {
    if (g_backend == kOpenVR) { *w = g_ovrRecW; *h = g_ovrRecH; return; }
    *w = (int)g_viewCfg[0].recommendedImageRectWidth;
    *h = (int)g_viewCfg[0].recommendedImageRectHeight;
}

// Unity creates its render textures with the game's D3D11 device. Called on the main thread, followed by
// render event 1 (first time) or 3 (resize), which bring the swapchains to the same sizes.
API void DVR_SetEyeTextures(void* left, void* right, int w, int h) {
    Hold(g_eyeSrc[0], (ID3D11Texture2D*)left, w, h);
    Hold(g_eyeSrc[1], (ID3D11Texture2D*)right, w, h);
}

API void DVR_SetUiTexture(void* tex, int w, int h) { Hold(g_uiSrc, (ID3D11Texture2D*)tex, w, h); }

API void DVR_SetUiQuad(float px, float py, float pz, float qx, float qy, float qz, float qw, float width, float height, int visible) {
    std::lock_guard<std::mutex> l(g_quadLock);
    g_ui = {{px, py, pz, qx, qy, qz, qw}, width, height, visible};
}

API void DVR_SetSymmetric(int on) { g_symmetric = on != 0; }
API void DVR_SetDebug(int flags) {
    g_debug = flags;
    Log("debug flags %d", flags);
    if (flags & 16) {
        char p[MAX_PATH];
        GetEnvironmentVariableA("USERPROFILE", p, MAX_PATH);
        bridge::InstallCrashStacks((std::string(p) + "\\AppData\\LocalLow\\DearEstherVR\\lab\\crash_stacks.txt").c_str());
    }
}

// Paces the game to the headset and returns the eye poses and views predicted for this frame's display time.
API void DVR_BeginFrame(Frame* out) {
    *out = {};
    if (g_backend == kOpenVR) { ovr::BeginFrame(out, g_symmetric.load()); return; }
    if (!g_instance || !g_session) return;
    PollEvents();
    if (!g_running) return;

    XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO};
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    XrResult r = xrWaitFrame(g_session, &wi, &fs);
    if (XR_FAILED(r)) { Check(r, "xrWaitFrame"); return; }

    out->running = 1;
    out->shouldRender = fs.shouldRender ? 1 : 0;
    out->periodSeconds = (float)(fs.predictedDisplayPeriod / 1e9);

    XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
    li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    li.displayTime = fs.predictedDisplayTime;
    li.space = g_local;
    XrViewState vs{XR_TYPE_VIEW_STATE};
    XrView views[2]{{XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
    uint32_t n = 0;
    if (XR_SUCCEEDED(xrLocateViews(g_session, &li, &vs, 2, &n, views)) && (vs.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT)) {
        out->viewsValid = 1;
        out->positionTracked = (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) && (vs.viewStateFlags & XR_VIEW_STATE_POSITION_TRACKED_BIT) ? 1 : 0;
        for (int i = 0; i < 2; i++) {
            out->rawTangents[i][0] = std::tan(views[i].fov.angleLeft);
            out->rawTangents[i][1] = std::tan(views[i].fov.angleRight);
            out->rawTangents[i][2] = std::tan(views[i].fov.angleUp);
            out->rawTangents[i][3] = std::tan(views[i].fov.angleDown);
            if (g_symmetric) views[i].fov = Symmetric(views[i].fov);
            auto& p = views[i].pose;
            out->eye[i] = {p.position.x, p.position.y, p.position.z, p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w};
            out->tangents[i][0] = std::tan(views[i].fov.angleLeft);
            out->tangents[i][1] = std::tan(views[i].fov.angleRight);
            out->tangents[i][2] = std::tan(views[i].fov.angleUp);
            out->tangents[i][3] = std::tan(views[i].fov.angleDown);
        }
    }
    if (g_stage) {
        XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
        if (XR_SUCCEEDED(xrLocateSpace(g_stage, g_local, fs.predictedDisplayTime, &loc)) && (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT)) {
            out->floorY = loc.pose.position.y;
            out->floorValid = 1;
        }
    }
    Pending pending{fs.predictedDisplayTime, fs.shouldRender == XR_TRUE, out->viewsValid != 0};
    pending.views[0] = views[0];
    pending.views[1] = views[1];
    {
        std::lock_guard<std::mutex> l(g_quadLock);
        pending.ui = g_ui;
    }
    std::lock_guard<std::mutex> l(g_queueLock);
    // A frame that never reached the render thread is dropped rather than letting the queue grow.
    while (g_queue.size() > 2) g_queue.pop_front();
    g_queue.push_back(pending);
}

// Ends the session cleanly, for example when the game quits.
API void DVR_RequestExit() {
    if (g_backend == kOpenXR && g_session && g_running) Check(xrRequestExitSession(g_session), "xrRequestExitSession");
}

// Whether a process with this executable name is running. The game's 32-bit Mono can't read the names of
// 64-bit processes, such as SteamVR's vrserver.exe, so this is asked natively.
API int DVR_ProcessRunning(const char* exeName) {
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (s == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32 e{sizeof e};
    int found = 0;
    for (BOOL ok = Process32First(s, &e); ok && !found; ok = Process32Next(s, &e))
        if (!_stricmp(e.szExeFile, exeName)) found = 1;
    CloseHandle(s);
    return found;
}

API void* DVR_GetRenderEvent() { return (void*)OnRenderEvent; }
API int DVR_SessionState() { return g_state.load(); }
API int DVR_IsRunning() { return g_backend == kOpenVR ? (ovr::Running() ? 1 : 0) : (g_running.load() ? 1 : 0); }
API int DVR_HasSession() { return g_backend == kOpenVR ? (ovr::Running() ? 1 : 0) : (g_session ? 1 : 0); }
API int DVR_ExitRequested() { return g_backend == kOpenVR ? (ovr::ExitRequested() ? 1 : 0) : (g_exitRequested.load() ? 1 : 0); }
API unsigned long long DVR_FramesSubmitted() { return g_framesSubmitted.load(); }
API const char* DVR_RuntimeName() { return g_runtimeName; }
API const char* DVR_SystemName() { return g_systemName; }

API int DVR_PopLog(char* buffer, int size) {
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_logs.empty()) return 0;
    strncpy_s(buffer, size, g_logs.front().c_str(), _TRUNCATE);
    g_logs.pop_front();
    return 1;
}
