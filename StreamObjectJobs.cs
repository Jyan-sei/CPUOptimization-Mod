using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// paced spawn/destroy of CaptureStore rows so one fat chunk cant Instantiate
/// hundreds of objects in a single frame. budget is StreamWindowPolicy.ObjectOpsPerFrame.
/// </summary>
internal static class StreamObjectJobs
{
	private sealed class SpawnJob
	{
		internal int List; // 0 traders .. 4 lights, 5 register/done
		internal int Index;
	}

	private sealed class DestroyJob
	{
		internal bool WritebackDone;
		internal bool Writeback;
		internal List<GameObject> Targets;
		internal int Index;
	}

	private static readonly Dictionary<long, SpawnJob> Spawns = new Dictionary<long, SpawnJob>();
	private static readonly Dictionary<long, DestroyJob> Destroys = new Dictionary<long, DestroyJob>();

	internal static void Clear()
	{
		Spawns.Clear();
		Destroys.Clear();
	}

	/// <summary>true when this chunk's objects are all spawned</summary>
	internal static bool SpawnStep(WorldGeneration world, int cx, int cy, ref int budget)
	{
		long key = Key(cx, cy);
		if (!Spawns.TryGetValue(key, out SpawnJob job))
		{
			job = new SpawnJob();
			Spawns[key] = job;
		}

		while (budget > 0 && job.List < 5)
		{
			bool spawned = false;
			switch (job.List)
			{
				case 0:
					spawned = ChunkObjects.SpawnNextTrader(cx, cy, ref job.Index);
					break;
				case 1:
					spawned = ChunkObjects.SpawnNextEnemy(cx, cy, ref job.Index);
					break;
				case 2:
					spawned = ChunkObjects.SpawnNextEntity(cx, cy, ref job.Index);
					break;
				case 3:
					spawned = ChunkObjects.SpawnNextWorldItem(cx, cy, ref job.Index);
					break;
				case 4:
					spawned = ChunkObjects.SpawnNextLight(cx, cy, ref job.Index);
					break;
			}

			if (spawned)
			{
				budget--;
				continue;
			}

			// end of this list (or a skip), advance
			job.List++;
			job.Index = 0;
		}

		if (job.List >= 5)
		{
			ChunkObjects.RegisterChunkRenderers(world, cx, cy);
			Spawns.Remove(key);
			return true;
		}

		return false;
	}

	/// <summary>true when this chunk's streamed objects are all destroyed</summary>
	internal static bool DestroyStep(WorldGeneration world, int cx, int cy, bool writeback, ref int budget)
	{
		long key = Key(cx, cy);
		if (!Destroys.TryGetValue(key, out DestroyJob job))
		{
			job = new DestroyJob { Writeback = writeback };
			Destroys[key] = job;
		}

		if (!job.WritebackDone)
		{
			var keys = new HashSet<long> { key };
			if (job.Writeback)
			{
				ChunkObjects.RemoveChunkSlicePublic(cx, cy);
				ChunkObjects.WritebackMatchingPublic(world, keys);
			}
			StreamState.SuppressBuildingDrops = true;
			try
			{
				job.Targets = ChunkObjects.CollectDestroyTargets(world, keys);
			}
			finally
			{
				StreamState.SuppressBuildingDrops = false;
			}
			job.WritebackDone = true;
			job.Index = 0;
		}

		if (job.Targets == null)
		{
			Destroys.Remove(key);
			return true;
		}

		StreamState.SuppressBuildingDrops = true;
		try
		{
			while (budget > 0 && job.Index < job.Targets.Count)
			{
				GameObject go = job.Targets[job.Index++];
				if (go != null)
				{
					AltarBlessing.Suppress(go);
					Object.Destroy(go);
					budget--;
				}
			}
		}
		finally
		{
			StreamState.SuppressBuildingDrops = false;
		}

		if (job.Index >= job.Targets.Count)
		{
			Destroys.Remove(key);
			return true;
		}

		return false;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
