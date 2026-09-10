using System.Reflection;
using CPUOptimization.Features.ChunkSim;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.Mp.Patches;

// krokmp forces building rb static when timeScale > 5. chunk-sim owns body type in-window
[HarmonyPatch]
internal static class KrokMpBuildingOptimizeBypass
{
	private static MethodBase TargetMethod()
	{
		var type = AccessTools.TypeByName("KrokoshaCasualtiesMP.BuildingEntity_Update_MultiplayerPatch");
		return type != null
			? AccessTools.Method(type, "NewBuildingOptimizeThing")
			: null;
	}

	private static bool Prefix(Rigidbody2D rb)
	{
		if (!ChunkSimState.IsActive || !rb)
			return true;

		return !ChunkSimState.ShouldSimulateWorldPos(rb.position);
	}
}
