# cpu optimization 2

increases frames by 500-700% in all of my tests
(ryzen 9 5900x3d + 64gb ddr5 + rtx 3080 12gb)

for friends on weak pcs / laptops with mobile cpu/gpus
increase was 100-200%

<img width="1469" height="544" alt="image" src="https://github.com/user-attachments/assets/6eb07ec7-f1ca-482e-a41b-4e2b6d3ced44" />


bepinex plugin for **casualties unknown**. tries to keep fps from dying when you're deep in a run or hosting krokmp.

not a gameplay mod - far chunks aren't kept as live objects. they're written down and built again when a window covers them.  

Known bugs:  

- Entities spawned by Custom Structures, if they contain custom data, won't always reliably be respawned in that state. Fix ETA: 9/29

---

## chunk stream

the main win. vanilla instantiates the whole layer. this mod only keeps a chunk window live.

during gen, stuff outside the window is recorded instead of placed. leftover terrain outside the window comes down on the load screen. in play, leaving a chunk waits 5 seconds, then the objects, tilemap, and collider come down. coming back rebuilds them a handful per frame so the pop-in doesn't hitch.  
  
basically:

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

## how it works:

During world gen:
```cs
Load starts
  -> stock paints every chunk (UpdateWorld)
  -> every Ground tilemap collider is turned on, and physics is synced
     (placement raycasts need the whole map, including chunks that will be culled)
  -> stock starts a scatter pass (traps, plants, enemies, traders, …)
       each hit:
         inside the camera window -> Instantiate, leave it live
         outside the window       -> write a record, do not Instantiate
            trader / enemy / item / building, including the block it sits on
  -> structures (GenerateEntityAtPos) use the same split, child by child
       tilemap interiors outside the window become entity records
       the structure's block stamp is already in worldBlocks, so it is skipped
  -> passes stock still Instantiates itself (ropes, bandages, remote traders, mini-barrels)
       -> copied into the same arrays, then destroyed if they landed outside the window as they generate
  -> generation finishes
```
After world gen:
```cs
FinishWorldGeneration
  -> copy whatever is still alive into the arrays
       elders and wall holes are marked resident and stay out of the arrays
       order: traders, enemies, buildings, structure tilemaps, climbables,
               sandvine hooks, oil pipes, loose items, lights
  -> client: stop here. arrays exist, nothing is destroyed
  -> host: loading screen, timeScale 0
       every chunk that has a tilemap starts at phase 3 (fully live)
       chunks inside the window stay there
       chunks outside walk down, a few per tick:

         phase 3  objects destroyed
                  (records already exist, so nothing is written back)
              -> phase 2  colliders off
                          (a chunk an elder or a corpse is standing on keeps its collider)
              -> phase 1  tilemap destroyed
                          tile edits packed, backdrop removed
              -> phase 0  chunk is only rows in the arrays

       window chunks stay at phase 3 the whole time
  -> loading screen off, timeScale restored
```
During gameplay:
```cs
camera / living players move
  -> desired set = union of each living player's window
  -> chunk entering the window:
       phase 0  create tilemap, renderer on, collider created but off
            -> phase 1  colliders on
            -> phase 2  spawn from the arrays, paced:
                        traders -> enemies -> entities -> items -> lights
                        then register the chunk's renderers
            -> phase 3  live
  -> chunk leaving the window:
       wait 5 seconds
       (walking back to put it into the chunk window range it cancels the wait)
       phase 3  write current health / loot / position back into the arrays,
                then destroy the objects
            -> phase 2  colliders off
            -> phase 1  destroy the tilemap
            -> phase 0  arrays only
            (These are all capped at 128/64/40 objects per frame based on chunk window size selection of 2x2dynamic / 3x3 / 4x4)
            (bigger window = less overall fps, but slower updates so potentially smoother on weaker pcs)
```
  
  
# Other stuff (smaller wins)


## screen cull

off by default. the chunk window already decides what's loaded. this only hides sprites and tilemaps outside the camera view.

wall holes and the chunk backdrop are left alone.  

(this caused flickering, may be stripped in later version)

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
