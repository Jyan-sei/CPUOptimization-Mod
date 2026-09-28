using HarmonyLib;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class VisibilityStreamPatch
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "UpdateChunkVisibility"),
			prefix: new HarmonyMethod(typeof(VisibilityStreamPatch), nameof(UpdateChunkVisibilityPrefix)));

		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "DisableAllChunks"),
			prefix: new HarmonyMethod(typeof(VisibilityStreamPatch), nameof(DisableAllChunksPrefix)));

		var renderer = AccessTools.Method(typeof(WorldGeneration), "GetClosestChunkRenderer", new[] { typeof(UnityEngine.Vector2Int) });
		if (renderer != null)
		{
			harmony.Patch(renderer, prefix: new HarmonyMethod(typeof(VisibilityStreamPatch), nameof(ClosestRendererPrefix)));
		}

		var chunk = AccessTools.Method(typeof(WorldGeneration), "GetClosestChunk", new[] { typeof(UnityEngine.Vector2Int) });
		if (chunk != null)
		{
			harmony.Patch(chunk, postfix: new HarmonyMethod(typeof(VisibilityStreamPatch), nameof(ClosestChunkPostfix)));
		}

		harmony.Patch(
			AccessTools.Method(typeof(BuildingEntity), "Update"),
			prefix: new HarmonyMethod(typeof(VisibilityStreamPatch), nameof(BuildingUpdatePrefix)));
	}

	private static bool UpdateChunkVisibilityPrefix(WorldGeneration __instance)
	{
		if (!StreamState.Active || Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return true;
		if (__instance.generatingWorld)
			return false;
		// bootstrap still needs Sync so cull keeps moving even if the cam hasnt moved
		StreamController.Sync(__instance);
		return false;
	}

	private static bool DisableAllChunksPrefix(WorldGeneration __instance)
	{
		if (!StreamState.Active)
			return true;

		if (__instance.renderChunks == null)
			return false;
		int w = __instance.renderChunks.GetLength(0);
		int h = __instance.renderChunks.GetLength(1);
		for (int x = 0; x < w; x++)
		for (int y = 0; y < h; y++)
		{
			TilemapRenderer r = __instance.renderChunks[x, y];
			if (r != null)
				r.enabled = false;
		}
		return false;
	}

	private static bool ClosestRendererPrefix(WorldGeneration __instance, UnityEngine.Vector2Int pos, ref TilemapRenderer __result)
	{
		if (!StreamState.Active)
			return true;

		int cx = UnityEngine.Mathf.Clamp(pos.x / WorldGeneration.CHUNKSIZE, 0, (int)__instance.chunkWidth - 1);
		int cy = UnityEngine.Mathf.Clamp(pos.y / WorldGeneration.CHUNKSIZE, 0, (int)__instance.chunkHeight - 1);
		if (__instance.renderChunks != null && __instance.renderChunks[cx, cy] != null)
		{
			__result = __instance.renderChunks[cx, cy];
			return false;
		}

		StreamState.EnsureDummy();
		__result = StreamState.DummyRenderer;
		return false;
	}

	private static void ClosestChunkPostfix(WorldGeneration __instance, UnityEngine.Vector2Int pos, ref Tilemap __result)
	{
		if (!StreamState.Active)
			return;
		if (__result != null)
			return;

		// callers that only wanted a parent can no-op on null
	}

	private static bool BuildingUpdatePrefix()
	{
		return !StreamState.SuppressBuildingDrops;
	}
}
