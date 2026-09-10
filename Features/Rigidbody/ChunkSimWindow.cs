using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// picks 2x2 chunk window (4 chunks) around camera. anchor shifts so camera block sits inside quad
internal sealed class ChunkSimWindow
{
	internal readonly Vector2Int Anchor;
	internal readonly Vector2Int CameraBlock;
	internal readonly Vector2Int CameraChunk;
	internal readonly int LocalX;
	internal readonly int LocalY;

	private readonly Vector2Int[] _chunks = new Vector2Int[4];

	internal ChunkSimWindow(Vector2Int anchor, Vector2Int cameraBlock, Vector2Int cameraChunk, int localX, int localY)
	{
		Anchor = anchor;
		CameraBlock = cameraBlock;
		CameraChunk = cameraChunk;
		LocalX = localX;
		LocalY = localY;
		_chunks[0] = new Vector2Int(anchor.x, anchor.y);
		_chunks[1] = new Vector2Int(anchor.x + 1, anchor.y);
		_chunks[2] = new Vector2Int(anchor.x, anchor.y + 1);
		_chunks[3] = new Vector2Int(anchor.x + 1, anchor.y + 1);
	}

	internal bool MatchesAnchor(ChunkSimWindow other) =>
		other != null && Anchor.x == other.Anchor.x && Anchor.y == other.Anchor.y;

	internal bool ContainsChunk(int cx, int cy) =>
		cx >= Anchor.x && cx <= Anchor.x + 1 && cy >= Anchor.y && cy <= Anchor.y + 1;

	internal bool ContainsWorldPos(WorldGeneration world, Vector3 worldPos)
	{
		if (!world || !world.worldExists)
			return false;
		Vector2Int block = world.WorldToBlockPos(worldPos);
		Vector2Int chunk = world.BlockToChunkPos(block);
		return ContainsChunk(chunk.x, chunk.y);
	}

	internal string FormatChunks()
	{
		return $"({ _chunks[0].x},{_chunks[0].y}) ({_chunks[1].x},{_chunks[1].y}) " +
		       $"({_chunks[2].x},{_chunks[2].y}) ({_chunks[3].x},{_chunks[3].y})";
	}

	internal static bool TryCreate(WorldGeneration world, Vector3 cameraWorld, out ChunkSimWindow window)
	{
		return TryCreateFromWorldPos(world, cameraWorld, out window);
	}

	internal static bool TryCreateFromWorldPos(WorldGeneration world, Vector3 worldPos, out ChunkSimWindow window)
	{
		window = null;
		if (!world || !world.worldExists || world.generatingWorld)
			return false;

		Vector2Int block = world.WorldToBlockPos(worldPos);
		Vector2Int chunk = world.BlockToChunkPos(block);
		int lx = Mod(block.x, WorldGeneration.CHUNKSIZE);
		int ly = Mod(block.y, WorldGeneration.CHUNKSIZE);

		int half = world.HALFCHUNKSIZE;
		int ax = chunk.x - (lx < half ? 1 : 0);
		int ay = chunk.y - (ly < half ? 1 : 0);
		int maxAx = (int)world.chunkWidth - 2;
		int maxAy = (int)world.chunkHeight - 2;
		if (maxAx < 0 || maxAy < 0)
			return false;
		ax = Mathf.Clamp(ax, 0, maxAx);
		ay = Mathf.Clamp(ay, 0, maxAy);

		window = new ChunkSimWindow(new Vector2Int(ax, ay), block, chunk, lx, ly);
		return true;
	}

	private static int Mod(int v, int m)
	{
		int r = v % m;
		return r < 0 ? r + m : r;
	}
}
