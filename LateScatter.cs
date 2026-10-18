using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// structures and entities used to finish inside one MoveNext. run them after the
/// player is in, spending ScatterBudget points a frame. a miss costs 1 and a
/// spawn or block circle costs 4, matching play. placement still raycasts the
/// ground, but the random stream is its own pass.
/// </summary>
internal static class LateScatter
{
	const int CostMiss = 1;
	const int CostSpawn = 4;

	static readonly MethodInfo DropCapsules = AccessTools.Method(typeof(WorldGeneration), "GenerateDropCapsules");
	static readonly MethodInfo CollapsedPods = AccessTools.Method(typeof(WorldGeneration), "GenerateCollapsedPods");
	static readonly MethodInfo LifePods = AccessTools.Method(typeof(WorldGeneration), "GenerateLifePods");
	static int _left;

	static readonly Ring[] BioRings =
	{
		new Ring(16, 3, 0.8f, 0f),
		new Ring(20, 4, 0.3f, 0f),
		new Ring(16, 0, 0.15f, 0f),
	};

	static readonly Ring[] PodRings =
	{
		new Ring(16, 3, 0.5f, 0f),
		new Ring(20, 4, 0.2f, 0f),
	};

	static bool _deferStructures;
	static bool _holdStructures;
	static bool _placed;
	static int _ground = int.MinValue;

	internal static bool Holding { get; private set; }

	readonly struct Ring
	{
		internal readonly int Size;
		internal readonly ushort Block;
		internal readonly float Chance;
		internal readonly float End;

		internal Ring(int size, ushort block, float chance, float end)
		{
			Size = size;
			Block = block;
			Chance = chance;
			End = end;
		}
	}

