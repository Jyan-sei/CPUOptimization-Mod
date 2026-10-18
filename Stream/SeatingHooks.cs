using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace CPUOptimization2;

/// <summary>
/// barbed wire dies and stalactites drop by listening on ChunkUpdated for the block they sit on.
/// rebuilding a chunk tilemap swaps in a fresh event, so the old listeners are gone. put them back.
/// </summary>
internal static class SeatingHooks
{
	private static readonly List<GameObject> _roster = new List<GameObject>(64);
	internal static void Bind(BuildingEntity build)
	{
		if (build == null)
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || world.ChunkUpdated == null)
			return;

		bool ground = build.requireGround;
		StalactiteDropper dropper = build.GetComponent<StalactiteDropper>();
		if (!ground && dropper == null)
			return;

		if (!TryGetEvent(world, build.blockPlacedOn, out UnityEvent evt))
			return;

		if (ground)
		{
			evt.RemoveListener(build.CheckSeating);
			evt.AddListener(build.CheckSeating);
		}
		if (dropper != null)
		{
			evt.RemoveListener(dropper.CheckSeating);
			evt.AddListener(dropper.CheckSeating);
		}
	}

	/// <summary>after the chunk event is replaced, hook up traps that are still alive and seated on it</summary>
	internal static void RebindChunk(WorldGeneration world, int cx, int cy)
	{
		if (world == null)
			return;
		ChunkRoster.CopyAll(_roster);
		for (int i = 0; i < _roster.Count; i++)
		{
			BuildingEntity build = _roster[i] != null ? _roster[i].GetComponent<BuildingEntity>() : null;
			if (build == null)
				continue;
			Vector2Int bp = build.blockPlacedOn;
			int hx = bp.x / WorldGeneration.CHUNKSIZE;
			int hy = bp.y / WorldGeneration.CHUNKSIZE;
			if (hx != cx || hy != cy)
				continue;
			Bind(build);
		}
	}

	private static bool TryGetEvent(WorldGeneration world, Vector2Int block, out UnityEvent evt)
	{
		evt = null;
		int cx = block.x / WorldGeneration.CHUNKSIZE;
		int cy = block.y / WorldGeneration.CHUNKSIZE;
		int w = world.ChunkUpdated.GetLength(0);
		int h = world.ChunkUpdated.GetLength(1);
		if (cx < 0 || cy < 0 || cx >= w || cy >= h)
			return false;
		evt = world.ChunkUpdated[cx, cy];
		if (evt == null)
		{
			evt = new UnityEvent();
			world.ChunkUpdated[cx, cy] = evt;
		}
		return true;
	}
}
