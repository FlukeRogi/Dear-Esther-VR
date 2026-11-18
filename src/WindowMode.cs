using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// Keeps the game windowed while VR runs. In VR the desktop window is rarely focused, and an unfocused
    /// fullscreen Unity window can lose its swapchain or stall on a mode change. The game's own fullscreen
    /// preference is left untouched, so the flat game comes back as it was.
    /// </summary>
    internal static class WindowMode
    {
        static bool _logged;

        public static void Poll()
        {
            if (!Screen.fullScreen) return;
            if (!_logged) { Log.Info("Switching the game to a window for VR"); _logged = true; }
            Screen.SetResolution(1280, 720, false);
        }
    }
}
