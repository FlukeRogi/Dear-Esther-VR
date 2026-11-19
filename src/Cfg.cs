using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace DearEstherVR
{
    /// <summary>One setting in DearEstherVR\DearEstherVR.cfg.</summary>
    internal abstract class Setting
    {
        public string Section, Key, Description;
        public abstract string Text { get; }
        public abstract void Parse(string text);
        public abstract void Reset();
    }

    internal sealed class FloatSetting : Setting
    {
        public float Default, Min, Max, _value;
        public float Value { get => _value; set { var v = Math.Min(Max, Math.Max(Min, value)); if (v == _value) return; _value = v; Cfg.Changed(); } }
        public override string Text => _value.ToString("0.###", CultureInfo.InvariantCulture);
        public override void Parse(string text) { if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _value = Math.Min(Max, Math.Max(Min, v)); }
        public override void Reset() => Value = Default;
    }

    internal sealed class BoolSetting : Setting
    {
        public bool Default, _value;
        public bool Value { get => _value; set { if (value == _value) return; _value = value; Cfg.Changed(); } }
        public override string Text => _value ? "true" : "false";
        public override void Parse(string text) { if (bool.TryParse(text.Trim(), out var v)) _value = v; }
        public override void Reset() => Value = Default;
    }

    internal sealed class StringSetting : Setting
    {
        public string Default, _value;
        public string Value { get => _value; set { if (value == _value) return; _value = value; Cfg.Changed(); } }
        public override string Text => _value;
        public override void Parse(string text) => _value = text.Trim();
        public override void Reset() => Value = Default;
    }

    /// <summary>
    /// Persistent settings, in DearEstherVR\DearEstherVR.cfg. A plain text file, written whenever a setting changes, so it
    /// can also be edited by hand while the game is closed.
    /// </summary>
    internal static class Cfg
    {
        static readonly List<Setting> All = new List<Setting>();
        static readonly string FilePath = Path.Combine(Log.Dir, "DearEstherVR.cfg");
        static bool _loading, _dirty;

        public static readonly StringSetting Runtime = Str("OpenXR", "Runtime", "auto",
            "How to reach the headset: auto (SteamVR if it is running, otherwise the active OpenXR runtime), steamvr, vdxr (Virtual Desktop), openxr (the active OpenXR runtime), off (play flat), or a full path to an OpenXR runtime JSON manifest.");
        public static readonly StringSetting ApiLayersMode = Str("OpenXR", "ApiLayers", "auto",
            "OpenXR API layers (add-ons such as overlays and toolkits) for this game only: auto switches off any that can't load in a 32-bit game, which otherwise stop VR starting (error -32), and all of them if VR still can't start; off switches all of them off; on leaves them all alone.");
        public static readonly FloatSetting WorldScale = Num("View", "WorldScale", 1f, 0.25f, 4f,
            "How large the world feels. 1 treats one game unit as a metre. Higher makes the world bigger and you smaller.");
        public static readonly StringSetting HeightMode = Str("View", "HeightMode", "standing",
            "standing: your real height above your real floor becomes your height in the game (needs a headset that knows where the floor is; falls back to seated). seated: recentering puts your eyes at the game's own eye height.");
        public static readonly FloatSetting HeightOffset = Num("View", "HeightOffset", 0f, -1f, 1f,
            "Raises or lowers your viewpoint, in metres, on top of the height mode.");
        public static readonly BoolSetting HeadBob = Bool("View", "HeadBob", false,
            "The game's head bob and sway while walking. Off by default, as it is uncomfortable in a headset.");
        public static readonly StringSetting WalkDirection = Str("Movement", "WalkDirection", "head",
            "Which way forward on the stick or keys walks: head (where you look) or body (the direction you last turned to with the stick or mouse).");
        public static readonly StringSetting Turning = Str("Movement", "Turning", "smooth",
            "Right stick turning: smooth, or snap (turns by SnapAngle at a time). The mouse always turns smoothly.");
        public static readonly FloatSetting SnapAngle = Num("Movement", "SnapAngle", 30f, 10f, 90f,
            "Degrees per snap turn.");
        public static readonly FloatSetting ResolutionScale = Num("Rendering", "ResolutionScale", 1f, 0.5f, 2f,
            "Scales the per-eye render resolution the headset recommends.");
        public static readonly FloatSetting TextureSharpness = Num("Rendering", "TextureSharpness", -0.5f, -1.5f, 0f,
            "Mip bias for every texture. A headset eye has about half the pixels per degree of a 4K screen, so textures drop to their blurrier levels sooner; below 0 keeps sharper levels further away (-1 is about the flat game's sharpness on a 4K screen), at the cost of some shimmer. 0 is the game's own.");
        public static readonly FloatSetting ShadowDepth = Num("Rendering", "ShadowDepth", 2f, 0f, 3f,
            "Makes dark areas darker without dimming bright ones: 0 off (the game's own), 1 low, 2 medium, 3 high. Brightness above mid-grey is never changed.");
        public static readonly FloatSetting UnderwaterDistortion = Num("Rendering", "UnderwaterDistortion", 0.25f, 0f, 1f,
            "Strength of the game's underwater warp (Esther's chapter). The game's effect also magnifies the view by about 15%; 0 gives the normal view with no warp, 0.25 a gentle warp with a small zoom, 1 the game's own.");
        public static readonly FloatSetting Antialiasing = Num("Rendering", "Antialiasing", 0f, 0f, 8f,
            "Eye anti-aliasing (MSAA samples): 0 follows the game's own setting (4x at its top quality), or 2, 4 or 8.");
        public static readonly BoolSetting SymmetricEyes = Bool("Rendering", "SymmetricEyes", false,
            "Render each eye with a symmetric frustum that contains the headset's own (and more pixels). Off renders exactly the headset's asymmetric views. Only for comparison and troubleshooting.");
        public static readonly BoolSetting DoubleVision = Bool("Rendering", "DoubleVision", false,
            "The game's screen-space double-vision effect. In a headset it reads as broken stereo, so it is off by default.");
        public static readonly FloatSetting HudDistance = Num("Interface", "HudDistance", 1.6f, 0.5f, 5f,
            "Distance to the game's interface panel during play, in metres.");
        public static readonly FloatSetting HudSize = Num("Interface", "HudSize", 1.0f, 0.4f, 2f,
            "Size of the interface panel during play. 1 matches the game's field of view.");
        public static readonly FloatSetting MenuDistance = Num("Interface", "MenuDistance", 1.4f, 0.5f, 5f,
            "Distance to the menu panel, in metres. Menus open in front of wherever you are looking.");
        public static readonly FloatSetting MenuSize = Num("Interface", "MenuSize", 0.8f, 0.4f, 2f,
            "Size of the menu panel. 1 matches the game's field of view.");
        public static readonly FloatSetting MsaaOverride = Num("Debug", "Msaa", 0f, 0f, 8f,
            "Eye MSAA samples. 0 follows the game's own anti-aliasing setting.");
        public static readonly BoolSetting DrawCopy = Bool("Debug", "DrawCopy", true,
            "Copy eye images with a flipped GUI draw (normal). Off uses a plain Blit, for troubleshooting only.");
        public static readonly StringSetting TestActiveRuntime = Str("Debug", "TestActiveRuntime", "",
            "Testing only: stands in for the registered active 32-bit OpenXR runtime. Leave empty.");
        public static readonly StringSetting TestVdxrRuntime = Str("Debug", "TestVdxrRuntime", "",
            "Testing only: stands in for VDXR's registered manifest. Leave empty.");
        public static readonly StringSetting Skip = Str("Debug", "Skip", "",
            "Troubleshooting only: leave out parts of the frame (submit, eyes, ui).");
        public static readonly BoolSetting LabCommands = Bool("Debug", "LabCommands", false,
            "Developer test automation, driven by a command file. Leave off.");
        static FloatSetting Num(string s, string k, float d, float min, float max, string desc) { var x = new FloatSetting { Section = s, Key = k, Default = d, Min = min, Max = max, _value = d, Description = desc }; All.Add(x); return x; }
        static BoolSetting Bool(string s, string k, bool d, string desc) { var x = new BoolSetting { Section = s, Key = k, Default = d, _value = d, Description = desc }; All.Add(x); return x; }
        static StringSetting Str(string s, string k, string d, string desc) { var x = new StringSetting { Section = s, Key = k, Default = d, _value = d, Description = desc }; All.Add(x); return x; }

        public static event Action OnChanged;

        public static void Load()
        {
            _loading = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    string section = "";
                    foreach (var raw in File.ReadAllLines(FilePath))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        if (line[0] == '[' && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        var key = line.Substring(0, eq).Trim();
                        var setting = All.FirstOrDefault(x => x.Section == section && x.Key == key);
                        setting?.Parse(line.Substring(eq + 1));
                    }
                }
            }
            catch (Exception e) { Log.Warn("Could not read the settings: " + e.Message); }
            finally { _loading = false; }
            Save();
        }

        internal static void Changed()
        {
            if (_loading) return;
            _dirty = true;
            OnChanged?.Invoke();
        }

        /// <summary>Called every frame. Writes the file at most once per frame, however many settings changed.</summary>
        public static void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            Save();
        }

        public static void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Dear Esther VR settings. Most of these can also be changed in the game, under Options > VR.");
            foreach (var group in All.GroupBy(x => x.Section))
            {
                sb.AppendLine().AppendLine($"[{group.Key}]");
                foreach (var s in group)
                {
                    sb.AppendLine("# " + s.Description);
                    sb.AppendLine($"{s.Key} = {s.Text}");
                }
            }
            try { File.WriteAllText(FilePath, sb.ToString()); }
            catch (Exception e) { Log.Warn("Could not save the settings: " + e.Message); }
        }

        public static string Dump() => string.Join(" ", All.Select(x => $"{x.Section}.{x.Key}={x.Text}").ToArray());

        /// <summary>Sets a setting by its key, as the test channel does.</summary>
        public static bool SetByName(string key, string text)
        {
            var s = All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (s == null) return false;
            s.Parse(text);
            Changed();
            return true;
        }

        public static bool VrDisabled => string.Equals((Runtime.Value ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Picks how to reach the headset. The game is 32-bit, and SteamVR's OpenXR runtime is 64-bit only, so
        /// SteamVR is reached through OpenVR. Other headsets use the 32-bit OpenXR runtime, such as VDXR. Auto
        /// prefers SteamVR when it is already running, and the system's active 32-bit OpenXR runtime otherwise.
        /// For OpenXR, <paramref name="manifest"/> is the runtime to load, or empty for the active one.
        /// </summary>
        /// <summary>One way to reach the headset: OpenXR with a runtime manifest (empty for the system's active
        /// one), or OpenVR for SteamVR.</summary>
        public struct Backend
        {
            public int Kind;
            public string Manifest;
            public string Name;
        }

        /// <summary>
        /// The ways to try, in order. A fixed Runtime setting gives just that one. Auto: SteamVR when it is
        /// already running; otherwise the active 32-bit OpenXR runtime if it really is a loadable 32-bit runtime,
        /// then Virtual Desktop's VDXR if installed, then SteamVR through OpenVR. Everything left out is logged
        /// with the reason, because a broken or 64-bit-only registration is the usual cause of "plays flat".
        /// </summary>
        public static List<Backend> Backends()
        {
            var list = new List<Backend>();
            var v = (Runtime.Value ?? "auto").Trim();
            if (v.Equals("steamvr", StringComparison.OrdinalIgnoreCase)) { list.Add(new Backend { Kind = Native.BackendOpenVR, Manifest = "", Name = "SteamVR (OpenVR)" }); return list; }
            if (v.Equals("openxr", StringComparison.OrdinalIgnoreCase)) { list.Add(new Backend { Kind = Native.BackendOpenXR, Manifest = "", Name = "the active OpenXR runtime" }); return list; }
            if (v.Equals("vdxr", StringComparison.OrdinalIgnoreCase))
            {
                var vd = FindOpenXRRuntime("virtualdesktop-openxr");
                if (vd.Length == 0) Log.Warn("VDXR's 32-bit runtime was not found in the OpenXR runtime list, so the active OpenXR runtime is used. Is Virtual Desktop's Streamer installed and up to date?");
                list.Add(new Backend { Kind = Native.BackendOpenXR, Manifest = vd, Name = "VDXR" });
                return list;
            }
            if (v.Length > 0 && !v.Equals("auto", StringComparison.OrdinalIgnoreCase)) { list.Add(new Backend { Kind = Native.BackendOpenXR, Manifest = v, Name = v }); return list; }

            if (Native.SteamVrRunning) list.Add(new Backend { Kind = Native.BackendOpenVR, Manifest = "", Name = "SteamVR (OpenVR), already running" });
            string why;
            var active = (TestActiveRuntime.Value ?? "").Length > 0 ? TestActiveRuntime.Value : ActiveOpenXRRuntime();
            if (active.Length == 0) Log.Info("Runtime: no active 32-bit OpenXR runtime is registered (HKLM\\SOFTWARE\\WOW6432Node\\Khronos\\OpenXR\\1 ActiveRuntime)");
            else if (Usable32(active, out why)) list.Add(new Backend { Kind = Native.BackendOpenXR, Manifest = (TestActiveRuntime.Value ?? "").Length > 0 ? active : "", Name = "the active OpenXR runtime (" + active + ")" });
            else Log.Warn("Runtime: the active 32-bit OpenXR runtime can't be used by this 32-bit game, so it is skipped: " + active + " - " + why);
            var vdxr = (TestVdxrRuntime.Value ?? "").Length > 0 ? TestVdxrRuntime.Value : FindOpenXRRuntime("virtualdesktop-openxr");
            if (vdxr.Length > 0 && !string.Equals(vdxr, active, StringComparison.OrdinalIgnoreCase))
            {
                if (Usable32(vdxr, out why)) list.Add(new Backend { Kind = Native.BackendOpenXR, Manifest = vdxr, Name = "VDXR (" + vdxr + ")" });
                else Log.Warn("Runtime: VDXR's manifest can't be used: " + vdxr + " - " + why);
            }
            if (!Native.SteamVrRunning) list.Add(new Backend { Kind = Native.BackendOpenVR, Manifest = "", Name = "SteamVR (OpenVR)" });
            return list;
        }

        // A 32-bit process reads the 32-bit OpenXR registration, which is what its loader uses.
        const string KhronosKey = @"SOFTWARE\Khronos\OpenXR\1";

        static string ActiveOpenXRRuntime()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(KhronosKey))
                    return (key?.GetValue("ActiveRuntime") as string) ?? "";
            }
            catch (Exception e) { Log.Warn("Could not read the active OpenXR runtime: " + e.Message); return ""; }
        }

        /// <summary>Whether an OpenXR runtime manifest points at a DLL this 32-bit game can load.</summary>
        public static bool Usable32(string manifest, out string why)
        {
            why = "";
            try
            {
                if (!File.Exists(manifest)) { why = "the manifest file doesn't exist (a leftover from an uninstalled runtime?)"; return false; }
                var text = File.ReadAllText(manifest);
                var m = System.Text.RegularExpressions.Regex.Match(text, "\"library_path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!m.Success) { why = "the manifest names no library_path"; return false; }
                var lib = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
                if (!Path.IsPathRooted(lib)) lib = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest), lib));
                if (!File.Exists(lib)) { why = "its runtime DLL doesn't exist: " + lib; return false; }
                var machine = PeMachine(lib);
                if (machine == 0x8664) { why = "its runtime DLL is 64-bit only (" + lib + "). SteamVR's OpenXR runtime is like this; the mod reaches SteamVR through OpenVR instead"; return false; }
                if (machine != 0x14c) { why = "its runtime DLL isn't a 32-bit Windows DLL (" + lib + ")"; return false; }
                return true;
            }
            catch (Exception e) { why = "it couldn't be read: " + e.Message; return false; }
        }

        /// <summary>The machine type in a DLL's PE header: 0x14c for 32-bit x86, 0x8664 for x64, 0 if unreadable.</summary>
        internal static int PeMachine(string path)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                if (f.Length < 0x40) return 0;
                f.Position = 0x3C;
                int pe = r.ReadInt32();
                if (pe <= 0 || pe + 6 > f.Length) return 0;
                f.Position = pe;
                if (r.ReadUInt32() != 0x00004550) return 0;
                return r.ReadUInt16();
            }
        }

        static string FindOpenXRRuntime(string nameStart)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(KhronosKey + @"\AvailableRuntimes"))
                {
                    var found = key?.GetValueNames().FirstOrDefault(n => Path.GetFileName(n).StartsWith(nameStart, StringComparison.OrdinalIgnoreCase) && File.Exists(n));
                    return found ?? "";
                }
            }
            catch (Exception e) { Log.Warn("Could not read the OpenXR runtime list: " + e.Message); return ""; }
        }
    }
}
