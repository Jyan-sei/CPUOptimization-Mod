using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// union of 2x2 chunk windows - one per player position
internal sealed class ChunkSimUnionWindow
{
	private readonly HashSet<long> _chunks = new HashSet<long>();
	private readonly List<Vector2Int> _chunkList = new List<Vector2Int>(32);

	internal int PlayerCount { get; private set; }
	internal int ChunkCount => _chunkList.Count;

	internal Vector2Int GetChunkAt(int index) => _chunkList[index];

	internal IEnumerable<Vector2Int> EnumerateChunks()
	{
		for (int i = 0; i < _chunkList.Count; i++)
			yield return _chunkList[i];
	}

	internal bool TryBuild(WorldGeneration world, IReadOnlyList<Vector3> playerPositions)
	{
		_chunks.Clear();
		_chunkList.Clear();
		PlayerCount = 0;

		if (!world || !world.worldExists || world.generatingWorld)
			return false;
		if (playerPositions == null || playerPositions.Count == 0)
			return false;

		PlayerCount = playerPositions.Count;
		for (int i = 0; i < playerPositions.Count; i++)
		{
			if (!ChunkSimWindow.TryCreateFromWorldPos(world, playerPositions[i], out ChunkSimWindow window))
				continue;

			AddWindow(window);
		}

		return _chunkList.Count > 0;
	}

	private void AddWindow(ChunkSimWindow window)
	{
		if (window == null)
			return;

		TryAddChunk(window.Anchor.x, window.Anchor.y);
		TryAddChunk(window.Anchor.x + 1, window.Anchor.y);
		TryAddChunk(window.Anchor.x, window.Anchor.y + 1);
		TryAddChunk(window.Anchor.x + 1, window.Anchor.y + 1);
	}

	private void TryAddChunk(int cx, int cy)
	{
		long key = PackChunk(cx, cy);
		if (!_chunks.Add(key))
			return;
		_chunkList.Add(new Vector2Int(cx, cy));
	}

	internal bool ContainsChunk(int cx, int cy) => _chunks.Contains(PackChunk(cx, cy));

	internal bool ContainsWorldPos(WorldGeneration world, Vector3 worldPos)
	{
		if (!world || !world.worldExists)
			return false;
		Vector2Int block = world.WorldToBlockPos(worldPos);
		Vector2Int chunk = world.BlockToChunkPos(block);
		return ContainsChunk(chunk.x, chunk.y);
	}

	internal bool Matches(ChunkSimUnionWindow other)
	{
		if (other == null)
			return false;
		if (_chunks.Count != other._chunks.Count)
			return false;
		foreach (long key in _chunks)
		{
			if (!other._chunks.Contains(key))
				return false;
		}
		return true;
	}

	internal string FormatChunks()
	{
		if (_chunkList.Count == 0)
			return "-";
		var sb = new StringBuilder(_chunkList.Count * 12);
		for (int i = 0; i < _chunkList.Count; i++)
		{
			if (i > 0)
				sb.Append(' ');
			Vector2Int c = _chunkList[i];
			sb.Append('(').Append(c.x).Append(',').Append(c.y).Append(')');
		}
		return sb.ToString();
	}

	private static long PackChunk(int cx, int cy) => ((long)cx << 32) | (uint)cy;
}
