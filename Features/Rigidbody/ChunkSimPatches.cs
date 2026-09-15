using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

[HarmonyPatch(typeof(Item), "Update")]
internal static class ItemChunkSimPatch
{
	// world-loose items: skip vanilla update, apply rb via sync cache. parented/dying = vanilla
	static bool Prefix(Item __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;

		if (__instance.transform.parent)
		{
			ChunkSimBodySync.RestoreItemIfOptDisabled(__instance);
			return true;
		}

		if (__instance.condition <= 0f && __instance.Stats.destroyAtZeroCondition)
		{
			ChunkSimBodySync.RestoreItemIfOptDisabled(__instance);
			return true;
		}

		WorldGeneration world = WorldGeneration.world;
		if (!world || !world.worldExists)
			return true;

		bool inSim = ChunkSimBodySync.ShouldSimulateItem(__instance);
		if (!inSim)
		{
			if (ChunkSimBodySync.IsItemAwake(__instance))
				ChunkSimBodySync.ApplyItemSleep(__instance);
			return false;
		}

		ChunkSimBodySync.ApplyItemWake(__instance);

		ChunkSimTrackState track = ChunkSimTrackTable.Get(__instance);
		track.ItemDecayAccum += Time.deltaTime;
		if (track.ItemDecayAccum >= ItemDecayHelper.IntervalSeconds)
		{
			ItemDecayHelper.ApplyDecay(__instance, track.ItemDecayAccum);
			track.ItemDecayAccum = 0f;
		}

		return false;
	}
}

[HarmonyPatch(typeof(WaterContainerItem), "Update")]
internal static class WaterContainerItemChunkSimPatch
{
	static bool Prefix(WaterContainerItem __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;

		Item item = __instance.GetComponent<Item>();
		if (!item)
			return true;

		if (item.transform.parent)
			return true;

		return ChunkSimBodySync.ShouldSimulateItem(item);
	}
}

[HarmonyPatch(typeof(BuildingEntity), "Update")]
internal static class BuildingChunkSimPatch
{
	// off-window healthy buildings skip update (rb from window sync). dying = vanilla destroy/drops
	static bool Prefix(BuildingEntity __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;

		if (__instance.health < 0.5f)
		{
			ChunkSimTrackState track = ChunkSimTrackTable.Get(__instance);
			track.BuildingDestroyQueued = true;
			ChunkSimBodySync.ForceEnableBuildingUpdate(__instance, track);
			return true;
		}

		if (__instance.GetComponent<ElderThornbackBehaviour>())
			return true;

		if (!ChunkSimUpdateSkip.ShouldRunBuildingUpdate(__instance))
			return false;

		if (!__instance.ignoreBodyOptimize)
			ApplyBodyTypeWithoutChunkLookup(__instance);

		return false;
	}

	static void ApplyBodyTypeWithoutChunkLookup(BuildingEntity building)
	{
		ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
		Rigidbody2D rb = track.Rb ??= building.GetComponent<Rigidbody2D>();
		if (!rb)
			return;

		bool inSim = ChunkSimState.ShouldSimulateWorldPos(building.transform.position);
		RigidbodyType2D want = inSim ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;
		if (rb.bodyType != want)
			rb.bodyType = want;
	}
}

[HarmonyPatch(typeof(SpiderHandler), "Update")]
internal static class SpiderChunkSimPatch
{
	static bool Prefix(SpiderHandler __instance)
	{
		BuildingEntity bld = __instance.GetComponent<BuildingEntity>();
		bool allow = bld
			? ChunkSimUpdateSkip.ShouldRunBuildingUpdate(bld)
			: ChunkSimUpdateSkip.ShouldRunAt(__instance);
		if (!allow)
			return false;

		return SpiderAnimThrottle.ShouldRunFullAnim(__instance);
	}
}

