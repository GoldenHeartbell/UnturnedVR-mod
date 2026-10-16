# UnturnedVR

A VR mod for Unturned (Unity/Mono). It runs on top of the normal Steam version of the game and only adds files: a module in `Modules/UnturnedVR/` plus the XR assets it needs. No core game files are modified.

**Status:** work in progress. Do not use it on BattlEye servers, launch the game without BattlEye.

This repository holds the source code of the module. The install zip is on the [Releases](../../releases) page.

## What it does

Play Unturned in your headset with real hands, real aiming and menus you can point at.

- **See the world:** true stereo rendering of the world, menus and HUD, head tracking (your body heading and aim pitch follow the headset, other players see it), per-eye water reflections, full-view screen effects (hit flashes, flashbangs, low health, night vision)
- **Use your hands:** 6DoF arms and hands that follow your controllers, picking things up with the hand that points at them, the equipped item in your hand (pass it over or hold it with both), a laser pointer for menus, the inventory and the pause menu, and hand-placed buildingmode.
- **Shoot and aim:** guns that fire from the barrel with two-handed aiming, aiming down the sights by raising the gun's sight to your eye, scopes with hand-shake smoothing, per-gun rumble, a laser dot that follows the gun, an attachment menu on the gun and binoculars with map markers.
- **Gear on your body:** holsters for the hotbar, a night vision gesture at your ear, the tactical light and laser on the second trigger, health, food, water and status icons on your left forearm, ammo and fire mode on the side of the gun, a vehicle dashboard, and group names and hit markers in the world.
- **Get around:** walking and smooth turning on the thumbsticks, crouch and prone, helicopters, planes and blimps flown with the thumbsticks, vehicle lock and siren, NPC dialogue with a button, the SteamVR keyboard for text fields and a VR settings page in the pause menu.
- **Servers (experimental):** an optional server part for fixing some server-side tracking and for syncing the VR pose to other players. Players without the mod can still join, they just do not get the VR effects.

The controls use your key binds from Unturned's controls settings.

### VR settings page

The pause menu has its own **VR** page, built from the game's own widgets so it looks like the rest of the menu and can be used with the laser pointer.

- **Holsters:** show or hide the holster, shoulder and ear zones as rings around your body.
- **Controls:** attachment menu on Hold or Toggle, building aim by head or by hand, and the speed at which you rotate a barricade before placing it.
- **Effects:** reversing up/down for hallucinations, whether the menu camera follows the main menu fly-in or holds still, the water reflection step (Off, Low, Medium, High or Ultra, a cap on the game's own reflection quality, never a raise) and a checkbox for sky and clouds in those reflections.
- **HUD:** choose for the status icons, the compass, the vehicle display and the gun's ammo and fire mode whether they sit on your wrist, the dashboard or the gun, or stay on the flat HUD panel.

Play the way you like: keep everything on your body for full immersion, put the HUD back on a flat panel, or mix the two.

## Install (players)

1. Download the zip from [Releases](../../releases).
2. Copy the `Modules` and `Unturned_Data` folders into your Unturned install folder and merge when asked. Nothing is overwritten.
3. Start SteamVR, then start Unturned without BattlEye and open a singleplayer game.

Requires a SteamVR (OpenVR) headset. Developed and tested with Valve Index and Steam Frame controllers.

## Install (servers, experimental)

For a dedicated server you run yourself. Players without the mod can still join, they just do not get the VR effects. The server only loads `UnturnedVR.Net.dll`, which has no VR code. The VR part (`UnturnedVR.dll`) is marked client-only and is not loaded on a server.

1. Install the mod on every player's game as above.
2. Copy `Modules/UnturnedVR` from the zip into the server's `Modules` folder (create it if missing). On a server only `UnturnedVR.module`, `UnturnedVR.Net.dll` and `0Harmony.dll` are used. `Unturned_Data` is not needed. Keep the file names exactly as in the zip on Linux.
3. Start the server with VAC and BattlEye off. The server log (`Logs/Server_<server name>.log`) should contain `[UnturnedVR]` lines including `net: registered` and `running on a dedicated server: no VR, no client patches`.
4. Join as a VR player. Your game's log shows `net: the server runs UnturnedVR network protocol 2` when the server part answered.
5. Optional: start the server with `-UnturnedVRLogPoses` to log a `net: pose #...` line per player every few seconds.

To remove it, delete `Modules/UnturnedVR` from the server.

## Credits and license

UnturnedVR is licensed under the **GNU General Public License v3.0**, see [LICENSE](LICENSE).

It is built after [VHVR (Valheim VR)](https://github.com/brandonmousseau/vhvr-mod) by Brandon Mousseau and contributors, also GPL-3.0. Parts of the VR initialization and rig code are adapted from it (see `VR/VRManager.cs`, `VR/VRGui.cs`, `VR/VRRig.cs`, `VR/VRRigHeight.cs`, `VR/VROpenVRPoseFallback.cs` and `Patches/FrameRatePatch.cs`), and the release ships its XR assets. The game's source is available as the [U3 SDK](https://github.com/SmartlyDressedGames/U3-SDK), which this project used for reference.

Unturned is made by Smartly Dressed Games. This mod is a fan project and is not affiliated with or endorsed by them.
