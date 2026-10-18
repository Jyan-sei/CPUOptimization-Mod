using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// paced spawn/destroy of CaptureStore rows so one fat chunk cant Instantiate
/// hundreds of objects in a single frame.
/// play (weighted) charges the config budget: spawn 4, item advance 2, other advances 1,
/// park 3, destroy-object 2, writeback 4, collect 2, slice/release/register 1.
/// bootstrap leaves weighted off and still spends 1 per instantiate.
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
		internal bool Sliced;
		internal bool WritebackDone;
		internal bool Collected;
		internal bool ReleasePending;
		internal bool Writeback;
		internal List<GameObject> Found;
		internal List<GameObject> Targets;
		internal int Index;
	}

	private static readonly Dictionary<long, SpawnJob> Spawns = new Dictionary<long, SpawnJob>();
	private static readonly Dictionary<long, DestroyJob> Destroys = new Dictionary<long, DestroyJob>();
	private static readonly string[] ListName = { "trader", "enemy", "entity", "item", "light" };

	internal struct UnloadCancel
	{
		internal bool HadJob;
		internal bool Wrote;
		internal bool DestroyedAny;
	}

	internal static void Clear()
	{
		Spawns.Clear();
		Destroys.Clear();
	}

	internal static bool HasSpawn(int cx, int cy) => Spawns.ContainsKey(Key(cx, cy));

	/// <summary>destroy already called Object.Destroy. cancelling now would leave a hole at phase 3</summary>
	internal static bool DestroyStarted(int cx, int cy)
	{
		return Destroys.TryGetValue(Key(cx, cy), out DestroyJob job) && job.Index > 0;
	}

	/// <summary>
	/// chunk came back before teardown finished.
	/// nothing destroyed yet: drop the job so the next leave writebacks again.
	/// some already destroyed: keep the job, caller has to finish it and respawn.
	/// </summary>
	internal static UnloadCancel CancelUnload(int cx, int cy)
	{
		long key = Key(cx, cy);
		if (!Destroys.TryGetValue(key, out DestroyJob job))
			return default;

		UnloadCancel result = new UnloadCancel
		{
			HadJob = true,
			Wrote = job.Writeback && job.WritebackDone,
			DestroyedAny = job.Index > 0,
		};
		if (!result.DestroyedAny)
			Destroys.Remove(key);
		return result;
	}

	/// <summary>
	/// bootstrap only. a chunk left while SpawnStep was mid-list.
	/// drop the partial instantiates and the job. ghost rows stay, so the next visit rolls once.
	/// </summary>
	internal static void RollbackPartialSpawn(WorldGeneration world, int cx, int cy)
	{
		long key = Key(cx, cy);
		if (!Spawns.Remove(key) || world == null)
			return;

		var keys = new HashSet<long> { key };
		long t0 = StepTrace.Stamp();
		int n = 0;
		StreamState.SuppressBuildingDrops = true;
		try
		{
			List<GameObject> targets = ChunkObjects.CollectDestroyTargets(world, keys);
			n = targets.Count;
			for (int i = 0; i < targets.Count; i++)
			{
				NetHold.DestroyHeld(targets[i]);
			}
		}
		finally
		{
			StreamState.SuppressBuildingDrops = false;
			StepTrace.Mark("tear", "rollback-spawn", cx, cy, 0, -1, "n=" + n, t0, true);
		}
	}

	/// <summary>true when this chunk's objects are all spawned</summary>
	internal static bool SpawnStep(WorldGeneration world, int cx, int cy, ref int budget, bool singleStep = false, bool weighted = false)
	{
		long key = Key(cx, cy);
		if (!Spawns.TryGetValue(key, out SpawnJob job))
		{
			job = new SpawnJob();
			Spawns[key] = job;
		}

		bool acted = false;
		while (budget > 0 && job.List < 5)
		{
			int list = job.List;
			if (weighted && budget < SpawnNeed(list, cx, cy, job.Index))
				break;

			acted = true;
			StepTrace.ClearName();
			long t0 = StepTrace.Stamp();
			bool spawned = false;
			switch (list)
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

			string kind = list >= 0 && list < ListName.Length ? ListName[list] : "list" + list;
			int charge;
			if (!weighted)
			{
				if (spawned)
					budget--;
				charge = singleStep || spawned ? 1 : 0;
			}
			else if (spawned)
			{
				charge = 4;
				if (budget < 4)
					budget = 0;
				else
					budget -= 4;
			}
			else
			{
				charge = list == 3 ? 2 : 1;
				if (budget < charge)
					budget = 0;
				else
					budget -= charge;
			}
			StepTrace.Mark("create", spawned ? "spawn-" + kind : "advance-" + kind, cx, cy, charge, budget, StepTrace.TakeName(), t0, spawned || singleStep || weighted);

			if (!spawned)
			{
				job.List++;
				job.Index = 0;
			}
			if (singleStep)
				break;
			if (!weighted && !spawned)
				continue;
		}

		if (job.List >= 5)
		{
			if (singleStep && acted)
				return false;
			if (weighted && budget < 1)
				return false;
			long tReg = StepTrace.Stamp();
			ChunkObjects.RegisterChunkRenderers(world, cx, cy);
			int regCharge = weighted || singleStep ? 1 : 0;
			if (weighted)
				budget--;
			StepTrace.Mark("create", "register-renderers", cx, cy, regCharge, budget, null, tReg, true);
			Spawns.Remove(key);
			return true;
		}

		return false;
	}

	/// <summary>
	/// points required before the next spawn-list call.
	/// an item call that might instantiate needs 4. a finished item list advances for 2.
	/// trader, enemy, entity, and light advances need 1. a real spawn still charges 4.
	/// </summary>
	private static int SpawnNeed(int list, int cx, int cy, int index)
	{
		if (list == 3)
			return ChunkObjects.ItemStepWeight(cx, cy, index);
		return 1;
	}

	/// <summary>spawn list:index, destroy index/count, or job none. load-screen log and overlay.</summary>
	internal static string ProgressText(int cx, int cy)
	{
		long key = Key(cx, cy);
		if (Spawns.TryGetValue(key, out SpawnJob spawn))
			return "spawn " + spawn.List + ":" + spawn.Index;
		if (Destroys.TryGetValue(key, out DestroyJob destroy))
		{
			int n = destroy.Targets != null ? destroy.Targets.Count : 0;
			return "destroy " + destroy.Index + "/" + n;
		}
		return "job none";
	}

	/// <summary>true until this chunk has paid for its writeback and target list</summary>
	internal static bool HasPendingSetup(int cx, int cy)
	{
		if (!Destroys.TryGetValue(Key(cx, cy), out DestroyJob job))
			return true;
		return !job.WritebackDone;
	}

	/// <summary>true when this chunk's streamed objects are all destroyed. setupCost is the writeback weight; 0 skips the charge. weighted uses the play point table.</summary>
	internal static bool DestroyStep(WorldGeneration world, int cx, int cy, bool writeback, ref int budget, int setupCost, bool singleStep = false, bool weighted = false)
	{
		long key = Key(cx, cy);
		// a half-finished SpawnStep must not resume after this chunk is torn down
		Spawns.Remove(key);
		if (!Destroys.TryGetValue(key, out DestroyJob job))
		{
			job = new DestroyJob { Writeback = writeback };
			Destroys[key] = job;
		}

		if (job.ReleasePending)
		{
			if (weighted && budget < 1)
				return false;
			long tRel = StepTrace.Stamp();
			ChunkObjects.ReleaseLiveItems(cx, cy);
			if (weighted)
				budget--;
			StepTrace.Mark("tear", "release-live", cx, cy, 1, budget, null, tRel, true);
			Destroys.Remove(key);
			return true;
		}

		var keys = new HashSet<long> { key };
		if (!job.Sliced)
		{
			if (weighted && budget < 1)
				return false;
			long tSlice = StepTrace.Stamp();
			// play unload replaces the slice with whatever is still alive.
			// bootstrap cull must not. the ghost rows are the only copy of plants,
			// enemies, and chests, and writeback is off so nothing would put them back.
			if (job.Writeback)
				ChunkObjects.RemoveChunkSlicePublic(cx, cy);
			int sliceCharge = weighted || singleStep ? 1 : 0;
			if (weighted)
				budget--;
			StepTrace.Mark("tear", "remove-slice", cx, cy, sliceCharge, budget, null, tSlice, true);
			job.Found = new List<GameObject>(ChunkRoster.Gather(world, keys));
			job.Sliced = true;
			if (singleStep || (weighted && budget <= 0))
				return false;
		}

		if (!job.WritebackDone)
		{
			int writeCost = weighted ? 4 : setupCost;
			if (writeCost > 0 && budget < writeCost)
				return false;
			if (job.Writeback)
			{
				ItemPath.Source = "destroy-step";
				long tWb = StepTrace.Stamp();
				try
				{
					ChunkObjects.WritebackFound(world, keys, job.Found);
				}
				finally
				{
					ItemPath.Source = "";
					StepTrace.Mark("tear", "writeback", cx, cy, writeCost, budget, "setupCharge=" + writeCost, tWb, true);
				}
			}
			job.WritebackDone = true;
			if (writeCost > 0)
				budget -= writeCost;
			if (singleStep || (weighted && budget <= 0))
				return false;
		}

		if (!job.Collected)
		{
			if (weighted && budget < 2)
				return false;
			StreamState.SuppressBuildingDrops = true;
			long tCol = StepTrace.Stamp();
			try
			{
				job.Targets = ChunkObjects.TargetsFrom(world, keys, job.Found);
			}
			finally
			{
				StreamState.SuppressBuildingDrops = false;
			}
			job.Collected = true;
			job.Index = 0;
			int itemTargets = 0;
			int targetCount = job.Targets != null ? job.Targets.Count : 0;
			if (job.Targets != null)
			{
				for (int i = 0; i < job.Targets.Count; i++)
				{
					if (job.Targets[i] != null && job.Targets[i].GetComponent<Item>() != null)
						itemTargets++;
				}
			}
			int collectCharge = weighted ? 2 : (singleStep ? 1 : (job.Writeback ? 0 : setupCost));
			if (weighted)
				budget -= 2;
			StepTrace.Mark("tear", "collect-targets", cx, cy, collectCharge, budget,
				"n=" + targetCount + " items=" + itemTargets + " setupCharge=" + setupCost, tCol, true);
			ItemPath.Sum("tear", cx, cy, "targets n=" + targetCount + " items=" + itemTargets + " writeback=" + (job.Writeback ? "1" : "0"));
			if (singleStep || (weighted && budget <= 0))
				return false;
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
				GameObject go = job.Targets[job.Index];
				if (go == null)
				{
					job.Index++;
					continue;
				}
				Item torn = go.GetComponent<Item>();
				int need = !weighted ? 1 : torn != null ? 3 : 2;
				if (budget < need)
					break;
				job.Index++;
				long tObj = StepTrace.Stamp();
				if (torn != null)
				{
					string id = string.IsNullOrEmpty(torn.id) ? go.name : torn.id;
					bool removed = NetHold.DestroyHeld(go);
					int cost = !weighted ? 1 : removed ? 2 : 3;
					budget -= cost;
					StepTrace.Mark("tear", removed ? "destroy-item" : "park-item", cx, cy, cost, budget, id, tObj, true);
					ItemPath.Note("tear", cx, cy, id, go.transform.position,
						(removed ? "destroy-held" : "stay-park")
						+ " iid=" + go.GetInstanceID());
					if (singleStep)
						break;
					continue;
				}
				string objName = go.name;
				if (!ChunkRoster.InActiveScene(go))
				{
					StepTrace.Mark("tear", "keep-template", cx, cy, 0, budget, objName, tObj, false);
					if (singleStep)
						break;
					continue;
				}
				// same rule as items. a Together replica stays. a local plant does not.
				if (!NetHold.DestroyHeld(go))
				{
					StepTrace.Mark("tear", "keep-replica", cx, cy, 0, budget, objName, tObj, false);
					if (singleStep)
						break;
					continue;
				}
				int objCost = weighted ? 2 : 1;
				budget -= objCost;
				StepTrace.Mark("tear", "destroy-object", cx, cy, objCost, budget, objName, tObj, true);
				if (singleStep)
					break;
			}
		}
		finally
		{
			StreamState.SuppressBuildingDrops = false;
		}

		if (job.Index >= job.Targets.Count)
		{
			if (singleStep || (weighted && budget < 1))
			{
				job.ReleasePending = true;
				return false;
			}
			// objects are gone. a row still marked live would never come back.
			long tRel = StepTrace.Stamp();
			ChunkObjects.ReleaseLiveItems(cx, cy);
			int relCharge = weighted ? 1 : 0;
			if (weighted)
				budget--;
			StepTrace.Mark("tear", "release-live", cx, cy, relCharge, budget, null, tRel, true);
			Destroys.Remove(key);
			return true;
		}

		return false;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
