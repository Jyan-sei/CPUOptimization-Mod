using System.Collections.Generic;
using CPUOptimization.Features.Mp;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// window-driven rb sync for buildings and world-loose items
internal static class ChunkSimBodySync
{
	internal static int ItemsSimCount { get; private set; }
	internal static int ItemsSleepCount { get; private set; }
	internal static int DespawnerSleepDisableCount { get; private set; }
	internal static int DespawnerWakeEnableCount { get; private set; }

	internal static void SyncAll()
	{
		if (!ChunkSimState.IsActive)
			return;

		SyncAllBuildings();
		SyncAllItems();
		ChunkSimSoundCannonSync.SyncAll();
		ChunkSimForceForMPSync.SyncAll();
	}

	internal static void ResetItemCounts()
	{
		ItemsSimCount = 0;
		ItemsSleepCount = 0;
	}

	internal static void IncrementItemsSim() => ItemsSimCount++;

	internal static void IncrementItemsSleep() => ItemsSleepCount++;

	internal static void SyncAllBuildings()
	{
		if (!ChunkSimState.IsActive)
			return;

		BuildingSimRegistry.ResetCounts();
		IReadOnlyList<BuildingEntity> buildings = BuildingSimRegistry.All;
		for (int i = 0; i < buildings.Count; i++)
		{
			BuildingEntity building = buildings[i];
			if (!building)
				continue;

			ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
			track.BuildingClass = BuildingSimClass.None;
			ApplyBuilding(building);
			SyncBuildingUpdateEnabled(building, track);
		}
	}

	internal static void SyncAllItems()
	{
		if (!ChunkSimState.IsActive)
			return;

		ItemsSimCount = 0;
		ItemsSleepCount = 0;

		for (int i = 0; i < Item.allItems.Count; i++)
		{
			Item item = Item.allItems[i];
			if (!item || item.transform.parent)
				continue;

			if (ApplyItem(item))
				ItemsSimCount++;
			else
				ItemsSleepCount++;
		}
	}

	internal static void ApplyBuilding(BuildingEntity building)
	{
		if (!building || !ChunkSimState.IsActive)
			return;
		if (Plugin.ChunkSimBuildingsEnabled == null || !Plugin.ChunkSimBuildingsEnabled.Value)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
		if (track.Rb == null)
		{
			track.Rb = building.GetComponent<Rigidbody2D>();
			track.IsElder = building.GetComponent<ElderThornbackBehaviour>();
			track.IgnoreBodyOptimize = building.ignoreBodyOptimize;
		}

		if (!track.Rb)
		{
			WorldGeneration world = WorldGeneration.world;
			if (!world || !world.worldExists)
				return;

			bool inSim = ChunkSimState.ShouldSimulateWorldPos(building.transform.position);
			BuildingSimClass rbLessClass = inSim ? BuildingSimClass.Dynamic : BuildingSimClass.Static;
			BuildingSimRegistry.SetClass(track.BuildingClass, rbLessClass);
			track.BuildingClass = rbLessClass;
			SyncBuildingUpdateEnabled(building, track);
			return;
		}

		BuildingSimClass newClass;
		if (track.IsElder)
		{
			track.Rb.bodyType = RigidbodyType2D.Dynamic;
			newClass = BuildingSimClass.Elder;
		}
		else if (track.IgnoreBodyOptimize)
		{
			newClass = track.Rb.bodyType == RigidbodyType2D.Dynamic
				? BuildingSimClass.Dynamic
				: BuildingSimClass.Static;
		}
		else
		{
			WorldGeneration world = WorldGeneration.world;
			if (!world || !world.worldExists)
				return;

			bool inSim = ChunkSimState.ShouldSimulateWorldPos(building.transform.position);
			track.Rb.bodyType = inSim ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;
			newClass = inSim ? BuildingSimClass.Dynamic : BuildingSimClass.Static;
		}

		BuildingSimRegistry.SetClass(track.BuildingClass, newClass);
		track.BuildingClass = newClass;
		SyncBuildingUpdateEnabled(building, track);
	}

	internal static void SyncBuildingUpdateEnabled(BuildingEntity building, ChunkSimTrackState track)
	{
		if (!building)
			return;

		if (building.health < 0.5f)
		{
			NotifyBuildingHealthChanged(building);
			return;
		}

		bool inSim = ChunkSimState.ShouldSimulateWorldPos(building.transform.position);
		bool wantEnabled = track.IsElder
			|| track.IgnoreBodyOptimize
			|| inSim;

		if (wantEnabled)
			RestoreBuildingUpdate(building, track);
		else if (!track.BuildingDisabledByOpt)
		{
			building.enabled = false;
			track.BuildingDisabledByOpt = true;
		}
	}

