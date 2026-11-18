using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// How sharp the island stays at a distance in the headset.
    ///
    /// Measured on 2026-11-14 (tests\texture_quality.ps1): the game already forces anisotropic filtering at its
    /// top quality, and almost every large texture is trilinear at 16x; it has no LOD groups and no Unity
    /// terrains (the ground is "Vertex Blend" meshes). What makes distant textures blurrier in a headset is the
    /// mip level: an eye has about half the pixels per degree of a 4K screen, so the GPU picks a level one step
    /// blurrier at the same distance. A negative mip bias (Texture Sharpness) brings sharper levels back:
    /// -0.5 made the band below the horizon 16% sharper, -1 made it 24% sharper, with near detail unchanged.
    /// </summary>
    internal static class TextureQuality
    {
        static readonly Dictionary<Texture2D, float> _savedBias = new Dictionary<Texture2D, float>();
        static float _appliedBias;
        static float _nextScan;

        /// <summary>Called about once a second in VR: applies the mip bias, including to textures a new level
        /// brought in (checked every few seconds).</summary>
        public static void Apply()
        {
            float bias = Cfg.TextureSharpness.Value;
            bool changed = bias != _appliedBias;
            if (!changed && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 5f;
            _appliedBias = bias;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (t == null || t.mipmapCount <= 1) continue;
                float orig;
                if (!_savedBias.TryGetValue(t, out orig)) { orig = t.mipMapBias; _savedBias[t] = orig; }
                else if (!changed) continue;
                t.mipMapBias = orig + bias;
            }
        }

        public static void Restore()
        {
            foreach (var kv in _savedBias) if (kv.Key != null) kv.Key.mipMapBias = kv.Value;
            _savedBias.Clear();
            _appliedBias = 0f;
        }

        static readonly string[] Candidates = { "_DetailDistance", "_DetailFade", "_DetailFadeDistance", "_DetailStart", "_DetailEnd", "_DetailRange",
            "_FadeDistance", "_FadeStart", "_FadeEnd", "_DetailScale", "_DetailStrength", "_DetailAmount", "_DetailBlend", "_DetailTiling", "_Detail",
            "_DetailTex", "_DetailAlbedoMap", "_DetailNormalMap", "_DistanceFade", "_NearFade", "_FarFade", "_Distance", "_MaxDistance", "_MinDistance",
            "_BlendDistance", "_DetailFalloff", "_Falloff", "_Range", "_Tiling", "_MainTex", "_BumpMap", "_Detail2", "_DetailMask" };

        /// <summary>Lab: which shaders use a texture whose name contains <paramref name="part"/>, and which of the
        /// likely detail-fade properties they have.</summary>
        public static string Materials(string part)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    bool uses = false;
                    var props = new List<string>();
                    foreach (var p in Candidates)
                    {
                        if (!m.HasProperty(p)) continue;
                        Texture t = null;
                        try { t = m.GetTexture(p); } catch { }
                        if (t != null) { props.Add(p + "=tex:" + t.name + "@" + m.GetTextureScale(p)); if (t.name.Contains(part)) uses = true; }
                        else { try { props.Add(p + "=" + m.GetFloat(p).ToString("G4")); } catch { props.Add(p); } }
                    }
                    if (!uses && !m.shader.name.Contains(part)) continue;
                    sb.Append(m.name).Append(" [").Append(m.shader.name).Append("] ").Append(string.Join(" ", props.ToArray())).AppendLine();
                    if (sb.Length > 6000) return sb.ToString();
                }
            }
            return sb.ToString();
        }

        static readonly string[] ShineProps = { "_Shininess", "_SpecColor", "_Gloss", "_Glossiness", "_Specular", "_SpecularPower", "_SpecPower", "_SpecInt",
            "_RimColor", "_RimPower", "_RimStrength", "_Rim", "_Fresnel", "_FresnelPower", "_Cube", "_ReflectColor", "_Reflection", "_ReflectionStrength",
            "_ReflectionAmount", "_Wet", "_Wetness", "_Sheen", "_EnvMap", "_EnvStrength", "_SpecMap", "_SpecTex", "_Noise", "_NoiseTex", "_Dither", "_Grain" };

        /// <summary>Lab: per shader in view, which shine/reflection/noise properties its materials have, with one
        /// example value each.</summary>
        public static string Shine(float radius)
        {
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var byShader = new Dictionary<string, string>();
            var counts = new Dictionary<string, int>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.isVisible || r.bounds.SqrDistance(cam.transform.position) > radius * radius) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var key = m.shader.name;
                    counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                    if (byShader.ContainsKey(key)) continue;
                    var props = new List<string>();
                    foreach (var p in ShineProps)
                    {
                        if (!m.HasProperty(p)) continue;
                        string v;
                        try { var t = m.GetTexture(p); v = t != null ? "tex:" + t.name : null; } catch { v = null; }
                        if (v == null) { try { v = m.GetColor(p).ToString(); } catch { try { v = m.GetFloat(p).ToString("G4"); } catch { v = "?"; } } }
                        props.Add(p + "=" + v);
                    }
                    byShader[key] = m.name + ": " + string.Join(" ", props.ToArray());
                }
            }
            var sb = new StringBuilder();
            foreach (var kv in byShader) sb.Append(kv.Key).Append(" x").Append(counts[kv.Key]).Append("  ").Append(kv.Value).AppendLine();
            return sb.ToString();
        }

        /// <summary>Lab: materials on renderers within <paramref name="radius"/> of the main camera.</summary>
        public static string Near(float radius)
        {
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var sb = new StringBuilder();
            var seen = new HashSet<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.isVisible || r.bounds.SqrDistance(cam.transform.position) > radius * radius) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    var tex = m.HasProperty("_MainTex") ? m.mainTexture as Texture2D : null;
                    sb.Append(m.name).Append(" [").Append(m.shader.name).Append("] on ").Append(r.name);
                    if (tex != null) sb.Append(" main=").Append(tex.name).Append(' ').Append(tex.width).Append('x').Append(tex.height).Append(" tiling=").Append(m.mainTextureScale).Append(" mips=").Append(tex.mipmapCount);
                    sb.AppendLine();
                    if (sb.Length > 7000) return sb.ToString();
                }
            }
            return sb.ToString();
        }

        /// <summary>A report of what the game uses, for the lab channel.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("anisoSetting=").Append(QualitySettings.anisotropicFiltering)
              .Append(" lodBias=").Append(QualitySettings.lodBias)
              .Append(" maxLOD=").Append(QualitySettings.maximumLODLevel)
              .Append(" texLimit=").Append(QualitySettings.masterTextureLimit).AppendLine();
            var byKind = new Dictionary<string, int>();
            int total = 0;
            float biasSum = 0;
            var odd = new List<string>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (t == null || t.width < 256 || t.mipmapCount <= 1) continue;
                total++;
                biasSum += t.mipMapBias;
                string k = t.filterMode + "/aniso" + t.anisoLevel;
                byKind[k] = byKind.ContainsKey(k) ? byKind[k] + 1 : 1;
                if ((t.filterMode == FilterMode.Bilinear || t.anisoLevel < 2) && odd.Count < 30) odd.Add(t.name + "(" + t.width + "," + t.filterMode + ",a" + t.anisoLevel + ")");
            }
            sb.Append("mipmapped textures >=256px: ").Append(total).Append(", mean mip bias ").Append(total > 0 ? (biasSum / total).ToString("F2") : "-").AppendLine();
            foreach (var kv in byKind) sb.Append("  ").Append(kv.Key).Append(" x").Append(kv.Value).AppendLine();
            sb.Append("  not trilinear 16x: ").Append(string.Join(", ", odd.ToArray())).AppendLine();
            int lods = 0, lodLevels = 0;
            foreach (var g in Object.FindObjectsOfType<LODGroup>()) { lods++; lodLevels += g.lodCount; }
            sb.Append("LOD groups ").Append(lods).Append(" with ").Append(lodLevels).Append(" levels").AppendLine();
            foreach (var tr in Object.FindObjectsOfType<Terrain>())
                sb.Append("terrain ").Append(tr.name).Append(" basemapDistance=").Append(tr.basemapDistance).AppendLine();
            return sb.ToString();
        }
    }
}
