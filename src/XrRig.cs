using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DearEstherVR
{
    /// <summary>
    /// Renders the game once per eye from the headset's own eye poses and projections, and presents the game's
    /// interface on a panel.
    ///
    /// Each frame, after all game logic, every camera that would draw to the screen is taken out of Unity's own
    /// render pass. At the end of the frame each one is moved to each eye in turn and rendered into that eye's
    /// texture with all of its image effects, so culling, shadows, water reflections and post effects all run
    /// separately for each eye. The head's movement since the last recenter goes on top of the camera's own
    /// position and heading, so the game still decides where the player stands and which way the body faces.
    ///
    /// The player camera is left at the head's pose between frames, so game logic that reads Camera.main (sound,
    /// shadow projector culling, distance checks) sees where the player actually looks. UFPS sets that camera
    /// from scratch in every LateUpdate, so this never accumulates.
    /// </summary>
    public class XrRig : MonoBehaviour
    {
        public static XrRig I;

        const int EvCreateSession = 1, EvSubmit = 2, EvResize = 3, EvShutdown = 4;

        bool _loaded, _created, _active, _hasCenter, _camsHidden, _inRender, _resizeRequested, _steamVrSession;
        float _nextCreate, _retryDelay = 2.5f;
        IntPtr _ev;
        readonly RenderTexture[] _eye = new RenderTexture[2], _submit = new RenderTexture[2];
        RenderTexture _ui, _uiSubmit;
        int _eyeW, _eyeH, _recW, _recH, _uiW, _uiH, _msaa = 1;
        Camera _uiCam;
        readonly List<Camera> _cams = new List<Camera>();
        readonly Dictionary<Camera, Pose> _base = new Dictionary<Camera, Pose>();
        readonly Dictionary<Camera, Pose> _leftAt = new Dictionary<Camera, Pose>(), _gamePose = new Dictionary<Camera, Pose>();
        readonly Dictionary<Canvas, KeyValuePair<RenderMode, Camera>> _routed = new Dictionary<Canvas, KeyValuePair<RenderMode, Camera>>();
        readonly Dictionary<Behaviour, bool> _suppressed = new Dictionary<Behaviour, bool>();
        float _nextSweep;
        int _sceneSignature;
        string _level = "";

        struct Pose { public Vector3 pos; public Quaternion rot; }

        // Tracking-space values, in Unity's left-handed convention, converted from OpenXR on arrival.
        Vector3 _centerPos;
        float _centerYaw;
        bool _recenterRequested;
        Vector3 _headPos;
        Quaternion _headRot = Quaternion.identity;
        float _headYaw;
        XrFrame _frame;
        bool _haveFrame;

        // Where the interface panel is, in tracking space.
        bool _menuOpen;
        Vector3 _panelBase, _panelPos;
        float _panelYaw;

        int _savedVSync, _savedFrameRate;
        bool _savedRunInBackground;

        // Diagnostics, read by the lab channel.
        public bool Active => _active;
        public int EyeWidth => _eyeW;
        public int EyeHeight => _eyeH;
        public int Msaa => _msaa;
        public bool MenuOpen => _menuOpen;
        public float FrameMs;
        public string LastCameras = "";
        public Vector3 PanelPos => _panelPos;
        public float PanelYaw => _panelYaw;
        public float CenterYaw => _centerYaw;
        public Vector3 CenterPos => _centerPos;
        public bool FloorKnown => _haveFrame && _frame.floorValid != 0;
        public float FloorY => _frame.floorY;
        public string HeightModeUsed = "";
        public Vector3 LastEyeWorld0, LastEyeWorld1, LastBasePos;
        public Quaternion LastEyeRot0;
        public float HeadYawFromBody;

        void Start()
        {
            I = this;
            if (Cfg.VrDisabled)
            {
                Log.Info("VR is off (Runtime = off), playing flat");
                return;
            }
            _loaded = Native.Load();
            if (!_loaded) return;
            _ev = Native.DVR_GetRenderEvent();
            Canvas.willRenderCanvases += OnWillRenderCanvases;
            StartCoroutine(Loop());
        }

        bool _symApplied;
        string _heightModeSeen;

        /// <summary>The fade drawn over both eyes this frame (alpha 0: none). See Fade.</summary>
        public Color FadeColor;

        IEnumerator Loop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                try { Tick(); }
                catch (Exception e) { Log.Error(e); }
                Cfg.Flush();
            }
        }

        void OnApplicationQuit()
        {
            if (_created) Native.DVR_RequestExit();
            if (_active) Game.RestoreAll();
        }

        void Tick()
        {
            if (!_created)
            {
                TryCreate();
                return;
            }
            if (_bindIn >= 0)
            {
                PrimeTextures();
                if (_bindIn-- == 0) BindTextures();
            }
            else if (NeedsRebind()) _bindIn = 2;
            else if (Time.frameCount % 90 == 0) CheckBindings();
            float t0 = Time.realtimeSinceStartup;
            XrFrame f;
            Native.DVR_BeginFrame(out f);
            Native.PumpLogs();
            if (f.running == 0)
            {
                if (_active) Deactivate();
                if (Native.DVR_ExitRequested() != 0) HandleSessionEnded();
                return;
            }
            if (!_active) Activate();
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            if (Input.GetKeyDown(KeyCode.F9)) _recenterRequested = true;
            if (Application.loadedLevelName != _level)
            {
                _level = Application.loadedLevelName;
                // A new level places the player afresh, so the head starts out facing the body again.
                _recenterRequested = true;
                Log.Info("Level " + _level);
            }
            // Switching to seated takes the current head height as the game's eye height, which needs a recenter.
            string heightMode = (Cfg.HeightMode.Value ?? "").Trim().ToLowerInvariant();
            if (heightMode != _heightModeSeen)
            {
                if (_heightModeSeen != null && heightMode == "seated") { _recenterRequested = true; Log.Info("Seated mode chosen: recentering"); }
                _heightModeSeen = heightMode;
            }
            if (f.viewsValid != 0)
            {
                _frame = f;
                _haveFrame = true;
                CheckEyeSize(f);
                ReadHead(f);
                // A recenter waits for a really tracked head. Right after the session starts, a runtime can
                // report a placeholder pose (VDXR gave one on the floor), and recentering on it put the logo
                // screen at floor height.
                if ((!_hasCenter || _recenterRequested) && HeadTracked(f))
                {
                    bool first = !_hasCenter;
                    Recenter();
                    // Tracking settles over the first moments, so the first recenter is repeated shortly after.
                    if (first) _settleRecenterAt = Time.realtimeSinceStartup + 1.5f;
                }
                if (_settleRecenterAt > 0f && Time.realtimeSinceStartup >= _settleRecenterAt && HeadTracked(f))
                {
                    _settleRecenterAt = -1f;
                    Recenter();
                }
                UpdateHeight();
            }
            if (_resizeRequested) ApplyResize();
            UpdatePanel();

            if (f.shouldRender != 0 && f.viewsValid != 0)
            {
                _inRender = true;
                string skip = Cfg.Skip.Value ?? "";
                Fade.HideLoadingCanvas();
                FadeColor = Fade.Current();
                try { if (!skip.Contains("eyes")) RenderEyes(f); if (!skip.Contains("ui")) RenderUi(); }
                finally { _inRender = false; }
                MirrorToWindow();
            }
            if (!(Cfg.Skip.Value ?? "").Contains("submit")) GL.IssuePluginEvent(_ev, EvSubmit);
            ShowCameras();
            FrameMs = Mathf.Lerp(FrameMs, (Time.realtimeSinceStartup - t0) * 1000f, 0.05f);

            if (Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + 1f;
                WindowMode.Poll();
                Game.Refresh();
                Game.FixWater();
                TextureQuality.Apply();
                Underwater.Apply();
                Game.FindMoons();
                Game.UprightIcons();
                int msaa = Cfg.Antialiasing.Value >= 2 ? (int)Cfg.Antialiasing.Value : Cfg.MsaaOverride.Value >= 1 ? (int)Cfg.MsaaOverride.Value : Mathf.Max(1, QualitySettings.antiAliasing);
                if (msaa != _msaa) { _msaa = msaa; Log.Info("Eye anti-aliasing " + msaa + "x"); CreateEyeTextures(_eyeW, _eyeH); }
                bool sym = Cfg.SymmetricEyes.Value;
                if (sym != _symApplied) { _symApplied = sym; Native.DVR_SetSymmetric(sym ? 1 : 0); Log.Info("Symmetric eye frustums " + (sym ? "on" : "off")); }
                int sig = SceneSignature();
                if (sig != _sceneSignature || Time.frameCount % 5 == 0)
                {
                    _sceneSignature = sig;
                    RouteCanvases();
                }
            }
        }

        static int SceneSignature()
        {
            int sig = SceneManager.sceneCount;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sig = sig * 31 + s.buildIndex * (s.isLoaded ? 2 : 1);
            }
            return sig;
        }

        void TryCreate()
        {
            if (Time.unscaledTime < _nextCreate) return;
            if ((Cfg.Skip.Value ?? "").Contains("delay") && Time.realtimeSinceStartup < 15f) return;
            // Back off while no headset is connected, so a long flat session stays quiet.
            _retryDelay = Mathf.Min(_retryDelay * 2f, 30f);
            _nextCreate = Time.unscaledTime + _retryDelay;
            // Connecting launches SteamVR, so it must not come back after the player quit it.
            if (_steamVrSession && !Native.SteamVrRunning) return;
            string manifest;
            int backend = Cfg.ResolveBackend(out manifest);
            int r = Native.DVR_Create(manifest, backend);
            Native.PumpLogs();
            if (r != 0)
            {
                if (r == 2) _nextCreate = float.MaxValue;
                return;
            }
            _created = true;
            _retryDelay = 2.5f;
            _steamVrSession = Native.RuntimeName.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) >= 0;
            Native.DVR_GetRecommended(out _recW, out _recH);
            _symApplied = Cfg.SymmetricEyes.Value;
            Native.DVR_SetSymmetric(_symApplied ? 1 : 0);
            { var sk = Cfg.Skip.Value ?? ""; Native.DVR_SetDebug((sk.Contains("copy") ? 1 : 0) | (sk.Contains("mtprotect") ? 4 : 0) | (sk.Contains("flush") ? 8 : 0) | (sk.Contains("crashstacks") ? 16 : 0) | (sk.Contains("trace") ? 32 : 0)); }
            _msaa = Cfg.Antialiasing.Value >= 2 ? (int)Cfg.Antialiasing.Value : Cfg.MsaaOverride.Value >= 1 ? (int)Cfg.MsaaOverride.Value : Mathf.Max(1, QualitySettings.antiAliasing);
            _sessionCreated = false;
            CreateEyeTextures(Mathf.RoundToInt(_recW * Cfg.ResolutionScale.Value), Mathf.RoundToInt(_recH * Cfg.ResolutionScale.Value));
            CreateUiTextures();
        }

        // Unity 5.3's multithreaded renderer creates a render texture's D3D11 resource on the render thread, some
        // time after Create() returns, and GetNativeTexturePtr can hand out a pointer that isn't the final
        // resource yet. Copying from that pointer crashed the graphics driver. So new textures are drawn to for a
        // few frames first, and only then are their pointers given to the native side. They are checked again
        // every so often, and rebound if Unity ever replaced one.
        int _bindIn = -1;
        bool _sessionCreated;
        readonly IntPtr[] _boundEye = new IntPtr[2];
        IntPtr _boundUi;

        void PrimeTextures()
        {
            var prev = RenderTexture.active;
            foreach (var rt in new[] { _eye[0], _eye[1], _submit[0], _submit[1], _ui, _uiSubmit })
            {
                if (rt == null) continue;
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
            }
            RenderTexture.active = prev;
        }

        void BindTextures()
        {
            _bindW = Screen.width; _bindH = Screen.height; _bindFull = Screen.fullScreen; _bindLevel = Application.loadedLevel;
            foreach (var rt in new[] { _eye[0], _eye[1], _submit[0], _submit[1], _ui, _uiSubmit })
                if (rt != null && !rt.IsCreated()) rt.Create();
            _boundEye[0] = _submit[0].GetNativeTexturePtr();
            _boundEye[1] = _submit[1].GetNativeTexturePtr();
            _boundUi = _uiSubmit.GetNativeTexturePtr();
            Native.DVR_SetEyeTextures(_boundEye[0], _boundEye[1], _eyeW, _eyeH);
            Native.DVR_SetUiTexture(_boundUi, _uiW, _uiH);
            GL.IssuePluginEvent(_ev, _sessionCreated ? EvResize : EvCreateSession);
            _sessionCreated = true;
        }

        // What the textures were last bound under. Unity 5.3 swaps render textures' D3D11 resources when the
        // window or resolution changes and around level loads, so any change rebinds them (the native side keeps
        // the old ones alive until then).
        int _bindW, _bindH, _bindLevel = -1;
        bool _bindFull;

        bool NeedsRebind()
        {
            bool changed = Screen.width != _bindW || Screen.height != _bindH || Screen.fullScreen != _bindFull || Application.loadedLevel != _bindLevel;
            foreach (var rt in new[] { _submit[0], _submit[1], _uiSubmit })
                if (rt != null && !rt.IsCreated()) { changed = true; rt.Create(); }
            return changed;
        }

        void CheckBindings()
        {
            if (_submit[0] == null || _uiSubmit == null) return;
            if (_submit[0].GetNativeTexturePtr() == _boundEye[0] && _submit[1].GetNativeTexturePtr() == _boundEye[1] && _uiSubmit.GetNativeTexturePtr() == _boundUi) return;
            Log.Warn("Unity replaced a submitted texture's D3D11 resource; rebinding");
            BindTextures();
        }

        /// <summary>The runtime ended the session. The game stays playable flat, and VR resumes if the runtime
        /// returns.</summary>
        void HandleSessionEnded()
        {
            Log.Info("The VR session ended, and will reconnect when the headset's runtime is available");
            GL.IssuePluginEvent(_ev, EvShutdown);
            _created = false;
            _nextCreate = Time.unscaledTime + 5f;
        }

        static RenderTexture MakeTexture(string name, int w, int h, int depth, int msaa = 1)
        {
            var rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = name, hideFlags = HideFlags.HideAndDontSave, antiAliasing = msaa };
            rt.Create();
            return rt;
        }

        static void Drop(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        void CreateEyeTextures(int w, int h)
        {
            _eyeW = Mathf.Clamp(w, 256, 4096) & ~1;
            _eyeH = Mathf.Clamp(h, 256, 4096) & ~1;
            // The native side lets go of the old textures first; frames go out without a world layer until
            // the new ones are bound.
            Native.DVR_SetEyeTextures(IntPtr.Zero, IntPtr.Zero, _eyeW, _eyeH);
            for (int i = 0; i < 2; i++)
            {
                Drop(ref _eye[i]);
                Drop(ref _submit[i]);
                // The game's own anti-aliasing is MSAA (4x at its top quality), so the eyes get the same.
                _eye[i] = MakeTexture("DEVR Eye " + i, _eyeW, _eyeH, 24, _msaa);
                _submit[i] = MakeTexture("DEVR Submit " + i, _eyeW, _eyeH, 0);
            }
            _bindIn = 3;
            Log.Info($"Eye render size {_eyeW}x{_eyeH} MSAA {_msaa}x, headset recommends {_recW}x{_recH}");
        }

        void CreateUiTextures()
        {
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            _uiH = 1080;
            _uiW = Mathf.RoundToInt(_uiH * aspect) & ~1;
            Native.DVR_SetUiTexture(IntPtr.Zero, _uiW, _uiH);
            Drop(ref _ui);
            Drop(ref _uiSubmit);
            _ui = MakeTexture("DEVR UI", _uiW, _uiH, 24);
            _uiSubmit = MakeTexture("DEVR UI Submit", _uiW, _uiH, 0);
            _bindIn = 3;
            if (_uiCam != null) _uiCam.targetTexture = _ui;
        }

        /// <summary>Sizes the eye textures so a widened (symmetric) view keeps the pixel density the headset
        /// recommends. With the headset's own views the size is just the recommended size.</summary>
        void CheckEyeSize(XrFrame f)
        {
            float rawW = f.rawLeftR - f.rawLeftL, rawH = f.rawLeftU - f.rawLeftD;
            if (rawW <= 0 || rawH <= 0) return;
            float scale = Cfg.ResolutionScale.Value;
            int w = Mathf.Clamp(Mathf.RoundToInt(_recW * scale * (f.leftR - f.leftL) / rawW), 256, 4096) & ~1;
            int h = Mathf.Clamp(Mathf.RoundToInt(_recH * scale * (f.leftU - f.leftD) / rawH), 256, 4096) & ~1;
            if (Mathf.Abs(w - _eyeW) > 8 || Mathf.Abs(h - _eyeH) > 8) { _wantW = w; _wantH = h; _resizeRequested = true; }
        }
        int _wantW, _wantH;

        void ApplyResize()
        {
            _resizeRequested = false;
            if (_wantW <= 0) return;
            CreateEyeTextures(_wantW, _wantH);
        }

        void Activate()
        {
            _active = true;
            _hasCenter = false;
            Log.Info($"VR session running on {Native.RuntimeName} ({Native.SystemName})");
            var go = new GameObject("DEVR UI Camera");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            _uiCam = go.AddComponent<Camera>();
            _uiCam.enabled = false;
            _uiCam.orthographic = true;
            _uiCam.orthographicSize = 1;
            _uiCam.nearClipPlane = 0.1f;
            _uiCam.farClipPlane = 10f;
            _uiCam.clearFlags = CameraClearFlags.SolidColor;
            _uiCam.backgroundColor = new Color(0, 0, 0, 0);
            _uiCam.cullingMask = 1 << 5;
            _uiCam.hdr = false;
            _uiCam.useOcclusionCulling = false;
            _uiCam.targetTexture = _ui;
            RouteCanvases();
            _savedVSync = QualitySettings.vSyncCount;
            _savedFrameRate = Application.targetFrameRate;
            _savedRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            Game.Refresh();
            Game.FixWater();
        }

        void Deactivate()
        {
            _active = false;
            Log.Info("VR session stopped, returning to the flat game");
            ShowCameras();
            foreach (var kv in _routed)
            {
                if (kv.Key == null) continue;
                kv.Key.renderMode = kv.Value.Key;
                kv.Key.worldCamera = kv.Value.Value;
            }
            _routed.Clear();
            RestoreEffects();
            Fade.RestoreLoadingCanvas();
            TextureQuality.Restore();
            Underwater.Restore();
            Game.RestoreIcons();
            ShadowDepth.Remove();
            Game.RestoreAll();
            if (_uiCam != null) Destroy(_uiCam.gameObject);
            QualitySettings.vSyncCount = _savedVSync;
            Application.targetFrameRate = _savedFrameRate;
            Application.runInBackground = _savedRunInBackground;
        }

        /// <summary>Screen-space canvases draw straight to the window and never reach the eye textures, so they
        /// are moved onto the interface camera.</summary>
        void RouteCanvases()
        {
            if (_uiCam == null) return;
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c == null || !c.isRootCanvas || !c.gameObject.scene.IsValid()) continue;
                if (c.renderMode == RenderMode.WorldSpace || (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == _uiCam)) continue;
                if (!_routed.ContainsKey(c)) _routed[c] = new KeyValuePair<RenderMode, Camera>(c.renderMode, c.worldCamera);
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = _uiCam;
                c.planeDistance = 1f;
            }
            var dead = new List<Canvas>();
            foreach (var c in _routed.Keys) if (c == null) dead.Add(c);
            foreach (var c in dead) _routed.Remove(c);
        }

        /// <summary>Effects that don't belong in a headset. The game can switch them back on, so this runs every
        /// frame, and what the game last asked for is restored when VR stops.</summary>
        void SuppressEffects()
        {
            foreach (var cam in _cams)
            {
                if (cam == null) continue;
                foreach (var b in cam.GetComponents<MonoBehaviour>())
                {
                    if (b == null || !ShouldSuppress(b.GetType().Name)) continue;
                    if (b.enabled) { _suppressed[b] = true; b.enabled = false; }
                    else if (!_suppressed.ContainsKey(b)) _suppressed[b] = false;
                }
            }
        }

        static bool ShouldSuppress(string type)
        {
            switch (type)
            {
                // A deliberate double image, which in a headset reads as broken stereo.
                case "CC_DoubleVision": return !Cfg.DoubleVision.Value;
                default: return false;
            }
        }

        void RestoreEffects()
        {
            foreach (var kv in _suppressed) if (kv.Key != null && kv.Value) kv.Key.enabled = true;
            _suppressed.Clear();
        }

        /// <summary>
        /// Runs after every Update and LateUpdate, just before Unity's render pass. The screen cameras are taken
        /// out of that pass, their poses as the game left them are kept as the base for the eyes, and the player
        /// camera is moved to the head so the rest of the frame (and the next frame's game logic) sees it there.
        /// </summary>
        void OnWillRenderCanvases()
        {
            if (!_active || _inRender || _camsHidden) return;
            _camsHidden = true;
            _cams.Clear();
            _base.Clear();
            Game.Refresh();
            foreach (var c in Camera.allCameras)
            {
                if (c == _uiCam || !c.enabled || c.targetTexture != null) continue;
                _cams.Add(c);
                var now = new Pose { pos = c.transform.position, rot = c.transform.rotation };
                // UFPS doesn't move the camera while the game is paused (time scale 0), so it may still be where
                // the mod left it last frame. That is the head's pose, not the game's, so the game's last pose
                // is used again instead of adding the head on top a second time.
                Pose left, game;
                if (_leftAt.TryGetValue(c, out left) && left.pos == now.pos && left.rot == now.rot && _gamePose.TryGetValue(c, out game)) now = game;
                _base[c] = now;
                _gamePose[c] = now;
                c.enabled = false;
            }
            _leftAt.Clear();
            _cams.Sort((a, b) => a.depth.CompareTo(b.depth));
            SuppressEffects();
            float turned = Game.ApplyVr();
            if (_hasCenter)
            {
                HeadYawFromBody = Mathf.DeltaAngle(_centerYaw, _headYaw);
                Game.SteerMovement(HeadYawFromBody);
                foreach (var c in _cams)
                {
                    if (!Game.IsPlayerCamera(c)) continue;
                    var b = _base[c];
                    if (turned != 0f)
                    {
                        // UFPS posed the camera before the snap (or the cancelled stick turn), so this frame
                        // already shows the new heading too.
                        b.rot = Quaternion.Euler(0, turned, 0) * b.rot;
                        _base[c] = b;
                        _gamePose[c] = b;
                    }
                    var anchor = Anchor(b.rot);
                    c.transform.position = EyeWorld(b.pos, anchor, _headPos);
                    c.transform.rotation = anchor * (Quaternion.Euler(0, -_centerYaw, 0) * _headRot);
                    _leftAt[c] = new Pose { pos = c.transform.position, rot = c.transform.rotation };
                }
            }
        }

        void ShowCameras()
        {
            if (!_camsHidden) return;
            _camsHidden = false;
            var names = new System.Text.StringBuilder();
            foreach (var c in _cams)
            {
                if (c == null) continue;
                c.enabled = true;
                names.Append(c.name).Append('@').Append(c.depth).Append(' ');
            }
            LastCameras = names.ToString();
        }

        static Vector3 U(XrPose p) => new Vector3(p.px, p.py, -p.pz);
        static Quaternion UQ(XrPose p) => new Quaternion(-p.qx, -p.qy, p.qz, p.qw);
        static float YawOf(Quaternion q) { var fw = q * Vector3.forward; return Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg; }

        /// <summary>The body's heading: the camera's yaw, with its pitch and roll dropped.</summary>
        static Quaternion Anchor(Quaternion camRot) => Quaternion.Euler(0, camRot.eulerAngles.y, 0);

        float WorldPerMetre => 1f / Mathf.Max(0.01f, Cfg.WorldScale.Value);

        /// <summary>Where a tracked point (in tracking space) is in the world, for a camera at basePos.</summary>
        Vector3 EyeWorld(Vector3 basePos, Quaternion anchor, Vector3 tracked)
        {
            var local = Quaternion.Euler(0, -_centerYaw, 0) * (tracked - _centerPos) * WorldPerMetre;
            return basePos + anchor * local + Vector3.up * Cfg.HeightOffset.Value;
        }

        void ReadHead(XrFrame f)
        {
            _headPos = (U(f.left) + U(f.right)) * 0.5f;
            _headRot = UQ(f.left);
            _headYaw = YawOf(_headRot);
        }

        public void RequestRecenter() { _recenterRequested = true; }

        float _settleRecenterAt = -1f;

        /// <summary>The head position is a real tracked one: the runtime says so, and when it knows the floor,
        /// the head is not lying on it.</summary>
        bool HeadTracked(XrFrame f) => f.positionTracked != 0 && !(f.floorValid != 0 && _headPos.y - f.floorY < 0.3f);

        void Recenter()
        {
            _recenterRequested = false;
            _hasCenter = true;
            _centerPos = _headPos;
            _centerYaw = _headYaw;
            UpdateHeight();
            // Recentering also brings the panel (logos, menus, the interface) to eye level straight ahead,
            // whether or not a menu is open.
            PlacePanel(_headPos, _headYaw);
            Log.Info($"Recentered: head {_headPos} yaw {_centerYaw:F1} height mode {HeightModeUsed}" + (FloorKnown ? $", head {_headPos.y - _frame.floorY:F2} m above the floor" : ""));
        }

        /// <summary>
        /// Standing: the real floor sits on the game's floor, so the game camera's height (feet plus the game's
        /// eye height) is replaced by the player's real eye height. Seated, or when the runtime doesn't know
        /// where the floor is: the head's height at the last recenter is the game's eye height.
        /// </summary>
        void UpdateHeight()
        {
            bool standing = string.Equals(Cfg.HeightMode.Value, "standing", StringComparison.OrdinalIgnoreCase) && FloorKnown;
            HeightModeUsed = standing ? "standing" : "seated";
            if (standing) _centerPos.y = _frame.floorY + Game.EyeHeight * Cfg.WorldScale.Value;
        }

        /// <summary>During play the panel sits ahead of the recentered direction. A menu opens in front of wherever
        /// the player is looking, and stays put until it closes.</summary>
        void UpdatePanel()
        {
            bool open = Game.MenuOpen;
            if (open != _menuOpen)
            {
                _menuOpen = open;
                if (open) PlacePanel(_headPos, _headYaw);
                else PlacePanel(_headPos, _centerYaw);
            }
            float d = _menuOpen ? Cfg.MenuDistance.Value : Cfg.HudDistance.Value;
            float size = _menuOpen ? Cfg.MenuSize.Value : Cfg.HudSize.Value;
            // At size 1 the panel covers 60 degrees vertically.
            float h = 2f * d * Mathf.Tan(30f * Mathf.Deg2Rad) * size;
            float w = h * _uiW / Mathf.Max(1, _uiH);
            var rot = Quaternion.Euler(0, _panelYaw, 0);
            var pos = _panelBase + rot * Vector3.forward * d;
            _panelPos = pos;
            // Back to OpenXR's right-handed convention. A quad is seen from its +Z side, which faces the head.
            Native.DVR_SetUiQuad(pos.x, pos.y, -pos.z, -rot.x, -rot.y, rot.z, rot.w, w, h, _ui != null ? 1 : 0);
        }

        void PlacePanel(Vector3 head, float yaw)
        {
            _panelBase = head;
            _panelYaw = yaw;
        }

        void RenderEyes(XrFrame f)
        {
            var eyes = new[] { f.left, f.right };
            var tangents = new[] { new Vector4(f.leftL, f.leftR, f.leftU, f.leftD), new Vector4(f.rightL, f.rightR, f.rightU, f.rightD) };
            ShadowDepth.Prepare(_cams);
            RenderEyeLoop(eyes, tangents);
        }

        void RenderEyeLoop(XrPose[] eyes, Vector4[] tangents)
        {
            for (int i = 0; i < 2; i++)
            {
                var eyeRot = Quaternion.Euler(0, -_centerYaw, 0) * UQ(eyes[i]);
                var t = tangents[i];
                var rt = _eye[i];
                bool drew = false;
                // Fully faded (a load, or a fade to black that has finished): nothing in the world can show.
                bool opaqueFade = FadeColor.a >= 0.999f;
                foreach (var cam in _cams)
                {
                    if (cam == null || opaqueFade) continue;
                    Pose b;
                    if (!_base.TryGetValue(cam, out b)) continue;
                    var tr = cam.transform;
                    var leftPos = tr.position;
                    var leftRot = tr.rotation;
                    float baseFov = cam.fieldOfView;
                    var anchor = Anchor(b.rot);
                    // The camera really moves to the eye for this render, so shaders, water reflections and
                    // effects that read its position see the eye.
                    tr.position = EyeWorld(b.pos, anchor, U(eyes[i]));
                    tr.rotation = anchor * eyeRot;
                    Game.AimMoons(tr.position);
                    // A symmetric frustum that contains the eye's view sets the field of view and aspect, which
                    // Unity's shadow fitting reads; the projection the eye really has then overrides rendering.
                    float hx = Mathf.Max(Mathf.Abs(t.x), Mathf.Abs(t.y)), vy = Mathf.Max(Mathf.Abs(t.z), Mathf.Abs(t.w));
                    cam.fieldOfView = 2f * Mathf.Atan(vy) * Mathf.Rad2Deg;
                    cam.aspect = hx / vy;
                    float n = cam.nearClipPlane;
                    cam.projectionMatrix = Frustum(t.x * n, t.y * n, t.w * n, t.z * n, n, cam.farClipPlane);
                    if (i == 0) { LastEyeWorld0 = tr.position; LastEyeRot0 = tr.rotation; LastBasePos = b.pos; } else LastEyeWorld1 = tr.position;
                    cam.targetTexture = rt;
                    cam.Render();
                    drew = true;
                    cam.targetTexture = null;
                    cam.ResetProjectionMatrix();
                    cam.ResetAspect();
                    cam.fieldOfView = baseFov;
                    // Back where the frame left it: the head for the player camera, the game's pose for others.
                    tr.position = leftPos;
                    tr.rotation = leftRot;
                }
                if (!drew)
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    GL.Clear(true, true, opaqueFade ? new Color(FadeColor.r, FadeColor.g, FadeColor.b, 1f) : Color.black);
                    RenderTexture.active = prev;
                }
                CopyFlipped(rt, _submit[i]);
                if (!opaqueFade) Fade.Draw(_submit[i], FadeColor);
            }
        }

        /// <summary>
        /// Copies a render texture with its rows flipped. Unity stores render textures bottom-up on D3D11, while
        /// OpenXR and OpenVR read row 0 as the top. Unity 5.3 has no Blit with a scale, and can't compile shaders
        /// at run time, so this draws one quad with flipped texture coordinates through Graphics.Blit's own
        /// shader (Hidden/BlitCopy, which every build contains). That shader doesn't blend, so the interface's
        /// alpha arrives intact.
        /// </summary>
        static void CopyFlipped(RenderTexture src, RenderTexture dst)
        {
            if (!Cfg.DrawCopy.Value) { Graphics.Blit(src, dst); return; }
            if (_copyMat == null)
            {
                var sh = Shader.Find("Hidden/BlitCopy");
                if (sh == null) { Log.Error("Hidden/BlitCopy is missing, images will be upside down"); Cfg.DrawCopy.Value = false; Graphics.Blit(src, dst); return; }
                _copyMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            _copyMat.mainTexture = src;
            GL.PushMatrix();
            GL.LoadOrtho();
            _copyMat.SetPass(0);
            float top = FlipRows ? 1f : 0f, bottom = 1f - top;
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0, top); GL.Vertex3(0, 0, 0);
            GL.TexCoord2(0, bottom); GL.Vertex3(0, 1, 0);
            GL.TexCoord2(1, bottom); GL.Vertex3(1, 1, 0);
            GL.TexCoord2(1, top); GL.Vertex3(1, 0, 0);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }
        static Material _copyMat;
        internal static bool FlipRows = true;

        /// <summary>The desktop window shows the left eye with the interface over it.</summary>
        void MirrorToWindow()
        {
            RenderTexture.active = null;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            float eyeAspect = (float)_eyeW / _eyeH, winAspect = (float)Screen.width / Math.Max(1, Screen.height);
            // The eye is shown whole, centred, at its own aspect ratio.
            float w = Screen.width, h = Screen.height;
            if (eyeAspect < winAspect) w = h * eyeAspect; else h = w / eyeAspect;
            GL.Clear(false, true, Color.black);
            Graphics.DrawTexture(new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h), _eye[0]);
            if (_ui != null) Graphics.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _ui);
            GL.PopMatrix();
        }

        void RenderUi()
        {
            if (_uiCam == null || _ui == null) return;
            if (Screen.height > 0 && Mathf.Abs((float)Screen.width / Screen.height - (float)_uiW / _uiH) > 0.01f) CreateUiTextures();
            _uiCam.Render();
            CopyFlipped(_ui, _uiSubmit);
        }

        internal static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
        {
            var m = new Matrix4x4();
            m.m00 = 2f * n / (r - l);
            m.m02 = (r + l) / (r - l);
            m.m11 = 2f * n / (t - b);
            m.m12 = (t + b) / (t - b);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -2f * f * n / (f - n);
            m.m32 = -1f;
            return m;
        }
    }
}