	internal static void RestoreBuildingUpdate(BuildingEntity building, ChunkSimTrackState track)
	{
		if (!building || !track.BuildingDisabledByOpt)
			return;

		building.enabled = true;
		track.BuildingDisabledByOpt = false;
	}

	internal static void ForceEnableBuildingUpdate(BuildingEntity building, ChunkSimTrackState track)
	{
		if (!building || track == null)
			return;

		if (track.BuildingDisabledByOpt || !building.enabled)
		{
			building.enabled = true;
			track.BuildingDisabledByOpt = false;
		}
	}

	// run vanilla destroy/drops when health drops while update was skipped or opt-disabled
	internal static void NotifyBuildingHealthChanged(BuildingEntity building)
	{
		if (!building || !ChunkSimState.IsActive || building.health >= 0.5f)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
		if (track.BuildingDestroyQueued)
			return;

		ForceEnableBuildingUpdate(building, track);
		track.BuildingDestroyQueued = true;
		building.Update();
	}

	internal static void ProcessDyingBuildings()
	{
		if (!ChunkSimState.IsActive)
			return;

		IReadOnlyList<BuildingEntity> buildings = BuildingSimRegistry.All;
		for (int i = 0; i < buildings.Count; i++)
		{
			BuildingEntity building = buildings[i];
			if (building && building.health < 0.5f)
				NotifyBuildingHealthChanged(building);
		}
	}

	// apply sim state for world-loose item. true when simulated
	internal static bool ApplyItem(Item item)
	{
		if (!item || item.transform.parent || !ChunkSimState.IsActive)
			return false;
		if (Plugin.ChunkSimItemsEnabled == null || !Plugin.ChunkSimItemsEnabled.Value)
			return false;

		bool inSim = ShouldSimulateItem(item);
		if (inSim)
			ApplyItemWake(item);
		else
			ApplyItemSleep(item);

		return inSim;
	}

	internal static bool ShouldSimulateItem(Item item)
	{
		if (!item || item.transform.parent)
			return false;

		// Exempt glowplants, lightbulbs etc. - they can stay forever (light cull will handle lights, sprites are cheap)
		if (item.id != null)
		{
			string id = item.id.ToLowerInvariant();
			if (id.Contains("glowplant") || id.Contains("lightbulb"))
				return true;
		}

		WorldGeneration world = WorldGeneration.world;
		if (!world || !world.worldExists)
			return false;

		return ChunkSimState.ShouldSimulateWorldPos(item.transform.position);
	}

	internal static bool IsItemAwake(Item item)
	{
		ChunkSimTrackState track = ChunkSimTrackTable.Get(item);
		return track.ItemInitialized && track.ItemLastInSim;
	}

	internal static void ApplyItemWake(Item item)
	{
		if (!item)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(item);
		RestoreItemComponents(item, track);

		if (track.ItemInitialized && track.ItemLastInSim)
			return;

		if (item.rb)
			item.rb.simulated = true;
		if (item.affect)
			item.affect.enabled = true;

		track.ItemLastInSim = true;
		track.ItemInitialized = true;
	}

	internal static void ApplyItemSleep(Item item)
	{
		if (!item)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(item);
		if (track.ItemInitialized && !track.ItemLastInSim && track.ItemDisabledByOpt)
		{
			DisableItemSiblings(item, track);
			return;
		}

		if (item.rb)
			item.rb.simulated = false;
		if (item.affect)
			item.affect.enabled = false;

		DisableItemSiblings(item, track);

		if (item.enabled)
		{
			item.enabled = false;
			track.ItemDisabledByOpt = true;
		}

		track.ItemLastInSim = false;
		track.ItemInitialized = true;
		track.ItemDecayAccum = 0f;
	}

	internal static void RestoreItemIfOptDisabled(Item item)
	{
		if (!item)
			return;

		RestoreItemComponents(item, ChunkSimTrackTable.Get(item));
	}

	// force initial baseline (as if in-sim) for crates/items at load to prevent fall-before-first-visit
	internal static void ForceBaselineItem(Item item)
	{
		if (!item)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(item);
		RestoreItemComponents(item, track);

		if (item.rb)
			item.rb.simulated = true;
		if (item.affect)
			item.affect.enabled = true;

		track.ItemLastInSim = true;
		track.ItemInitialized = true;
		track.ItemDisabledByOpt = false;
		track.ItemDecayAccum = 0f;
	}

