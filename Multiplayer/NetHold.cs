using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// Chunk cull must not destroy a Together entity, and the rebuild must not AddEntity again.
/// A synced item is disabled in place. The same object and knetid come back when the chunk does.
/// A client keeps the replica it already has. Stream unload stays host-only.
/// </summary>
internal static class NetHold
{
	private sealed class Slot
	{
		internal WorldItemRecord Item;
		internal ItemContentRecord Content;

		internal bool Has
		{
			get
			{
				if (Item != null)
					return Item.HasNetId;
				return Content != null && Content.HasNetId;
			}
		}

		internal ushort Id
		{
			get
			{
				if (Item != null)
					return Item.NetId;
				return Content != null ? Content.NetId : (ushort)0;
			}
		}

		internal void Set(ushort id)
		{
			if (Item != null)
			{
				Item.HasNetId = true;
				Item.NetId = id;
			}
			if (Content != null)
			{
				Content.HasNetId = true;
				Content.NetId = id;
			}
		}

		internal void MarkLive()
		{
			if (Item != null)
				Item.LiveAtCapture = true;
		}
	}

	private static readonly Dictionary<int, Slot> Links = new Dictionary<int, Slot>();
	private static readonly HashSet<int> Armed = new HashSet<int>();
	private static readonly HashSet<int> Parked = new HashSet<int>();
	private static readonly Dictionary<ushort, GameObject> ByNet = new Dictionary<ushort, GameObject>();

	private static bool _resolved;
	private static bool _ready;
	private static bool _patched;
	private static bool _loggedMiss;
	private static bool _loggedRebind;

	private static Type _trackerType;
	private static Type _knetType;
	private static MethodInfo _newGo;
	private static MethodInfo _ensure;
	private static MethodInfo _tryGet;
	private static MethodInfo _shouldNot;
	private static FieldInfo _knetId;
	private static FieldInfo _trackerSyncInfo;
	private static FieldInfo _trackerSyncId;
	private static FieldInfo _syncGo;
	private static FieldInfo _syncTracker;
	private static FieldInfo _registry;
	private static FieldInfo _inSim;
	private static MethodInfo _distance;

	internal static void Reset()
	{
		Links.Clear();
		Armed.Clear();
		Parked.Clear();
		ByNet.Clear();
	}

	internal static void Link(GameObject go, WorldItemRecord record)
	{
		if (go == null || record == null)
			return;
		Links[go.GetInstanceID()] = new Slot { Item = record };
	}

	internal static void Link(GameObject go, ItemContentRecord record)
	{
		if (go == null || record == null)
			return;
		Links[go.GetInstanceID()] = new Slot { Content = record };
	}

	internal static void CopyId(WorldItemRecord from, WorldItemRecord to)
	{
		if (from == null || to == null || !from.HasNetId || to.HasNetId)
			return;
		to.HasNetId = true;
		to.NetId = from.NetId;
	}

	/// <summary>
	/// the fresh row replaces the one this object already had, including a barrel that rolled
	/// out of the 4-unit claim. returns the previous row so writeback can drop it.
	/// </summary>
	internal static WorldItemRecord Adopt(GameObject go, WorldItemRecord fresh)
	{
		if (go == null || fresh == null)
			return null;
		WorldItemRecord previous = null;
		if (Links.TryGetValue(go.GetInstanceID(), out Slot slot) && slot != null)
		{
			previous = slot.Item;
			if (previous != null && !ReferenceEquals(previous, fresh))
				CopyId(previous, fresh);
			else if (slot.Content != null && slot.Content.HasNetId && !fresh.HasNetId)
			{
				fresh.HasNetId = true;
				fresh.NetId = slot.Content.NetId;
			}
		}
		Links[go.GetInstanceID()] = new Slot { Item = fresh };
		return ReferenceEquals(previous, fresh) ? null : previous;
	}

	internal static WorldItemRecord Linked(GameObject go)
	{
		if (go == null)
			return null;
		if (!Links.TryGetValue(go.GetInstanceID(), out Slot slot) || slot == null)
			return null;
		return slot.Item;
	}

