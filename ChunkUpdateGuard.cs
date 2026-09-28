using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// UpdateChunk copies worldBlocks onto the chunk tilemap. a streamed-out chunk
/// has no tilemap. Telchak's full-map refresh (altar Start) would null-ref there.
/// the bytes are already stored. skip the paint until the chunk exists again.
/// </summary>
internal static class ChunkUpdateGuard
{
	internal static void Apply(Harmony harmony)
	{
		MethodInfo method = AccessTools.Method(typeof(WorldGeneration), "UpdateChunk", new[] { typeof(Vector2Int) });
		if (method == null)
		{
			Plugin.Log.LogWarning("WorldGeneration.UpdateChunk was not found. null tilemaps stay unprotected.");
			return;
		}
		harmony.Patch(method, prefix: new HarmonyMethod(typeof(ChunkUpdateGuard), nameof(Prefix)));
	}

	private static bool Prefix(WorldGeneration __instance, Vector2Int chunk)
	{
		if (__instance == null)
			return true;
		Tilemap[,] chunks = WorldArrays.Chunks(__instance);
		if (chunks == null)
			return false;
		if (chunk.x < 0 || chunk.y < 0 || chunk.x >= chunks.GetLength(0) || chunk.y >= chunks.GetLength(1))
			return false;
		return chunks[chunk.x, chunk.y] != null;
	}
}
