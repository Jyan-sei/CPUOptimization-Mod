using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class ChunkObjects
{
	internal static void WritebackAndDestroyChunks(WorldGeneration world, List<Vector2Int> chunks)
	{
		if (chunks == null || chunks.Count == 0)
			return;

		HashSet<long> keys = new HashSet<long>();
		for (int i = 0; i < chunks.Count; i++)
		{
			keys.Add(Key(chunks[i].x, chunks[i].y));
			RemoveChunkSlice(chunks[i].x, chunks[i].y);
		}

		ItemPath.Source = "batch";
		try
		{
			List<GameObject> found = ChunkRoster.Gather(world, keys);
			WritebackMatching(world, keys, found);
			DestroyMatching(world, keys, found);
		}
		finally
		{
			ItemPath.Source = "";
		}
	}

	/// <summary>
	/// destroy live objects in these chunks. leave CaptureStore alone.
	/// bootstrap cull, after a full capture already filled the arrays
	/// </summary>
	internal static void DestroyChunksNoWriteback(WorldGeneration world, List<Vector2Int> chunks)
	{
		if (chunks == null || chunks.Count == 0)
			return;

		HashSet<long> keys = new HashSet<long>();
		for (int i = 0; i < chunks.Count; i++)
			keys.Add(Key(chunks[i].x, chunks[i].y));
		DestroyMatching(world, keys, null);
	}

	/// <summary>
	/// append live objects into CaptureStore then destroy. doesnt wipe ghost records that are already there
	/// </summary>
	internal static void AppendWritebackAndDestroy(WorldGeneration world, HashSet<long> chunkKeys)
	{
		if (chunkKeys == null || chunkKeys.Count == 0)
			return;
		ItemPath.Source = "append";
		try
		{
			List<GameObject> found = ChunkRoster.Gather(world, chunkKeys);
			WritebackMatching(world, chunkKeys, found);
			DestroyMatching(world, chunkKeys, found);
		}
		finally
		{
			ItemPath.Source = "";
		}
	}

	/// <summary>full sync spawn (tests / fallback). stream path uses SpawnNext* plus StreamObjectJobs</summary>
	internal static void Spawn(WorldGeneration world, int cx, int cy)
	{
		int budget = int.MaxValue;
		while (!StreamObjectJobs.SpawnStep(world, cx, cy, ref budget))
		{
			// drain
		}
	}

	internal static void RegisterChunkRenderers(WorldGeneration world, int cx, int cy)
	{
		if (world != null && world.renderChunks != null)
		{
			TilemapRenderer tr = world.renderChunks[cx, cy];
			if (tr != null)
				ScreenCull.RegisterRenderer(tr);
		}
	}

	internal static void WritebackMatchingPublic(WorldGeneration world, HashSet<long> chunkKeys) =>
		WritebackMatching(world, chunkKeys, null);

	internal static void WritebackFound(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found) =>
		WritebackMatching(world, chunkKeys, found);

	internal static List<GameObject> TargetsFrom(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found) =>
		CollectDestroyTargets(world, chunkKeys, found);

	internal static void RemoveChunkSlicePublic(int cx, int cy) => RemoveChunkSlice(cx, cy);

	/// <summary>
	/// world-gen capture only. play teardown uses ChunkRoster.
	/// includes inactive items. FindObjectsOfType would skip a disabled gravbag.
	/// </summary>
	internal static void CollectSceneItems(List<Item> into)
	{
		if (into == null)
			return;
		into.Clear();
		Item[] found = Resources.FindObjectsOfTypeAll<Item>();
		for (int i = 0; i < found.Length; i++)
		{
			Item item = found[i];
			if (item == null)
				continue;
			GameObject go = item.gameObject;
			if (!ChunkRoster.InActiveScene(go))
				continue;
			into.Add(item);
		}
	}

	/// <summary>
	/// the chunk's objects are gone. rows marked live at capture would never instantiate.
	/// </summary>
	internal static void ReleaseLiveItems(int cx, int cy)
	{
		int cleared = 0;
		int held = 0;
		for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
		{
			WorldItemRecord rec = CaptureStore.WorldItems[i];
			if (rec == null || rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (rec.HeldInPlace)
			{
				held++;
				continue;
			}
			if (!rec.LiveAtCapture)
				continue;
			rec.LiveAtCapture = false;
			cleared++;
		}
		if (cleared > 0 || held > 0)
			ItemPath.Sum("load", cx, cy, "clear-live n=" + cleared + " held=" + held);
	}

	/// <summary>
	/// phase 2 is about to drop colliders. a loose item still in the scene has to be a
	/// WorldItemRecord before the ground goes, or it falls and the slice never sees it.
	/// an empty scene leaves ghost rows alone.
	/// </summary>
	internal static void StoreLooseItemsIfPresent(WorldGeneration world, int cx, int cy)
	{
		if (world == null)
			return;
		long key = Key(cx, cy);
		var keys = new HashSet<long> { key };
		List<GameObject> found = ChunkRoster.Gather(world, keys);
		bool any = false;
		for (int i = 0; i < found.Count; i++)
		{
			Item item = found[i] != null ? found[i].GetComponent<Item>() : null;
			if (item == null || !InKeys(world, item.transform.position, keys, out _, out _))
				continue;
			if (item.transform.parent != null && ChunkObjects.UnderItem(item.transform))
				continue;
			if (PlayerCarry.Holds(item.transform))
				continue;
			any = true;
			break;
		}
		if (!any)
			return;

		ItemPath.Source = "loose";
		try
		{
			WritebackItems(world, keys, new HashSet<int>(), PlayerRoot(), found);
		}
		finally
		{
			ItemPath.Source = "";
		}

		int killed = 0;
		for (int i = 0; i < found.Count; i++)
		{
			Item item = found[i] != null ? found[i].GetComponent<Item>() : null;
			if (item == null || !InKeys(world, item.transform.position, keys, out _, out _))
				continue;
			if (PlayerCarry.Holds(item.transform))
				continue;
			if (ElderResident.IsResident(world, item.transform))
				continue;
			// a lifepod (and its PDA) stays for the layer. destroying it here stacks a respawn.
			if (StaysWithBuilding(item.transform))
			{
				ItemPath.Note("tear", cx, cy, string.IsNullOrEmpty(item.id) ? item.gameObject.name : item.id, item.transform.position,
					"skip-stay iid=" + item.GetInstanceID());
				continue;
			}
			if (NetHold.DestroyHeld(item.gameObject))
				killed++;
		}
		if (killed > 0)
			Plugin.Log.LogInfo("CPUOpt2 item cull " + cx + "," + cy + " n=" + killed);
	}

	/// <summary>
	/// writeback already copied the live objects, then the player stepped back before any Destroy.
	/// those rows must not spawn a second copy while the originals are still in the scene.
	/// </summary>
	internal static void MarkChunkLive(int cx, int cy)
	{
		for (int i = 0; i < CaptureStore.Traders.Count; i++)
			if (CaptureStore.Traders[i].Chunk.x == cx && CaptureStore.Traders[i].Chunk.y == cy)
				CaptureStore.Traders[i].LiveAtCapture = true;
		for (int i = 0; i < CaptureStore.Enemies.Count; i++)
			if (CaptureStore.Enemies[i].Chunk.x == cx && CaptureStore.Enemies[i].Chunk.y == cy)
				CaptureStore.Enemies[i].LiveAtCapture = true;
		for (int i = 0; i < CaptureStore.Entities.Count; i++)
			if (CaptureStore.Entities[i].Chunk.x == cx && CaptureStore.Entities[i].Chunk.y == cy)
				CaptureStore.Entities[i].LiveAtCapture = true;
		int marked = 0;
		for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
			if (CaptureStore.WorldItems[i].Chunk.x == cx && CaptureStore.WorldItems[i].Chunk.y == cy)
			{
				CaptureStore.WorldItems[i].LiveAtCapture = true;
				marked++;
			}
		if (marked > 0)
			ItemPath.Sum("load", cx, cy, "mark-live n=" + marked);
		for (int i = 0; i < CaptureStore.Lights.Count; i++)
			if (CaptureStore.Lights[i].Chunk.x == cx && CaptureStore.Lights[i].Chunk.y == cy)
				CaptureStore.Lights[i].LiveAtCapture = true;
	}

	internal static bool UnderItem(Transform t)
	{
		for (Transform p = t != null ? t.parent : null; p != null; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return true;
		}
		return false;
	}

	/// <summary>
	/// lifepod and structure tilemaps are not culled. their pocket items have to stay
	/// parented, or the chunk respawn stacks a second PDA on the one still in the pod.
	/// </summary>
	private static bool StaysWithBuilding(Transform t)
	{
		for (Transform p = t != null ? t.parent : null; p != null; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return false;
			BuildingEntity build = p.GetComponent<BuildingEntity>();
			if (build == null)
				continue;
			if (LifepodGroups.HostsPocket(build))
				return true;
			if (StructureTilemapVault.IsStructureTilemap(p, WorldGeneration.world))
				return true;
		}
		return false;
	}

	/// <summary>gather GOs DestroyMatching would kill. paced StreamObjectJobs uses this</summary>
	internal static List<GameObject> CollectDestroyTargets(WorldGeneration world, HashSet<long> chunkKeys)
	{
		return CollectDestroyTargets(world, chunkKeys, null);
	}

	private static List<GameObject> CollectDestroyTargets(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found)
	{
		if (found == null)
			found = ChunkRoster.Gather(world, chunkKeys);
		var kill = new List<GameObject>();
		var seen = new HashSet<int>();
		var lifepodRoots = new HashSet<int>();

		for (int n = 0; n < found.Count; n++)
		{
			GameObject go = found[n];
			if (go == null)
				continue;
			BuildingEntity build = go.GetComponent<BuildingEntity>();
			if (build == null)
				continue;
			if (!InKeys(world, build.transform.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(build.transform))
				continue;
			if (ElderResident.IsResident(world, build.transform))
				continue;
			if (StructureTilemapVault.IsStructureTilemap(build.transform, world))
				continue;
			if (LifepodGroups.ConsumeDestroy(build, world, chunkKeys, lifepodRoots, kill))
				continue;
			Take(kill, seen, build.gameObject);
		}

		for (int n = 0; n < found.Count; n++)
		{
			GameObject go = found[n];
			if (go == null)
				continue;
			Item item = go.GetComponent<Item>();
			if (item == null)
				continue;
			if (!InKeys(world, item.transform.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(item.transform))
				continue;
			if (ElderResident.IsResident(world, item.transform))
				continue;
			if (StaysWithBuilding(item.transform))
				continue;
			Take(kill, seen, item.gameObject);
		}

		for (int n = 0; n < found.Count; n++)
		{
			GameObject go = found[n];
			if (go == null)
				continue;
			Climbable climb = go.GetComponent<Climbable>();
			if (climb == null)
				continue;
			if (!InKeys(world, climb.transform.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(climb.transform))
				continue;
			if (ElderResident.IsResident(world, climb.transform))
				continue;
			Take(kill, seen, climb.gameObject);
		}

		for (int n = 0; n < found.Count; n++)
		{
			GameObject go = found[n];
			if (go == null)
				continue;
			TraderScript trader = go.GetComponent<TraderScript>();
			if (trader == null)
				continue;
			if (!InKeys(world, trader.transform.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(trader.transform))
				continue;
			Take(kill, seen, trader.gameObject);
		}

		WorldScanner.AddBackgroundShellTargets(world, chunkKeys, kill, seen);
		CollectNamed(world, chunkKeys, "sandvinehook", kill, found, seen);
		CollectNamed(world, chunkKeys, "droppings", kill, found, seen);
		CollectOilPipes(world, chunkKeys, kill, found, seen);
		CollectLights(world, chunkKeys, kill, found, seen);
		return kill;
	}

	private static void Take(List<GameObject> kill, HashSet<int> seen, GameObject go)
	{
		if (go == null || !seen.Add(go.GetInstanceID()))
			return;
		kill.Add(go);
	}

	private static void WritebackMatching(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found)
	{
		if (found == null)
			found = ChunkRoster.Gather(world, chunkKeys);
		Transform playerRoot = PlayerRoot();
		HashSet<int> claimedItems = new HashSet<int>();
		HashSet<int> claimedBuildings = new HashSet<int>();

		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			TraderScript trader = found[i].GetComponent<TraderScript>();
			if (trader == null || !InKeys(world, trader.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			BuildingEntity build = FieldAccess.Get<BuildingEntity>(trader, "build")
				?? trader.GetComponent<BuildingEntity>();
			if (build != null)
				claimedBuildings.Add(build.GetInstanceID());
			CaptureStore.Traders.Add(FillTrader(cx, cy, trader, build));
		}

		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			SpiderHandler spider = found[i].GetComponent<SpiderHandler>();
			if (spider == null || !InKeys(world, spider.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			BuildingEntity build = FieldAccess.Get<BuildingEntity>(spider, "bld")
				?? spider.GetComponent<BuildingEntity>();
			if (ElderResident.IsResident(world, spider.transform))
			{
				if (build != null)
					claimedBuildings.Add(build.GetInstanceID());
				continue;
			}
			if (build != null)
				claimedBuildings.Add(build.GetInstanceID());
			EnemyRecord spiderRec = new EnemyRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = IdOf(spider.gameObject, build),
				Health = build != null ? build.health : 0f,
				RequireGround = build == null || build.requireGround,
				Position = spider.transform.position,
				RotationZ = spider.transform.eulerAngles.z,
				ScaleNegX = spider.transform.localScale.x < 0f ? true : (bool?)null,
			};
			StructureLiveState.CaptureEnemy(spider.gameObject, spiderRec);
			CaptureStore.Enemies.Add(spiderRec);
		}

		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			BuildingEntity build = found[i].GetComponent<BuildingEntity>();
			if (build == null || !InKeys(world, build.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			if (claimedBuildings.Contains(build.GetInstanceID()))
				continue;
			if (ElderResident.IsResident(world, build.transform))
			{
				claimedBuildings.Add(build.GetInstanceID());
				continue;
			}
			if (build.animal)
			{
				claimedBuildings.Add(build.GetInstanceID());
				EnemyRecord animalRec = new EnemyRecord
				{
					Chunk = new Vector2Int(cx, cy),
					Id = IdOf(build.gameObject, build),
					Health = build.health,
					RequireGround = build.requireGround,
					Position = build.transform.position,
					RotationZ = build.transform.eulerAngles.z,
					ScaleNegX = build.transform.localScale.x < 0f ? true : (bool?)null,
				};
				StructureLiveState.CaptureEnemy(build.gameObject, animalRec);
				CaptureStore.Enemies.Add(animalRec);
				continue;
			}

			if (LifepodGroups.TryGroup(build, claimedBuildings, out Transform groupRoot))
			{
				// parent chunk is leaving: one row. a later chunk of the same pod only refreshes the mode.
				if (InKeys(world, groupRoot.position, chunkKeys, out _, out _))
				{
					EntityRecord grouped = LifepodGroups.BuildRecord(world, groupRoot);
					LifepodGroups.Stamp(groupRoot, grouped, claimedItems, playerRoot);
					grouped.LiveAtCapture = true;
					CaptureStore.Entities.Add(grouped);
				}
				else if (!LifepodGroups.AnyPieceLoaded(groupRoot, world, chunkKeys))
					LifepodGroups.RefreshStored(groupRoot);
				continue;
			}

			claimedBuildings.Add(build.GetInstanceID());
			EntityRecord er = BaseEntity(cx, cy, build.gameObject, build);
			CaptureVineHook(build.gameObject, er);
			ResolveStructurePrefab(build.gameObject, er);
			AttachStructureTilemapVault(world, build.gameObject, er);
			// still in the scene. the row must not spawn a second copy on the way back.
			if (StructureTilemapVault.IsStructureTilemap(build.transform, world))
				er.LiveAtCapture = true;
			AttachExtras(build, er, claimedItems, playerRoot);
			er.ModState = ModBehaviourState.Capture(build.gameObject);
			StructureLiveState.CaptureEntity(build.gameObject, build, er);
			CaptureStore.Entities.Add(er);
		}

		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			Climbable climb = found[i].GetComponent<Climbable>();
			if (climb == null || !InKeys(world, climb.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			if (climb.GetComponentInParent<BuildingEntity>() != null)
				continue;
			EntityRecord er = new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = Clean(climb.gameObject.name),
				Position = climb.transform.position,
				RotationZ = climb.transform.eulerAngles.z,
			};
			FillRope(climb, er);
			StructureLiveState.CaptureEntity(climb.gameObject, null, er);
			CaptureStore.Entities.Add(er);
		}

		WorldScanner.WritebackBackgroundShells(world, chunkKeys);
		WritebackStructureTilemaps(world, chunkKeys, claimedBuildings, claimedItems, playerRoot);

		WritebackItems(world, chunkKeys, claimedItems, playerRoot, found);

		// wallholes are WallholeResident. never writeback, destroy, or respawn them
		WritebackNamed(world, chunkKeys, "sandvinehook", found);
		WritebackOilPipes(world, chunkKeys, found);
		WritebackLights(world, chunkKeys, claimedBuildings, found);
	}

	// a scene item this close to a stored row with the same id is that row.
	private const float SameRowReachSqr = 16f;
	// a carried item can be anywhere in the chunk. do not spawn a second copy of it.
	private const float HeldRowReachSqr = 96f * 96f;

	private sealed class ItemChunkMerge
	{
		internal int Scene, Claimed, Child, Carry, Lift;
		internal readonly List<WorldItemRecord> Old = new List<WorldItemRecord>();
		internal readonly List<WorldItemRecord> Fresh = new List<WorldItemRecord>();
		internal readonly List<bool> Used = new List<bool>();
		internal WorldItemRecord ClaimedRow;

		internal bool Claim(string id, Vector3 pos, float reachSqr)
		{
			int best = -1;
			float bestD = reachSqr;
			for (int i = 0; i < Old.Count; i++)
			{
				if (Used[i])
					continue;
				WorldItemRecord rec = Old[i];
				if (rec == null || !string.Equals(rec.Id ?? "", id ?? "", System.StringComparison.Ordinal))
					continue;
				float d = (rec.Position - pos).sqrMagnitude;
				if (d > bestD)
					continue;
				best = i;
				bestD = d;
			}
			if (best < 0)
			{
				ClaimedRow = null;
				return false;
			}
			Used[best] = true;
			ClaimedRow = Old[best];
			return true;
		}

		internal void Consume(WorldItemRecord rec)
		{
			if (rec == null)
				return;
			for (int i = 0; i < Old.Count; i++)
			{
				if (ReferenceEquals(Old[i], rec))
					Used[i] = true;
			}
		}

		internal void ConsumeNet(ushort netId)
		{
			for (int i = 0; i < Old.Count; i++)
			{
				WorldItemRecord rec = Old[i];
				if (rec == null || !rec.HasNetId || rec.NetId != netId)
					continue;
				Used[i] = true;
			}
		}

		internal float NearestUnused(string id, Vector3 pos)
		{
			float best = float.MaxValue;
			for (int i = 0; i < Old.Count; i++)
			{
				if (Used[i])
					continue;
				WorldItemRecord rec = Old[i];
				if (rec == null || !string.Equals(rec.Id ?? "", id ?? "", System.StringComparison.Ordinal))
					continue;
				float d = (rec.Position - pos).sqrMagnitude;
				if (d < best)
					best = d;
			}
			return best;
		}
	}

	/// <summary>
	/// loose rows, including a dropped container and the items parented under it.
	/// scene items refresh the row they match. rows the scan does not see stay,
	/// so a short scan cannot shrink the array the next visit instantiates.
	/// </summary>
	private static void WritebackItems(
		WorldGeneration world,
		HashSet<long> chunkKeys,
		HashSet<int> claimedItems,
		Transform playerRoot,
		List<GameObject> found)
	{
		if (chunkKeys == null)
			return;

		var buckets = new Dictionary<long, ItemChunkMerge>();
		for (int i = CaptureStore.WorldItems.Count - 1; i >= 0; i--)
		{
			WorldItemRecord rec = CaptureStore.WorldItems[i];
			if (rec == null)
			{
				CaptureStore.WorldItems.RemoveAt(i);
				continue;
			}
			long key = Key(rec.Chunk.x, rec.Chunk.y);
			if (!chunkKeys.Contains(key))
				continue;
			if (!buckets.TryGetValue(key, out ItemChunkMerge bucket))
			{
				bucket = new ItemChunkMerge();
				buckets[key] = bucket;
			}
			bucket.Old.Add(rec);
			bucket.Used.Add(false);
			CaptureStore.WorldItems.RemoveAt(i);
		}

		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			Item item = found[i].GetComponent<Item>();
			if (item == null)
				continue;
			if (!InKeys(world, item.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			long key = Key(cx, cy);
			if (!buckets.TryGetValue(key, out ItemChunkMerge row))
			{
				row = new ItemChunkMerge();
				buckets[key] = row;
			}
			string id = string.IsNullOrEmpty(item.id) ? Clean(item.gameObject.name) : item.id;
			if (ChunkObjects.UnderItem(item.transform))
			{
				row.Child++;
				row.Claim(id, item.transform.position, SameRowReachSqr);
				continue;
			}
			// parked or use-slot copies are still the bag's contents. not a loose drop.
			if (BagVirt.Active && BagVirt.IsParked(item))
			{
				row.Child++;
				row.Claim(id, item.transform.position, BagVirt.HoldReachSqr);
				continue;
			}
			row.Scene++;
			if (PlayerCarry.Holds(item.transform))
			{
				row.Carry++;
				row.Claim(id, item.transform.position, HeldRowReachSqr);
				continue;
			}
			if (LiftedFromSource(world, item.transform))
				row.Lift++;
			if (claimedItems.Contains(item.GetInstanceID()))
			{
				row.Claimed++;
				row.Claim(id, item.transform.position, SameRowReachSqr);
				continue;
			}
			WorldItemRecord wir = new WorldItemRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = id,
				Condition = item.condition,
				Battery = BatterySnap.Read(item),
				Position = item.transform.position,
				RotationZ = item.transform.eulerAngles.z,
				LocalScale = item.transform.localScale,
				FromWriteback = true,
			};
			CopyLiquids(item, wir);
			BuildingLoot.AppendChildren(item.transform, wir.Children, claimedItems, playerRoot);
			wir.ModState = ModBehaviourState.Capture(item.gameObject);
			StructureLiveState.CaptureItem(item, wir);
			StructureLiveState.NotePocketFromItem(item.transform);
			bool stay = StaysWithBuilding(item.transform);
			if (stay)
			{
				wir.LiveAtCapture = true;
				wir.HeldInPlace = true;
			}
			WorldItemRecord previous = NetHold.Adopt(item.gameObject, wir);
			if (previous != null)
			{
				float adoptD = (previous.Position - wir.Position).sqrMagnitude;
				ItemPath.Note("store", cx, cy, wir.Id, wir.Position,
					"adopt prev=" + (previous.Id ?? "?")
					+ " prevPos=" + previous.Position.x.ToString("0.0") + "," + previous.Position.y.ToString("0.0")
					+ " d2=" + adoptD.ToString("0.0")
					+ " prevNet=" + (previous.HasNetId ? previous.NetId.ToString() : "-")
					+ " iid=" + item.GetInstanceID());
				CaptureStore.WorldItems.Remove(previous);
				row.Consume(previous);
			}
			else
			{
				ItemPath.Note("store", cx, cy, wir.Id, wir.Position,
					"adopt prev=none iid=" + item.GetInstanceID()
					+ " stay=" + (stay ? "1" : "0"));
			}
			if (id == "gravbag")
			{
				string cell = wir.Battery.NoCell ? "none" : (wir.Battery.Type ?? "");
				Plugin.Log.LogInfo(
					"CPUOpt2 item store gravbag chunk=" + cx + "," + cy
					+ " cond=" + item.condition.ToString("0.00")
					+ " cell=" + (string.IsNullOrEmpty(cell) ? "none" : cell)
					+ " kids=" + wir.Children.Count);
			}
			row.Fresh.Add(wir);
		}

		foreach (var kv in buckets)
		{
			ItemChunkMerge row = kv.Value;
			int snapX = (int)(kv.Key >> 32);
			int snapY = (int)kv.Key;
			FoldStackedCopies(row);
			BagVirt.DropLooseCopies(row.Fresh, row.Old, row.Used);
			int stay = 0;
			for (int i = 0; i < row.Fresh.Count; i++)
			{
				WorldItemRecord wir = row.Fresh[i];
				bool hit = row.Claim(wir.Id, wir.Position, SameRowReachSqr);
				if (wir.HasNetId)
					row.ConsumeNet(wir.NetId);
				float near = hit ? 0f : row.NearestUnused(wir.Id, wir.Position);
				if (wir.HeldInPlace)
					stay++;
				ItemPath.Note("store", snapX, snapY, wir.Id, wir.Position,
					"fresh claim=" + (hit ? "hit" : "miss")
					+ " near=" + (near >= float.MaxValue ? "none" : near.ToString("0.0"))
					+ " net=" + (wir.HasNetId ? wir.NetId.ToString() : "-")
					+ " live=" + (wir.LiveAtCapture ? "1" : "0")
					+ " held=" + (wir.HeldInPlace ? "1" : "0"));
				CaptureStore.WorldItems.Add(wir);
			}
			int kept = 0;
			for (int i = 0; i < row.Old.Count; i++)
			{
				if (row.Used[i] || row.Old[i] == null)
					continue;
				WorldItemRecord keptRow = row.Old[i];
				CaptureStore.WorldItems.Add(keptRow);
				kept++;
				ItemPath.Note("store", snapX, snapY, keptRow.Id, keptRow.Position,
					"kept net=" + (keptRow.HasNetId ? keptRow.NetId.ToString() : "-")
					+ " live=" + (keptRow.LiveAtCapture ? "1" : "0")
					+ " fromWb=" + (keptRow.FromWriteback ? "1" : "0"));
			}
			int worldCount = row.Fresh.Count + kept;
			ItemPath.Sum("store", snapX, snapY,
				"sum fresh=" + row.Fresh.Count
				+ " kept=" + kept
				+ " old=" + row.Old.Count
				+ " scene=" + row.Scene
				+ " stay=" + stay
				+ " child=" + row.Child
				+ " carry=" + row.Carry
				+ " lift=" + row.Lift);
			ItemTrace.Snap(
				snapX,
				snapY,
				row.Scene,
				worldCount,
				row.Claimed,
				row.Child,
				row.Carry,
				row.Lift,
				kept);
			// snap is the end of the gap. DestroyStep's Object.Destroy is after this.
			ItemGoneLog.ReleaseChunk(snapX, snapY);
		}
	}

	private static void WritebackStructureTilemaps(
		WorldGeneration world,
		HashSet<long> chunkKeys,
		HashSet<int> claimedBuildings,
		HashSet<int> claimedItems,
		Transform playerRoot)
	{
		if (world.worldGrid == null)
			return;
		Transform grid = world.worldGrid.transform;
		for (int i = 0; i < grid.childCount; i++)
		{
			Transform child = grid.GetChild(i);
			if (child == null || !StructureTilemapVault.IsStructureTilemap(child, world))
				continue;
			if (StructureTilemapVault.IsPinnedCornerTilemap(child))
				continue;
			BuildingEntity be = child.GetComponent<BuildingEntity>();
			if (be != null && claimedBuildings.Contains(be.GetInstanceID()))
				continue;
			// still write the record if we keep the live tilemap. the origin chunk's
			// slice was already wiped, and a later unload needs that row to respawn it
			if (!InKeys(world, child.position, chunkKeys, out int cx, out int cy))
				continue;
			string id = Clean(child.name);
			EntityRecord rec = new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = id,
				Health = be != null ? be.health : 0f,
				RequireGround = be == null || be.requireGround,
				Position = child.position,
				RotationZ = child.eulerAngles.z,
				HasBlockPlacedOn = be != null,
				BlockPlacedOn = be != null ? be.blockPlacedOn : default,
			};
			if (StructureCatalog.TryResolve(child.gameObject, id, out string prefabPath))
				rec.StructurePrefab = prefabPath;
			rec.StructureVaultKey = StructureTilemapVault.EnsureTemplate(child.gameObject, id);
			rec.LiveAtCapture = true;
			if (be != null)
				BuildingLoot.Capture(be, rec, claimedItems, playerRoot);
			StructureLiveState.CaptureEntity(child.gameObject, be, rec);
			CaptureStore.Entities.Add(rec);
		}
	}

	private static void AttachStructureTilemapVault(WorldGeneration world, GameObject go, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		if (!StructureTilemapVault.IsStructureTilemap(go.transform, world))
			return;
		if (StructureTilemapVault.IsPinnedCornerTilemap(go.transform))
			return;
		record.StructureVaultKey = StructureTilemapVault.EnsureTemplate(go, record.Id);
		record.LiveAtCapture = true;
	}

	private static TraderRecord FillTrader(int cx, int cy, TraderScript trader, BuildingEntity build)
	{
		TraderRecord record = new TraderRecord
		{
			Chunk = new Vector2Int(cx, cy),
			Position = trader.transform.position,
			Health = build != null ? build.health : 0f,
			RequireGround = build == null || build.requireGround,
			HasBlockPlacedOn = build != null,
			BlockPlacedOn = build != null ? build.blockPlacedOn : default,
			StartedConvo = trader.startedConvo,
			Character = StructureLiveState.ResolveTraderPrefab(trader),
			Reputation = trader.reputation,
			Hostility = trader.hostility,
			ValueGiven = trader.valueGiven,
			TotalValueGiven = trader.totalValueGiven,
			HasItems = trader.items != null,
			MoveRangeMin = trader.MoveRange.min,
			MoveRangeMax = trader.MoveRange.max,
			FarEnoughToMove = trader.farEnoughToMove,
			DidMove = trader.didMove,
			HeightMult = trader.heightMult,
			RotationZ = trader.transform.eulerAngles.z,
			LocalScale = trader.transform.localScale,
		};
		if (trader.items != null)
		{
			for (int j = 0; j < trader.items.Count; j++)
			{
				TraderItem item = trader.items[j];
				if (item == null || string.IsNullOrEmpty(item.id))
					continue;
				record.Items.Add(new TraderItemSnap
				{
					Id = item.id,
					Bought = item.bought,
					Value = item.value,
					Preference = item.preference,
				});
			}
		}
		if (record.MoveRangeMin == 0f && record.MoveRangeMax == 0f)
		{
			float x = trader.transform.position.x;
			record.MoveRangeMin = x - 5f;
			record.MoveRangeMax = x + 5f;
		}
		StructureLiveState.CaptureTraderBody(trader, record);
		return record;
	}

	private static void ResolveStructurePrefab(GameObject go, EntityRecord record)
	{
		if (StructureCatalog.TryResolve(go, record.Id, out string path))
			record.StructurePrefab = path;
	}

	private static void DestroyMatching(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found)
	{
		StreamState.SuppressBuildingDrops = true;
		try
		{
			List<GameObject> kill = CollectDestroyTargets(world, chunkKeys, found);
			for (int i = 0; i < kill.Count; i++)
			{
				if (kill[i] != null)
					NetHold.DestroyHeld(kill[i]);
			}
		}
		finally
		{
			StreamState.SuppressBuildingDrops = false;
		}
	}

	/// <summary>
	/// kept tilemap is still in the scene. spawning the record again would stack a second interior.
	/// </summary>
	private static bool StructureTilemapAlreadyLive(EntityRecord rec)
	{
		if (rec == null)
			return false;
		if (rec.StructureVaultKey < 0 && string.IsNullOrEmpty(rec.StructurePrefab))
			return false;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || world.worldGrid == null)
			return false;

		Transform grid = world.worldGrid.transform;
		Vector3 pos = rec.Position;
		for (int i = 0; i < grid.childCount; i++)
		{
			Transform child = grid.GetChild(i);
			if (child == null || child.GetComponent<ChunkScript>() != null)
				continue;
			if (child.GetComponent<Tilemap>() == null)
				continue;
			if ((child.position - pos).sqrMagnitude >= 0.25f)
				continue;
			// lifepod Map and KMPSR_Lifepod share the pod origin. same spot, different interior
			if (Clean(child.name) != Clean(rec.Id))
				continue;
			return true;
		}
		return false;
	}

	private static void CollectNamed(WorldGeneration world, HashSet<long> chunkKeys, string key, List<GameObject> kill, List<GameObject> found, HashSet<int> seen)
	{
		for (int i = 0; i < found.Count; i++)
		{
			Transform t = found[i] != null ? found[i].transform : null;
			if (t == null)
				continue;
			if (Clean(t.name).IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			if (!InKeys(world, t.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(t))
				continue;
			Take(kill, seen, t.gameObject);
		}
	}

	private static void CollectLights(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill, List<GameObject> found, HashSet<int> seen)
	{
		Light2D ambient = world.ambientLight;
		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			Light2D light = found[i].GetComponent<Light2D>();
			if (light == null)
				continue;
			if (!WorldScanner.IsStandaloneWorldLight(light, ambient, PlayerRoot()))
				continue;
			if (!InKeys(world, light.transform.position, chunkKeys, out _, out _))
				continue;
			Take(kill, seen, light.gameObject);
		}
	}

	private static void WritebackNamed(WorldGeneration world, HashSet<long> chunkKeys, string key, List<GameObject> found)
	{
		for (int i = 0; i < found.Count; i++)
		{
			Transform t = found[i] != null ? found[i].transform : null;
			if (t == null)
				continue;
			string name = Clean(t.name);
			if (name.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			if (!InKeys(world, t.position, chunkKeys, out int cx, out int cy))
				continue;
			if (t.GetComponent<Climbable>() != null || t.GetComponent<BuildingEntity>() != null)
				continue;
			SpriteRenderer sr = t.GetComponent<SpriteRenderer>();
			AudioSource audio = t.GetComponent<AudioSource>();
			EntityRecord named = new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = name,
				Position = t.position,
				RotationZ = t.eulerAngles.z,
				Color = sr != null ? sr.color : (Color?)null,
				FlipX = sr != null ? sr.flipX : (bool?)null,
				Pitch = audio != null ? audio.pitch : (float?)null,
				LengthScale = t.localScale.x,
			};
			StructureLiveState.CaptureEntity(t.gameObject, t.GetComponent<BuildingEntity>(), named);
			CaptureStore.Entities.Add(named);
		}
	}

	/// <summary>spawn one matching trader from Index on. bumps Index. false means the list is done</summary>
	private static void NoteSpawn(string kind, int cx, int cy, int index, string id)
	{
		StepTrace.SetName(kind + " id=" + (string.IsNullOrEmpty(id) ? "-" : id) + " i=" + index);
		if (!StreamState.Bootstrapping)
			return;
		Plugin.Log.LogInfo(
			"CPUOpt2 spawn next " + kind
			+ " chunk=" + cx + "," + cy
			+ " i=" + index
			+ " id=" + (string.IsNullOrEmpty(id) ? "-" : id));
	}

	internal static bool SpawnNextTrader(int cx, int cy, ref int index)
	{
		// host spawns these and Together replicates them. a client copy would stack.
		if (MpSession.Running && !MpSession.IsServer)
			return false;
		while (index < CaptureStore.Traders.Count)
		{
			TraderRecord rec = CaptureStore.Traders[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (rec.LiveAtCapture)
				continue;
			if (rec.Character < 1)
			{
				Plugin.Log.LogWarning($"stream spawn trader character {rec.Character} has no prefab");
				return true;
			}
			NoteSpawn("trader", cx, cy, index - 1, "trader" + rec.Character);
			GameObject prefab = Resources.Load<GameObject>("trader" + rec.Character);
			if (prefab == null)
			{
				Plugin.Log.LogWarning($"stream spawn missing trader{rec.Character}");
				return true;
			}
			// inactive until character is the locale index (trader2 -> 1).
			// Start has to run: it builds the shop, krok adds the tracker, Talker spawns the text.
			// a prefix that skipped Start left the prefab pose and no text over the head.
			GameObject go = InstantiateInactive(prefab, rec.Position, Quaternion.Euler(0f, 0f, rec.RotationZ));
			if (go == null)
				return true;
			ScreenCull.RegisterGameObject(go);
			TraderScript trader = go.GetComponent<TraderScript>();
			BuildingEntity build = go.GetComponent<BuildingEntity>();
			if (build != null)
			{
				build.health = rec.Health;
				// prefab requireGround is true and blockPlacedOn is (0,0). OnBecameVisible
				// runs on activate, before Start, and CheckSeating zeros health on air.
				build.requireGround = rec.RequireGround;
				if (rec.HasBlockPlacedOn)
					build.blockPlacedOn = rec.BlockPlacedOn;
				if (rec.Hostility >= 100f)
					build.cantHit = false;
				SeatingHooks.Bind(build);
			}
			if (trader != null)
			{
				int gameCharacter = StructureLiveState.GameCharacterForPrefab(rec.Character);
				trader.character = gameCharacter;
				// OnWillRender calls MeetPlayer while this is false, which rerolls reputation
				trader.startedConvo = rec.StartedConvo;
				trader.reputation = rec.Reputation;
				trader.hostility = rec.Hostility;
				trader.valueGiven = rec.ValueGiven;
				trader.farEnoughToMove = rec.FarEnoughToMove;
				trader.didMove = rec.DidMove;

				float min = rec.MoveRangeMin;
				float max = rec.MoveRangeMax;
				if (min == 0f && max == 0f)
				{
					min = rec.Position.x - 5f;
					max = rec.Position.x + 5f;
				}
				trader.MoveRange = new RangeF(min, max);
				trader.heightMult = rec.HeightMult;
				TraderPatches.ApplyPose(trader, rec.RotationZ, rec.LocalScale);
				FieldAccess.Set(trader, "desiredPos", (Vector2)rec.Position);

				StreamState.TraderRestore[trader.GetInstanceID()] = new StreamState.TraderSpawnState
				{
					GameCharacter = gameCharacter,
					Reputation = rec.Reputation,
					Hostility = rec.Hostility,
					StartedConvo = rec.StartedConvo,
					ValueGiven = rec.ValueGiven,
					TotalValueGiven = rec.TotalValueGiven,
					HasItems = rec.HasItems,
					FarEnoughToMove = rec.FarEnoughToMove,
					DidMove = rec.DidMove,
					MoveRangeMin = min,
					MoveRangeMax = max,
					HeightMult = rec.HeightMult,
					RotationZ = rec.RotationZ,
					LocalScale = rec.LocalScale,
					Items = rec.Items,
				};
			}
			if (go != null && !go.activeSelf)
				go.SetActive(true);
			// head sprite is headSprites on this prefab, rewritten every OnWillRender.
			// stamping a capture on top of the wrong prefab left a trader1 head on a trader2 body
			return true;
		}
		return false;
	}

	internal static bool SpawnNextEnemy(int cx, int cy, ref int index)
	{
		if (MpSession.Running && !MpSession.IsServer)
			return false;
		while (index < CaptureStore.Enemies.Count)
		{
			EnemyRecord rec = CaptureStore.Enemies[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (rec.LiveAtCapture)
				continue;
			NoteSpawn("enemy", cx, cy, index - 1, rec.Id);
			GameObject go = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ, inactive: true);
			if (go == null)
				return true;
			WakeSeating(go, rec.RequireGround, rec.Health, false, default);
			if (rec.ScaleNegX == true)
			{
				Vector3 s = go.transform.localScale;
				go.transform.localScale = new Vector3(-Mathf.Abs(s.x), s.y, s.z);
			}
			StructureLiveState.ApplyEnemy(go, rec);
			return true;
		}
		return false;
	}

	/// <summary>
	/// structure tilemaps: vault template first, then StructureCatalog, then Resources.Load.
	/// ghost-gen records sometimes only have StructurePrefab.
	/// </summary>
	private static GameObject SpawnEntityObject(EntityRecord rec, out string via)
	{
		via = "none";
		Transform grid = WorldGeneration.world != null && WorldGeneration.world.worldGrid != null
			? WorldGeneration.world.worldGrid.transform
			: null;

		if (rec.StructureVaultKey >= 0)
		{
			GameObject fromVault = StructureTilemapVault.Instantiate(
				rec.StructureVaultKey, rec.Position, rec.RotationZ, grid);
			if (fromVault != null)
			{
				via = "vault";
				return fromVault;
			}
			via = "vault-miss";
		}

		if (!string.IsNullOrEmpty(rec.StructurePrefab))
		{
			GameObject fromCatalog = StructureCatalog.InstantiateChild(
				rec.StructurePrefab, rec.Id, rec.Position, rec.RotationZ, grid);
			if (fromCatalog != null)
			{
				via = via == "none" ? "catalog" : via + "+catalog";
				return fromCatalog;
			}
			via = via == "none" ? "catalog-miss" : via + "+catalog-miss";
		}

		// decorative structure props are a sprite shell, even when a resource shares the name.
		// Custom Structures never instantiates that resource for these ids.
		GameObject loaded = null;
		if (IsBackgroundShellRecord(rec))
		{
			loaded = CreateBareSprite(rec);
			if (loaded != null)
			{
				via = via == "none" ? "bare" : via + "+bare";
				return loaded;
			}
		}

		loaded = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ, inactive: true, warn: false);
		if (loaded != null)
		{
			via = via == "none" ? "resources" : via + "+resources";
			return loaded;
		}

		// custom structure buttons are a stoneplant shell plus a sprite. the building id
		// lifepodbuttonheat is not a Resources prefab, so Load returns nothing.
		if (ModBehaviourState.HasForeign(rec.ModState))
		{
			loaded = CreateBareSprite(rec);
			if (loaded != null)
			{
				via = via == "none" ? "bare" : via + "+bare";
				return loaded;
			}
		}

		Plugin.Log.LogWarning($"stream spawn missing prefab '{rec.Id}'");
		return null;
	}

	private static bool IsBackgroundShellRecord(EntityRecord rec)
	{
		if (rec == null || rec.ModState == null || rec.ModState.Behaviours == null)
			return false;
		for (int i = 0; i < rec.ModState.Behaviours.Count; i++)
		{
			ModBehaviourState.BehaviourSnap snap = rec.ModState.Behaviours[i];
			if (snap == null || string.IsNullOrEmpty(snap.TypeName))
				continue;
			if (snap.TypeName.IndexOf("BackgroundObjectIdentity", System.StringComparison.Ordinal) >= 0)
				return true;
		}
		return false;
	}

	/// <summary>sprite and a building, nothing else. mod state puts the button parts back.</summary>
	private static GameObject CreateBareSprite(EntityRecord rec)
	{
		GameObject go = new GameObject(string.IsNullOrEmpty(rec.Id) ? "bgobj" : rec.Id);
		go.SetActive(false);
		// hover and use only see the Descriptor layer. Default makes a solid wall.
		int descriptor = LayerMask.NameToLayer("Descriptor");
		if (descriptor >= 0)
			go.layer = descriptor;
		go.AddComponent<SpriteRenderer>();
		BuildingEntity build = go.AddComponent<BuildingEntity>();
		if (build != null)
		{
			build.id = rec.Id;
			// health 0 with a null drop list makes Update throw every frame and never destroy
			build.health = rec.Health >= 0.5f ? rec.Health : 5f;
			build.requireGround = false;
			build.cantHit = true;
			if (rec.HasBlockPlacedOn)
				build.blockPlacedOn = rec.BlockPlacedOn;
		}
		Transform grid = WorldGeneration.world != null && WorldGeneration.world.worldGrid != null
			? WorldGeneration.world.worldGrid.transform
			: null;
		if (grid != null)
			go.transform.SetParent(grid, false);
		go.transform.position = rec.Position;
		go.transform.rotation = Quaternion.Euler(0f, 0f, rec.RotationZ);
		return go;
	}

	private static void FitBareCollider(GameObject go)
	{
		if (go == null || go.GetComponent<Collider2D>() != null)
			return;
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		BoxCollider2D box = go.AddComponent<BoxCollider2D>();
		if (sr != null && sr.sprite != null)
		{
			Vector2 size = sr.sprite.bounds.size;
			if (size.x > 0f && size.y > 0f)
				box.size = size;
		}
		box.isTrigger = false;
	}

	internal static bool SpawnNextEntity(int cx, int cy, ref int index)
	{
		ItemTrace.Begin(cx, cy);
		while (index < CaptureStore.Entities.Count)
		{
			EntityRecord rec = CaptureStore.Entities[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (rec.LiveAtCapture)
				continue;
			if (WallholeResident.IsWallholeName(rec.Id))
				continue;
			// stock heat/shower fixtures only come back inside the lifepod parent.
			// a custom-structure button reuses that building id and carries its own mod state.
			if (!rec.LifepodGroup && !rec.KeepLooseFixture && LifepodGroups.IsLooseFixtureId(rec.Id)
				&& !ModBehaviourState.HasForeign(rec.ModState))
				continue;
			if (rec.LifepodGroup && LifepodGroups.AlreadyLive(rec))
				continue;
			// lifepod Map is the terrain stamp. a record from an older capture must not come back
			if (StructureCatalog.IsBlockStampRecord(rec))
			{
				if (StructureBgLog.Watch(rec.Id, true))
					StructureBgLog.SpawnSkip(rec, "block-stamp");
				continue;
			}
			bool watchBg = StructureBgLog.Watch(rec.Id, false) || rec.StructureVaultKey >= 0;
			if (StructureTilemapAlreadyLive(rec))
			{
				if (watchBg)
					StructureBgLog.SpawnSkip(rec, "already-live");
				continue;
			}

			NoteSpawn("entity", cx, cy, index - 1, rec.Id);
			GameObject go = SpawnEntityObject(rec, out string via);
			if (go == null)
			{
				if (watchBg)
					StructureBgLog.Spawned(rec, via, null);
				return true;
			}
			if (rec.MagmaPipe)
				ApplyMagmaPipe(go);
			// still inactive. marker and animalCorpse have to be on before Start rolls them
			StructureLiveState.PrimeBeforeStart(go, rec);
			// pump has its own row. a nested copy would roll the 20% destroy and double up
			if (rec.LifepodGroup)
				LifepodGroups.StripNestedPumps(go);
			// still inactive. CheckSeating runs from OnBecameVisible and zeros health if requireGround is on
			WakeSeating(go, rec.RequireGround, rec.Health, rec.HasBlockPlacedOn, rec.BlockPlacedOn);
			if (!string.IsNullOrEmpty(rec.CrystalValue))
				StructureLiveState.ReplayCrystalEffects(go);
			if (watchBg || go.GetComponent<Tilemap>() != null)
			{
				StructureTilemapVault.WakeRenderer(go);
				StructureBgLog.Spawned(rec, via, go);
			}
			ScreenCull.RegisterGameObject(go);
			if (rec.RootScaleX.HasValue)
			{
				Vector3 s = go.transform.localScale;
				float x = rec.RootScaleX.Value;
				if (rec.ScaleNegX == true && x > 0f)
					x = -x;
				go.transform.localScale = new Vector3(x, s.y, s.z);
			}
			else if (rec.ScaleNegX == true)
			{
				Vector3 s = go.transform.localScale;
				go.transform.localScale = new Vector3(-Mathf.Abs(s.x), s.y, s.z);
			}
			else if (!(rec.RopePointA.HasValue || rec.RopePointB.HasValue) && rec.LengthScale.HasValue)
			{
				// sandvinehook (and droppings) stash width in LengthScale
				Vector3 s = go.transform.localScale;
				go.transform.localScale = new Vector3(rec.LengthScale.Value, s.y, s.z);
			}

			if (rec.CrystalSize.HasValue)
			{
				CrystalBehaviour crystal = go.GetComponent<CrystalBehaviour>()
					?? go.GetComponentInChildren<CrystalBehaviour>();
				if (crystal != null)
					crystal.crystalSize = rec.CrystalSize.Value;
			}

			if (rec.RopePointA.HasValue || rec.RopePointB.HasValue)
			{
				Climbable climb = go.GetComponent<Climbable>() ?? go.GetComponentInChildren<Climbable>();
				if (climb != null)
				{
					climb.points.Clear();
					if (rec.RopePointA.HasValue)
						climb.points.Add(rec.RopePointA.Value);
					if (rec.RopePointB.HasValue)
						climb.points.Add(rec.RopePointB.Value);
					if (rec.DownwardsVelocity.HasValue)
						climb.downwardsVelocity = rec.DownwardsVelocity.Value;
				}
				if (rec.LengthScale.HasValue && go.transform.childCount > 0)
				{
					Vector3 s = go.transform.GetChild(0).localScale;
					go.transform.GetChild(0).localScale = new Vector3(s.x, rec.LengthScale.Value, s.z);
				}
			}

			SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
			if (sr != null)
			{
				if (rec.SpriteSize.HasValue)
				{
					// sandvine length is the tiled size. a Simple prefab ignores size and the shoot stays huge
					if (!string.IsNullOrEmpty(rec.Id)
						&& rec.Id.IndexOf("sandvine", System.StringComparison.OrdinalIgnoreCase) >= 0
						&& sr.drawMode == SpriteDrawMode.Simple)
						sr.drawMode = SpriteDrawMode.Tiled;
					sr.size = rec.SpriteSize.Value;
				}
				if (rec.Color.HasValue)
					sr.color = rec.Color.Value;
				if (rec.FlipX.HasValue)
					sr.flipX = rec.FlipX.Value;
			}
			if (rec.Pitch.HasValue)
			{
				AudioSource audio = go.GetComponent<AudioSource>();
				if (audio != null)
					audio.pitch = rec.Pitch.Value;
			}
			if (rec.ScrapAmount.HasValue)
			{
				ScrapEaterScript eater = go.GetComponent<ScrapEaterScript>();
				if (eater != null)
					eater.scrapAmount = rec.ScrapAmount.Value;
			}
			if (rec.StalactiteDropped.HasValue)
			{
				StalactiteDropper dropper = go.GetComponent<StalactiteDropper>();
				HarmonyLib.AccessTools.Field(typeof(StalactiteDropper), "dropped")
					?.SetValue(dropper, rec.StalactiteDropped.Value);
			}

			ModBehaviourState.Apply(go, rec.ModState);
			if (via != null && via.IndexOf("bare", System.StringComparison.Ordinal) >= 0)
				FitBareCollider(go);
			BuildingLoot.Apply(go.GetComponent<BuildingEntity>(), rec);
			// scale last so a custom size / flip Y beats the sign-of-X and vine thickness writes
			StructureLiveState.ApplyEntity(go, rec);
			// Start already ran and forced the hot tooltip. put the saved mode back after that.
			if (rec.LifepodGroup)
				LifepodGroups.ApplySaved(go, rec);
			return true;
		}
		return false;
	}

	private static void WritebackLights(WorldGeneration world, HashSet<long> chunkKeys, HashSet<int> claimedBuildings, List<GameObject> found)
	{
		_ = claimedBuildings;
		Light2D ambient = world.ambientLight;
		Transform playerRoot = PlayerRoot();
		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			Light2D light = found[i].GetComponent<Light2D>();
			if (light == null)
				continue;
			if (!WorldScanner.IsStandaloneWorldLight(light, ambient, playerRoot))
				continue;
			if (!InKeys(world, light.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			LightRecord rec = WorldScanner.FillLight(world, light);
			rec.Chunk = new Vector2Int(cx, cy);
			CaptureStore.Lights.Add(rec);
		}
	}

	internal static bool SpawnNextLight(int cx, int cy, ref int index)
	{
		while (index < CaptureStore.Lights.Count)
		{
			LightRecord rec = CaptureStore.Lights[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (rec.LiveAtCapture)
				continue;
			NoteSpawn("light", cx, cy, index - 1, rec.Id);

			if (string.IsNullOrEmpty(rec.Id) || LooksLikeUnityLightChildName(rec.Id))
			{
				SpawnBareLight(rec);
				return true;
			}

			GameObject go = null;
			try
			{
				GameObject prefab = Resources.Load<GameObject>(rec.Id);
				if (prefab == null)
					prefab = Resources.Load<GameObject>("Special/" + rec.Id);
				if (prefab != null)
				{
					go = Object.Instantiate(prefab, rec.Position, Quaternion.Euler(0f, 0f, rec.RotationZ));
					ChunkRoster.Note(go);
				}
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning($"stream spawn light prefab '{rec.Id}' failed: {ex.Message}");
			}

			if (go == null)
			{
				SpawnBareLight(rec);
				return true;
			}

			ScreenCull.RegisterGameObject(go);
			ApplyLightRecord(go, rec);
			return true;
		}
		return false;
	}

	private static bool LooksLikeUnityLightChildName(string id)
	{
		// "Light 2D (N)" is a wallhole/prop kid, not a catalog id
		if (id.StartsWith("Light 2D", System.StringComparison.OrdinalIgnoreCase))
			return true;
		if (id.StartsWith("Light2D", System.StringComparison.OrdinalIgnoreCase))
			return true;
		return false;
	}

	private static void SpawnBareLight(LightRecord rec)
	{
		try
		{
			GameObject go = new GameObject(string.IsNullOrEmpty(rec.Id) ? "streamLight" : rec.Id);
			go.transform.position = rec.Position;
			go.transform.eulerAngles = new Vector3(0f, 0f, rec.RotationZ);
			Light2D light = go.AddComponent<Light2D>();
			ScreenCull.RegisterGameObject(go);
			ChunkRoster.Note(go);
			ApplyLightProps(light, rec);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"stream bare light '{rec.Id}' failed: {ex.Message}");
		}
	}

	private static void ApplyLightRecord(GameObject go, LightRecord rec)
	{
		Light2D light = go.GetComponent<Light2D>() ?? go.GetComponentInChildren<Light2D>();
		if (light == null)
		{
			Plugin.Log.LogWarning($"stream spawn light '{rec.Id}' has no Light2D");
			return;
		}
		ApplyLightProps(light, rec);
	}

	private static void ApplyLightProps(Light2D light, LightRecord rec)
	{
		try
		{
			QoLLightScale.Apply(light, rec.Intensity);
			light.color = rec.Color;
			light.pointLightOuterRadius = rec.OuterRadius;
			light.pointLightInnerRadius = rec.InnerRadius;
			light.lightType = (Light2D.LightType)rec.LightType;
			light.falloffIntensity = rec.FalloffIntensity;
			light.shadowsEnabled = rec.ShadowEnabled;
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"stream light props '{rec.Id}' failed: {ex.Message}");
		}
	}

	private static void LogGravbagRows(int cx, int cy)
	{
		int n = 0;
		for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
		{
			WorldItemRecord rec = CaptureStore.WorldItems[i];
			if (rec == null || rec.Chunk.x != cx || rec.Chunk.y != cy || rec.Id != "gravbag")
				continue;
			n++;
			string cell = rec.Battery.NoCell || string.IsNullOrEmpty(rec.Battery.Type) ? "none" : rec.Battery.Type;
			Plugin.Log.LogInfo(
				"CPUOpt2 item row gravbag chunk=" + cx + "," + cy
				+ " cond=" + rec.Condition.ToString("0.00")
				+ " cell=" + cell
				+ " kids=" + (rec.Children != null ? rec.Children.Count : 0)
				+ " live=" + (rec.LiveAtCapture ? "1" : "0"));
		}
		if (n > 0)
			Plugin.Log.LogInfo("CPUOpt2 item gravbag rows " + cx + "," + cy + " n=" + n);
	}

	private static readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<WorldItemRecord>> SpawnSlice
		= new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<WorldItemRecord>>();

	/// <summary>
	/// 2 when the item list is already built and finished (advance only).
	/// 4 when the next call might instantiate.
	/// </summary>
	internal static int ItemStepWeight(int cx, int cy, int index)
	{
		long key = Key(cx, cy);
		if (index > 0 && SpawnSlice.TryGetValue(key, out System.Collections.Generic.List<WorldItemRecord> slice) && index >= slice.Count)
			return 2;
		return 4;
	}

	internal static bool SpawnNextWorldItem(int cx, int cy, ref int index)
	{
		// same as traders and enemies. the host's replica is the item she should see.
		if (MpSession.Running && !MpSession.IsServer)
			return false;
		ItemTrace.Begin(cx, cy);
		long key = Key(cx, cy);
		if (index == 0 || !SpawnSlice.TryGetValue(key, out System.Collections.Generic.List<WorldItemRecord> slice))
		{
			slice = new System.Collections.Generic.List<WorldItemRecord>();
			int liveSkip = 0;
			int beside = 0;
			for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
			{
				WorldItemRecord row = CaptureStore.WorldItems[i];
				if (row == null || row.Chunk.x != cx || row.Chunk.y != cy)
					continue;
				if (row.LiveAtCapture)
				{
					liveSkip++;
					continue;
				}
				if (BagVirt.IsStoredBesideBag(row))
				{
					beside++;
					continue;
				}
				slice.Add(row);
			}
			SpawnSlice[key] = slice;
			ItemPath.Sum("build", cx, cy, "slice n=" + slice.Count + " liveSkip=" + liveSkip + " beside=" + beside);
			LogGravbagRows(cx, cy);
		}
		while (index < slice.Count)
		{
			WorldItemRecord rec = slice[index++];
			NoteSpawn("item", cx, cy, index - 1, rec != null ? rec.Id : null);
			try
			{
			GameObject pinned = NetHold.ClaimPinned(rec);
			if (pinned != null)
			{
				int near = ItemPath.Nearby(rec.Id, rec.Position, pinned);
				ItemPath.Note("build", cx, cy, rec.Id, rec.Position,
					"revive nearby=" + near
					+ " net=" + rec.NetId
					+ " iid=" + pinned.GetInstanceID());
				pinned.transform.position = rec.Position;
				pinned.transform.rotation = Quaternion.Euler(0f, 0f, rec.RotationZ);
				NetHold.Wake(pinned, rec);
				NetHold.Link(pinned, rec);
				ItemTrace.Spawned(rec);
				ItemGoneLog.Watch(cx, cy, pinned);
				return true;
			}
			GameObject go = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ, inactive: true);
			int spawnedNear = ItemPath.Nearby(rec.Id, rec.Position, go);
			ItemPath.Note("build", cx, cy, rec != null ? rec.Id : null, rec != null ? rec.Position : Vector3.zero,
				(spawnedNear > 0 ? "DUPE " : "") + "new nearby=" + spawnedNear
				+ " pin=" + (rec != null && rec.HasNetId ? "miss" : "none")
				+ " net=" + (rec != null && rec.HasNetId ? rec.NetId.ToString() : "-")
				+ " live=" + (rec != null && rec.LiveAtCapture ? "1" : "0")
				+ " fromWb=" + (rec != null && rec.FromWriteback ? "1" : "0")
				+ (go == null ? " prefab=missing" : " iid=" + go.GetInstanceID()));
			if (go == null)
			{
				ItemTrace.Fail(rec);
				return true;
			}
			if (rec.LocalScale.HasValue)
				go.transform.localScale = rec.LocalScale.Value;
			rec.Battery.Prime(go, rec.Condition);
			StructureLiveState.ApplyAmmo(go, rec.AmmoRounds);
			StructureLiveState.ApplyItemVisual(go, rec.Sprite, rec.FlipX, rec.PlushIndex);
			// spawn scripts are added in Item.Start, which has not run yet
			ModBehaviourState.Expect(go, rec.ModState);
			SpawnItemTree(go.transform, rec.Children);
			NetHold.Wake(go, rec);
			NetHold.Link(go, rec);
			// WaterContainerItem.stack is null until Awake. DrainAll before that throws and aborts the bag.
			ApplyLiquidsTree(go, rec.HasLiquids, rec.Liquids, rec.Children);
			rec.Battery.Finish(go);
			FinishTree(go.transform, rec.Children);
			LogBag(go, rec);
			ItemTrace.Spawned(rec);
			ItemGoneLog.Watch(cx, cy, go);
			return true;
			}
			catch (System.Exception ex)
			{
				string frame = ex.StackTrace;
				int nl = frame != null ? frame.IndexOf('\n') : -1;
				if (nl > 0)
					frame = frame.Substring(0, nl).Trim();
				Plugin.Log.LogWarning("CPUOpt2 item spawn failed " + (rec != null ? rec.Id : "?") + " " + ex.Message
					+ (string.IsNullOrEmpty(frame) ? "" : " @ " + frame));
				ItemTrace.Fail(rec);
				return true;
			}
		}
		SpawnSlice.Remove(key);
		ItemTrace.Flush(cx, cy);
		return false;
	}

	/// <summary>
	/// parented under a structure, custom structure, or corpse, and not inside another item.
	/// writeback still stores a loose world row. this only marks the snap.
	/// </summary>
	private static bool LiftedFromSource(WorldGeneration world, Transform t)
	{
		if (t == null)
			return false;
		for (Transform p = t.parent; p != null; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return false;
			if (p.GetComponent<CorpseScript>() != null || p.GetComponent<BuildingEntity>() != null)
				return true;
			if (StructureTilemapVault.IsStructureTilemap(p, world))
				return true;
		}
		return false;
	}

	private static bool _registryLooked;
	private static System.Reflection.MethodInfo _registryResolve;

	/// <summary>
	/// CUCoreLib.CustomInstantiate.ResolvePrefab. buildings and custom items.
	/// no compile ref. missing CCL just returns null and the old warning fires.
	/// </summary>
	private static GameObject TryRegistryPrefab(string id)
	{
		if (string.IsNullOrEmpty(id))
			return null;
		if (!_registryLooked)
		{
			System.Type type = TypeLookup.Find("CUCoreLib.Helpers.CustomInstantiate");
			if (type == null && !TypeLookup.PluginsReady)
				return null;
			_registryLooked = true;
			if (type != null)
				_registryResolve = AccessTools.Method(type, "ResolvePrefab", new System.Type[] { typeof(string) });
		}
		if (_registryResolve == null)
			return null;
		try
		{
			return _registryResolve.Invoke(null, new object[] { id }) as GameObject;
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"registry prefab '{id}' failed: {ex.Message}");
			return null;
		}
	}

	internal static GameObject LoadPrefab(string id)
	{
		if (string.IsNullOrEmpty(id))
			return null;
		// hook building id is "sandvine". the prefab is Special/sandvinehook. rope is Special/sandvinerope.
		id = ResolveVinePrefab(id);
		GameObject prefab = Resources.Load<GameObject>(id);
		if (prefab == null)
			prefab = Resources.Load<GameObject>("Special/" + id);
		if (prefab == null)
			prefab = TryRegistryPrefab(id);
		return prefab;
	}

	private static GameObject InstantiatePrefab(string id, Vector3 pos, float rotZ, bool inactive = false, bool warn = true)
	{
		if (string.IsNullOrEmpty(id))
			return null;

		GameObject prefab = LoadPrefab(id);
		if (prefab == null)
		{
			if (id.IndexOf("climbingrope", System.StringComparison.OrdinalIgnoreCase) >= 0)
			{
				GameObject rope = Utils.Create(id, (Vector2)pos, rotZ);
				ScreenCull.RegisterGameObject(rope);
				ChunkRoster.Note(rope);
				return rope;
			}
			if (warn)
				Plugin.Log.LogWarning($"stream spawn missing prefab '{id}'");
			return null;
		}
		GameObject go = inactive
			? InstantiateInactive(prefab, pos, Quaternion.Euler(0f, 0f, rotZ))
			: Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, rotZ));
		// CCL item templates are stored inactive. a straight Instantiate copies that.
		if (!inactive && go != null && !go.activeSelf)
			go.SetActive(true);
		ScreenCull.RegisterGameObject(go);
		if (!inactive)
			ChunkRoster.Note(go);
		return go;
	}

	/// <summary>
	/// source prefab is flipped off for the Instantiate call, then put back.
	/// an active copy runs OnBecameVisible before we can restore requireGround
	/// </summary>
	internal static GameObject InstantiateInactive(GameObject prefab, Vector3 pos, Quaternion rot)
	{
		if (prefab == null)
			return null;
		bool was = prefab.activeSelf;
		if (was)
			prefab.SetActive(false);
		try
		{
			GameObject spawned = Object.Instantiate(prefab, pos, rot);
			ChunkRoster.Note(spawned);
			return spawned;
		}
		finally
		{
			if (was)
				prefab.SetActive(true);
		}
	}

	private static void FinishTree(Transform parent, System.Collections.Generic.List<ItemContentRecord> items)
	{
		if (parent == null || items == null)
			return;
		int cursor = 0;
		for (int i = 0; i < parent.childCount && cursor < items.Count; i++)
		{
			Transform child = parent.GetChild(i);
			if (child.GetComponent<Item>() == null)
				continue;
			ItemContentRecord rec = items[cursor++];
			rec.Battery.Finish(child.gameObject);
			FinishTree(child, rec.Children);
		}
	}

	private static void LogBag(GameObject go, WorldItemRecord rec)
	{
		if (go == null || rec == null || go.GetComponent<Container>() == null)
			return;
		Item item = go.GetComponent<Item>();
		string cell = "none";
		if (item != null && item.battery != null && !string.IsNullOrEmpty(item.battery.batteryType))
			cell = item.battery.batteryType;
		int live = 0;
		for (int i = 0; i < go.transform.childCount; i++)
		{
			if (go.transform.GetChild(i).GetComponent<Item>() != null)
				live++;
		}
		int stored = rec.Children != null ? rec.Children.Count : 0;
		float cond = item != null ? item.condition : -1f;
		Plugin.Log.LogInfo(
			"CPUOpt2 item bag " + rec.Id
			+ " cond=" + cond.ToString("0.00")
			+ " cell=" + cell
			+ " kids=" + live
			+ " stored=" + stored);
	}

	private static void FoldStackedCopies(ItemChunkMerge row)
	{
		if (row == null)
			return;
		for (int i = row.Fresh.Count - 1; i >= 0; i--)
		{
			if (StackedOnBag(row.Fresh[i], row.Fresh, row.Old, i, -1))
			{
				if (row.Fresh[i] != null && row.Fresh[i].Id == "gravbag")
					Plugin.Log.LogInfo("CPUOpt2 item fold gravbag kids=" + (row.Fresh[i].Children != null ? row.Fresh[i].Children.Count : 0));
				row.Fresh.RemoveAt(i);
			}
		}
		for (int i = 0; i < row.Old.Count; i++)
		{
			if (row.Used[i])
				continue;
			if (StackedOnBag(row.Old[i], row.Fresh, row.Old, -1, i))
				row.Used[i] = true;
		}
	}

	private static bool StackedOnBag(
		WorldItemRecord rec,
		System.Collections.Generic.List<WorldItemRecord> fresh,
		System.Collections.Generic.List<WorldItemRecord> old,
		int skipFresh,
		int skipOld)
	{
		if (rec == null || string.IsNullOrEmpty(rec.Id))
			return false;
		if (rec.Children != null && rec.Children.Count > 0)
			return false;
		if (fresh != null)
		{
			for (int i = 0; i < fresh.Count; i++)
			{
				if (i == skipFresh)
					continue;
				if (BagHolds(fresh[i], rec))
					return true;
			}
		}
		if (old != null)
		{
			for (int i = 0; i < old.Count; i++)
			{
				if (i == skipOld)
					continue;
				if (BagHolds(old[i], rec))
					return true;
			}
		}
		return false;
	}

	private static bool BagHolds(WorldItemRecord bag, WorldItemRecord rec)
	{
		if (bag == null || rec == null || ReferenceEquals(bag, rec))
			return false;
		if (bag.Children == null || bag.Children.Count == 0)
			return false;
		if ((bag.Position - rec.Position).sqrMagnitude > SameRowReachSqr)
			return false;
		return ContentHas(bag.Children, rec.Id);
	}

	private static bool ContentHas(System.Collections.Generic.List<ItemContentRecord> items, string id)
	{
		if (items == null)
			return false;
		for (int i = 0; i < items.Count; i++)
		{
			ItemContentRecord child = items[i];
			if (child == null)
				continue;
			if (string.Equals(child.Id ?? "", id, System.StringComparison.Ordinal))
				return true;
			if (ContentHas(child.Children, id))
				return true;
		}
		return false;
	}

	internal static void SpawnItemTree(Transform parent, System.Collections.Generic.List<ItemContentRecord> items)
	{
		if (parent == null || items == null)
			return;
		for (int i = 0; i < items.Count; i++)
			SpawnOneItem(parent, items[i]);
	}

	private static void SpawnOneItem(Transform parent, ItemContentRecord child)
	{
		if (parent == null || child == null)
			return;
		GameObject pinned = NetHold.ClaimPinned(child);
		if (pinned != null)
		{
			ItemPath.Note("build", 0, 0, child.Id, parent.position,
				"child-revive net=" + child.NetId + " parent=" + parent.name);
			pinned.transform.SetParent(parent, false);
			pinned.transform.localPosition = Vector3.zero;
			pinned.transform.localRotation = Quaternion.identity;
			NetHold.Wake(pinned, child);
			SeatInContainer(parent, pinned);
			return;
		}
		// seat on the bag. the captured world pose is where the bag was, and a rigidbody
		// will snap back to the instantiate point once the object wakes.
		GameObject itemGo = InstantiatePrefab(child.Id, parent.position, parent.eulerAngles.z, inactive: true);
		int childNear = ItemPath.Nearby(child.Id, parent.position, itemGo);
		ItemPath.Note("build", 0, 0, child.Id, parent.position,
			(childNear > 0 ? "DUPE " : "") + "child-new nearby=" + childNear
			+ " net=" + (child.HasNetId ? child.NetId.ToString() : "-")
			+ (itemGo == null ? " prefab=missing" : ""));
		if (itemGo == null)
			return;
		if (child.LocalScale.HasValue)
			itemGo.transform.localScale = child.LocalScale.Value;
		itemGo.transform.SetParent(parent, false);
		itemGo.transform.localPosition = Vector3.zero;
		itemGo.transform.localRotation = Quaternion.identity;
		Rigidbody2D rb = itemGo.GetComponent<Rigidbody2D>();
		if (rb != null)
			rb.simulated = false;
		child.Battery.Prime(itemGo, child.Condition);
		StructureLiveState.ApplyAmmo(itemGo, child.AmmoRounds);
		StructureLiveState.ApplyItemVisual(itemGo, child.Sprite, child.FlipX, child.PlushIndex);
		ModBehaviourState.Expect(itemGo, child.ModState);
		SpawnItemTree(itemGo.transform, child.Children);
		NetHold.Wake(itemGo, child);
		SeatInContainer(parent, itemGo);
	}

	/// <summary>
	/// Container.LoadItem hides the sprite unless the bag shows its contents.
	/// a fresh prefab leaves those renderers on, so the art sits outside the bag.
	/// </summary>
	private static void SeatInContainer(Transform parent, GameObject itemGo)
	{
		if (parent == null || itemGo == null)
			return;
		Container container = parent.GetComponent<Container>();
		if (container == null)
			return;
		itemGo.transform.localPosition = Vector3.zero;
		Rigidbody2D rb = itemGo.GetComponent<Rigidbody2D>();
		if (rb != null)
		{
			rb.simulated = false;
			rb.velocity = Vector2.zero;
			rb.angularVelocity = 0f;
		}
		bool visible = container.itemsVisible;
		if (visible)
			itemGo.transform.localEulerAngles = new Vector3(0f, 0f, 180f);
		SpriteRenderer bagSr = parent.GetComponent<SpriteRenderer>();
		int sort = bagSr != null ? bagSr.sortingOrder : 0;
		SpriteRenderer[] sprites = itemGo.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			SpriteRenderer sr = sprites[i];
			if (sr == null || PackedUnderNestedItem(sr.transform, itemGo.transform))
				continue;
			if (!visible)
			{
				sr.enabled = false;
				continue;
			}
			if (sr.gameObject == itemGo && bagSr != null)
				sr.sortingOrder = sort;
		}
		Wearable wearable = itemGo.GetComponent<Wearable>();
		if (wearable != null)
			wearable.ClearSprites();
		Container.UpdateItemLight(itemGo, !visible);
	}

	private static bool PackedUnderNestedItem(Transform t, Transform root)
	{
		for (Transform p = t; p != null && p != root; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return true;
		}
		return false;
	}

	/// <summary>
	/// call after the root is active. Awake pours defaultContents, then this replaces that stack.
	/// </summary>
	private static void ApplyLiquidsTree(GameObject go, bool hasLiquids, List<LiquidRecord> liquids, System.Collections.Generic.List<ItemContentRecord> children)
	{
		ApplyLiquids(go, hasLiquids, liquids);
		if (go == null || children == null)
			return;
		int cursor = 0;
		Transform parent = go.transform;
		for (int i = 0; i < parent.childCount && cursor < children.Count; i++)
		{
			Transform child = parent.GetChild(i);
			if (child.GetComponent<Item>() == null)
				continue;
			ItemContentRecord rec = children[cursor++];
			ApplyLiquidsTree(child.gameObject, rec.HasLiquids, rec.Liquids, rec.Children);
		}
	}

	/// <summary>
	/// prefab Awake already poured defaultContents. AddLiquid on top of a full jug does nothing.
	/// drain first, then pour the captured stack (maybe empty).
	/// </summary>
	private static void ApplyLiquids(GameObject go, bool hasLiquids, List<LiquidRecord> liquids)
	{
		if (go == null || !hasLiquids)
			return;
		WaterContainerItem water = go.GetComponent<WaterContainerItem>();
		if (water == null)
			return;
		water.DrainAll();
		if (liquids == null)
			return;
		for (int L = 0; L < liquids.Count; L++)
		{
			LiquidRecord row = liquids[L];
			if (row == null || string.IsNullOrEmpty(row.LiquidId) || row.Amount <= 0f)
				continue;
			water.AddLiquid(row.LiquidId, row.Amount);
		}
	}

	private static void WakeSeating(GameObject go, bool requireGround, float health, bool hasBlock, Vector2Int block)
	{
		if (go == null)
			return;
		BuildingEntity build = go.GetComponent<BuildingEntity>();
		if (build != null)
		{
			build.requireGround = requireGround;
			build.health = health;
			if (hasBlock)
				build.blockPlacedOn = block;
		}
		if (!go.activeSelf)
			go.SetActive(true);
		if (build != null)
			SeatingHooks.Bind(build);
	}

	private static EntityRecord BaseEntity(int cx, int cy, GameObject go, BuildingEntity build)
	{
		return new EntityRecord
		{
			Chunk = new Vector2Int(cx, cy),
			Id = IdOf(go, build),
			Health = build != null ? build.health : 0f,
			RequireGround = build == null || build.requireGround,
			Position = go.transform.position,
			RotationZ = go.transform.eulerAngles.z,
			// turret, stalactite, etc face by flipping localScale.x. rotation stays 0
			ScaleNegX = go.transform.localScale.x < 0f ? true : (bool?)null,
			HasBlockPlacedOn = build != null,
			BlockPlacedOn = build != null ? build.blockPlacedOn : default,
		};
	}

	private static void AttachExtras(BuildingEntity build, EntityRecord er, HashSet<int> claimedItems, Transform playerRoot)
	{
		CrystalBehaviour crystal = build.GetComponent<CrystalBehaviour>()
			?? build.GetComponentInChildren<CrystalBehaviour>();
		if (crystal != null)
			er.CrystalSize = crystal.crystalSize;

		Climbable climb = build.GetComponent<Climbable>() ?? build.GetComponentInChildren<Climbable>();
		if (climb != null)
			FillRope(climb, er);

		CorpseScript corpse = build.GetComponent<CorpseScript>();
		if (corpse != null)
			StructureLiveState.NoteCorpse(corpse, er);

		StructureLiveState.NoteCannon(build.gameObject, er);

		BuildingLoot.Capture(build, er, claimedItems, playerRoot);

		StalactiteDropper dropper = build.GetComponent<StalactiteDropper>();
		if (dropper != null)
			er.StalactiteDropped = FieldAccess.Get<bool>(dropper, "dropped");

		ScrapEaterScript eater = build.GetComponent<ScrapEaterScript>();
		if (eater != null)
			er.ScrapAmount = eater.scrapAmount;
	}

	private static void FillRope(Climbable climb, EntityRecord er)
	{
		if (climb.points != null && climb.points.Count >= 1)
			er.RopePointA = climb.points[0];
		if (climb.points != null && climb.points.Count >= 2)
			er.RopePointB = climb.points[1];
		er.DownwardsVelocity = climb.downwardsVelocity;
		if (climb.transform.childCount > 0)
			er.LengthScale = climb.transform.GetChild(0).localScale.y;
		SpriteRenderer sr = climb.GetComponent<SpriteRenderer>();
		if (sr != null)
		{
			er.Color = sr.color;
			er.FlipX = sr.flipX;
			if (sr.drawMode == SpriteDrawMode.Tiled || sr.drawMode == SpriteDrawMode.Sliced)
				er.SpriteSize = sr.size;
		}
		er.RootScaleX = climb.transform.localScale.x;
	}

	private static void CopyLiquids(Item item, WorldItemRecord record)
	{
		if (record == null)
			return;
		record.HasLiquids = BuildingLoot.CopyLiquids(item, record.Liquids);
	}

	private static void RemoveChunkSlice(int cx, int cy)
	{
		CaptureStore.Traders.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.Enemies.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.Entities.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		// world items stay until WritebackItems merges them. a smaller scene scan
		// must not throw away rows the chunk already stored.
		CaptureStore.Lights.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
	}

	internal static bool InChunkKeys(WorldGeneration world, Vector3 worldPos, HashSet<long> keys)
	{
		return InKeys(world, worldPos, keys, out _, out _);
	}

	private static bool InKeys(WorldGeneration world, Vector3 worldPos, HashSet<long> keys, out int cx, out int cy)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));
		cx = chunk.x;
		cy = chunk.y;
		return keys.Contains(Key(cx, cy));
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

	private static Transform PlayerRoot()
	{
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			return PlayerCamera.main.body.transform;
		return null;
	}

	private static string IdOf(GameObject go, BuildingEntity build)
	{
		return PrefabIdFor(go, build);
	}

	/// <summary>
	/// fungal sidestabberflip is its own prefab. the building id on it is still the left stabber,
	/// so Resources.Load(build.id) would respawn the wrong one.
	/// </summary>
	/// <summary>
	/// desert and jungle both spawn Special/sandvinehook + Special/sandvinerope.
	/// the hook's BuildingEntity.id is sandvine, which Resources.Load cannot see.
	/// </summary>
	internal static string ResolveVinePrefab(string id)
	{
		if (string.IsNullOrEmpty(id))
			return id;
		string file = id;
		int slash = file.LastIndexOf('/');
		if (slash >= 0)
			file = file.Substring(slash + 1);
		if (file.Equals("sandvine", System.StringComparison.OrdinalIgnoreCase)
			|| file.Equals("sandvinehook", System.StringComparison.OrdinalIgnoreCase))
			return "Special/sandvinehook";
		if (file.Equals("sandvinerope", System.StringComparison.OrdinalIgnoreCase))
			return "Special/sandvinerope";
		return id;
	}

	internal static void CaptureVineHook(GameObject go, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		// same building id as the hook. the rope has to keep its own prefab or the hook tiles down the drop
		string name = Clean(go.name);
		if (name.IndexOf("sandvinerope", System.StringComparison.OrdinalIgnoreCase) >= 0)
		{
			record.Id = "Special/sandvinerope";
			return;
		}
		if (ResolveVinePrefab(record.Id) != "Special/sandvinehook"
			&& name.IndexOf("sandvinehook", System.StringComparison.OrdinalIgnoreCase) < 0)
			return;

		record.Id = "Special/sandvinehook";
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		if (sr != null)
		{
			record.Color = sr.color;
			record.FlipX = sr.flipX;
		}
		// thickness is localScale.x (0.15..1), not a flip
		record.RootScaleX = go.transform.localScale.x;
	}

	internal static string PrefabIdFor(GameObject go, BuildingEntity build)
	{
		string name = Clean(go != null ? go.name : null);
		if (name.IndexOf("sidestabberflip", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return name;
		// rope and hook share building id "sandvine". the name is the only split.
		if (name.IndexOf("sandvinerope", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return "Special/sandvinerope";
		if (name.IndexOf("sandvinehook", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return "Special/sandvinehook";
		if (build != null && !string.IsNullOrEmpty(build.id))
			return build.id;
		return name;
	}

	internal static bool IsMagmaPipe(GameObject go)
	{
		if (go == null)
			return false;
		Component[] all = go.GetComponents<Component>();
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i] != null && all[i].GetType().Name == "MagmaPipeTag")
				return true;
		}
		return false;
	}

	internal static void ApplyMagmaPipe(GameObject go)
	{
		if (go == null || IsMagmaPipe(go))
			return;
		System.Type tag = TypeLookup.Find("SuperSecret.MagmaPipeTag");
		if (tag == null)
			return;
		go.AddComponent(tag);
	}

	private static void CollectOilPipes(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill, List<GameObject> found, HashSet<int> seen)
	{
		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			OilPipeScript pipe = found[i].GetComponent<OilPipeScript>();
			if (pipe == null || !InKeys(world, pipe.transform.position, chunkKeys, out _, out _))
				continue;
			if (PlayerCarry.Holds(pipe.transform))
				continue;
			Take(kill, seen, pipe.gameObject);
		}
	}

	private static void WritebackOilPipes(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> found)
	{
		for (int i = 0; i < found.Count; i++)
		{
			if (found[i] == null)
				continue;
			OilPipeScript pipe = found[i].GetComponent<OilPipeScript>();
			if (pipe == null || !InKeys(world, pipe.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			EntityRecord pipeRec = new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = "oilpipe",
				Position = pipe.transform.position,
				RotationZ = pipe.transform.eulerAngles.z,
				MagmaPipe = IsMagmaPipe(pipe.gameObject),
			};
			StructureLiveState.CaptureEntity(pipe.gameObject, pipe.GetComponent<BuildingEntity>(), pipeRec);
			CaptureStore.Entities.Add(pipeRec);
		}
	}

	private static string Clean(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unknown";
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}

}
