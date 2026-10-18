# cpu optimization 2

report bugs to @jyanmoon on discord

increases frames by 500-700% in all of my tests
(ryzen 9 5900x3d + 64gb ddr5 + rtx 3080 12gb)

for friends on weak pcs / laptops with mobile cpu/gpus
increase was 100-200%

(image displays current optimization vs previous iteration of this mod vs vanilla game in wasteland)
<img width="1469" height="544" alt="image" src="https://github.com/user-attachments/assets/6eb07ec7-f1ca-482e-a41b-4e2b6d3ced44" />


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

## what it does

- chunk streaming - far chunks and their contents aren't kept as live objects. they're written down and built again when you get close
- unload wait - walking away waits about 5 seconds before a chunk comes down. walking back cancels that
- paced rebuild - a chunk comes back a few objects per frame, so walking into it doesn't hitch
- window size - video menu **Stream Window**. smaller keeps less of the map up and runs lighter. bigger keeps more up
- single player - the window follows your camera
- multiplayer host - the window covers every living player
- multiplayer client - the window follows your camera
- dead players - the ground under a corpse stays solid so the body doesn't fall through the map
- elders and graboids - they stay in the world the whole run, and the ground under them stays solid
- wall holes - they stay. their lights and sounds turn off when you're far
- buildings - health, open doors, lights, and ammo in a mag are saved before a building is removed, and put back on the new one
- chests - what's inside is saved and put back
- traders - stock, what you already bought, and reputation come back with the trader
- worn gear - stuff you're wearing or carrying isn't deleted with the chunk
- chunk edges - a thin wall sits where a loaded chunk meets an unloaded one, so items and water stop there
- colliders and chunk pictures - off unless you turn them on. taking them down helps fps. putting them back can stutter on a weak pc
- faster layer load - off unless you turn it on. the map fills in the background, you get placed sooner, and buildings show up a few at a time
- screen cull - off unless you turn it on. hides sprites and tilemaps outside the camera. can flicker
- potato lighting - video menu **Super Potato Lighting**. cheaper lights. a small fps bump. screen overlays stay on
- menus - main menu, pause, and settings run at 60 fps. your video framerate comes back when they close
- hitch recovery - a long frame doesn't make physics replay a big backlog on the next one
- camera - once you're still, it stops writing the same follow every frame
- hud - bars and numbers that didn't change aren't redrawn
- invisible overlays - full-screen images you can't see aren't drawn
- continue button photo - the qol layer picture sits behind the button frame
- other mods - they can ask if a spot is inside the keep window. stream off always answers yes
