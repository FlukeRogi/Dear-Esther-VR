using System.Collections.Generic;
using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// Shadow Depth: makes dark areas darker without dimming bright ones, in VR only.
    ///
    /// Unity 5.3 can't compile a new shader at run time, so this reuses the game's own colour grading, Amplify
    /// Color: a second AmplifyColorEffect is added to each eye camera, after the game's own effects, with a
    /// lookup table made here. The table applies a curve to brightness only: values above the knee (mid-grey,
    /// as the game's gamma-space image stores it) are unchanged, values below it are pulled down along a smooth
    /// curve that starts at black with a gentler slope and meets the straight line at the knee. Colours keep
    /// their hue, because red, green and blue are scaled together.
    /// </summary>
    internal static class ShadowDepth
    {
        const float Knee = 0.5f;
        static readonly float[] StartSlope = { 1f, 0.6f, 0.4f, 0.25f };  // Off, Low, Medium, High
        static Texture2D _lut;
        static int _lutLevel = -1;
        static readonly List<AmplifyColorEffect> _added = new List<AmplifyColorEffect>();

        public static int Level => Mathf.Clamp(Mathf.RoundToInt(Cfg.ShadowDepth.Value), 0, 3);

        /// <summary>The curve, for one brightness value in 0..1.</summary>
        public static float Curve(float y, int level)
        {
            if (y >= Knee || level <= 0) return y;
            float s0 = StartSlope[level], t = y / Knee, t2 = t * t, t3 = t2 * t;
            // Cubic Hermite from (0, 0) with slope s0 to (Knee, Knee) with slope 1.
            float h10 = t3 - 2f * t2 + t, h01 = -2f * t3 + 3f * t2, h11 = t3 - t2;
            return Knee * (h10 * s0 + h01 + h11);
        }

        static Texture2D Lut(int level)
        {
            if (_lut != null && _lutLevel == level) return _lut;
            if (_lut == null)
                _lut = new Texture2D(1024, 32, TextureFormat.RGB24, false, true) { name = "DEVR Shadow Depth LUT", hideFlags = HideFlags.HideAndDontSave, anisoLevel = 1, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[1024 * 32];
            // Amplify Color's layout (AmplifyColorBase.CreateDefaultLut): x = blue * 32 + red, y = green.
            for (int b = 0; b < 32; b++)
                for (int g = 0; g < 32; g++)
                    for (int r = 0; r < 32; r++)
                    {
                        float rf = r / 31f, gf = g / 31f, bf = b / 31f;
                        float y = Mathf.Max(rf, Mathf.Max(gf, bf));
                        float k = y > 0.0001f ? Curve(y, level) / y : 1f;
                        px[b * 32 + g * 1024 + r] = new Color32((byte)Mathf.RoundToInt(rf * k * 255f), (byte)Mathf.RoundToInt(gf * k * 255f), (byte)Mathf.RoundToInt(bf * k * 255f), 255);
                    }
            _lut.SetPixels32(px);
            _lut.Apply(false);
            _lutLevel = level;
            return _lut;
        }

        /// <summary>Before the eyes render: every camera that will render gets the grading pass, on or off.</summary>
        public static void Prepare(List<Camera> cams)
        {
            int level = Level;
            foreach (var cam in cams)
            {
                if (cam == null) continue;
                AmplifyColorEffect ours = null;
                foreach (var e in cam.GetComponents<AmplifyColorEffect>()) if (_added.Contains(e)) ours = e;
                if (ours == null)
                {
                    if (level == 0) continue;
                    ours = cam.gameObject.AddComponent<AmplifyColorEffect>();
                    ours.UseVolumes = false;
                    _added.Add(ours);
                    Log.Info("Shadow Depth added to " + cam.name);
                }
                ours.enabled = level > 0;
                if (level > 0) ours.LutTexture = Lut(level);
            }
        }

        /// <summary>When VR stops: the flat game never has it.</summary>
        public static void Remove()
        {
            foreach (var e in _added) if (e != null) Object.Destroy(e);
            _added.Clear();
        }
    }
}
