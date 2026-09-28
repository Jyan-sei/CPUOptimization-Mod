using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class WorldArrays
{
	internal static Tilemap[,] Chunks(WorldGeneration world) =>
		FieldAccess.Get<Tilemap[,]>(world, "chunks");

	internal static ChunkScript[,] ChunkScripts(WorldGeneration world) =>
		FieldAccess.Get<ChunkScript[,]>(world, "chunkScripts");

	internal static ushort[,] WorldBlocks(WorldGeneration world) =>
		FieldAccess.Get<ushort[,]>(world, "worldBlocks");

	internal static void SetChunk(WorldGeneration world, int x, int y, Tilemap map)
	{
		Tilemap[,] chunks = Chunks(world);
		if (chunks != null)
			chunks[x, y] = map;
	}

	internal static void SetChunkScript(WorldGeneration world, int x, int y, ChunkScript script)
	{
		ChunkScript[,] scripts = ChunkScripts(world);
		if (scripts != null)
			scripts[x, y] = script;
	}

	internal static bool IsLoaded(WorldGeneration world, int x, int y)
	{
		Tilemap[,] chunks = Chunks(world);
		return chunks != null && x >= 0 && y >= 0 && x < chunks.GetLength(0) && y < chunks.GetLength(1) && chunks[x, y] != null;
	}
}
