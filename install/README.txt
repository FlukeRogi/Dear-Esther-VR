Dear Esther VR 1.0.1
====================

Plays Dear Esther: Landmark Edition in a PC VR headset, in full stereoscopic 3D: the island is rendered separately
for each eye, from your head's own position, with the headset's own lens views. Head tracking is full 6DoF.
Works with SteamVR, and with Virtual Desktop's own VDXR runtime.

It is for Dear Esther: Landmark Edition (the 2017 release on Steam), only. It does not work with the original
2012 Dear Esther, or with the Half-Life 2 mod it grew from.

It is an unofficial fan mod, not made or endorsed by The Chinese Room.


Install
-------
1. Unzip this folder anywhere.
2. Double-click "Install Dear Esther VR.bat" and type 1. It shows the game folder it found and the version
   installed.
3. Start SteamVR or Virtual Desktop, then start Dear Esther: Landmark Edition from Steam.

To remove the mod, double-click "Install Dear Esther VR.bat" and type 2. Your saves and settings aren't touched.
The installer looks for the game in your Steam libraries, and asks for the folder if it can't find it.

The mod loads itself through a small file called winhttp.dll (Unity Doorstop). No other mod loader is needed.


Which headset runtime
---------------------
Dear Esther is a 32-bit game, which matters for VR:

  * SteamVR: the game connects to it directly. Start SteamVR first, or let the game start it.
  * Virtual Desktop: VDXR (Virtual Desktop's OpenXR runtime) is selected automatically when SteamVR isn't running.
  * Other headsets work if they offer a 32-bit OpenXR runtime (Meta's does).


Controls
--------
Play with a gamepad or keyboard and mouse, as usual. Motion controllers aren't used yet.

  * Recenter: F9, or Options > VR > Recenter View. It makes the way you face straight ahead, and brings the logo, menu and interface panel to eye
    level in front of you. The game recenters by itself when it starts (once tracking has settled) and at each
    new chapter. In standing mode your height always comes from your real floor, so F9 never changes it.
  * Walking: forward goes where your head looks (can be changed to the body's direction).
  * Turning: the right stick turns smoothly (can be changed to snap turns). The mouse always turns smoothly.
    Look up and down with your head; the game's own up/down look is switched off in VR.

Menus open on a panel in front of you.


Options > VR
------------
A VR page is in the game's own Options menu (from the main menu, or the pause menu during play), below
Language. Use it like the other pages: up and down to pick a row, left and right (or the arrows) to change it.
Changes apply at once.

  Recenter View       Makes the way you face straight ahead, and brings the menus to eye level in front of you.
  Height              Standing: your real height above your real floor is your height on the island.
                      Seated: recentering puts your eyes at the game's own eye height. Choosing Seated
                      recenters straight away.
                      Standing needs a headset that knows where the floor is, and falls back to seated otherwise.
  Height Offset       Raises or lowers your view.
  World Scale         Higher makes the world bigger and you smaller.
  Walk Direction      Head: forward goes where you look. Body: forward goes the way you last turned.
  Turning             Smooth or snap, on the right stick. The mouse always turns smoothly.
  Snap Angle          How far one snap turn goes.
  Head Bob            The game's walking head bob. Off by default.
  Texture Sharpness   Keeps textures sharp further away. A headset shows fewer pixels per degree than a 4K
                      screen, so textures blur sooner at a distance. Original is the game's own; Sharp (the
                      default) and Sharper bring the detail back; Sharpest goes further but can shimmer.
  Shadow Depth        Makes dark areas darker without dimming bright ones, so caves and interiors are dark and
                      the torch matters. Off is the game's own; Low, Medium (the default) or High. Nothing above
                      mid-grey is changed, so sunlit areas and the sky keep their full brightness.
  Underwater Distortion  The game's underwater warp also zooms the view in by about 15%, which in a headset
                      feels like the world changing size. Off gives the normal view with no warp; Low (the
                      default) keeps a gentle warp with a 3% zoom; Medium; Full is the game's own.
  Resolution          Sharper or faster. Lower it if the game stutters.
  Anti-aliasing       Smoother edges. Game (4x) is the game's own setting; 8x is smoother, 2x faster.

The same settings, plus the headset runtime, are in DearEstherVR\DearEstherVR.cfg in the game folder:
  Runtime   auto (SteamVR if running, otherwise the active OpenXR runtime), steamvr, vdxr, openxr, or off
Leave its [Debug] section as it is.


Things to know in this build
----------------------------
  * Loading between scenes shows black in the headset, for exactly as long as the load takes.
  * While VR runs the game plays in a window on your desktop, showing the left eye and the menus.
  * The moon always faces you, in both eyes, wherever you look.
  * With developer commentary on, its floating icons stay upright when you tilt your head.
  * If fine textures look slightly soft with both eyes open but sharp with either eye alone, try a higher
    bitrate or less sharpening in Virtual Desktop: the mod's two eye images were checked to match, so the likely
    cause is each eye being compressed separately for the stream. Anti-aliasing 8x can also help.
  * Not done yet: subtitle placement choices, and the game's zoom button and the ending's zoom (they don't zoom in
    VR yet).
  * If the game plays flat instead of in VR, DearEstherVR\DearEstherVR.log in the game folder says why. Its first
    line shows the version that ran. The usual cause is the active OpenXR runtime Windows has registered for
    32-bit programs: SteamVR's is 64-bit only, and uninstalled runtimes can leave a broken entry. The mod skips
    those and tries VDXR, then SteamVR. To choose yourself, set "Runtime = vdxr" (Virtual Desktop) or
    "Runtime = steamvr" in DearEstherVR\DearEstherVR.cfg, or start SteamVR before the game. With Quest Link, set
    Meta as the active OpenXR runtime in the Meta app.