	internal static void Apply(Harmony harmony)
	{
		var reset = new HarmonyMethod(typeof(LateScatter), nameof(ResetPlaced));
		reset.priority = Priority.First;
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "WorldGenerateTerrain", System.Type.EmptyTypes),
			prefix: reset);
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "WorldGenerateStructures"),
			prefix: new HarmonyMethod(typeof(LateScatter), nameof(StructuresPrefix)));
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "WorldPlacePlayer"),
			postfix: new HarmonyMethod(typeof(LateScatter), nameof(PlacePostfix)));
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "WorldPlaceEntities"),
			prefix: new HarmonyMethod(typeof(LateScatter), nameof(EntitiesPrefix)));
	}

	// a new layer's structures run before its player exists. drop the previous place.
	static bool ResetPlaced()
	{
		_placed = false;
		_deferStructures = false;
		_holdStructures = false;
		return true;
	}

	static void PlacePostfix(ref IEnumerator __result)
	{
		if (__result == null)
			return;
		__result = WatchPlace(__result);
	}

	static IEnumerator WatchPlace(IEnumerator inner)
	{
		while (inner != null && inner.MoveNext())
			yield return inner.Current;
		_placed = true;
	}

	static bool StructuresPrefix(WorldGeneration __instance, ref IEnumerator __result)
	{
		if (LeaveStock(__instance))
			return true;
		// depth > 1 calls this from inside terrain. an early place already set
		// _placed, but borders still have to run before these props.
		// waiting here would deadlock the place that comes after terrain.
		if (_holdStructures || !Spawned(__instance))
		{
			_holdStructures = false;
			_deferStructures = true;
			__result = Done();
			return false;
		}
		_deferStructures = false;
		__result = Structures(__instance);
		return false;
	}

	static bool EntitiesPrefix(WorldGeneration __instance, ref IEnumerator __result)
	{
		if (LeaveStock(__instance))
			return true;
		__result = Entities(__instance);
		return false;
	}

	static bool LeaveStock(WorldGeneration world)
	{
		if (world == null || !Plugin.LayerLoadOn)
			return true;
		// seeded qol wraps these coroutines too. pacing them changes the seed.
		if (QolSeed.Active)
			return true;
		return world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial
			|| world.biomeOverride == WorldGeneration.OverrideSceneType.Debug;
	}

	static bool Spawned(WorldGeneration world)
	{
		return world != null && _placed;
	}

	// terrain already put the player in. keep structures for the entity pass.
	internal static void HoldStructures()
	{
		_holdStructures = true;
	}

	static IEnumerator Done()
	{
		yield break;
	}

	static void Enter()
	{
		Holding = true;
		if (!MpSession.Running && Time.timeScale == 0f)
			Time.timeScale = 1f;
		Arm();
	}

	static int Points()
	{
		int n = Plugin.ScatterBudget != null ? Plugin.ScatterBudget.Value : 8;
		if (n < 1)
			n = 1;
		if (n > 40)
			n = 40;
		return n;
	}

	static void Arm()
	{
		_left = Points();
	}

	// a miss is one point. a spawn or block circle is four, the play spawn weight.
	// the pool refills after a yield, so the pass tracks ScatterBudget per frame.
	static IEnumerable Breath(int cost = CostMiss)
	{
		if (cost < 1)
			cost = 1;
		_left -= cost;
		if (_left > 0)
			yield break;
		yield return null;
		Arm();
	}

	static IEnumerator Structures(WorldGeneration world)
	{
		Enter();
		float started = Time.realtimeSinceStartup;
		try
		{
			world.SetLoadingText("gencreatingstructures");
			yield return null;
			Arm();
			foreach (object tick in StructuresOf(world))
				yield return tick;
			ushort[,] blocks = WorldArrays.WorldBlocks(world);
			if (blocks != null)
				InstantLoad.RefreshPainted(world, 0, blocks.GetLength(0));
		}
		finally
		{
			Holding = false;
			Plugin.Log.LogInfo(
				"scatter structures " + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + "ms");
		}
	}

	static IEnumerator Entities(WorldGeneration world)
	{
		Enter();
		float started = Time.realtimeSinceStartup;
		try
		{
			while (!Spawned(world))
				yield return null;
			if (_deferStructures)
			{
				_deferStructures = false;
				IEnumerator structures = Structures(world);
				while (structures.MoveNext())
					yield return structures.Current;
				Enter();
			}
			world.SetLoadingText("genplacingentities");
			yield return null;
			Arm();
			foreach (object tick in EntitiesOf(world))
				yield return tick;
		}
		finally
		{
			Holding = false;
			Plugin.Log.LogInfo(
				"scatter entities " + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + "ms");
		}
	}

	static IEnumerable StructuresOf(WorldGeneration world)
	{
		int depth = world.biomeDepth;
		if (depth <= 1 && world.biomeOverride == WorldGeneration.OverrideSceneType.None)
		{
			foreach (object tick in Pods(world, 0.12f, 0.13f, 0.055f, 0.066f, 1f))
				yield return tick;
			if (depth > 0)
			{
				float bio = Area(world, 0.05f, 0.07f, world.totalLootRarity);
				foreach (object tick in Stamp(world, bio, "BioContainer", 1f, true, true, true, BioRings))
					yield return tick;
				float bridge = Area(world, 0.09f, 0.12f, world.totalLootRarity);
				foreach (object tick in Stamp(world, bridge, "Structures/SteelBridge", 0.85f, true, false, false, null))
					yield return tick;
			}
			float pods = Area(world, 0.06f, 0.08f, world.totalLootRarity);
			foreach (object tick in Stamp(world, pods, "Structures/CratePod", 0.82f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, pods, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.045f, 0.05f, world.totalLootRarity), "Structures/SteelThing", 0.9f, false, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Life(world))
				yield return tick;
			yield break;
		}
		if (depth == 2 || depth == 3)
		{
			float collapsed = depth == 2 ? 1f : 2.5f;
			foreach (object tick in Pods(world, 0.12f, 0.13f, 0.066f, 0.077f, collapsed))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.05f, 0.07f, world.totalLootRarity), "BioContainer", 1f, true, true, true, BioRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.04f, 0.05f, world.totalLootRarity), "Structures/MedicalBuilding", 0.98f, true, true, false, null))
				yield return tick;
			float bridge = Area(world, 0.09f, 0.12f, world.totalLootRarity);
			foreach (object tick in Stamp(world, bridge, "Structures/SteelBridge", 0.95f, true, false, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, bridge, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Life(world))
				yield return tick;
			yield break;
		}
		if (depth == 4)
		{
			foreach (object tick in Pods(world, 0.12f, 0.13f, 0.066f, 0.077f, 1f))
				yield return tick;
			foreach (object tick in Canopy(world, Area(world, 0.9f, 1.1f, 1f), false))
				yield return tick;
			float pods = Area(world, 0.06f, 0.08f, world.totalLootRarity);
			foreach (object tick in Stamp(world, pods, "Structures/CratePod", 0.82f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, pods, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.95f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.95f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.04f, 0.05f, world.totalLootRarity), "Structures/BrickLoot", 0.925f, true, true, false, null))
				yield return tick;
			Ring[] bio =
			{
				new Ring(16, 3, 0.8f, 0f),
				new Ring(20, 4, 0.3f, 0f),
				new Ring(16, 0, 0.15f, 0f),
			};
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.04f, world.totalLootRarity), "BioContainer", 0.975f, true, true, true, bio))
				yield return tick;
			foreach (object tick in Veins(world, false))
				yield return tick;
			foreach (object tick in Veins(world, true))
				yield return tick;
			foreach (object tick in Life(world))
				yield return tick;
			yield break;
		}
		if (depth == 5)
		{
			foreach (object tick in Pods(world, 0.12f, 0.13f, 0.055f, 0.066f, 1f))
				yield return tick;
			float pods = Area(world, 0.06f, 0.08f, world.totalLootRarity);
			foreach (object tick in Stamp(world, pods, "Structures/CratePod", 0.82f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, pods, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.045f, 0.05f, world.totalLootRarity), "Structures/SteelThing", 0.9f, false, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Life(world))
				yield return tick;
			yield break;
		}
		if (depth == 6)
		{
			foreach (object tick in Canopy(world, Area(world, 0.9f, 1.1f, 1f), true))
				yield return tick;
			foreach (object tick in Pods(world, 0.12f, 0.13f, 0.055f, 0.066f, 1f))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.05f, 0.07f, world.totalLootRarity), "BioContainer", 1f, true, true, true, BioRings))
				yield return tick;
			float bridge = Area(world, 0.09f, 0.12f, world.totalLootRarity);
			foreach (object tick in Stamp(world, bridge, "Structures/SteelBridge", 0.85f, true, false, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, bridge, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.94f, false, true, false, null))
				yield return tick;
			foreach (object tick in Life(world))
				yield return tick;
			yield break;
		}
		if (depth != 7)
			yield break;
		foreach (object tick in Pods(world, 0.12f, 0.13f, 0.15f, 0.2f, 1f))
			yield return tick;
		float crates = Area(world, 0.06f, 0.08f, world.totalLootRarity);
		foreach (object tick in Stamp(world, crates, "Structures/CratePod", 0.82f, true, true, false, PodRings))
			yield return tick;
		foreach (object tick in Stamp(world, crates, "Structures/MiniPod", 0.88f, true, true, false, PodRings))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.045f, 0.05f, world.totalLootRarity), "Structures/SteelThing", 0.9f, false, true, false, PodRings))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodCross", 0.94f, false, true, false, null))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.03f, 0.05f, world.totalLootRarity), "Structures/WoodHorizontal", 0.94f, false, true, false, null))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.05f, 0.07f, world.totalLootRarity), "BioContainer", 1f, true, true, true, BioRings))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.09f, 0.12f, world.totalLootRarity), "Structures/SteelBridge", 0.85f, true, false, false, null))
			yield return tick;
		foreach (object tick in Stamp(world, Area(world, 0.07f, 0.1f, 1f), "Structures/LongCorridor", 0.95f, true, false, false, null))
			yield return tick;
		ushort[,] blocks = WorldArrays.WorldBlocks(world);
		int height = blocks != null ? blocks.GetLength(1) : 0;
		Vector2Int pad = new Vector2Int((int)world.halfWidth, height - 30);
		GameObject crystal = Resources.Load<GameObject>("Structures/CrystalSpawnPlatform");
		if (crystal != null)
		{
			Tilemap map = crystal.GetComponent<Tilemap>();
			if (map != null)
				world.GenerateObjectAtPos(pad, map, 1f, true);
			world.GenerateEntityAtPos(world.BlockToWorldPos(pad), crystal);
		}
		foreach (object tick in Breath(CostSpawn))
			yield return tick;
	}

	static IEnumerable EntitiesOf(WorldGeneration world)
	{
		int depth = world.biomeDepth;
		float loot = world.totalLootRarity;
		float trap = world.totalTrapRarity;
		float mult = world.lootRarityMultiplier;
		if (depth <= 1 && world.biomeOverride == WorldGeneration.OverrideSceneType.None)
		{
			world.PlaceCrystals();
			foreach (object tick in Breath(Points()))
				yield return tick;
			foreach (object tick in Prop(world, "glowplant", 2.7f, 3.5f, 1.25f, 10f, 0.25f, flip: true, check: pos => Soft(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, flip: true, check: pos => Soft(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "ceilingrye", 0.3f, 0.4f, 1f, 10f, 0.5f, flip: true, check: pos => Soft(world, pos), dir: Vector2.up))
				yield return tick;
			foreach (object tick in Prop(world, "medcrate", 0.18f * loot, 0.2f * loot, 3f, 180f))
				yield return tick;
			foreach (object tick in Prop(world, "containercrate", 0.05f * loot, 0.07f * loot, 3f, 180f))
				yield return tick;
			foreach (object tick in Prop(world, "foodbox", 0.1f * loot, 0.13f * loot, 3f, 180f))
				yield return tick;
			foreach (object tick in Prop(world, "spikestabber", 0.4f * trap, 0.5f * trap, trap: true))
				yield return tick;
			foreach (object tick in Prop(world, "shadecrawler", 0.4f * trap, 0.42f * trap, 2f, 180f, trap: true))
				yield return tick;
			foreach (object tick in Prop(world, "corpse", 1f * mult, 1.1f * mult, check: pos => Flat(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "animalcorpse", 0.6f * mult, 0.7f * mult, check: pos => Flat(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "drillpod", 0.09f, 0.1f, ground: true, flip: true, trap: true))
				yield return tick;
			if (depth > 0)
			{
				foreach (object tick in Prop(world, "barbedwirefence", 0.6f * trap, 0.8f * trap, 4.8f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "beartrap", 0.2f * trap, 0.25f * trap, 1f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "CaveTicks", 0.15f * trap, 0.2f * trap, 4f, 0f, 3f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "geyser", 1.6f, 1.8f, 0.6f, check: pos => Soft(world, pos)))
					yield return tick;
			}
			else
			{
				foreach (object tick in Prop(world, "geyser", 0.7f, 0.8f, 0.6f, check: pos => Soft(world, pos)))
					yield return tick;
			}
			foreach (object tick in Prop(world, "jumppad", 0.6f * trap, 0.8f * trap, trap: true))
				yield return tick;
			foreach (object tick in Bandages(world, Area(world, 0.2f, 0.3f, loot)))
				yield return tick;
			foreach (object tick in Ropes(world, Area(world, 0.8f, 1f, 1f)))
				yield return tick;
			foreach (object tick in Dropped(world, Area(world, 0.3f, 0.5f, 1f), "droppings", false))
				yield return tick;
			if (depth == 0)
			{
				foreach (object tick in Dropped(world, Area(world, 0.015f, 0.02f, loot), "fleshchunk", true))
					yield return tick;
			}
			foreach (object tick in Prop(world, "geotree", 2.7f, 3f, 3f, 6f, 0.15f, flip: true, check: pos => world.isSoil(pos)))
				yield return tick;
			foreach (object tick in Prop(world, "hydreed", 1.4f, 1.6f, 2.6f, 6f, 0.4f, flip: true, check: pos => world.isSoil(pos)))
				yield return tick;
			foreach (object tick in Prop(world, "leadbush", 2f, 2.2f, 0.6f, 6f, 0.1f, flip: true, check: pos => world.isSoil(pos)))
				yield return tick;
			yield break;
		}
		if (depth == 2 || depth == 3)
		{
			float plant = depth == 2 ? 1f : 2f;
			float stone = depth == 2 ? 1f : 3f;
			float later = depth == 2 ? 1f : 2.5f;
			world.PlaceCrystals();
			foreach (object tick in Breath(Points()))
				yield return tick;
			foreach (object tick in Prop(world, "glowplant", 2.4f * plant, 2.5f * plant, 1.25f, 10f, 0.25f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "stoneplant", 0.4f * stone, 0.5f * stone, 1.9f, 10f, 0.1f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "cactus", 1.4f, 1.6f, 2.1f, 10f, 0.3f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "sandrose", 1.3f, 1.4f, 1.5f, 10f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "drybush", 6f, 7f, 2f, 20f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "brownshroom", 4f, 5f, 0.9f, 10f, flip: true, check: pos => Desert(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "stalagmite", 10f, 15f, 2.8f, 0f, 0.15f, flip: true, check: pos => Spike(world, pos)))
				yield return tick;
			foreach (object tick in Prop(world, "jumppad", 0.25f * trap, 0.35f * trap, trap: true))
				yield return tick;
			foreach (object tick in Prop(world, "landmine", 0.13f * trap, 0.16f * trap, 0.4f, trap: true))
				yield return tick;
			foreach (object tick in Prop(world, "ceilingrye", 0.08f, 0.1f, 1f, 10f, 0.5f, flip: true, check: pos => Soft(world, pos), dir: Vector2.up))
				yield return tick;
			if (depth == 3)
			{
				foreach (object tick in Prop(world, "spentfuel", 0.3f * trap, 0.35f * trap, 1.875f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "soundcannon", 0.4f * trap, 0.45f * trap, 1f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "foodbox", 0.1f * loot, 0.13f * loot, 3f, 180f))
					yield return tick;
				foreach (object tick in Prop(world, "pop", 3f * loot, 4f * loot, 2f, 20f, 0.2f, flip: true, check: pos => Desert(world, pos)))
					yield return tick;
				foreach (object tick in Prop(world, "coil", 0.2f * trap, 0.3f * trap, 2f, trap: true))
					yield return tick;
			}
			else
			{
				foreach (object tick in Prop(world, "wallbiter", 0.12f * trap, 0.13f * trap, 4.8f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "shadecrawler", 0.2f * trap, 0.2f * trap, 4.8f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "droppings", 0.75f, 0.82f))
					yield return tick;
				foreach (object tick in Prop(world, "beartrap", 0.1f * trap, 0.2f * trap, 1f, trap: true))
					yield return tick;
				foreach (object tick in Prop(world, "barbedwirefence", 0.7f * trap, 0.8f * trap, 4.8f, trap: true))
					yield return tick;
			}
			foreach (object tick in Pipes(world, Area(world, 0.3f, 0.4f, 1f)))
				yield return tick;
			float turretScale = depth == 2 ? 1f : 0.66f;
			foreach (object tick in Turrets(world, Area(world, 0.12f, 0.15f, trap * turretScale)))
				yield return tick;
			foreach (object tick in Stalactites(world, Area(world, 1.5f, 2f, trap)))
				yield return tick;
			float vineScale = depth == 2 ? 1f : 0.1f;
			foreach (object tick in Vines(world, Area(world, 6f, 7f, vineScale)))
				yield return tick;
			foreach (object tick in Prop(world, "rag", 0.12f * mult * later, 0.2f * mult * later, 1f))
				yield return tick;
			float corpse = depth == 2 ? 1f : 2f;
			foreach (object tick in Prop(world, "corpse", 0.75f * mult * corpse, 0.82f * mult * corpse, check: pos => Flat(world, pos)))
				yield return tick;
			yield break;
		}
		if (depth != 4)
			yield break;
		foreach (object tick in Vines(world, Area(world, 4f, 5f, 1f)))
			yield return tick;
		foreach (object tick in Prop(world, "glowplant", 0.2f, 0.3f, 1.25f, 10f, 0.25f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "shadecrawler", 0.45f * trap, 0.5f * trap, 2f, 180f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "wallbiter", 0.1f * trap, 0.11f * trap, 4.8f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "thornbackyoung", 0.24f * trap, 0.26f * trap, 4.8f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "overgrowntick", 0.1f * trap, 0.12f * trap, 4.8f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "caveticks", 0.15f * trap, 0.16f * trap, 4.8f, trap: true))
			yield return tick;
		for (int i = 0; i < 3; i++)
		{
			Object.Instantiate(
				Resources.Load("thornbackelder"),
				new Vector2(UnityEngine.Random.Range(-world.halfWidth, world.halfWidth), UnityEngine.Random.Range(-world.halfHeight, BodyY(world) - 20f)),
				Quaternion.identity);
			foreach (object tick in Breath(CostSpawn))
				yield return tick;
		}
		foreach (object tick in Prop(world, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "ceilingrye", 0.65f, 0.8f, 1f, 10f, 0.5f, flip: true, check: pos => Soft(world, pos), dir: Vector2.up))
			yield return tick;
		foreach (object tick in Prop(world, "medcrate", 0.18f * loot, 0.2f * loot, 3f, 180f))
			yield return tick;
		foreach (object tick in Prop(world, "containercrate", 0.05f * loot, 0.07f * loot, 3f, 180f))
			yield return tick;
		foreach (object tick in Prop(world, "foodbox", 0.1f * loot, 0.13f * loot, 3f, 180f))
			yield return tick;
		foreach (object tick in Prop(world, "corpse", 1.1f * mult, 1.2f * mult, check: pos => Flat(world, pos)))
			yield return tick;
		foreach (object tick in Prop(world, "animalcorpse", 0.9f * mult, 0.95f * mult, check: pos => Flat(world, pos)))
			yield return tick;
		foreach (object tick in Prop(world, "geotree", 0.4f, 0.5f, 3f, 6f, 0.15f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "browncap", 0.4f, 0.5f, 3f, 6f, 0.15f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "hydreed", 0.6f, 0.7f, 2.6f, 6f, 0.4f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "leadbush", 1.1f, 1.2f, 0.6f, 6f, 0.1f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "droppings", 3.7f, 4f))
			yield return tick;
		foreach (object tick in Prop(world, "pop", 1f * loot, 1.1f * loot, 2f, 20f, 0.2f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "bananaplant", 1.9f * trap, 2f * trap, 0.4f, 15f, 0.1f, flip: true, check: pos => world.isSoil(pos)))
			yield return tick;
		foreach (object tick in Prop(world, "coil", 0.2f * trap, 0.3f * trap, 2f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "beartrap", 0.1f * trap, 0.2f * trap, 1f, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "jumppad", 0.25f * trap, 0.35f * trap, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "spikestabber", 0.4f * trap, 0.5f * trap, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "grabberplant", 0.4f * trap, 0.5f * trap, trap: true))
			yield return tick;
		foreach (object tick in Prop(world, "geyser", 0.7f, 0.8f, 0.6f, check: pos => Soft(world, pos)))
			yield return tick;
		foreach (object tick in Prop(world, "skullcrusher", 1.1f, 1.2f, 1f, 10f, flip: true, dir: Vector2.up))
			yield return tick;
		foreach (object tick in Flowers(world, Area(world, 6f, 7f, 1f)))
			yield return tick;
		world.PlaceCrystals();
		foreach (object tick in Breath(Points()))
			yield return tick;
	}

	static IEnumerable Pods(WorldGeneration world, float dropMin, float dropMax, float colMin, float colMax, float colScale)
	{
		Call(DropCapsules, world, UnityEngine.Random.Range(dropMin, dropMax));
		foreach (object tick in Breath(Points()))
			yield return tick;
		Call(CollapsedPods, world, UnityEngine.Random.Range(colMin, colMax) * colScale);
		foreach (object tick in Breath(Points()))
			yield return tick;
	}

	static IEnumerable Life(WorldGeneration world)
	{
		Call(LifePods, world, UnityEngine.Random.Range(0.088f, 0.1f));
		foreach (object tick in Breath(Points()))
			yield return tick;
	}

	static void Call(MethodInfo method, WorldGeneration world, float amt)
	{
		if (method == null)
			return;
		method.Invoke(world, new object[] { amt });
	}

	static IEnumerable Stamp(WorldGeneration world, float num, string path, float chance, bool entity, bool snap, bool bio, Ring[] rings)
	{
		if (num <= 0f)
			yield break;
		GameObject prefab = Resources.Load<GameObject>(path);
		if (prefab == null)
			yield break;
		Tilemap map = null;
		if (bio)
		{
			if (prefab.transform.childCount > 0)
				map = prefab.transform.GetChild(0).GetComponent<Tilemap>();
		}
		else
			map = prefab.GetComponent<Tilemap>();
		if (map == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = RandInset(world);
			if (snap)
				point = SnapDown(point, 400f);
			Vector2Int pos = world.WorldToBlockPos(point);
			if (rings != null)
			{
				for (int r = 0; r < rings.Length; r++)
				{
					Ring ring = rings[r];
					world.GenerateBlockCircle(point, ring.Size, ring.Block, ring.Chance, ring.End, true);
					foreach (object tick in Breath(CostSpawn))
						yield return tick;
				}
				InstantLoad.RefreshPainted(world, pos.x - 24, pos.x + 24);
			}
			world.GenerateObjectAtPos(pos, map, chance, true);
			if (entity)
				world.GenerateEntityAtPos(world.BlockToWorldPos(pos), prefab);
			foreach (object tick in Breath(CostSpawn))
				yield return tick;
		}
	}

	static IEnumerable Canopy(WorldGeneration world, float num, bool mushroom)
	{
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 origin = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfHeight, world.halfHeight));
			RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.down, WorldGeneration.CHUNKSIZE, Ground());
			if (hit)
			{
				Vector2Int pos = world.WorldToBlockPos(hit.point);
				if (mushroom)
					world.GenerateBigMushroom(pos);
				else
					world.GenerateTree(pos);
			}
			foreach (object tick in Breath(hit ? CostSpawn : CostMiss))
				yield return tick;
		}
	}

	static IEnumerable Veins(WorldGeneration world, bool vertical)
	{
		ushort[,] blocks = WorldArrays.WorldBlocks(world);
		if (blocks == null)
			yield break;
		int width = blocks.GetLength(0);
		int height = blocks.GetLength(1);
		float num = (float)world.chunkWidth * (float)world.chunkHeight * UnityEngine.Random.Range(0.35f, 0.5f);
		for (int i = 0; (float)i < num; i++)
		{
			int x = UnityEngine.Random.Range(0, width);
			int y = UnityEngine.Random.Range(0, height);
			int len = UnityEngine.Random.Range(1, 5);
			if (!vertical)
			{
				for (int dx = 0; dx < len; dx++)
				{
					if (x + dx < width && y < height)
						blocks[x + dx, y] = 5;
				}
			}
			else
			{
				for (int dy = 0; dy < len; dy++)
				for (int dx = 0; dx < 2; dx++)
				{
					if (x + dx < width && y + dy < height)
						blocks[x + dx, y + dy] = 5;
				}
			}
			foreach (object tick in Breath())
				yield return tick;
		}
	}

	static IEnumerable Prop(
		WorldGeneration world, string path, float min, float max,
		float y = 0f, float rot = 0f, float dev = 0f,
		bool ground = false, bool flip = false,
		WorldGeneration.PlaceCheckDelegate check = null,
		bool trap = false, Vector2 dir = default)
	{
		foreach (object tick in Spread(world, path, min, max, y, rot, dev, ground, flip, check, trap, dir, false))
			yield return tick;
	}

	static IEnumerable Spread(
		WorldGeneration world, string path, float min, float max,
		float y, float rot, float dev, bool ground, bool flip,
		WorldGeneration.PlaceCheckDelegate check, bool trap, Vector2 dir, bool forceFlip)
	{
		GameObject prefab = Resources.Load<GameObject>(path);
		if (prefab == null || world == null)
			yield break;
		if (dir == Vector2.zero)
			dir = Vector2.down;
		float num = (float)world.chunkWidth * (float)world.chunkHeight * UnityEngine.Random.Range(min, max);
		float yMax = trap ? BodyY(world) - 5f : world.halfHeight;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfHeight, yMax));
			bool open = !Physics2D.OverlapPoint(point, Ground()) || ground;
			int spent = CostMiss;
			if (open)
			{
				RaycastHit2D hit = Physics2D.Raycast(point, dir, WorldGeneration.CHUNKSIZE, Ground());
				Vector2Int seat = world.WorldToBlockPos(hit.point - Vector2.up * 0.5f);
				bool inside = hit
					&& Mathf.Abs(hit.point.x) < world.halfWidth - 1f
					&& Mathf.Abs(hit.point.y) < world.halfHeight - 1f
					&& (check == null || check(seat));
				if (inside)
				{
					float offset = UnityEngine.Random.Range(y - dev, y + dev);
					GameObject made = Object.Instantiate(
						prefab,
						hit.point - dir * offset,
						Quaternion.Euler(0f, 0f, prefab.transform.eulerAngles.z + UnityEngine.Random.Range(-rot, rot)));
					BuildingEntity built = made.GetComponent<BuildingEntity>();
					if (built != null)
					{
						Vector2Int on = world.WorldToBlockPos(hit.point + dir * 0.5f);
						built.blockPlacedOn = on;
						if (built.requireGround)
							Listen(world, on, built.CheckSeating);
					}
					if (flip)
					{
						float face = UnityEngine.Random.Range(0f, 1f) > 0.5f ? -1f : 1f;
						made.transform.localScale = new Vector3(face, 1f, 1f);
					}
					if (forceFlip)
						made.transform.localScale = new Vector3(-1f, 1f, 1f);
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Bandages(WorldGeneration world, float num)
	{
		Object bandage = Resources.Load("bandage");
		if (bandage == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground()))
			{
				RaycastHit2D hit = Physics2D.Raycast(point, Vector2.down, WorldGeneration.CHUNKSIZE, Ground());
				if (hit)
				{
					GameObject made = (GameObject)Object.Instantiate(
						bandage, hit.point + Vector2.up, Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f)));
					Item item = made.GetComponent<Item>();
					if (item != null)
						item.condition = UnityEngine.Random.Range(1, 4) * 0.33f;
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Ropes(WorldGeneration world, float num)
	{
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground())
				&& !Physics2D.Raycast(point, Vector2.down, 12f, Ground()))
			{
				spent = CostSpawn;
				RaycastHit2D hit = Physics2D.Linecast(point, point + Vector2.down * 25f, Ground());
				Vector2 end = hit ? hit.point : point + Vector2.down * 25f;
				GameObject rope = Utils.Create("climbingropeextended", point, 0f);
				if (rope != null)
				{
					Climbable climb = rope.GetComponent<Climbable>();
					if (climb != null)
					{
						climb.points.Add(end);
						climb.points.Add(point);
					}
					if (rope.transform.childCount > 0)
						rope.transform.GetChild(0).localScale = new Vector2(1f, Vector2.Distance(point, end) / 25f);
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Dropped(WorldGeneration world, float num, string path, bool spin)
	{
		Object prefab = Resources.Load(path);
		if (prefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground()))
			{
				RaycastHit2D hit = Physics2D.Raycast(point, Vector2.down, WorldGeneration.CHUNKSIZE, Ground());
				if (hit)
				{
					Quaternion rot = spin
						? Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f))
						: Quaternion.identity;
					Object.Instantiate(prefab, hit.point + Vector2.up, rot);
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Pipes(WorldGeneration world, float num)
	{
		Object prefab = Resources.Load("oilpipe");
		if (prefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Object.Instantiate(
				prefab,
				new Vector2(
					UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
					UnityEngine.Random.Range(-world.halfHeight, world.halfHeight)),
				Quaternion.Euler(0f, 0f, UnityEngine.Random.value * 360f));
			foreach (object tick in Breath(CostSpawn))
				yield return tick;
		}
	}

	static IEnumerable Turrets(WorldGeneration world, float num)
	{
		Object prefab = Resources.Load("turret");
		if (prefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfHeight, BodyY(world) - 5f));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground()))
			{
				float side = UnityEngine.Random.value > 0.5f ? 1f : -1f;
				RaycastHit2D hit = Physics2D.Raycast(point, Vector2.right * side, WorldGeneration.CHUNKSIZE, Ground());
				if (hit)
				{
					GameObject made = (GameObject)Object.Instantiate(prefab, hit.point + Vector2.left * side, Quaternion.identity);
					made.transform.localScale = new Vector2(-side, 1f);
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Stalactites(WorldGeneration world, float num)
	{
		Object prefab = Resources.Load("stalactite");
		if (prefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfHeight, BodyY(world) - 5f));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground()))
			{
				RaycastHit2D hit = Physics2D.Raycast(point, Vector2.up, WorldGeneration.CHUNKSIZE * 2, Ground());
				if (hit && hit.point.y < world.halfHeight - 5f)
				{
					GameObject made = (GameObject)Object.Instantiate(prefab, hit.point + Vector2.down * 1.5f, Quaternion.identity);
					BuildingEntity built = made.GetComponent<BuildingEntity>();
					StalactiteDropper drop = made.GetComponent<StalactiteDropper>();
					Vector2Int on = world.WorldToBlockPos(hit.point + Vector2.up * 0.5f);
					if (built != null)
						built.blockPlacedOn = on;
					if (drop != null)
						Listen(world, on, drop.CheckSeating);
					float face = UnityEngine.Random.Range(0f, 1f) > 0.5f ? -1f : 1f;
					made.transform.localScale = new Vector3(face, 1f, 1f);
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Vines(WorldGeneration world, float num)
	{
		Object hookPrefab = Resources.Load("Special/sandvinehook");
		Object ropePrefab = Resources.Load("Special/sandvinerope");
		if (hookPrefab == null || ropePrefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Vector2 point = new Vector2(
				UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
				UnityEngine.Random.Range(-world.halfHeight, world.halfHeight));
			int spent = CostMiss;
			if (!Physics2D.OverlapPoint(point, Ground()))
			{
				RaycastHit2D up = Physics2D.Raycast(point, Vector2.up, WorldGeneration.CHUNKSIZE * 4, Ground());
				RaycastHit2D down = Physics2D.Raycast(point, Vector2.down, WorldGeneration.CHUNKSIZE * 4, Ground());
				Vector2Int pos = world.WorldToBlockPos(up.point - Vector2.up * 0.5f);
				if (up && down && down.point.y < world.halfHeight - 5f)
				{
					Color color = Color.Lerp(Color.gray, Color.white, UnityEngine.Random.value);
					Vector2 hookAt = world.BlockToWorldPos(pos);
					GameObject hook = (GameObject)Object.Instantiate(hookPrefab, hookAt, Quaternion.identity);
					GameObject rope = (GameObject)Object.Instantiate(ropePrefab, (hookAt + down.point) * 0.5f, Quaternion.identity);
					SpriteRenderer ropeSprite = rope.GetComponent<SpriteRenderer>();
					SpriteRenderer hookSprite = hook.GetComponent<SpriteRenderer>();
					if (ropeSprite != null)
					{
						ropeSprite.size = new Vector2(2.5f, Mathf.Abs(hookAt.y - down.point.y));
						ropeSprite.color = color;
						ropeSprite.flipX = UnityEngine.Random.value > 0.5f;
					}
					if (hookSprite != null)
					{
						hookSprite.color = color;
						hookSprite.flipX = UnityEngine.Random.value > 0.5f;
					}
					float scale = UnityEngine.Random.Range(0.15f, 1f);
					hook.transform.localScale = new Vector3(scale, 1f);
					rope.transform.localScale = new Vector3(scale, 1f);
					Climbable climb = rope.GetComponent<Climbable>();
					if (climb != null)
					{
						climb.points.Add(down.point);
						climb.points.Add(up.point);
						climb.downwardsVelocity = (1f - scale) * 16f;
					}
					spent = CostSpawn;
				}
			}
			foreach (object tick in Breath(spent))
				yield return tick;
		}
	}

	static IEnumerable Flowers(WorldGeneration world, float num)
	{
		Object prefab = Resources.Load("wallflower");
		if (prefab == null)
			yield break;
		for (int i = 0; (float)i < num; i++)
		{
			Object.Instantiate(
				prefab,
				new Vector2(
					UnityEngine.Random.Range(-world.halfWidth, world.halfWidth),
					UnityEngine.Random.Range(-world.halfHeight, world.halfHeight)),
				Quaternion.Euler(0f, 0f, UnityEngine.Random.value * 360f));
			foreach (object tick in Breath(CostSpawn))
				yield return tick;
		}
	}

	static void Listen(WorldGeneration world, Vector2Int cell, UnityEngine.Events.UnityAction call)
	{
		if (call == null || world.ChunkUpdated == null)
			return;
		int cx = cell.x / WorldGeneration.CHUNKSIZE;
		int cy = cell.y / WorldGeneration.CHUNKSIZE;
		var events = world.ChunkUpdated;
		if (cx < 0 || cy < 0 || cx >= events.GetLength(0) || cy >= events.GetLength(1))
			return;
		if (events[cx, cy] != null)
			events[cx, cy].AddListener(call);
	}

	static bool Soft(WorldGeneration world, Vector2Int pos)
	{
		return world.GetBlock(pos) < 3 || world.isSoil(pos);
	}

	static bool Desert(WorldGeneration world, Vector2Int pos)
	{
		int id = world.GetBlock(pos);
		return id == 12 || id == 13 || world.isSoil(pos);
	}

	static bool Spike(WorldGeneration world, Vector2Int pos)
	{
		int id = world.GetBlock(pos);
		return id == 18 || id == 17 || id == 19;
	}

	static bool Flat(WorldGeneration world, Vector2Int pos)
	{
		return world.GetBlock(pos) > 0
			&& world.GetBlock(pos + Vector2Int.right) > 0
			&& world.GetBlock(pos - Vector2Int.right) > 0;
	}

	static Vector2 RandInset(WorldGeneration world)
	{
		return new Vector2(
			UnityEngine.Random.Range(-world.halfWidth + 32, world.halfWidth - 32),
			UnityEngine.Random.Range(-world.halfWidth + 32, world.halfWidth - 32));
	}

	static Vector2 SnapDown(Vector2 point, float distance)
	{
		RaycastHit2D hit = Physics2D.Raycast(point, Vector2.down, distance, Ground());
		return hit ? hit.point : point;
	}

	static float BodyY(WorldGeneration world)
	{
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			return PlayerCamera.main.body.transform.position.y;
		return 0f;
	}

	static float Area(WorldGeneration world, float min, float max, float scale)
	{
		return (float)world.chunkWidth * (float)world.chunkHeight * UnityEngine.Random.Range(min, max) * scale;
	}

	static int Ground()
	{
		if (_ground == int.MinValue)
			_ground = LayerMask.GetMask("Ground");
		return _ground;
	}
}