	/// <summary>false: this object stays. a client replica is not a chunk teardown.</summary>
	internal static bool DestroyHeld(GameObject go)
	{
		if (go == null)
			return false;
		// a parked custom-item template must survive chunk teardown
		if (!ChunkRoster.InActiveScene(go))
			return false;
		if (!Release(go))
			return false;
		AltarBlessing.Suppress(go);
		UnityEngine.Object.Destroy(go);
		return true;
	}

	internal static void Wake(GameObject go, WorldItemRecord record)
	{
		if (go == null)
			return;
		Wake(go, record != null && record.HasNetId, record != null ? record.NetId : (ushort)0, record, null);
	}

	internal static void Wake(GameObject go, ItemContentRecord record)
	{
		if (go == null)
			return;
		Wake(go, record != null && record.HasNetId, record != null ? record.NetId : (ushort)0, null, record);
	}

	internal static GameObject ClaimPinned(WorldItemRecord record)
	{
		if (record == null || !record.HasNetId)
			return null;
		return ClaimPinned(record.NetId);
	}

	internal static GameObject ClaimPinned(ItemContentRecord record)
	{
		if (record == null || !record.HasNetId)
			return null;
		return ClaimPinned(record.NetId);
	}

	internal static void TryPatch(Harmony harmony)
	{
		if (_patched || harmony == null)
			return;
		Resolve();
		if (_newGo == null)
			return;
		harmony.Patch(_newGo, prefix: new HarmonyMethod(typeof(NetHold), nameof(SkipNew)));
		if (_distance != null)
			harmony.Patch(_distance, postfix: new HarmonyMethod(typeof(NetHold), nameof(KeepParked)));
		_patched = true;
		Plugin.Log.LogInfo("CPUOpt2 net hold - chunk rebuild reuses the same object");
	}

	private static bool SkipNew(GameObject __0)
	{
		return !IsArmed(__0);
	}

	private static void KeepParked(object __instance)
	{
		Component tracker = __instance as Component;
		if (tracker == null || _inSim == null)
			return;
		GameObject go = tracker.gameObject;
		if (go == null)
			return;
		int id = go.GetInstanceID();
		// a parked or still-loaded stream item is outside the host sim range on purpose.
		// the far pass would destroy its tracker, and that destroy nulls the net entity.
		if (!Parked.Contains(id) && !Links.ContainsKey(id))
			return;
		_inSim.SetValue(__instance, true);
	}

	private static GameObject ClaimPinned(ushort netId)
	{
		if (!ByNet.TryGetValue(netId, out GameObject go) || go == null)
		{
			ByNet.Remove(netId);
			return null;
		}
		Parked.Remove(go.GetInstanceID());
		return go;
	}

	private static void Park(GameObject go, ushort id)
	{
		Transform parent = go.transform.parent;
		// a building destroy takes its children with it. a parked item has to outlive that.
		if (parent != null && parent.GetComponent<Item>() == null)
			go.transform.SetParent(null, true);
		ByNet[id] = go;
		Parked.Add(go.GetInstanceID());
		if (go.activeSelf)
			go.SetActive(false);
	}

	private static void Wake(GameObject go, bool hasNet, ushort netId, WorldItemRecord item, ItemContentRecord content)
	{
		bool hold = Host();
		if (hold)
			Armed.Add(go.GetInstanceID());
		try
		{
			if (!go.activeSelf)
				go.SetActive(true);
			if (!hold)
				return;
			if (hasNet && Rebind(go, netId))
			{
				NetPath(go, item, content, "rebind net=" + netId);
				return;
			}
			if (ShouldNot(go))
			{
				NetPath(go, item, content, "skip-shouldnot");
				return;
			}
			ushort id = Ensure(go);
			if (id == 0)
			{
				NetPath(go, item, content, "ensure-failed");
				return;
			}
			NetPath(go, item, content, "ensure net=" + id);
			if (item != null)
			{
				item.HasNetId = true;
				item.NetId = id;
			}
			if (content != null)
			{
				content.HasNetId = true;
				content.NetId = id;
			}
		}
		finally
		{
			if (hold)
				Armed.Remove(go.GetInstanceID());
		}
	}

