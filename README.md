# cpu optimization 2

bepinex plugin for **casualties unknown**. tries to keep fps from dying when you're deep in a run or hosting krokmp.

not a gameplay mod - far chunks aren't kept as live objects. they're written down and built again when a window covers them.

---

## chunk stream

the main win. vanilla instantiates the whole layer. this mod only keeps a chunk window live.

during gen, stuff outside the window is recorded instead of placed. leftover terrain outside the window comes down on the load screen. in play, leaving a chunk waits 5 seconds, then the objects, tilemap, and collider come down. coming back rebuilds them a handful per frame so the pop-in doesn't hitch.

window size is the video menu **Stream Window**, or `[Stream] WindowMode`:

- **0** - 2×2, or 2×3 on ultrawide (measured from the camera). 128 object spawn/destroy ops per frame
- **1** - fixed 3×3 (default). 64 per frame
- **2** - fixed 4×4. 40 per frame

spawns eat that budget first so fast movement doesn't cause late pop-ins from a long queue of destroys

**single player:** the window around your camera.

**mp host:** union of every living player's window, plus the ground under dead player corpses so a ragdoll doesn't fall through when the camera moves off.

**mp client:** capture only. the host owns unload and load. a client does not destroy objects the host owns.

stuff outside the window isn't gone from the world. the record stays, and it comes back when a window covers it.

elders, graboids, and wall holes stay up the whole time. wall-hole lights and sounds still follow the window. chest loot and mod machine settings are saved before a building is removed and put back on the new one.

config section: `[Stream]`

other mods can ask "is this world pos in the keep window?" via `CPUOptimization2.StreamWindowQuery` (krokmpoptimization uses this for registry cull). stream off answers yes, so callers don't cull.

---



## screen cull

off by default. the chunk window already decides what's loaded. this only hides sprites and tilemaps outside the camera view.

wall holes and the chunk backdrop are left alone.

config section: `[ScreenCull]`

---



## potato lighting

off by default. video menu **Super Potato Lighting** flips it immediately. unchecking puts stock lighting back.  
if you are desperate for frames, this will give you a slight edge.

- smaller light render targets, and fewer of them (8 instead of the usual 16)
- multiply lights share one blend slot
- volumetric glow and light normal maps off
- msaa, depth, and some heavy renderer features off

config section: `[RenderTune]`

---



## menus

main menu, pause, and the settings menu are capped at 60 fps. your video framerate setting is put back when those close.

---



## config file

`BepInEx/config/com.local.worldcapture.cfg`

`[General] Enabled` is the master switch. `[Stream] Enabled` defaults on. if an entity pops in as you walk up to it, that's the stream working. `[Stream] VerboseLogging` logs every chunk load and unload and costs frame time. leave it off.

---



## logs

bepienx/logoutput.log ->`CPUOptimization2`. capture counts, stream window moves, and potato on/off are prefixed that way.