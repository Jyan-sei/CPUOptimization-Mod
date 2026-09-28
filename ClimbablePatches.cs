using System.Collections.Generic;
using HarmonyLib;

namespace CPUOptimization2;

/// <summary>
/// stock Climbable.OnDestroy does allClimbables.Remove with no null check.
/// the list only exists after Start, so a pre-Start destroy (stream cull etc) NREs.
/// </summary>
internal static class ClimbablePatches
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(Climbable), "OnDestroy"),
			prefix: new HarmonyMethod(typeof(ClimbablePatches), nameof(OnDestroyPrefix)));
	}

	private static bool OnDestroyPrefix(Climbable __instance)
	{
		List<Climbable> all = Climbable.allClimbables;
		if (all == null)
			return false;
		all.Remove(__instance);
		return false;
	}
}