	private static bool Release(GameObject go)
	{
		Resolve();
		if (!_ready || !MpSession.Running)
		{
			Path(go, "destroy local ready=" + (_ready ? "1" : "0") + " running=" + (MpSession.Running ? "1" : "0"));
			return true;
		}

		// the replica is the host's object. a local gen object is this client's to cull.
		if (!MpSession.IsServer)
		{
			if (!HasReplica(go))
			{
				Path(go, "destroy client-local");
				return true;
			}
			MarkLive(go);
			Path(go, "stay client-replica");
			return false;
		}

		if (go.GetComponent<Item>() == null)
			return true;

		ushort existing = ReadExisting(go);
		Slot slot = Find(go);
		if (existing == 0 && ShouldNot(go))
		{
			Path(go, "destroy should-not");
			return true;
		}

		ushort id = existing;
		if (id == 0 && slot != null && slot.Has)
			id = slot.Id;
		if (id == 0)
			id = Ensure(go);
		if (id == 0)
		{
			Path(go, "destroy ensure-failed");
			return true;
		}

		Stamp(go, slot, id);
		Park(go, id);
		Path(go, "stay park net=" + id + " active=" + (go.activeSelf ? "1" : "0"));
		return false;
	}

	private static void NetPath(GameObject go, WorldItemRecord item, ItemContentRecord content, string detail)
	{
		string id = item != null ? item.Id : content != null ? content.Id : null;
		Vector3 pos = go != null ? go.transform.position : Vector3.zero;
		int cx = 0;
		int cy = 0;
		WorldGeneration world = WorldGeneration.world;
		if (world != null && go != null)
		{
			Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
			cx = chunk.x;
			cy = chunk.y;
		}
		ItemPath.Note("net", cx, cy, id, pos, detail + (go != null ? " iid=" + go.GetInstanceID() : ""));
	}

