using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

internal static class TraderPatches
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(TraderScript), "Start"),
			prefix: new HarmonyMethod(typeof(TraderPatches), nameof(StartPrefix)));
	}

	/// <summary>
	/// stock Start resets rep to 100 and remakes inventory.
	/// after a stream respawn we already put capture state back, skip that wipe
	/// </summary>
	private static bool StartPrefix(TraderScript __instance)
	{
		if (__instance == null)
			return true;
		int id = __instance.GetInstanceID();
		if (!StreamState.SkipTraderStart.Remove(id))
			return true;

		// keep capture MoveRange / hostility / inventory. only fix the runtime caches Start would set
		FieldAccess.Set(__instance, "desiredPos", (Vector2)__instance.transform.position);
		FieldAccess.Set(__instance, "standAmount", 0f);
		if (__instance.MoveRange.min == 0f && __instance.MoveRange.max == 0f)
		{
			float x = __instance.transform.position.x;
			__instance.MoveRange = new RangeF(x - 5f, x + 5f);
			Plugin.Log.LogWarning(
				$"trader Start skip: MoveRange was 0,0 — fallback ±5 at x={x:0.#} (aggro would clamp to world x=0)");
		}
		if (__instance.items == null || __instance.items.Count == 0)
		{
			var gen = AccessTools.Method(typeof(TraderScript), "GenerateInventory");
			gen?.Invoke(__instance, null);
		}
		return false;
	}
}
