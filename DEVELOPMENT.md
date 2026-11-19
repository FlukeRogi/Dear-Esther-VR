# Dear Esther VR: development notes

## The game

Dear Esther: Landmark Edition is a **32-bit** Unity **5.3.6f1** game (Mono, .NET 3.5 profile), rendering with **Direct3D 11** (feature level 11.0, Unity's multithreaded renderer), **forward** rendering, no HDR, **gamma** colour space, 4x MSAA. The player is a UFPS (`vp_FPCamera` / `vp_FPController`) rig; most game logic is PlayMaker.

- Everything native is built for Win32. SteamVR's OpenXR runtime is 64-bit only, so SteamVR is reached through **OpenVR**; other headsets through a 32-bit **OpenXR** runtime (Virtual Desktop's VDXR, Meta's).
- Unity 5.3's built-in VR only knows Oculus, so the mod does its own stereo.

## How it works

- **Loading:** Unity Doorstop (`winhttp.dll`) runs `src/Entrypoint.cs` before the engine starts. Unity 5.3 has no `SceneManager.sceneLoaded`, so the mod starts from the first `Camera.onPreCull` callback (a plain static delegate).
- **[src/XrRig.cs](src/XrRig.cs):** after all game logic, every screen camera is taken out of Unity's pass (`Canvas.willRenderCanvases`). At the end of the frame each is moved to each eye and rendered with `Camera.Render()` into that eye's MSAA render texture, with the runtime's own **asymmetric** projection, so culling, shadows, water reflections and image effects all run per eye. The head pose since the last recenter goes on top of the game camera's position and heading; the game decides where the player is. The player camera is left at the head pose between frames so game logic (sound, culling, distance checks) sees it.
- **[src/Game.cs](src/Game.cs):** everything touching UFPS: pitch held level (the headset supplies it), head bob off, head-direction walking (UFPS moves along the body's heading, so the stick vector is turned by the head's yaw; skipped while swimming, which already uses the camera's forward), snap turning (this frame's stick turn is computed with UFPS's own formula and taken back, because the game re-applies its pad sensitivity every frame), the sea's alternate-frame reflections switched off, the moon and commentary icons kept facing each eye.
- **Choosing the runtime ([src/Cfg.cs](src/Cfg.cs) `Backends`):** in auto mode, SteamVR if it is running; otherwise the active 32-bit OpenXR runtime only if its manifest names an existing 32-bit DLL (read from the PE header); then VDXR; then SteamVR through OpenVR. A runtime whose `xrCreateInstance` fails is dropped and the next one tried; "no headset yet" keeps retrying the same one, so a Virtual Desktop player never gets SteamVR started.
- **OpenXR API layers ([src/ApiLayers.cs](src/ApiLayers.cs)):** the loader fails `xrCreateInstance` with `XR_ERROR_FILE_ACCESS_ERROR` (-32) for every runtime when the first implicit layer it tries can't be loaded, and a 32-bit process reads `HKLM\SOFTWARE\WOW6432Node\Khronos\OpenXR\1\ApiLayers\Implicit` plus the `HKCU` key it shares with 64-bit programs. Before the first OpenXR attempt, any layer whose DLL is missing or not 32-bit is switched off in this process by setting its `disable_environment` variable; after a failed create, every layer is switched off and the same runtime tried once more. `ApiLayers = off` / `on` in the settings file override this.
- **[native/DEVRNative.cpp](native/DEVRNative.cpp):** owns the OpenXR session (`xrWaitFrame`/`xrLocateViews` on the main thread; swapchains, `xrBeginFrame`/`xrEndFrame` on Unity's render thread via `GL.IssuePluginEvent`). [native/OpenVRBackend.cpp](native/OpenVRBackend.cpp) does the same for SteamVR. The floor height comes from the STAGE space (OpenXR) or the standing pose (OpenVR).
- **Interface:** screen-space canvases move onto one camera that draws into a texture, shown as an OpenXR quad layer or an OpenVR overlay. The VR options page ([src/VrMenu.cs](src/VrMenu.cs)) is built from the game's own Audio page and option rows.
- **Fades and loads:** the game's own fade quads are camera children that already cover each eye; PlayMaker OnGUI fades are read and drawn into the eyes; level loads (`Application.isLoadingLevel`) black the eyes ([src/Fade.cs](src/Fade.cs)).

## Things that are easy to get wrong

- **Unity 5.3 swaps a render texture's D3D11 resource** on resolution or window changes and around level loads, and frees the old one. Copying from a stale pointer crashed the NVIDIA driver at random. The bridge holds its own reference on every texture it copies from (released on the render thread), and the mod rebinds after any change. Unity creates its D3D11 device single-threaded.
- **No flipped Blit and no run-time shader compilation in 5.3:** the vertical flip draws one quad through `Hidden/BlitCopy`; Shadow Depth reuses Amplify Color with a generated lookup table; the fade colour uses `UI/Default` (with `_ClipRect` opened up, or it clips everything).
- **UFPS doesn't pose the camera at time scale 0**, so the rig keeps the game's last pose rather than adding the head twice.
- **The game's localiser rewrites any UI text it doesn't know** into `#NotInStringTable#…`, so the VR page re-applies its own labels.
- **Distant blur in a headset is the mip level** (fewer pixels per degree than a 4K screen), not filtering: the game already forces 16x anisotropic filtering and has no LOD groups. Texture Sharpness is a mip bias.
- **The underwater effect** (`RefractiveOverlay`) zooms by about 15% as well as warping; both scale with its `_RefractionAmount`.

## Build

Needs Visual Studio 2022 C++ build tools, CMake and the .NET 8 SDK, with the game installed.

```bash
powershell -ExecutionPolicy Bypass -File tools/get_deps.ps1
```

```bash
cmake -S native -B native/build -G "Visual Studio 17 2022" -A Win32
```

```bash
cmake --build native/build --config Release
```

```bash
dotnet build DearEstherVR.csproj -c Release
```

The C# build deploys the mod, Doorstop and the native DLLs to the game folder. `tools/make_package.ps1` builds the release zip in `dist`.

## Test

Both drive the real game with no headset:

```bash
powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File tests/steamvr_test.ps1
```

- **Mock headset** ([native/mock/MockRuntime.cpp](native/mock/MockRuntime.cpp)): a small 32-bit OpenXR runtime with a scripted head and deliberately asymmetric views, which saves both eyes and the interface panel on request and reports OpenXR API misuse. `run_tests.ps1` uses it for about 70 checks, including projection accuracy (the asymmetric eye against a wider symmetric render), eye-to-eye consistency, disparity, orientation, heights, walking, turning, fades, the VR page, Shadow Depth, the moon and the underwater zoom.
- **SteamVR null HMD:** `steamvr_test.ps1` switches SteamVR to its built-in virtual headset and restores your settings afterwards.
- **Runtime fallback:** `runtime_fallback.ps1` stages a broken registered 32-bit OpenXR runtime (missing, 64-bit only, or failing to start, the players' error -32) through the test-only `TestActiveRuntime` and `TestVdxrRuntime` settings, and checks the mod explains it, skips it and connects through the next way, without starting SteamVR. The registry is never touched.
- **API layers:** `api_layers.ps1` registers a test layer under `HKCU\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit` (64-bit only, missing, or failing to load), with an `enable_environment` so it is inert for every other program, checks that the loader really fails with -32 when it is left on and that the mod connects otherwise, and removes it again.
- **Test mode** (`LabCommands = true` in the settings file) is a command-file channel in `%USERPROFILE%\AppData\LocalLow\DearEstherVR\lab`. Players never have it on; the installer switches it off.
- `tests/texture_quality.ps1` measures distant sharpness at each Texture Sharpness level.