	private static void Path(GameObject go, string detail)
	{
		if (go == null)
			return;
		Item item = go.GetComponent<Item>();
		if (item == null)
			return;
		int cx = 0;
		int cy = 0;
		WorldGeneration world = WorldGeneration.world;
		if (world != null)
		{
			Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(go.transform.position));
			cx = chunk.x;
			cy = chunk.y;
		}
		ItemPath.Note("tear", cx, cy, string.IsNullOrEmpty(item.id) ? go.name : item.id, go.transform.position,
			detail + " iid=" + go.GetInstanceID());
	}

	private static bool Host()
	{
		Resolve();
		return _ready && MpSession.Running && MpSession.IsServer;
	}

	private static bool IsArmed(GameObject go)
	{
		for (Transform t = go != null ? go.transform : null; t != null; t = t.parent)
		{
			if (Armed.Contains(t.gameObject.GetInstanceID()))
				return true;
		}
		return false;
	}

	private static bool HasReplica(GameObject go)
	{
		if (_trackerType == null || go == null)
			return false;
		Component[] found;
		try
		{
			found = go.GetComponentsInChildren(_trackerType, true);
		}
		catch
		{
			return false;
		}
		if (found == null)
			return false;
		for (int i = 0; i < found.Length; i++)
		{
			if (found[i] != null && _trackerSyncInfo.GetValue(found[i]) != null)
				return true;
		}
		return false;
	}

	private static void MarkLive(GameObject go)
	{
		for (Transform t = go.transform; t != null; t = t.parent)
		{
			if (!Links.TryGetValue(t.gameObject.GetInstanceID(), out Slot slot))
				continue;
			slot.MarkLive();
			if (slot.Item != null)
				return;
		}
	}

	private static Slot Find(GameObject go)
	{
		Links.TryGetValue(go.GetInstanceID(), out Slot slot);
		return slot;
	}

	private static void Stamp(GameObject go, Slot slot, ushort id)
	{
		if (slot != null)
		{
			slot.Set(id);
			return;
		}
		Item item = go.GetComponent<Item>();
		if (item == null)
			return;
		string iid = string.IsNullOrEmpty(item.id) ? go.name : item.id;
		Vector3 pos = go.transform.position;
		WorldItemRecord best = null;
		float bestD = 16f;
		List<WorldItemRecord> rows = CaptureStore.WorldItems;
		for (int i = 0; i < rows.Count; i++)
		{
			WorldItemRecord rec = rows[i];
			if (rec == null || rec.HasNetId)
				continue;
			if (!string.Equals(rec.Id ?? "", iid ?? "", StringComparison.Ordinal))
				continue;
			float d = (rec.Position - pos).sqrMagnitude;
			if (d > bestD)
				continue;
			best = rec;
			bestD = d;
		}
		if (best != null)
		{
			best.HasNetId = true;
			best.NetId = id;
			return;
		}
		for (int i = 0; i < rows.Count; i++)
		{
			if (StampChild(rows[i] != null ? rows[i].Children : null, iid, pos, id))
				return;
		}
	}

	private static bool StampChild(List<ItemContentRecord> items, string iid, Vector3 pos, ushort id)
	{
		if (items == null)
			return false;
		for (int i = 0; i < items.Count; i++)
		{
			ItemContentRecord rec = items[i];
			if (rec == null)
				continue;
			if (!rec.HasNetId
				&& string.Equals(rec.Id ?? "", iid ?? "", StringComparison.Ordinal)
				&& (rec.Position - pos).sqrMagnitude <= 16f)
			{
				rec.HasNetId = true;
				rec.NetId = id;
				return true;
			}
			if (StampChild(rec.Children, iid, pos, id))
				return true;
		}
		return false;
	}

	private static ushort ReadExisting(GameObject go)
	{
		if (_trackerType == null)
			return 0;
		Component tracker = go.GetComponent(_trackerType);
		if (tracker == null || _trackerSyncInfo.GetValue(tracker) == null)
			return 0;
		return ReadKnet(_trackerSyncId.GetValue(tracker));
	}

	private static ushort Ensure(GameObject go)
	{
		if (_ensure == null || go == null)
			return 0;
		object sync;
		try
		{
			sync = _ensure.Invoke(null, new object[] { go });
		}
		catch (Exception ex)
		{
			if (!_loggedMiss)
			{
				_loggedMiss = true;
				Plugin.Log.LogWarning("CPUOpt2 net hold register failed: " + ex.Message);
			}
			return 0;
		}
		ushort id = ReadExisting(go);
		if (id == 0 && sync != null)
			id = ReadKnet(TrackerKnet(sync));
		return id;
	}

	private static bool Rebind(GameObject go, ushort netId)
	{
		if (go == null || _tryGet == null || netId == 0)
			return false;
		object key = Activator.CreateInstance(_knetType);
		_knetId.SetValue(key, netId);
		object[] args = { key, null };
		bool found;
		try
		{
			found = _tryGet.Invoke(null, args) is bool ok && ok && args[1] != null;
		}
		catch (Exception ex)
		{
			if (!_loggedRebind)
			{
				_loggedRebind = true;
				Plugin.Log.LogWarning("CPUOpt2 net hold lookup failed: " + ex.Message);
			}
			return false;
		}
		if (!found)
		{
			if (!_loggedRebind)
			{
				_loggedRebind = true;
				Plugin.Log.LogWarning("CPUOpt2 net hold missed entity " + netId + " for " + go.name);
			}
			return false;
		}

		object sync = args[1];
		// the previous body still points at this entity. its tracker OnDestroy
		// nulls go and destroys the entity after this body has taken it.
		ReleasePrevious(sync, go);
		Component tracker = go.GetComponent(_trackerType);
		if (tracker == null)
			tracker = go.AddComponent(_trackerType);
		object knet = Activator.CreateInstance(_knetType);
		_knetId.SetValue(knet, netId);
		_trackerSyncInfo.SetValue(tracker, sync);
		_trackerSyncId.SetValue(tracker, knet);
		_syncGo.SetValue(sync, go);
		if (_syncTracker != null)
			_syncTracker.SetValue(sync, tracker);
		if (_registry != null && _registry.GetValue(null) is IDictionary registry)
			registry[go] = sync;
		return true;
	}

	private static void ReleasePrevious(object sync, GameObject keep)
	{
		if (sync == null || keep == null)
			return;
		Component previous = _syncTracker != null ? _syncTracker.GetValue(sync) as Component : null;
		if (previous != null)
			Silence(previous, keep);
		if (_registry == null || !(_registry.GetValue(null) is IDictionary registry))
			return;
		var drop = new List<object>();
		foreach (DictionaryEntry entry in registry)
		{
			if (!ReferenceEquals(entry.Value, sync))
				continue;
			GameObject key = entry.Key as GameObject;
			if (key == null || key == keep)
				continue;
			Component tracker = _trackerType != null ? key.GetComponent(_trackerType) : null;
			if (tracker != null)
				Silence(tracker, keep);
			drop.Add(key);
		}
		for (int i = 0; i < drop.Count; i++)
			registry.Remove(drop[i]);
	}

	private static void Silence(Component tracker, GameObject keep)
	{
		if (tracker == null || _trackerSyncInfo == null)
			return;
		GameObject body = tracker.gameObject;
		if (body == null || body == keep)
			return;
		_trackerSyncInfo.SetValue(tracker, null);
	}

	private static bool ShouldNot(GameObject go)
	{
		if (_shouldNot == null || go == null)
			return false;
		try
		{
			return _shouldNot.Invoke(null, new object[] { go }) is bool skip && skip;
		}
		catch
		{
			return false;
		}
	}

	private static object TrackerKnet(object sync)
	{
		if (sync == null || _syncTracker == null)
			return null;
		object tracker = _syncTracker.GetValue(sync);
		if (tracker == null)
			return null;
		return _trackerSyncId.GetValue(tracker);
	}

	private static ushort ReadKnet(object knet)
	{
		if (knet == null || _knetId == null)
			return 0;
		object raw = _knetId.GetValue(knet);
		if (raw is ushort id)
			return id;
		return 0;
	}

	private static void Resolve()
	{
		if (_resolved)
			return;

		Type registry = TypeLookup.Find("Together.NetObjectRegistry");
		_trackerType = TypeLookup.Find("Together.SyncInfoGameObjectTracker");
		Type syncType = TypeLookup.Find("Together.SyncInfo");
		_knetType = TypeLookup.Find("Together.knetid");
		if (registry == null && !TypeLookup.PluginsReady)
			return;
		_resolved = true;
		if (registry == null || _trackerType == null || syncType == null || _knetType == null)
			return;

		const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		_newGo = AccessTools.Method(registry, "NewGO", new[] { typeof(GameObject) });
		_ensure = AccessTools.Method(registry, "Server_EnsureItemIsNetworkRegistered", new[] { typeof(GameObject) });
		_shouldNot = AccessTools.Method(registry, "ObjectShouldNotBeSynced", new[] { typeof(GameObject) });
		_tryGet = AccessTools.Method(registry, "TryGetSyncInfo", new[] { _knetType, syncType.MakeByRefType() });
		if (_tryGet == null)
			_tryGet = AccessTools.Method(registry, "TryGetSyncInfo", new[] { _knetType, syncType });
		_knetId = _knetType.GetField("id", inst);
		_trackerSyncInfo = _trackerType.GetField("syncInfo", inst);
		_trackerSyncId = _trackerType.GetField("syncId", inst);
		_syncGo = syncType.GetField("go", inst);
		_syncTracker = syncType.GetField("tracker", inst);
		_registry = registry.GetField("SyncRegistry", stat);
		_inSim = _trackerType.GetField("isInSimulationRange", inst);
		_distance = AccessTools.Method(_trackerType, "UpdateDistanceChecks", Type.EmptyTypes);

		_ready = _newGo != null
			&& _ensure != null
			&& _tryGet != null
			&& _knetId != null
			&& _trackerSyncInfo != null
			&& _trackerSyncId != null
			&& _syncGo != null
			&& _inSim != null
			&& _distance != null;
		if (!_ready)
			Plugin.Log.LogWarning("CPUOpt2 net hold skipped - Together sync types did not match");
	}
}
