# cpu optimization

bepinex plugin for **casualties unknown**. tries to keep fps from dying when you're deep in a run or hosting krokmp.

not a gameplay mod - it just stops simulating/rendering stuff you can't see or aren't standing next to.

---

## lights

decorative light2d adds up fast in bases. this mod:

- turns off lights that are too far from the camera (radius follows your view by default, lights come on farther out so they dont pop in)
- on some traps (jumppad, coil, turret, **sidestabber**): kills the light2d and fakes the glow by flashing the sprite instead
- **floor spike traps are left alone** - still get the blinking red light

config section: `[Lights]`

---

## chunk sim

the main cpu win. vanilla keeps rigidbodies, updates, and chunk colliders hot across way too much map.

this mod tracks a **2×2 chunk window** and only sims inside it:

- world-loose items sleep off-window (rb off, update skipped)
- buildings/traps/spiders same idea - dormant until you're near
- dropper raycasts, glowshroom sprite osc, and cave-tick swarm particles sleep off-window
- chunk composite colliders only enabled for active chunks (252 others stay off)
- when the window moves, rb state gets synced in a batch instead of per-entity every frame

**single player:** 2×2 around your camera.

**mp host:** union of every living player's 2×2 plus dead player corpses - so remote players' areas still sim, and corpses keep ground colliders if a spectator freecams away.

**mp client:** local 2×2 only. you don't pay for simming someone else's side of the map.

stuff outside the window isn't gone - it wakes back up when a window covers it.

config section: `[ChunkSim]`

other mods can ask "is this world pos in the sim window?" via `CPUOptimization.ChunkSimQuery` (krokmpoptimization uses this for registry cull).

---

## fluids

water/lava sim range gets clamped to the chunk window so off-map fluid isn't chewing cpu.

fluid particle rendering can be spread across frames instead of one fat `RenderFluids` spike.

config section: `[Fluids]`

---

## krokmp perf

optional patches if **krokmp** is installed (soft dependency - mod loads without it):

- throttle host `OnWillRenderObject` forceformp spam
- budget trap/trader force sync per tick
- skip voicechat update when vc is off and you're not recording
- item despawner runs every N frames instead of every frame
- perf targets outside chunk window get skipped

pairs well with krokmpoptimization2 (sync/queue side) but neither requires the other.

config section: `[KrokMpPerf]`

---

## config file

`BepInEx/config/com.local.cpu.optimization.cfg`

defaults are tuned for mp host / weak computers. if lights pop in/out weird, tweak `[Lights] CullRadius` / `CullCameraMargin`. if items feel frozen until you walk closer, that's chunk sim working - widen isn't really an option without losing the point.

---

## logs

grep bepinex log for `[CPUOpt]`. chunk window moves, light cull stats, mp union changes - all prefixed that way.
