using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.UI;

namespace DearEstherVR
{
    /// <summary>
    /// The colour drawn over both eyes this frame.
    ///
    /// The game's fades are PlayMaker actions (CameraFadeIn, CameraFadeOut, DrawFullscreenColor) drawn with
    /// OnGUI, which only ever reaches the desktop window. Their current colour is read here every frame so the
    /// headset gets the same fade.
    ///
    /// The loading screen ("Loading Screen Canvas" under the game's persistent "Curve" object) is never shown on
    /// the VR panel. While it, or a level load, is showing, the eyes are simply black: loads take a moment on a
    /// modern PC, and a black view is calmer than a floating loading card.
    /// </summary>
    internal static class Fade
    {
        static readonly FieldInfo OutLerp = typeof(CameraFadeOut).GetField("colorLerp", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo InLerp = typeof(CameraFadeIn).GetField("colorLerp", BindingFlags.Instance | BindingFlags.NonPublic);

        static Canvas _loading;
        static bool _loadingWasEnabled, _loadingHidden;
        public static string Source = "";
        /// <summary>Lab only: a fade to draw instead of the game's.</summary>
        public static Color? LabFade;

        static bool GameLoading
        {
            get
            {
                if (Application.isLoadingLevel) return true;
                try { var c = GameObject.Find("Curve"); return c != null && c.GetComponentInChildren<Curve.Loader>() != null && Curve.Loader.LoadingLevel; }
                catch { return false; }
            }
        }

        /// <summary>Finds the loading canvas (it lives on the persistent Curve object) and keeps it off the panel.</summary>
        public static void HideLoadingCanvas()
        {
            if (_loading == null)
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                    if (c != null && c.name == "Loading Screen Canvas" && c.gameObject.scene.IsValid()) { _loading = c; _loadingHidden = false; break; }
                if (_loading == null) return;
            }
            if (!_loadingHidden) { _loadingWasEnabled = _loading.enabled; _loadingHidden = true; }
            _loading.enabled = false;
        }

        public static void RestoreLoadingCanvas()
        {
            if (_loading != null && _loadingHidden) _loading.enabled = _loadingWasEnabled;
            _loadingHidden = false;
        }

        /// <summary>Whether the loading screen's contents are showing (its canvas itself is kept switched off).</summary>
        public static bool LoadingVisible
        {
            get
            {
                if (_loading == null || !_loading.gameObject.activeInHierarchy) return false;
                foreach (var g in _loading.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.enabled || g.color.a <= 0.01f) continue;
                    float a = g.color.a;
                    var group = g.GetComponentInParent<CanvasGroup>();
                    if (group != null) a *= group.alpha;
                    if (a > 0.01f) return true;
                }
                return false;
            }
        }

        /// <summary>The fade colour for this frame; alpha 0 means no fade.</summary>
        public static Color Current()
        {
            Source = "";
            if (LabFade.HasValue) { Source = "lab"; return LabFade.Value; }
            if (GameLoading) { Source = "level load"; return Color.black; }
            if (LoadingVisible) { Source = "loading screen"; return Color.black; }
            var best = Color.clear;
            var list = PlayMakerFSM.FsmList;
            for (int i = 0; i < list.Count; i++)
            {
                var pm = list[i];
                if (pm == null || !pm.enabled || pm.Fsm == null || !pm.Fsm.Active) continue;
                var state = pm.Fsm.ActiveState;
                if (state == null) continue;
                var actions = state.ActiveActions;
                for (int k = 0; k < actions.Count; k++)
                {
                    var a = actions[k];
                    Color c;
                    if (a is CameraFadeOut && OutLerp != null) c = (Color)OutLerp.GetValue(a);
                    else if (a is CameraFadeIn && InLerp != null) c = (Color)InLerp.GetValue(a);
                    else if (a is DrawFullscreenColor) c = ((DrawFullscreenColor)a).color.Value;
                    else continue;
                    if (c.a > best.a) { best = c; Source = pm.name + "/" + state.Name + "/" + a.GetType().Name; }
                }
            }
            return best;
        }

        static Material _mat;

        /// <summary>Blends the fade colour over a whole render texture.</summary>
        public static void Draw(RenderTexture rt, Color c)
        {
            if (c.a <= 0.002f || rt == null) return;
            if (_mat == null)
            {
                var sh = Shader.Find("UI/Default");
                if (sh == null) { Log.Error("UI/Default shader missing, fades can't be drawn"); return; }
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                // UI/Default clips to _ClipRect (empty unless a UI mask sets it) and tests depth by unity_GUIZTestMode.
                _mat.SetVector("_ClipRect", new Vector4(-1e6f, -1e6f, 1e6f, 1e6f));
                _mat.SetFloat("unity_GUIZTestMode", 8f);
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadOrtho();
            _mat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(0, 0, 0); GL.Vertex3(0, 1, 0); GL.Vertex3(1, 1, 0); GL.Vertex3(1, 0, 0);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }
    }
}
