# Dear Esther VR

Play **Dear Esther: Landmark Edition** in a PC VR headset, in full stereoscopic 3D.

The island is rendered separately for each eye, from your head's own position and with your headset's own lens views, so depth, scale and distance look as they should. Head tracking is full 6DoF: lean, crouch, look around, and walk with a gamepad or keyboard as usual.

> **For Dear Esther: Landmark Edition only** (the 2017 release on Steam). It does not work with the original 2012 *Dear Esther*, or with the Half-Life 2 mod it grew from.

This is an unofficial fan mod. It is not made or endorsed by The Chinese Room.

## Download

Get **Dear-Esther-VR-1.0.0.zip** from the [Releases](../../releases) page.

## Install

1. Unzip the download anywhere.
2. Double-click **Install Dear Esther VR.bat** and type **1**. It finds the game in your Steam libraries (or asks for the folder), shows the version installed, and checks every file it copied.
3. Start SteamVR or Virtual Desktop, then start Dear Esther: Landmark Edition from Steam.

To remove it, run **Install Dear Esther VR.bat** again and type **2**. The game goes back to its original files; your saves and settings are not touched.

No other mod loader is needed. The mod loads itself through Unity Doorstop (a small `winhttp.dll` in the game folder).

## Headsets

Dear Esther: Landmark Edition is a 32-bit game, which decides how it reaches the headset:

- **Virtual Desktop** (Quest and others): uses Virtual Desktop's own OpenXR runtime, VDXR, automatically when SteamVR isn't running.
- **SteamVR**: connects to SteamVR directly. Start SteamVR first, or let the game start it.
- **Other headsets** work if they offer a 32-bit OpenXR runtime (Meta's does).

## Playing

- Play with a gamepad or keyboard and mouse. Motion controllers aren't used.
- **Walking** goes where your head looks (or the way your body faces, if you prefer).
- **Turning**: the right stick turns smoothly, or in snap turns. The mouse always turns smoothly. Look up and down with your head.
- **Recenter**: F9, or *Options > VR > Recenter View*. The game also recenters by itself when it starts and at each chapter.
- Menus open on a panel in front of you. Loading between scenes is a moment of black.

## Options > VR

A VR page is added to the game's own Options menu, in the main menu and the pause menu. Changes apply at once.

| Option | What it does |
|---|---|
| Recenter View | Makes the way you face straight ahead, and brings menus to eye level in front of you. |
| Height | **Standing**: your real height above your real floor is your height on the island. **Seated**: your eyes are put at the game's own eye height (choosing it recenters straight away). |
| Height Offset | Raises or lowers your view. |
| World Scale | Makes the world feel bigger or smaller. |
| Walk Direction | Head or Body. |
| Turning / Snap Angle | Smooth or snap turning, and how far one snap goes. |
| Head Bob | The game's walking head bob. Off by default. |
| Texture Sharpness | Keeps textures sharp further away: Original, **Sharp** (default), Sharper, Sharpest. A headset has fewer pixels per degree than a 4K screen, so textures blur sooner at a distance without it. |
| Shadow Depth | Makes dark areas darker without dimming bright ones, so caves and interiors are properly dark: Off, Low, **Medium** (default), High. |
| Underwater Distortion | The game's underwater warp also zooms the view by about 15%, which in a headset feels like the world changing size. Off (normal view), **Low** (default, a gentle warp), Medium, Full. |
| Resolution | Sharper or faster. |
| Anti-aliasing | Game (4x), 2x or 8x. |

The same settings, plus the choice of headset runtime, are in `DearEstherVR\DearEstherVR.cfg` in the game folder.

## Good to know

- While VR runs, the game plays in a window on your desktop that shows the left eye and the menus.
- The moon and the developer commentary icons always face you upright.
- If fine textures look slightly soft with both eyes open but sharp with either eye alone, try a higher bitrate or less sharpening in Virtual Desktop. Each eye is compressed separately for the stream; the mod's two eye images were checked to match.
- If the game plays flat instead of in VR, `DearEstherVR\DearEstherVR.log` in the game folder says why. Its first line shows the version that ran.

## Credits

Built with [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop), the [Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK) and [Valve's OpenVR](https://github.com/ValveSoftware/openvr). Technical notes for developers are in [DEVELOPMENT.md](DEVELOPMENT.md).
