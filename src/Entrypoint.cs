namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop calls this before the engine has started, so nothing Unity related may run yet.
    /// Unity 5.3 has no SceneManager.sceneLoaded event, so the mod boots from the first camera cull instead:
    /// Camera.onPreCull is a plain static delegate field, setting it touches no engine code, and the engine
    /// fires it on the main thread once rendering is running.
    /// </summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            DearEstherVR.Log.Early("Doorstop entry point reached");
            try { DearEstherVR.Boot.Arm(); }
            catch (System.Exception e) { DearEstherVR.Log.Early("Boot failed: " + e); }
        }
    }
}

namespace DearEstherVR
{
    using UnityEngine;

    internal static class Boot
    {
        static bool _started;

        public static void Arm() => Camera.onPreCull += OnFirstCull;

        static void OnFirstCull(Camera cam)
        {
            if (_started) return;
            _started = true;
            Camera.onPreCull -= OnFirstCull;
            Log.Early("First camera cull: " + cam.name + " in level " + Application.loadedLevelName);
            try { Mod.Start(); }
            catch (System.Exception e) { Log.Error("Start failed: " + e); }
        }
    }
}
