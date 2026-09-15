using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ChunkSimLifecyclePatches
{
	[HarmonyPatch(typeof(BuildingEntity), "Start")]
	internal static class BuildingStartPatch
	{
		static void Postfix(BuildingEntity __instance)
		{
			ParticleCullRegistry.RegisterTree(__instance);

			if (!ChunkSimState.IsActive)
				return;

			BuildingSimRegistry.Register(__instance);
			ChunkSimBodySync.ApplyBuilding(__instance);

			SoundCannon cannon = __instance.GetComponent<SoundCannon>();
			if (cannon)
			{
				SoundCannonSimRegistry.Register(cannon);
				ChunkSimSoundCannonSync.Apply(cannon);
			}
		}
	}

	[HarmonyPatch(typeof(BuildingEntity))]
	internal static class BuildingDestroyPatch
	{
		static MethodBase TargetMethod() => AccessTools.Method(typeof(BuildingEntity), "OnDestroy");

		static void Prefix(BuildingEntity __instance)
		{
			if (!ChunkSimState.IsActive)
				return;

			ChunkSimBodySync.OnBuildingDestroyed(__instance);
		}
	}

	[HarmonyPatch(typeof(Item), "Start")]
	internal static class ItemStartPatch
	{
		static void Postfix(Item __instance)
		{
			if (!ChunkSimState.IsActive)
				return;

			if (!__instance.transform.parent)
				ChunkSimBodySync.ApplyItem(__instance);
		}
	}

	[HarmonyPatch(typeof(Item), "OnDestroy")]
	internal static class ItemDestroyPatch
	{
		static void Prefix(Item __instance)
		{
			ChunkSimBodySync.OnItemDestroyed(__instance);
		}
	}

	[HarmonyPatch(typeof(Item), "OnTransformParentChanged")]
	internal static class ItemParentChangedPatch
	{
		static void Postfix(Item __instance)
		{
			if (__instance.transform.parent)
				ChunkSimBodySync.RestoreItemIfOptDisabled(__instance);
		}
	}

	[HarmonyPatch(typeof(IKHandle), "Start")]
	internal static class IkHandleStartPatch
	{
		static void Postfix(IKHandle __instance)
		{
			if (!ChunkSimState.IsActive)
				return;

			ChunkSimTrackState track = ChunkSimTrackTable.Get(__instance);
			track.IkSpider = __instance.GetComponentInParent<SpiderHandler>();
			track.IkBuilding = __instance.GetComponentInParent<BuildingEntity>();
			track.IkGateRevision = -1;
		}
	}

	[HarmonyPatch(typeof(IKHandle), "OnDestroy")]
	internal static class IkHandleDestroyPatch
	{
		static void Prefix(IKHandle __instance) => ChunkSimTrackTable.Remove(__instance);
	}

	[HarmonyPatch(typeof(SoundCannon), "OnDestroy")]
	internal static class SoundCannonDestroyPatch
	{
		static void Prefix(SoundCannon __instance) => ChunkSimSoundCannonSync.OnDestroyed(__instance);
	}
}
