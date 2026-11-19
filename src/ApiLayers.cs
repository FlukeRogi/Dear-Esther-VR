using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DearEstherVR
{
    /// <summary>
    /// OpenXR implicit API layers are add-ons (overlays, toolkits, hand tracking, capture tools) that the OpenXR
    /// loader puts in front of the runtime for every OpenXR program. A 32-bit program sees the layers registered
    /// under HKLM\SOFTWARE\WOW6432Node\Khronos\OpenXR\1\ApiLayers\Implicit and, shared with 64-bit programs,
    /// HKCU\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit. If the first such layer can't be loaded (a 64-bit-only
    /// DLL, or one left over after an uninstall), the loader fails xrCreateInstance with
    /// XR_ERROR_FILE_ACCESS_ERROR (-32) whatever the runtime, which is how a PC that runs 64-bit VR fine plays
    /// this game flat. Every implicit layer names an environment variable that switches it off, so layers are
    /// switched off for this game's process only. Nothing is changed for any other program, and the registry is
    /// only read.
    /// </summary>
    internal static class ApiLayers
    {
        const string ImplicitKey = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetEnvironmentVariableW(string name, string value);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetEnvironmentVariableW(string name, System.Text.StringBuilder value, int size);

        static bool _scanned;
        public static bool AllOff { get; private set; }

        struct Layer { public string Manifest, Name, Lib, Disable, Enable; }

        static bool EnvSet(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // With no buffer this returns the size the value needs, so any set variable, even an empty one, is > 0.
            return GetEnvironmentVariableW(name, null, 0) > 0;
        }

        static void TurnOff(Layer l) { SetEnvironmentVariableW(l.Disable, "1"); }

        /// <summary>
        /// Called before the first OpenXR attempt. ApiLayers = auto switches off only the layers that can't load in
        /// this 32-bit game; off switches off every implicit layer; on leaves them all alone. Each layer is logged.
        /// </summary>
        public static void Prepare()
        {
            if (_scanned) return;
            _scanned = true;
            var mode = (Cfg.ApiLayersMode.Value ?? "auto").Trim().ToLowerInvariant();
            var layers = Find();
            if (layers.Count == 0) { Log.Info("OpenXR API layers: none registered for 32-bit programs"); return; }
            foreach (var l in layers)
            {
                string label = "OpenXR API layer " + (l.Name.Length > 0 ? l.Name : "(unnamed)") + " (" + l.Manifest + ")";
                if (l.Disable.Length == 0) { Log.Warn(label + ": its manifest names no disable_environment, so it can't be switched off for this game"); continue; }
                if (l.Enable.Length > 0 && !EnvSet(l.Enable)) { Log.Info(label + ": inactive (only loads when " + l.Enable + " is set)"); continue; }
                if (EnvSet(l.Disable)) { Log.Info(label + ": already switched off (" + l.Disable + " is set)"); continue; }
                if (mode == "on") { Log.Info(label + ": kept (ApiLayers = on)"); continue; }
                if (mode == "off") { TurnOff(l); Log.Info(label + ": switched off for this game (ApiLayers = off)"); continue; }
                string why;
                if (Usable32(l, out why)) Log.Info(label + ": kept, " + why);
                else { TurnOff(l); Log.Warn(label + ": switched off for this game, because " + why + ". A layer like this stops every 32-bit OpenXR game starting (error -32)"); }
            }
            if (mode == "off") AllOff = true;
        }

        /// <summary>
        /// After xrCreateInstance failed to load something: switches off every implicit layer still on, for one
        /// more try. Returns false if there was nothing left to switch off (or ApiLayers = on).
        /// </summary>
        public static bool TurnAllOff()
        {
            if (AllOff) return false;
            AllOff = true;
            if ((Cfg.ApiLayersMode.Value ?? "").Trim().Equals("on", StringComparison.OrdinalIgnoreCase)) return false;
            int n = 0;
            foreach (var l in Find())
            {
                if (l.Disable.Length == 0 || EnvSet(l.Disable)) continue;
                if (l.Enable.Length > 0 && !EnvSet(l.Enable)) continue;
                TurnOff(l);
                n++;
                Log.Info("OpenXR API layer " + l.Name + " switched off for this game");
            }
            return n > 0;
        }

        static List<Layer> Find()
        {
            var list = new List<Layer>();
            Read(Registry.LocalMachine, "HKLM", list);
            Read(Registry.CurrentUser, "HKCU", list);
            return list;
        }

        static void Read(RegistryKey root, string rootName, List<Layer> list)
        {
            try
            {
                using (var key = root.OpenSubKey(ImplicitKey))
                {
                    if (key == null) return;
                    foreach (var manifest in key.GetValueNames())
                    {
                        // The loader only uses entries whose DWORD value is 0.
                        object v = key.GetValue(manifest);
                        if (!(v is int) || (int)v != 0) continue;
                        list.Add(Parse(manifest));
                    }
                }
            }
            catch (Exception e) { Log.Warn("Could not read the OpenXR API layers in " + rootName + ": " + e.Message); }
        }

        static string Field(string text, string name)
        {
            var m = Regex.Match(text, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/") : "";
        }

        static Layer Parse(string manifest)
        {
            var l = new Layer { Manifest = manifest, Name = "", Lib = "", Disable = "", Enable = "" };
            try
            {
                if (!File.Exists(manifest)) return l;
                var text = File.ReadAllText(manifest);
                l.Name = Field(text, "name");
                l.Lib = Field(text, "library_path");
                l.Disable = Field(text, "disable_environment");
                l.Enable = Field(text, "enable_environment");
            }
            catch (Exception) { }
            return l;
        }

        static bool Usable32(Layer l, out string why)
        {
            why = "";
            if (!File.Exists(l.Manifest)) { why = "its manifest file doesn't exist (a leftover from an uninstall?)"; return false; }
            if (l.Lib.Length == 0) { why = "its manifest names no library_path"; return false; }
            // As the loader does: a bare file name is found by Windows' DLL search, which can't be checked here.
            if (l.Lib.IndexOf('\\') < 0 && l.Lib.IndexOf('/') < 0) { why = "its DLL " + l.Lib + " is found by Windows' search, so it isn't checked"; return true; }
            var lib = l.Lib;
            try
            {
                if (!Path.IsPathRooted(lib)) lib = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(l.Manifest), lib));
                if (!File.Exists(lib)) { why = "its DLL doesn't exist: " + lib; return false; }
                int machine = Cfg.PeMachine(lib);
                if (machine == 0x8664) { why = "its DLL is 64-bit only: " + lib; return false; }
                if (machine != 0x14c) { why = "its DLL isn't a 32-bit Windows DLL: " + lib; return false; }
            }
            catch (Exception e) { why = "its DLL couldn't be read (" + e.Message + ")"; return false; }
            why = "its DLL is 32-bit";
            return true;
        }
    }
}
