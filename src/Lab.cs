using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// Developer test channel. Polls a command file so a test script can drive the game without a person at the
    /// controls. Only exists when Debug LabCommands is on.
    /// </summary>
    public class Lab : MonoBehaviour
    {
        static string CmdFile => Path.Combine(Log.LabDir, "cmd.txt");
        static string OutFile => Path.Combine(Log.LabDir, "out.txt");
        float _next;
        Vector2 _walk;
        float _walkUntil;

        void Start() => Directory.CreateDirectory(Log.LabDir);

        void LateUpdate()
        {
            // After the game's input has set this frame's move vector, before the rig turns it to the head.
            if (Time.time < _walkUntil) Game.LabMove(_walk);
        }

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (!File.Exists(CmdFile)) return;
            string[] lines;
            try { lines = File.ReadAllLines(CmdFile); File.Delete(CmdFile); } catch { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                string result;
                try { result = Run(line); }
                catch (Exception e) { result = "ERROR " + e; }
                File.AppendAllText(OutFile, $"> {line}\n{result}\n");
            }
        }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{v.x:F3},{v.y:F3},{v.z:F3}";

        string Run(string line)
        {
            int sp = line.IndexOf(' ');
            var cmd = sp < 0 ? line : line.Substring(0, sp);
            var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            var rig = XrRig.I;
            switch (cmd)
            {
                case "vr":
                    return $"active={rig.Active} state={Native.DVR_SessionState()} submitted={Native.DVR_FramesSubmitted()} eye={rig.EyeWidth}x{rig.EyeHeight} msaa={rig.Msaa} " +
                           $"menu={rig.MenuOpen} panel={V(rig.PanelPos)} panelYaw={rig.PanelYaw:F1} centerYaw={rig.CenterYaw:F1} center={V(rig.CenterPos)} runtime={Native.RuntimeName.Replace(' ', '_')} " +
                           $"floorKnown={rig.FloorKnown} floorY={rig.FloorY:F3} height={rig.HeightModeUsed} headFromBody={rig.HeadYawFromBody:F1} " +
                           $"win={Screen.width}x{Screen.height} modMs={rig.FrameMs:F2} fps={1f / Time.smoothDeltaTime:F1} eye0={V(rig.LastEyeWorld0)} eye1={V(rig.LastEyeWorld1)} base={V(rig.LastBasePos)} " +
                           $"eyeYaw={rig.LastEyeRot0.eulerAngles.y:F1} eyePitch={rig.LastEyeRot0.eulerAngles.x:F1} cams={rig.LastCameras.Trim().Replace(' ', ';')}";
                case "state":
                    return $"session=1 timeScale={Time.timeScale} " + Game.State();
                case "cams": return DumpCameras();
                case "render": return $"colorSpace={QualitySettings.activeColorSpace} quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} aa={QualitySettings.antiAliasing} vsync={QualitySettings.vSyncCount} target={Application.targetFrameRate} fullscreen={Screen.fullScreen} background={Application.runInBackground}";
                case "recenter": rig.RequestRecenter(); return "ok";
                case "cfg": return Cfg.Dump();
                case "set": { var a = arg.Split(' '); return Cfg.SetByName(a[0], a[1]) ? "ok" : "unknown setting"; }
                case "ts": Time.timeScale = F(arg); return "timeScale " + Time.timeScale;
                case "load": Application.LoadLevelAsync(arg); return "loading " + arg + " asynchronously, as the game's PlayMaker LoadLevel does";
                case "fade": { var c = XrRig.I.FadeColor; return $"fade={c.r:F2},{c.g:F2},{c.b:F2},{c.a:F3} source={Fade.Source.Replace(' ', '_')} loadingVisible={Fade.LoadingVisible}"; }
                case "fadetest": Fade.LabFade = arg == "off" ? (Color?)null : new Color(0, 0, 0, F(arg)); return "ok";
                case "yaw": Game.LabYaw(F(arg)); return "ok";
                case "walk": { var a = arg.Split(' '); _walk = new Vector2(F(a[0]), F(a[1])); _walkUntil = Time.time + F(a[2]); return "walking"; }
                case "flip": XrRig.FlipRows = arg == "1"; return "flip " + XrRig.FlipRows;
                case "tp": { var a = arg.Split(' '); Game.LabTeleport(new Vector3(F(a[0]), F(a[1]), F(a[2]))); return "ok"; }
                case "stick": Game.LabStickX = arg == "off" ? (float?)null : F(arg); return "stick " + arg;
                case "res": { var a = arg.Split(' '); Screen.SetResolution(int.Parse(a[0]), int.Parse(a[1]), a[2] == "1"); return "resolution set"; }
                case "water": return Game.WaterState();
                case "textures": return TextureQuality.Report();
                case "materials": return TextureQuality.Materials(arg);
                case "near": return TextureQuality.Near(F(arg));
                case "shine": return TextureQuality.Shine(F(arg));
                case "refract": return Underwater.Lab(arg);
                case "moon": return Game.MoonState(XrRig.I.LastEyeWorld0);
                case "commentary": HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmBool("DirectorsCommentaryIsOn").Value = arg == "1"; return "commentary " + arg;
                case "icons": return Icons(arg);
                case "objinfo": { var t = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.name == arg); if (t == null) return "not found"; var r = t.GetComponent<Renderer>(); var cam = Camera.main; return PathOf(t) + " active=" + t.gameObject.activeInHierarchy + " layer=" + t.gameObject.layer + " pos=" + V(t.position) + " scale=" + V(t.lossyScale) + (r != null ? " enabled=" + r.enabled + " bounds.center=" + V(r.bounds.center) + " bounds.size=" + V(r.bounds.size) + " queue=" + r.sharedMaterial.renderQueue + " cam=" + (cam != null ? V(cam.transform.position) : "-") : "") + " comps=" + string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()); }
                case "rendertoggle": { var t = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.name == arg.Split(' ')[0]); if (t == null) return "not found"; var r = t.GetComponent<Renderer>(); r.enabled = arg.Split(' ')[1] == "1"; return "renderer " + r.enabled; }
                case "tree": return Tree(arg);
                case "vrinfo": { var p = VrMenu.Open; return p == null ? "vr page closed" : p.Describe(); }
                case "vrrow": { var p = VrMenu.Open; if (p == null) return "vr page closed"; var a = arg.Split(' '); return p.SetRow(a[0], a[1]); }
                case "find": return Find(arg);
                case "fsmscan": return FsmScan(arg);
                case "overlay": { var a = arg.Split(' '); var t = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.name == a[0]); if (t == null) return "not found"; var r = t.GetComponent<Renderer>(); t.gameObject.SetActive(F(a[1]) > 0); if (r != null && r.material.HasProperty("_Color")) { var c = r.material.color; c.a = F(a[1]); r.material.color = c; } return "overlay " + a[0] + " layer=" + t.gameObject.layer + " camMask=0x" + (Camera.main != null ? Camera.main.cullingMask.ToString("X") : "-") + " color=" + (r != null && r.material.HasProperty("_Color") ? r.material.color.ToString() : "-") + " shader=" + (r != null ? r.sharedMaterial.shader.name : "-") + " lp=" + V(t.localPosition) + " ls=" + V(t.localScale) + " bounds=" + (r != null ? V(r.bounds.size) : "-"); }
                case "click": return Click(arg);
                case "nav": return Nav(arg);
                case "selected": { var es = UnityEngine.EventSystems.EventSystem.current; var go = es != null ? es.currentSelectedGameObject : null; return "selected=" + (go != null ? PathOf(go.transform) : "none"); }
                case "quit": Application.Quit(); return "quitting";
                default: return "unknown command";
            }
        }

        /// <summary>Submits a button (by name, active ones only) the way the game's input module does.</summary>
        static string Click(string name)
        {
            var b = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.name == name && x.gameObject.activeInHierarchy);
            if (b == null) return "no active button " + name;
            UnityEngine.EventSystems.ExecuteEvents.Execute(b.gameObject, new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
            return "submitted " + PathOf(b.transform);
        }

        /// <summary>Gamepad-style navigation from the selected control: up, down, left, right, submit, cancel.</summary>
        static string Nav(string dir)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return "nothing selected";
            if (dir == "submit") UnityEngine.EventSystems.ExecuteEvents.Execute(go, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
            else if (dir == "cancel") UnityEngine.EventSystems.ExecuteEvents.Execute(go, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.cancelHandler);
            else
            {
                var e = new UnityEngine.EventSystems.AxisEventData(es);
                switch (dir)
                {
                    case "up": e.moveDir = UnityEngine.EventSystems.MoveDirection.Up; break;
                    case "down": e.moveDir = UnityEngine.EventSystems.MoveDirection.Down; break;
                    case "left": e.moveDir = UnityEngine.EventSystems.MoveDirection.Left; break;
                    case "right": e.moveDir = UnityEngine.EventSystems.MoveDirection.Right; break;
                    default: return "unknown direction";
                }
                UnityEngine.EventSystems.ExecuteEvents.Execute(go, e, UnityEngine.EventSystems.ExecuteEvents.moveHandler);
            }
            var now = es.currentSelectedGameObject;
            return "selected=" + (now != null ? PathOf(now.transform) : "none");
        }

        /// <summary>Renderers (active or not) whose material's main texture name contains <paramref name="part"/>:
        /// what kind of renderer, how it billboards, its shader, rotation, and the PlayMaker FSMs above it.</summary>
        static string Icons(string part)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                var m = r.sharedMaterial;
                var tex = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (tex == null || tex.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++n > 12) { sb.AppendLine("..."); break; }
                sb.Append(PathOf(r.transform)).Append(" active=").Append(r.gameObject.activeInHierarchy).Append(' ').Append(r.GetType().Name);
                var pr = r as ParticleSystemRenderer;
                if (pr != null) sb.Append("(mode=").Append(pr.renderMode).Append(')');
                sb.Append(" shader=").Append(m.shader.name).Append(" tex=").Append(tex.name).Append(" rot=").Append(V(r.transform.eulerAngles));
                for (var t = r.transform; t != null; t = t.parent)
                    foreach (var pm in t.GetComponents<PlayMakerFSM>()) sb.Append(" fsm@").Append(t.name).Append('=').Append(pm.FsmName);
                foreach (var c in r.GetComponents<MonoBehaviour>()) if (c != null) sb.Append(" +").Append(c.GetType().Name);
                sb.AppendLine();
            }
            return n == 0 ? "none" : sb.ToString();
        }

        /// <summary>Every PlayMaker state (in loaded scenes) holding an action whose type name contains one of the
        /// comma-separated words, with that action's field values.</summary>
        static string FsmScan(string words)
        {
            var parts = words.Split(',');
            var sb = new StringBuilder();
            foreach (var pm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (pm == null || !pm.gameObject.scene.IsValid() || pm.Fsm == null) continue;
                HutongGames.PlayMaker.FsmState[] states;
                try { states = pm.FsmStates; } catch { continue; }
                foreach (var st in states)
                {
                    HutongGames.PlayMaker.FsmStateAction[] acts;
                    try { acts = st.Actions; } catch { continue; }
                    foreach (var a in acts)
                    {
                        if (a == null) continue;
                        var n = a.GetType().Name;
                        if (!parts.Any(p => n.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        sb.Append(PathOf(pm.transform)).Append(" fsm=").Append(pm.FsmName).Append(" state=").Append(st.Name).Append(' ').Append(n).Append(':');
                        foreach (var f in a.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            object v = null;
                            try { v = f.GetValue(a); } catch { }
                            var nv = v as HutongGames.PlayMaker.NamedVariable;
                            string s = nv != null ? (nv.UsesVariable ? "$" + nv.Name : nv.ToString()) : (v != null ? v.ToString() : "null");
                            var fp = v as HutongGames.PlayMaker.FsmProperty;
                            if (fp != null) s = fp.TargetTypeName + "." + fp.PropertyName + "(" + (fp.TargetObject != null && fp.TargetObject.Value != null ? fp.TargetObject.Value.name : (fp.TargetObject != null ? "$" + fp.TargetObject.Name : "?")) + ")=" + (fp.FloatParameter != null ? fp.FloatParameter.ToString() : "") + (fp.Vector3Parameter != null ? fp.Vector3Parameter.ToString() : "") + (fp.BoolParameter != null ? fp.BoolParameter.ToString() : "");
                            var fo = v as HutongGames.PlayMaker.FsmOwnerDefault;
                            if (fo != null) s = fo.OwnerOption + (fo.GameObject != null && fo.GameObject.Value != null ? ":" + fo.GameObject.Value.name : "");
                            var o = v as UnityEngine.Object;
                            if (o != null) s = o.name;
                            sb.Append(' ').Append(f.Name).Append('=').Append(s);
                        }
                        sb.AppendLine();
                        if (sb.Length > 25000) return sb.ToString();
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>Paths of every object (active or not) with a component of this type name.</summary>
        static string Find(string type)
        {
            var sb = new StringBuilder();
            foreach (var c in Resources.FindObjectsOfTypeAll<Component>())
                if (c != null && c.gameObject.scene.IsValid() && c.GetType().Name == type)
                    sb.AppendLine(PathOf(c.transform) + (c.gameObject.activeInHierarchy ? " (active)" : ""));
            return sb.ToString();
        }

        static string Tree(string path)
        {
            // Inactive objects too, which GameObject.Find can't see.
            var t = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && (x.name == path || PathOf(x).EndsWith(path, StringComparison.Ordinal)));
            if (t == null) return "not found";
            var sb = new StringBuilder();
            Walk(t, 0, sb);
            return sb.ToString();
        }

        static void Walk(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 6 || sb.Length > 30000) return;
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            var rt = t as RectTransform;
            if (rt != null) sb.Append($" pos={rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0} size={rt.sizeDelta.x:F0}x{rt.sizeDelta.y:F0}");
            sb.Append(" [");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform || c is CanvasRenderer) continue;
                sb.Append(c.GetType().Name);
                var sel = c as UnityEngine.UI.Selectable;
                if (sel != null) sb.Append("(nav=").Append(sel.navigation.mode).Append(')');
                var text = c as UnityEngine.UI.Text;
                if (text != null) sb.Append("(\"").Append(text.text.Replace('\n', ' ')).Append("\")");
                var btn = c as UnityEngine.UI.Button;
                if (btn != null) for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++) sb.Append("{").Append(btn.onClick.GetPersistentTarget(i) != null ? btn.onClick.GetPersistentTarget(i).name : "null").Append('.').Append(btn.onClick.GetPersistentMethodName(i)).Append('}');
                sb.Append(' ');
            }
            sb.AppendLine("]");
            foreach (Transform child in t) Walk(child, depth + 1, sb);
        }

        static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        static string DumpCameras()
        {
            var sb = new StringBuilder();
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!c.gameObject.scene.IsValid()) continue;
                sb.AppendLine($"{PathOf(c.transform)} enabled={c.enabled} active={c.gameObject.activeInHierarchy} depth={c.depth} path={c.actualRenderingPath} " +
                              $"fov={c.fieldOfView:F1} near={c.nearClipPlane} far={c.farClipPlane} mask=0x{c.cullingMask:X} clear={c.clearFlags} target={(c.targetTexture != null ? c.targetTexture.name : "-")} tag={c.tag} pos={V(c.transform.position)} rot={V(c.transform.eulerAngles)}");
                foreach (var b in c.GetComponents<Behaviour>()) if (!(b is Camera)) sb.AppendLine($"    {b.GetType().FullName} enabled={b.enabled}");
            }
            return sb.ToString();
        }
    }
}