[HarmonyPatch(typeof(SpiderHandler), "FixedUpdate")]
internal static class SpiderFixedUpdateChunkSimPatch
{
	static bool Prefix(SpiderHandler __instance)
	{
		BuildingEntity bld = __instance.GetComponent<BuildingEntity>();
		if (bld)
			return ChunkSimUpdateSkip.ShouldRunBuildingUpdate(bld);
		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(SpiderHandler), "OnCollisionStay2D")]
internal static class SpiderCollisionChunkSimPatch
{
	static bool Prefix(SpiderHandler __instance)
	{
		BuildingEntity bld = __instance.GetComponent<BuildingEntity>();
		if (bld)
			return ChunkSimUpdateSkip.ShouldRunBuildingUpdate(bld);
		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(IKHandle), "Update")]
internal static class IkHandleChunkSimPatch
{
	static bool Prefix(IKHandle __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(__instance);
		if (track.IkGateRevision != ChunkSimState.WindowRevision)
		{
			track.IkGateRevision = ChunkSimState.WindowRevision;
			track.IkGateAllow = ComputeGate(__instance, track);
		}

		if (!track.IkGateAllow)
			return false;

		if (track.IkSpider && !SpiderAnimThrottle.ShouldRunFullAnim(track.IkSpider))
			return false;

		return true;
	}

	static bool ComputeGate(IKHandle handle, ChunkSimTrackState track)
	{
		if (track.IkBuilding)
			return ChunkSimUpdateSkip.ShouldRunBuildingUpdate(track.IkBuilding);

		if (track.IkSpider)
			return ChunkSimUpdateSkip.ShouldRunAt(track.IkSpider);

		return ChunkSimUpdateSkip.ShouldRunAt(handle);
	}
}

[HarmonyPatch(typeof(JumpPadScript), "Update")]
internal static class JumpPadChunkSimPatch
{
	static bool Prefix(JumpPadScript __instance) =>
		ChunkSimUpdateSkip.ShouldRunAt(__instance);
}

[HarmonyPatch(typeof(CoilScript), "Update")]
internal static class CoilChunkSimPatch
{
	static bool Prefix(CoilScript __instance) =>
		ChunkSimUpdateSkip.ShouldRunAt(__instance);
}

[HarmonyPatch(typeof(TurretScript), "Update")]
internal static class TurretChunkSimPatch
{
	static bool Prefix(TurretScript __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;

		BuildingEntity build = __instance.GetComponent<BuildingEntity>();
		if (build && build.health < 350f)
			return true;

		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(MineScript), "Update")]
internal static class MineChunkSimPatch
{
	static bool Prefix(MineScript __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;
		if (__instance.timeSincePressed > 0f)
			return true;

		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(BearTrap), "Update")]
internal static class BearTrapChunkSimPatch
{
	static bool Prefix(BearTrap __instance)
	{
		if (!ChunkSimState.IsActive)
			return true;
		if (__instance.caughtLimb)
			return true;

		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(StalactiteDropper), "Start")]
internal static class DropperParticleRegisterPatch
{
	static void Postfix(StalactiteDropper __instance)
	{
		ParticleCullRegistry.RegisterTree(__instance);
	}
}

[HarmonyPatch(typeof(StalactiteDropper), "OnWillRenderObject")]
internal static class DropperChunkSimPatch
{
	static bool Prefix(StalactiteDropper __instance) =>
		ChunkSimUpdateSkip.ShouldRunAt(__instance);
}

[HarmonyPatch(typeof(CaveTickSpawner), "Update")]
internal static class CaveTickSpawnerChunkSimPatch
{
	static bool Prefix(CaveTickSpawner __instance)
	{
		ParticleCullRegistry.Register(__instance.GetComponent<ParticleSystem>());
		return ChunkSimUpdateSkip.ShouldRunAt(__instance);
	}
}

[HarmonyPatch(typeof(ExperimentOscillate), "Update")]
internal static class GlowshroomOscillateChunkSimPatch
{
	static bool Prefix(ExperimentOscillate __instance) =>
		ChunkSimUpdateSkip.ShouldRunAt(__instance);
}