	internal static void RestoreItemComponents(Item item, ChunkSimTrackState track)
	{
		if (!item || track == null)
			return;

		if (track.ItemDisabledByOpt)
		{
			item.enabled = true;
			track.ItemDisabledByOpt = false;
		}

		RestoreItemSiblings(item, track);
	}

	internal static void DisableItemSiblings(Item item, ChunkSimTrackState track)
	{
		if (!item || track == null)
			return;

		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water && water.enabled)
		{
			water.enabled = false;
			track.ItemSiblingsDisabledByOpt |= ItemSiblingOptFlags.WaterContainer;
		}

		BatteryItem battery = item.GetComponent<BatteryItem>();
		if (battery && battery.enabled)
		{
			battery.enabled = false;
			track.ItemSiblingsDisabledByOpt |= ItemSiblingOptFlags.Battery;
		}

		LightItem light = item.GetComponent<LightItem>();
		if (light && light.enabled)
		{
			light.enabled = false;
			track.ItemSiblingsDisabledByOpt |= ItemSiblingOptFlags.Light;
		}

		Behaviour despawner = GetItemDespawner(item);
		if (despawner && despawner.enabled)
		{
			despawner.enabled = false;
			track.ItemSiblingsDisabledByOpt |= ItemSiblingOptFlags.ItemDespawner;
			DespawnerSleepDisableCount++;
		}
	}

	internal static void RestoreItemSiblings(Item item, ChunkSimTrackState track)
	{
		if (!item || track == null || track.ItemSiblingsDisabledByOpt == ItemSiblingOptFlags.None)
			return;

		if ((track.ItemSiblingsDisabledByOpt & ItemSiblingOptFlags.WaterContainer) != 0)
		{
			WaterContainerItem water = item.GetComponent<WaterContainerItem>();
			if (water)
				water.enabled = true;
		}

		if ((track.ItemSiblingsDisabledByOpt & ItemSiblingOptFlags.Battery) != 0)
		{
			BatteryItem battery = item.GetComponent<BatteryItem>();
			if (battery)
				battery.enabled = true;
		}

		if ((track.ItemSiblingsDisabledByOpt & ItemSiblingOptFlags.Light) != 0)
		{
			LightItem light = item.GetComponent<LightItem>();
			if (light)
				light.enabled = true;
		}

		if ((track.ItemSiblingsDisabledByOpt & ItemSiblingOptFlags.ItemDespawner) != 0)
		{
			Behaviour despawner = GetItemDespawner(item);
			if (despawner)
				despawner.enabled = true;
			DespawnerWakeEnableCount++;
		}

		track.ItemSiblingsDisabledByOpt = ItemSiblingOptFlags.None;
	}

	internal static void RestoreAllOptDisabled()
	{
		IReadOnlyList<BuildingEntity> buildings = BuildingSimRegistry.All;
		for (int i = 0; i < buildings.Count; i++)
		{
			BuildingEntity building = buildings[i];
			if (!building)
				continue;
			RestoreBuildingUpdate(building, ChunkSimTrackTable.Get(building));
		}

		for (int i = 0; i < Item.allItems.Count; i++)
		{
			Item item = Item.allItems[i];
			if (item)
				RestoreItemIfOptDisabled(item);
		}

		ChunkSimSoundCannonSync.RestoreAllOptDisabled();
	}

	internal static void OnBuildingDestroyed(BuildingEntity building)
	{
		if (!building)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
		RestoreBuildingUpdate(building, track);
		BuildingSimRegistry.SetClass(track.BuildingClass, BuildingSimClass.None);
		BuildingSimRegistry.Unregister(building);
		ChunkSimTrackTable.Remove(building);
	}

	internal static void OnItemDestroyed(Item item)
	{
		if (item)
			RestoreItemIfOptDisabled(item);
		ChunkSimTrackTable.Remove(item);
	}

	internal static void ConsumeDespawnerProbe(out int sleepDisable, out int wakeEnable)
	{
		sleepDisable = DespawnerSleepDisableCount;
		wakeEnable = DespawnerWakeEnableCount;
		DespawnerSleepDisableCount = 0;
		DespawnerWakeEnableCount = 0;
	}

	private static Behaviour GetItemDespawner(Item item)
	{
		if (!item || KrokMpReflect.ItemDespawnerType == null)
			return null;
		return item.GetComponent(KrokMpReflect.ItemDespawnerType) as Behaviour;
	}
}
