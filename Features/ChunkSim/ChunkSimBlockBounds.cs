using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// block-space bounds for active chunk window (union or sp 2x2)
internal static class ChunkSimBlockBounds
{
	internal static bool TryGetSimulationRanges(
		WorldGeneration world,
		out RangeI rangeX,
		out RangeI rangeY,
		int marginChunks = 1)
	{
		rangeX = default;
		rangeY = default;
		if (!ChunkSimState.IsActive || !world || !world.worldExists)
			return false;

		int chunkSize = WorldGeneration.CHUNKSIZE;
		int margin = marginChunks * chunkSize;

		int minCx = int.MaxValue;
		int minCy = int.MaxValue;
		int maxCx = int.MinValue;
		int maxCy = int.MinValue;

		if (ChunkSimState.UseMpUnion && ChunkSimState.CurrentUnion != null)
		{
			ChunkSimUnionWindow union = ChunkSimState.CurrentUnion;
			for (int i = 0; i < union.ChunkCount; i++)
			{
				Vector2Int c = union.GetChunkAt(i);
				if (c.x < minCx) minCx = c.x;
				if (c.y < minCy) minCy = c.y;
				if (c.x > maxCx) maxCx = c.x;
				if (c.y > maxCy) maxCy = c.y;
			}
		}
		else if (ChunkSimState.Current != null)
		{
			ChunkSimWindow window = ChunkSimState.Current;
			minCx = window.Anchor.x;
			minCy = window.Anchor.y;
			maxCx = window.Anchor.x + 1;
			maxCy = window.Anchor.y + 1;
		}
		else
		{
			return false;
		}

		int minX = minCx * chunkSize - margin;
		int minY = minCy * chunkSize - margin;
		int maxX = (maxCx + 1) * chunkSize + margin;
		int maxY = (maxCy + 1) * chunkSize + margin;

		ClampRanges(world, ref minX, ref maxX, ref minY, ref maxY);
		rangeX = new RangeI(minX, maxX);
		rangeY = new RangeI(minY, maxY);
		return true;
	}

	internal static void ClampRanges(WorldGeneration world, ref int minX, ref int maxX, ref int minY, ref int maxY)
	{
		int maxBlock = (int)world.width - 2;
		int maxBlockY = (int)world.height - 2;

		if (minX < 1) minX = 1;
		if (minY < 1) minY = 1;
		if (maxX < 1) maxX = 1;
		if (maxY < 1) maxY = 1;
		if (maxX > maxBlock) maxX = maxBlock;
		if (maxY > maxBlockY) maxY = maxBlockY;
		if (minX > maxBlock) minX = maxBlock;
		if (minY > maxBlockY) minY = maxBlockY;
	}
}
