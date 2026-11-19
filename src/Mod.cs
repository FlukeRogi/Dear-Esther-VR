using UnityEngine;

namespace DearEstherVR
{
    internal static class Mod
    {
        public const string Version = "1.0.1";

        public static void Start()
        {
            Log.Info($"Dear Esther VR {Version} starting, Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceVersion}");
            Cfg.Load();
            if (!Cfg.VrDisabled)
            {
                // Starting SteamVR opens its own windows, which take focus, and Unity pauses an unfocused game.
                Application.runInBackground = true;
            }
            var go = new GameObject("DearEstherVR");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            go.AddComponent<XrRig>();
            go.AddComponent<MenuHook>();
            if (Cfg.LabCommands.Value)
            {
                Log.Warn("Developer test mode is on");
                go.AddComponent<Lab>();
            }
        }
    }
}
