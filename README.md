# UnturnedVR

A VR mod for Unturned (Unity/Mono). It runs on top of the normal Steam version of the game and only adds files: a module in `Modules/UnturnedVR/` plus the XR assets it needs. No core game files are modified.

**Status:** work in progress. Do not use it on BattlEye servers; launch the game without BattlEye.

This repository holds the source code of the module. The install zip is on the [Releases](../../releases) page.

## What it does

Head-tracked first person view, hand-aimed guns, VR menus and HUD as world-space panels, controller input, binoculars, holsters, vehicle controls and a VR settings page in the pause menu. The player-facing feature list and install steps are in the README inside each release zip.

## Install (players)

1. Download the zip from [Releases](../../releases).
2. Copy the `Modules` and `Unturned_Data` folders into your Unturned install folder and merge when asked. Nothing is overwritten.
3. Start SteamVR, then start Unturned without BattlEye and open a singleplayer game.

Requires a SteamVR (OpenVR) headset. Developed and tested with Valve Index controllers.

## Credits and license

UnturnedVR is licensed under the **GNU General Public License v3.0**, see [LICENSE](LICENSE).

It is built after [VHVR (Valheim VR)](https://github.com/brandonmousseau/vhvr-mod) by Brandon Mousseau and contributors, also GPL-3.0. Parts of the VR initialization and rig code are adapted from it (see `VR/VRManager.cs`, `VR/VRGui.cs`, `VR/VRRig.cs`, `VR/VRRigHeight.cs`, `VR/VROpenVRPoseFallback.cs` and `Patches/FrameRatePatch.cs`), and the release ships its XR assets. The game's source is available as the [U3 SDK](https://github.com/SmartlyDressedGames/U3-SDK), which this project used for reference.

Unturned is made by Smartly Dressed Games. This mod is a fan project and is not affiliated with or endorsed by them.
