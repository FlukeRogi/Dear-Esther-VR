using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// The underwater distortion in Esther's chapter: the RefractiveOverlay image effect on the player camera,
    /// whose strength the game animates through the _OverlayAlpha of the CameraOverlay quad's material.
    ///
    /// Measured 2026-11-14 (tests\run_tests.ps1, Esther): the effect magnifies the picture as well as warping it,
    /// by 15% at the game's _RefractionAmount (0.09), 6% at half, none at 0. In a headset that zoom is applied
    /// about each eye's own image centre, which also pulls the two images apart. The shader ties zoom and wobble
    /// to the same _RefractionAmount, so the Underwater Distortion option scales that value (in VR only).
    /// </summary>
    internal static class Underwater
    {
        static readonly Dictionary<Material, float> _original = new Dictionary<Material, float>();

        /// <summary>Called about once a second in VR.</summary>
        public static void Apply()
        {
            float k = Cfg.UnderwaterDistortion.Value;
            foreach (var ro in Object.FindObjectsOfType<RefractiveOverlay>())
            {
                var m = ro.material;
                if (m == null || !m.HasProperty("_RefractionAmount")) continue;
                float orig;
                if (!_original.TryGetValue(m, out orig)) { orig = m.GetFloat("_RefractionAmount"); _original[m] = orig; }
                float want = orig * k;
                if (m.GetFloat("_RefractionAmount") != want) m.SetFloat("_RefractionAmount", want);
            }
        }

        public static void Restore()
        {
            foreach (var kv in _original) if (kv.Key != null) kv.Key.SetFloat("_RefractionAmount", kv.Value);
            _original.Clear();
        }

        static readonly string[] Probe = { "_OverlayAlpha", "_RefractionAmount", "_MainTex", "_BumpMap" };

        static string Props(Material m)
        {
            if (m == null) return "none";
            var sb = new StringBuilder(m.name + " [" + m.shader.name + "]");
            foreach (var p in Probe)
            {
                if (!m.HasProperty(p)) continue;
                string v;
                try { var t = m.GetTexture(p); v = t != null ? "tex:" + t.name : null; } catch { v = null; }
                if (v == null) { try { v = m.GetFloat(p).ToString("G4"); } catch { v = "?"; } }
                sb.Append(' ').Append(p).Append('=').Append(v);
            }
            return sb.ToString();
        }

        /// <summary>Lab: "refract" reports; "refract a" forces the overlay strength (the game's _OverlayAlpha);
        /// "refract amount x" sets the effect's _RefractionAmount directly.</summary>
        public static string Lab(string arg)
        {
            RefractiveOverlay ro = null;
            foreach (var x in Object.FindObjectsOfType<RefractiveOverlay>()) if (x.gameObject.activeInHierarchy) { ro = x; break; }
            if (ro == null) return "no active RefractiveOverlay (main camera is " + (Camera.main != null ? Camera.main.name : "none") + ")";
            var overlay = ro.transform.Find("CameraOverlay");
            var r = overlay != null ? overlay.GetComponent<Renderer>() : null;
            if (arg.StartsWith("amount "))
            {
                ro.material.SetFloat("_RefractionAmount", float.Parse(arg.Substring(7), System.Globalization.CultureInfo.InvariantCulture));
                arg = "";
            }
            if (arg.Length > 0 && r != null)
            {
                float a = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                ro.enabled = true;
                overlay.gameObject.SetActive(a > 0f);
                r.sharedMaterial.SetFloat("_OverlayAlpha", a);
            }
            return "enabled=" + ro.enabled + " amount=" + ro.amount + " effect: " + Props(ro.material) + " | overlay: " + (r != null ? Props(r.sharedMaterial) + " active=" + overlay.gameObject.activeInHierarchy : "none");
        }
    }
}
