using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DearEstherVR
{
    /// <summary>
    /// Adds a VR page to the game's own Options menu (main menu and pause menu), built from the game's own
    /// pieces so it looks and behaves like the other pages: the Audio page is copied for the panel, its option
    /// rows (DearEstherOptionsButton: a name and left/right arrows) for each setting, the Video page's
    /// "Advanced" button for Recenter, and the Language button for the "VR" entry in the Options list.
    ///
    /// Cloned objects keep the EventTriggers of the originals, which call the Audio or Language page. Those
    /// calls are switched off and replaced with the VR page's own. The game's localiser rewrites every UI text
    /// it hasn't seen into "#NotInStringTable#...", so the page puts its own labels back whenever they change.
    /// </summary>
    internal static class VrMenu
    {
        static readonly List<VrPanel> _panels = new List<VrPanel>();
        static readonly HashSet<MainOptionsPanel> _skipped = new HashSet<MainOptionsPanel>();

        /// <summary>Called about once a second: adds the VR page to any Options menu that doesn't have one yet.</summary>
        public static void Install()
        {
            foreach (var main in Resources.FindObjectsOfTypeAll<MainOptionsPanel>())
            {
                if (main == null || !main.gameObject.scene.IsValid() || _skipped.Contains(main)) continue;
                if (main.transform.Find("VR Button") != null) continue;
                try { Build(main); }
                catch (Exception e) { Log.Error("Could not add the VR options page: " + e); }
            }
        }

        public static VrPanel Open => _panels.Find(p => p != null && p.gameObject.activeInHierarchy);

        static void Build(MainOptionsPanel main)
        {
            // The pages are siblings of the Options list; the list's own references are not always filled in.
            var parent = main.transform.parent;
            var audio = main.audioPanel as AudioPanel ?? (parent != null ? parent.GetComponentInChildren<AudioPanel>(true) : null);
            OptionPanelBase video = main.videoPanel ?? (parent != null ? parent.GetComponentInChildren<VideoPanel>(true) : null);
            var languageT = main.languageButton != null ? main.languageButton.transform : main.transform.Find("Language Button");
            var language = languageT != null ? languageT.gameObject : null;
            if (audio == null || video == null || language == null)
            {
                if (_skipped.Add(main)) Log.Warn("Options menu without Audio/Video/Language pages; no VR page added to " + Path(main.transform));
                return;
            }

            // The panel: a copy of the Audio page with its rows taken out.
            var panelGo = UnityEngine.Object.Instantiate(audio.gameObject);
            panelGo.name = "MenuVRPanel";
            panelGo.transform.SetParent(audio.transform.parent, false);
            panelGo.SetActive(false);
            var oldPanel = panelGo.GetComponent<AudioPanel>();
            var rowTemplate = panelGo.GetComponentInChildren<DearEstherOptionsButton>(true);
            var done = FindChild(panelGo.transform, "Done Button");
            var title = FindChild(panelGo.transform, "Audio Title Text");
            var panel = panelGo.AddComponent<VrPanel>();
            panel.previousPanel = audio.previousPanel;
            panel.clickSound = audio.clickSound; panel.overSound = audio.overSound; panel.selectSound = audio.selectSound; panel.backSound = audio.backSound;
            UnityEngine.Object.DestroyImmediate(oldPanel);

            // The "VR" entry in the Options list, below Language.
            var vrButton = UnityEngine.Object.Instantiate(language);
            vrButton.name = "VR Button";
            vrButton.transform.SetParent(language.transform.parent, false);
            var lp = ((RectTransform)language.transform).anchoredPosition;
            ((RectTransform)vrButton.transform).anchoredPosition = new Vector2(lp.x, lp.y - 26f);
            vrButton.transform.SetSiblingIndex(language.transform.GetSiblingIndex() + 1);
            Retrigger(vrButton, () =>
            {
                main.GoToPanel(panel.gameObject, panel.firstSelectedObject, null);
                if (main.hideBackground && main.background != null) main.background.SetActive(false);
            }, main.OnCancel);
            panel.previousSelection = vrButton;
            panel.Label(vrButton.GetComponentInChildren<Text>(true), "VR");

            if (title != null) panel.Label(title.GetComponent<Text>(), "VR");
            if (done != null) Retrigger(done.gameObject, panel.OnCancel, panel.OnCancel);

            // Rows. Recenter is a wide button like Video's "Advanced"; every setting is an arrow row.
            var anchorY = rowTemplate != null ? ((RectTransform)rowTemplate.transform).anchoredPosition : new Vector2(0, 110);
            float top = 120f, step = 20.5f;
            var rows = new List<GameObject>();
            var advanced = FindChild(video.transform, "Advanced");
            if (advanced != null)
            {
                var rec = UnityEngine.Object.Instantiate(advanced.gameObject);
                rec.name = "Recenter";
                rec.transform.SetParent(panelGo.transform, false);
                ((RectTransform)rec.transform).anchoredPosition = new Vector2(((RectTransform)advanced).anchoredPosition.x, top);
                Retrigger(rec, () => { if (XrRig.I != null) XrRig.I.RequestRecenter(); if (panel.selectSound != null) panel.selectSound.Play(); }, panel.OnCancel);
                panel.Label(rec.GetComponentInChildren<Text>(true), "Recenter View");
                rows.Add(rec);
            }
            foreach (var opt in VrOption.All)
            {
                var row = UnityEngine.Object.Instantiate(rowTemplate.gameObject);
                row.name = "VR " + opt.Name;
                row.transform.SetParent(panelGo.transform, false);
                ((RectTransform)row.transform).anchoredPosition = new Vector2(anchorY.x, top - step * rows.Count);
                var b = row.GetComponent<DearEstherOptionsButton>();
                b.optionPanel = panel;
                b.localiseText = false;
                b.optionStrings = opt.Labels;
                panel.Label(b.optionName, opt.Name);
                panel.Add(b, opt);
                rows.Add(row);
            }
            // The Audio page's own rows go.
            foreach (var old in panelGo.GetComponentsInChildren<Selectable>(true))
            {
                if (old == null || rows.Contains(old.gameObject) || (done != null && old.transform == done)) continue;
                if (old.GetComponent<DearEstherOptionsButton>() != null || old.GetComponent<DearEstherOptionsSlider>() != null)
                    UnityEngine.Object.DestroyImmediate(old.gameObject);
            }

            // Gamepad and keyboard navigation: straight down the rows to Done, and round again.
            var chain = new List<Selectable>();
            foreach (var r in rows) chain.Add(r.GetComponent<Selectable>());
            if (done != null) chain.Add(done.GetComponent<Selectable>());
            for (int i = 0; i < chain.Count; i++)
            {
                var nav = new Navigation { mode = Navigation.Mode.Explicit };
                nav.selectOnUp = chain[(i + chain.Count - 1) % chain.Count];
                nav.selectOnDown = chain[(i + 1) % chain.Count];
                chain[i].navigation = nav;
            }
            panel.firstSelectedObject = rows.Count > 0 ? rows[0] : null;
            _panels.Add(panel);
            Log.Info("VR options page added to " + Path(main.transform));
        }

        /// <summary>Switches off a cloned control's EventTrigger calls (they open the original's page) and gives it
        /// its own: submit and click run <paramref name="onSubmit"/>, cancel runs <paramref name="onCancel"/>.
        /// Hover and select sounds and highlights are kept.</summary>
        static void Retrigger(GameObject go, UnityAction onSubmit, UnityAction onCancel)
        {
            var et = go.GetComponent<EventTrigger>();
            if (et == null) et = go.AddComponent<EventTrigger>();
            foreach (var entry in et.triggers)
            {
                if (entry.eventID != EventTriggerType.Submit && entry.eventID != EventTriggerType.PointerClick && entry.eventID != EventTriggerType.Cancel) continue;
                for (int i = 0; i < entry.callback.GetPersistentEventCount(); i++) entry.callback.SetPersistentListenerState(i, UnityEventCallState.Off);
                entry.callback.RemoveAllListeners();
            }
            Add(et, EventTriggerType.Submit, onSubmit);
            Add(et, EventTriggerType.PointerClick, onSubmit);
            Add(et, EventTriggerType.Cancel, onCancel);
            var button = go.GetComponent<Button>();
            if (button != null) button.onClick = new Button.ButtonClickedEvent();
        }

        static void Add(EventTrigger et, EventTriggerType type, UnityAction action)
        {
            EventTrigger.Entry entry = null;
            foreach (var e in et.triggers) if (e.eventID == type) { entry = e; break; }
            if (entry == null) { entry = new EventTrigger.Entry { eventID = type }; et.triggers.Add(entry); }
            entry.callback.AddListener(_ => action());
        }

        static Transform FindChild(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = FindChild(c, name); if (f != null) return f; }
            return null;
        }

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }

    /// <summary>One VR setting shown as an arrow row: its labels and the setting values they stand for.</summary>
    internal sealed class VrOption
    {
        public string Name;
        public string[] Labels;
        public Func<int> Current;
        public Action<int> Set;

        static VrOption Choice(string name, StringSetting s, string[] labels, string[] values) => new VrOption
        {
            Name = name, Labels = labels,
            Current = () => Math.Max(0, Array.FindIndex(values, v => string.Equals(v, s.Value, StringComparison.OrdinalIgnoreCase))),
            Set = i => s.Value = values[i],
        };

        static VrOption Toggle(string name, BoolSetting s) => new VrOption
        {
            Name = name, Labels = new[] { "Off", "On" },
            Current = () => s.Value ? 1 : 0,
            Set = i => s.Value = i == 1,
        };

        static VrOption Steps(string name, FloatSetting s, float[] values, Func<float, string> label)
        {
            var labels = Array.ConvertAll(values, v => label(v));
            return new VrOption
            {
                Name = name, Labels = labels,
                Current = () => { int best = 0; for (int i = 1; i < values.Length; i++) if (Math.Abs(values[i] - s.Value) < Math.Abs(values[best] - s.Value)) best = i; return best; },
                Set = i => s.Value = values[i],
            };
        }

        static float[] Range(float from, float to, float step)
        {
            var l = new List<float>();
            for (float v = from; v <= to + step * 0.01f; v += step) l.Add((float)Math.Round(v, 3));
            return l.ToArray();
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static readonly VrOption[] All =
        {
            Choice("Height", Cfg.HeightMode, new[] { "Standing", "Seated" }, new[] { "standing", "seated" }),
            Steps("Height Offset", Cfg.HeightOffset, Range(-0.5f, 0.5f, 0.05f), v => (v > 0.001f ? "+" : "") + Math.Round(v * 100).ToString(Inv) + " cm"),
            Steps("World Scale", Cfg.WorldScale, Range(0.5f, 2f, 0.1f), v => v.ToString("0.0", Inv) + "x"),
            Choice("Walk Direction", Cfg.WalkDirection, new[] { "Head", "Body" }, new[] { "head", "body" }),
            Choice("Turning", Cfg.Turning, new[] { "Smooth", "Snap" }, new[] { "smooth", "snap" }),
            Steps("Snap Angle", Cfg.SnapAngle, new[] { 15f, 30f, 45f, 60f, 90f }, v => v.ToString(Inv) + " degrees"),
            Toggle("Head Bob", Cfg.HeadBob),
            Steps("Texture Sharpness", Cfg.TextureSharpness, new[] { 0f, -0.5f, -1f, -1.5f }, v => v == 0f ? "Original" : v == -0.5f ? "Sharp" : v == -1f ? "Sharper" : "Sharpest"),
            Steps("Shadow Depth", Cfg.ShadowDepth, new[] { 0f, 1f, 2f, 3f }, v => v == 0f ? "Off" : v == 1f ? "Low" : v == 2f ? "Medium" : "High"),
            Steps("Underwater Distortion", Cfg.UnderwaterDistortion, new[] { 0f, 0.25f, 0.5f, 1f }, v => v == 0f ? "Off" : v == 0.25f ? "Low" : v == 0.5f ? "Medium" : "Full"),
            Steps("Resolution", Cfg.ResolutionScale, Range(0.5f, 2f, 0.1f), v => Math.Round(v * 100).ToString(Inv) + "%"),
            Steps("Anti-aliasing", Cfg.Antialiasing, new[] { 0f, 2f, 8f }, v => v == 0f ? "Game (4x)" : v.ToString(Inv) + "x"),
        };
    }

    /// <summary>The VR page itself. Rows apply as soon as they change; the settings file is written by Cfg.</summary>
    public class VrPanel : OptionPanelBase
    {
        readonly List<KeyValuePair<DearEstherOptionsButton, VrOption>> _rows = new List<KeyValuePair<DearEstherOptionsButton, VrOption>>();
        readonly Dictionary<Text, string> _labels = new Dictionary<Text, string>();
        readonly Dictionary<DearEstherOptionsButton, string> _shown = new Dictionary<DearEstherOptionsButton, string>();
        bool _refresh = true;

        internal void Add(DearEstherOptionsButton b, VrOption o) => _rows.Add(new KeyValuePair<DearEstherOptionsButton, VrOption>(b, o));

        internal void Label(Text t, string s)
        {
            if (t == null) return;
            _labels[t] = s;
            t.text = s;
        }

        protected override void SetUpPanel() { _refresh = true; }
        protected override void OnDisplay() { _refresh = true; }
        void OnEnable() { _refresh = true; }

        public override void SaveOptions() => Cfg.Flush();

        void Update()
        {
            foreach (var kv in _labels) if (kv.Key != null && kv.Key.text != kv.Value) kv.Key.text = kv.Value;
            if (_refresh)
            {
                // After the rows' own Awake (which resets them to their first option) has run.
                _refresh = false;
                foreach (var r in _rows)
                {
                    r.Key.UpdateOption(r.Key.optionStrings[r.Value.Current()]);
                    _shown[r.Key] = r.Key.CurrentOption;
                }
                return;
            }
            foreach (var r in _rows)
            {
                string now = r.Key.CurrentOption;
                string was;
                if (_shown.TryGetValue(r.Key, out was) && was == now) continue;
                _shown[r.Key] = now;
                int i = Array.IndexOf(r.Key.optionStrings, now);
                if (i >= 0) { r.Value.Set(i); Log.Info($"VR option {r.Value.Name} = {now}"); }
            }
        }

        /// <summary>Lab: the rows and what they show.</summary>
        internal string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in _rows) sb.Append(r.Value.Name.Replace(' ', '_')).Append('=').Append(r.Key.CurrentOption.Replace(' ', '_')).Append(' ');
            return sb.ToString();
        }

        /// <summary>Lab: sets a row as the arrows would.</summary>
        internal string SetRow(string name, string label)
        {
            foreach (var r in _rows)
            {
                if (!string.Equals(r.Value.Name.Replace(' ', '_'), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (Array.IndexOf(r.Key.optionStrings, label.Replace('_', ' ')) < 0) return "no option " + label;
                r.Key.UpdateOption(label.Replace('_', ' '));
                return "ok";
            }
            return "no row " + name;
        }
    }
}

namespace DearEstherVR
{
    /// <summary>Keeps the VR page in every Options menu, in VR or flat (its settings apply either way).</summary>
    internal sealed class MenuHook : UnityEngine.MonoBehaviour
    {
        float _next;
        void Update()
        {
            if (UnityEngine.Time.unscaledTime < _next) return;
            _next = UnityEngine.Time.unscaledTime + 1f;
            VrMenu.Install();
        }
    }
}
