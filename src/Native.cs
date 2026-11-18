using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DearEstherVR
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XrPose { public float px, py, pz, qx, qy, qz, qw; }

    /// <summary>Matches Frame in DEVRNative.cpp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XrFrame
    {
        public int running, shouldRender, viewsValid, pad;
        public XrPose left, right;
        // Tangents of each eye's view edges: left, right, up, down.
        public float leftL, leftR, leftU, leftD, rightL, rightR, rightU, rightD;
        public float periodSeconds;
        // The headset's own views, before the widening to a symmetric frustum.
        public float rawLeftL, rawLeftR, rawLeftU, rawLeftD, rawRightL, rawRightR, rawRightU, rawRightD;
        // Height of the real floor in the tracking space, when the runtime knows it.
        public float floorY;
        public int floorValid;
        // The head position is really tracked (not a placeholder from before tracking started).
        public int positionTracked;
    }

    /// <summary>
    /// The native bridge. Unity 5.3's Mono only looks for [DllImport] libraries in its own folders, so the bridge
    /// is loaded from the mod folder by hand and each export is bound through a function pointer.
    /// </summary>
    internal static class Native
    {
        const string Dll = "DEVRNative";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int CreateFn(string runtimeJson, int backend);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SizeFn(out int w, out int h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void EyeTexturesFn(IntPtr left, IntPtr right, int w, int h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void UiTextureFn(IntPtr tex, int w, int h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void UiQuadFn(float px, float py, float pz, float qx, float qy, float qz, float qw, float width, float height, int visible);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntFn(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FrameFn(out XrFrame frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetIntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int NameFn(string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate ulong GetULongFn();
        // A byte array, not a StringBuilder: this Mono doesn't copy a StringBuilder back through a function pointer.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int PopLogFn([Out] byte[] buffer, int size);

        public static CreateFn DVR_Create;
        public static SizeFn DVR_GetRecommended;
        public static EyeTexturesFn DVR_SetEyeTextures;
        public static UiTextureFn DVR_SetUiTexture;
        public static UiQuadFn DVR_SetUiQuad;
        public static IntFn DVR_SetSymmetric, DVR_SetDebug;
        public static FrameFn DVR_BeginFrame;
        public static VoidFn DVR_RequestExit;
        public static PtrFn DVR_GetRenderEvent, DVR_RuntimeName, DVR_SystemName;
        public static GetIntFn DVR_SessionState, DVR_HasSession, DVR_ExitRequested, DVR_Backend;
        public static GetULongFn DVR_FramesSubmitted;
        public static NameFn DVR_ProcessRunning;

        /// <summary>Whether SteamVR's server is running. Mono's own process list can't see 64-bit processes.</summary>
        public static bool SteamVrRunning => DVR_ProcessRunning != null && DVR_ProcessRunning("vrserver.exe") != 0;
        static PopLogFn DVR_PopLog;

        /// <summary>Which API reaches the headset. Keep in sync with DEVRNative.cpp.</summary>
        public const int BackendOpenXR = 1, BackendOpenVR = 2;

        static IntPtr _module;

        static T Bind<T>(string name) where T : class
        {
            var p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>Loads the bridge, with its OpenXR and OpenVR loaders, from the mod folder.</summary>
        public static bool Load()
        {
            const uint LOAD_WITH_ALTERED_SEARCH_PATH = 8;
            var path = Path.Combine(Log.Dir, Dll + ".dll");
            _module = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (_module == IntPtr.Zero)
            {
                Log.Error($"Could not load {path} (Windows error {Marshal.GetLastWin32Error()}). " +
                          "If Smart App Control is on, it can block unsigned mod DLLs.");
                return false;
            }
            try
            {
                DVR_Create = Bind<CreateFn>("DVR_Create");
                DVR_GetRecommended = Bind<SizeFn>("DVR_GetRecommended");
                DVR_SetEyeTextures = Bind<EyeTexturesFn>("DVR_SetEyeTextures");
                DVR_SetUiTexture = Bind<UiTextureFn>("DVR_SetUiTexture");
                DVR_SetUiQuad = Bind<UiQuadFn>("DVR_SetUiQuad");
                DVR_SetSymmetric = Bind<IntFn>("DVR_SetSymmetric");
                DVR_SetDebug = Bind<IntFn>("DVR_SetDebug");
                DVR_BeginFrame = Bind<FrameFn>("DVR_BeginFrame");
                DVR_RequestExit = Bind<VoidFn>("DVR_RequestExit");
                DVR_GetRenderEvent = Bind<PtrFn>("DVR_GetRenderEvent");
                DVR_RuntimeName = Bind<PtrFn>("DVR_RuntimeName");
                DVR_SystemName = Bind<PtrFn>("DVR_SystemName");
                DVR_SessionState = Bind<GetIntFn>("DVR_SessionState");
                DVR_HasSession = Bind<GetIntFn>("DVR_HasSession");
                DVR_ExitRequested = Bind<GetIntFn>("DVR_ExitRequested");
                DVR_Backend = Bind<GetIntFn>("DVR_Backend");
                DVR_FramesSubmitted = Bind<GetULongFn>("DVR_FramesSubmitted");
                DVR_ProcessRunning = Bind<NameFn>("DVR_ProcessRunning");
                DVR_PopLog = Bind<PopLogFn>("DVR_PopLog");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("The native bridge doesn't match this version of the mod: " + e.Message);
                return false;
            }
        }

        public static string RuntimeName => Marshal.PtrToStringAnsi(DVR_RuntimeName()) ?? "";
        public static string SystemName => Marshal.PtrToStringAnsi(DVR_SystemName()) ?? "";

        public static void PumpLogs()
        {
            var buffer = new byte[1024];
            while (DVR_PopLog(buffer, buffer.Length) != 0)
            {
                int n = Array.IndexOf(buffer, (byte)0);
                Log.Info("[native] " + Encoding.UTF8.GetString(buffer, 0, n < 0 ? buffer.Length : n));
            }
        }
    }
}
