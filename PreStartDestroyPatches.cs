using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// BatteryRecharger.joints and Talker.text are filled in Start.
/// stream cull and a layer unload destroy copies that never started, and both
/// OnDestroy methods walk those fields with no null check.
/// </summary>
internal static class PreStartDestroyPatches
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(BatteryRecharger), "OnDestroy"),
			prefix: new HarmonyMethod(typeof(PreStartDestroyPatches), nameof(RechargerOnDestroy)));
		harmony.Patch(
			AccessTools.Method(typeof(Talker), "OnDestroy"),
			prefix: new HarmonyMethod(typeof(PreStartDestroyPatches), nameof(TalkerOnDestroy)));
	}

	private static bool RechargerOnDestroy(BatteryRecharger __instance)
	{
		return FieldAccess.Get<FixedJoint2D[]>(__instance, "joints") != null;
	}

	private static bool TalkerOnDestroy(Talker __instance)
	{
		return __instance != null && __instance.text != null;
	}
}
