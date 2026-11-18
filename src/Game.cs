using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DearEstherVR
{
    /// <summary>
    /// Everything that touches Dear Esther's own scripts: the UFPS player camera and controller, the sea's
    /// water, and the in-game menu. Kept in one class so the rest of the mod only talks to Unity.
    ///
    /// While VR runs, the game's camera keeps deciding where the player is and which way the body faces, but:
    /// pitch is held level (the headset supplies pitch and roll), head bob and strafe roll are off unless the
    /// HeadBob setting is on, and the right stick can snap-turn. Everything is put back when VR stops.
    /// </summary>
    internal static class Game
    {
        static vp_FPCamera _cam;
        static vp_FPController _controller;
        static InGameMenu _menu;
        static readonly FieldInfo MoveVectorField = typeof(vp_FPController).GetField("m_MoveVector", BindingFlags.Instance | BindingFlags.NonPublic);

        // What the game had before VR changed it.
        struct Saved { public Vector4 bobAmp; public float strafeRoll; public Vector2 pitchLimit; }
        static readonly Dictionary<vp_FPCamera, Saved> _saved = new Dictionary<vp_FPCamera, Saved>();
        static bool _snapLatched;
        /// <summary>Lab only: stands in for the right stick's X axis.</summary>
        public static float? LabStickX;

        static readonly HashSet<string> Chapters = new HashSet<string> { "Donnelley", "Jakobson", "Paul", "Esther" };

        /// <summary>The level is one of the four chapters, as opposed to the start-up, title or menu scenes.</summary>
        public static bool InChapter => Chapters.Contains(Application.loadedLevelName);

        /// <summary>Finds the player's camera and controller again. Called about once a second.</summary>
        public static void Refresh()
        {
            var main = Camera.main;
            var cam = main != null ? main.GetComponent<vp_FPCamera>() : null;
            if (cam != _cam)
            {
                _cam = cam;
                _controller = cam != null ? cam.GetComponentInParent<vp_FPController>() : null;
            }
            if (_menu == null) _menu = Object.FindObjectOfType<InGameMenu>();
        }

        /// <summary>The camera that UFPS drives every frame, or null in menus and cutscenes.</summary>
        public static Camera PlayerCamera => _cam != null ? _cam.GetComponent<Camera>() : null;

        public static bool IsPlayerCamera(Camera c) => _cam != null && c != null && c.gameObject == _cam.gameObject;

        /// <summary>The game's eye height above the player's feet, in game units.</summary>
        public static float EyeHeight => _cam != null ? _cam.PositionOffset.y : 1.9f;

        /// <summary>Whether a menu shows: the title and menu scenes, or the pause menu during a chapter.</summary>
        public static bool MenuOpen
        {
            get
            {
                if (!InChapter) return true;
                return _menu != null && _menu.menuPanel != null && _menu.menuPanel.activeInHierarchy;
            }
        }

        /// <summary>
        /// Applied every frame while VR runs, after the game's own Update and LateUpdate. Returns how many
        /// degrees it turned the body this frame, which the rig applies to the frame being rendered too (UFPS
        /// has already posed the camera for this frame).
        /// </summary>
        public static float ApplyVr()
        {
            if (_cam == null) return 0f;
            if (!_saved.ContainsKey(_cam))
            {
                _saved[_cam] = new Saved { bobAmp = _cam.BobAmplitude, strafeRoll = _cam.RotationStrafeRoll, pitchLimit = _cam.RotationPitchLimit };
                Log.Info("Player camera found: " + _cam.name + " in " + Application.loadedLevelName);
            }
            var s = _saved[_cam];
            // The headset supplies pitch. A pitch limit of zero keeps the mouse and stick from tilting the body.
            _cam.RotationPitchLimit = Vector2.zero;
            _cam.Pitch = 0f;
            bool bob = Cfg.HeadBob.Value;
            _cam.BobAmplitude = bob ? s.bobAmp : Vector4.zero;
            _cam.RotationStrafeRoll = bob ? s.strafeRoll : 0f;

            bool snap = string.Equals(Cfg.Turning.Value, "snap", System.StringComparison.OrdinalIgnoreCase);
            if (!snap || Time.timeScale == 0f || Time.deltaTime == 0f) return 0f;
            float turned = 0f;
            // Smooth turning is the game's own right-stick look. For snap turning, this frame's stick turn is
            // worked out exactly as UFPS did it (vp_FPCamera.UpdateStickLook) and taken back. The game's pad
            // speeds are left alone: its input script re-applies them from the options every frame. The mouse
            // still turns smoothly.
            if (BlockDelayField == null || (float)BlockDelayField.GetValue(_cam) >= 1f)
            {
                float ax = vp_Input.GetAxisRaw("RightStickX");
                float stickTurn = ax * Time.timeScale * (Time.deltaTime * 60f) * Mathf.Lerp(_cam.PadMinSpeed.x, _cam.PadMaxSpeed.x, Mathf.Abs(ax));
                turned -= stickTurn;
            }
            float x = LabStickX ?? vp_Input.GetAxisRaw("RightStickX");
            if (!_snapLatched && Mathf.Abs(x) > 0.6f)
            {
                _snapLatched = true;
                turned += Mathf.Sign(x) * Cfg.SnapAngle.Value;
            }
            else if (_snapLatched && Mathf.Abs(x) < 0.3f) _snapLatched = false;
            if (turned != 0f) _cam.Yaw = Mathf.Repeat(_cam.Yaw + turned + 360f, 720f) - 360f;
            return turned;
        }

        static readonly FieldInfo BlockDelayField = typeof(vp_FPCamera).GetField("blockCameraDelay", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Walking where the head looks. UFPS moves along the body's heading; the stick's direction is turned by
        /// how far the head is turned from the body. Runs after the game's input has set this frame's move
        /// vector and before the next physics step uses it.
        /// </summary>
        public static void SteerMovement(float headYawFromBody)
        {
            if (_controller == null || MoveVectorField == null) return;
            if (!string.Equals(Cfg.WalkDirection.Value, "head", System.StringComparison.OrdinalIgnoreCase)) return;
            // Swimming (the river in Esther's chapter) switches UFPS to free flight, which already moves along the
            // camera's forward direction, and in VR the camera is the head. Steering again would double the turn.
            if (_controller.MotorFreeFly) return;
            var v = (Vector2)MoveVectorField.GetValue(_controller);
            if (v.sqrMagnitude < 1e-6f) return;
            // Stick: x right, y forward. A positive yaw turns to the right.
            float a = -headYawFromBody * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            MoveVectorField.SetValue(_controller, new Vector2(v.x * c - v.y * s, v.x * s + v.y * c));
        }

        /// <summary>The sea renders its reflection and refraction on alternate renders when the camera is high
        /// above it. With two eye renders per frame one eye would always get stale images, so that is off.</summary>
        static readonly List<Transform> _moons = new List<Transform>();

        /// <summary>
        /// The moon is a flat billboard. The game turns it to the camera's rotation once a frame (MoonAngles), which
        /// faces the screen's plane rather than the viewer: invisible on a monitor, but in a headset the disc is
        /// visibly tilted once it is off to the side, and differently in each eye. Just before each eye renders,
        /// it is turned to face that eye's own position, kept upright to the world.
        /// </summary>
        /// <summary>Finds the moon billboards again; called about once a second.</summary>
        static readonly Dictionary<ParticleSystemRenderer, ParticleSystemRenderMode> _icons = new Dictionary<ParticleSystemRenderer, ParticleSystemRenderMode>();
        static float _nextIconScan;
        static string _iconLevel = "";

        /// <summary>
        /// Developer commentary icons (the floating "VO DC Trigger" particles, DE_icons_* textures) are particles in
        /// Billboard mode, which faces the camera's screen plane and so rolls with the head in VR. Vertical
        /// Billboard keeps them upright and turns them only about the vertical axis to face the viewer. VR only;
        /// put back when VR stops. Rescanned after each level load and every few seconds.
        /// </summary>
        public static void UprightIcons()
        {
            if (Application.loadedLevelName == _iconLevel && Time.unscaledTime < _nextIconScan) return;
            _iconLevel = Application.loadedLevelName;
            _nextIconScan = Time.unscaledTime + 5f;
            foreach (var r in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid() || _icons.ContainsKey(r)) continue;
                var m = r.sharedMaterial;
                var tex = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (tex == null || !tex.name.StartsWith("DE_icons") || r.renderMode != ParticleSystemRenderMode.Billboard) continue;
                _icons[r] = r.renderMode;
                r.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            }
        }

        public static void RestoreIcons()
        {
            foreach (var kv in _icons) if (kv.Key != null) kv.Key.renderMode = kv.Value;
            _icons.Clear();
            _iconLevel = "";
        }

        public static void FindMoons()
        {
            _moons.Clear();
            foreach (var m in Object.FindObjectsOfType<MoonAngles>()) _moons.Add(m.transform);
        }

        public static void AimMoons(Vector3 eye)
        {
            for (int i = 0; i < _moons.Count; i++)
            {
                var m = _moons[i];
                if (m == null) continue;
                var dir = m.position - eye;
                if (dir.sqrMagnitude > 1e-6f) m.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }
        }

        public static string MoonState(Vector3 eye)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in _moons) if (m != null) sb.Append(m.name).Append(" angleToEye=").Append(Vector3.Angle(m.forward, m.position - eye).ToString("F2")).Append(" dist=").Append((m.position - eye).magnitude.ToString("F0")).Append(' ');
            return sb.Length > 0 ? sb.ToString() : "no moon";
        }

        public static void FixWater()
        {
            if (TickCheckField == null) return;
            foreach (var w in Object.FindObjectsOfType<Water>())
                if ((bool)TickCheckField.GetValue(w)) { TickCheckField.SetValue(w, false); Log.Info("Water " + w.name + ": alternate-frame updates off for stereo"); }
        }

        static readonly FieldInfo TickCheckField = typeof(Water).GetField("tickCheck", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static string WaterState()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var w in Object.FindObjectsOfType<Water>()) sb.Append(w.name).Append(":tick=").Append(TickCheckField != null ? TickCheckField.GetValue(w) : "?").Append(' ');
            return sb.ToString();
        }

        public static void RestoreAll()
        {
            foreach (var kv in _saved)
            {
                if (kv.Key == null) continue;
                kv.Key.BobAmplitude = kv.Value.bobAmp;
                kv.Key.RotationStrafeRoll = kv.Value.strafeRoll;
                kv.Key.RotationPitchLimit = kv.Value.pitchLimit;
            }
            _saved.Clear();
        }

        public static string State()
        {
            var cam = _cam;
            return $"level={Application.loadedLevelName} chapter={InChapter} menu={MenuOpen} player={(cam != null ? cam.name : "none")} " +
                   (cam != null ? $"yaw={cam.Yaw:F1} pitch={cam.Pitch:F1} padYaw={cam.PadMaxSpeed.x:F2} feet={V(_controller != null ? _controller.transform.position : Vector3.zero)} " : "");
        }

        /// <summary>Lab only: sets the walking input as a stick would, for the movement tests.</summary>
        public static void LabMove(Vector2 v)
        {
            if (_controller == null) return;
            MoveVectorField.SetValue(_controller, v);
            // UFPS scales walking speed by the stick's deflection, which its input script sets every frame.
            _controller.MotorAnalogStickMultiplier = Mathf.Clamp01(v.magnitude);
        }

        public static void LabYaw(float yaw) { if (_cam != null) _cam.Yaw = yaw; }

        public static void LabTeleport(Vector3 p) { if (_controller != null) { _controller.SetPosition(p); _controller.Stop(); } }

        static string V(Vector3 v) => $"{v.x:F3},{v.y:F3},{v.z:F3}";
    }
}
