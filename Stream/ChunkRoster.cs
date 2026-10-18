using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CPUOptimization2;

/// <summary>
/// live objects per chunk. world gen capture and spawn register them.
/// a teardown reads this list plus one overlap of that chunk for anything that
/// walked or dropped in afterwards. it does not search the scene.
/// </summary>
internal static class ChunkRoster
{
	private sealed class Slot
	{
		internal GameObject Go;
		internal long Chunk;
	}

	private static readonly Dictionary<int, Slot> ById = new Dictionary<int, Slot>();
	private static readonly Dictionary<long, List<int>> ByChunk = new Dictionary<long, List<int>>();
	private static readonly List<GameObject> Found = new List<GameObject>(64);
	private static readonly HashSet<int> Seen = new HashSet<int>();
	private static readonly List<int> DropIds = new List<int>(32);
	private static Collider2D[] Hits = new Collider2D[128];

	internal static void Clear()
	{
		ById.Clear();
		ByChunk.Clear();
		Found.Clear();
		Seen.Clear();
	}

	/// <summary>
	/// the loaded gameplay scene only. a DontDestroyOnLoad object is a template,
	/// not a piece of the map, even when its position falls inside a chunk.
	/// </summary>
	internal static bool InActiveScene(GameObject go)
	{
		if (go == null)
			return false;
		Scene scene = go.scene;
		if (!scene.IsValid() || !scene.isLoaded)
			return false;
		return scene == SceneManager.GetActiveScene();
	}

	internal static void Note(GameObject go)
	{
		// DontDestroyOnLoad templates (CUCoreLib custom items) are not world loot.
		// noting them lets a later chunk teardown destroy the only craft prefab.
		if (!InActiveScene(go))
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null)
			return;
		if (!ChunkOf(world, go.transform.position, out int cx, out int cy))
			return;
		int id = go.GetInstanceID();
		long key = Key(cx, cy);
		if (ById.TryGetValue(id, out Slot slot) && slot != null)
		{
			if (slot.Chunk != key)
				Move(id, slot, key);
			slot.Go = go;
			return;
		}
		slot = new Slot { Go = go, Chunk = key };
		ById[id] = slot;
		List(key).Add(id);
	}

	internal static void CountRenderers(out int objects, out int enabled, out int visible)
	{
		objects = 0;
		enabled = 0;
		visible = 0;
		foreach (var kv in ById)
		{
			Slot slot = kv.Value;
			if (slot == null || slot.Go == null)
				continue;
			objects++;
			Renderer renderer = slot.Go.GetComponent<Renderer>();
			if (renderer == null || !renderer.enabled)
				continue;
			enabled++;
			if (renderer.isVisible)
				visible++;
		}
	}

	internal static void CopyAll(List<GameObject> into)
	{
		if (into == null)
			return;
		into.Clear();
		foreach (var kv in ById)
		{
			GameObject go = kv.Value != null ? kv.Value.Go : null;
			if (go != null)
				into.Add(go);
		}
	}

	internal static bool AnyItemNear(Vector3 pos, float reachSqr)
	{
		foreach (var kv in ById)
		{
			Slot slot = kv.Value;
			if (slot == null || slot.Go == null)
				continue;
			Item item = slot.Go.GetComponent<Item>();
			if (item == null)
				continue;
			if ((item.transform.position - pos).sqrMagnitude <= reachSqr)
				return true;
		}
		return false;
	}

	internal static int CountItems(string id, Vector3 pos, GameObject ignore, float reachSqr)
	{
		if (string.IsNullOrEmpty(id))
			return 0;
		int n = 0;
		foreach (var kv in ById)
		{
			Slot slot = kv.Value;
			if (slot == null || slot.Go == null || slot.Go == ignore)
				continue;
			Item item = slot.Go.GetComponent<Item>();
			if (item == null)
				continue;
			string iid = string.IsNullOrEmpty(item.id) ? slot.Go.name : item.id;
			if (!string.Equals(iid, id, System.StringComparison.Ordinal))
				continue;
			if ((item.transform.position - pos).sqrMagnitude > reachSqr)
				continue;
			n++;
		}
		return n;
	}

	/// <summary>objects whose position is in these chunks, plus active newcomers the overlap hits.</summary>
	internal static List<GameObject> Gather(WorldGeneration world, HashSet<long> keys)
	{
		Found.Clear();
		Seen.Clear();
		DropIds.Clear();
		if (world == null || keys == null || keys.Count == 0)
			return Found;

		foreach (long key in keys)
		{
			if (!ByChunk.TryGetValue(key, out List<int> ids))
				continue;
			for (int i = ids.Count - 1; i >= 0; i--)
			{
				int id = ids[i];
				if (!ById.TryGetValue(id, out Slot slot) || slot == null || slot.Go == null)
				{
					DropIds.Add(id);
					continue;
				}
				if (!InActiveScene(slot.Go))
				{
					DropIds.Add(id);
					continue;
				}
				if (!ChunkOf(world, slot.Go.transform.position, out int cx, out int cy))
				{
					DropIds.Add(id);
					continue;
				}
				long now = Key(cx, cy);
				if (now != slot.Chunk)
					Move(id, slot, now);
				if (!keys.Contains(now))
					continue;
				Add(slot.Go);
			}
			Overlap(world, key);
		}

		for (int i = 0; i < DropIds.Count; i++)
			Forget(DropIds[i]);
		ExpandItems();
		return Found;
	}

	private static void Overlap(WorldGeneration world, long key)
	{
		Decode(key, out int cx, out int cy);
		int size = WorldGeneration.CHUNKSIZE;
		Vector2 origin = world.BlockToWorldPos(new Vector2Int(cx * size, cy * size));
		Vector2 far = world.BlockToWorldPos(new Vector2Int(cx * size + size, cy * size + size));
		Vector2 center = (origin + far) * 0.5f;
		Vector2 box = new Vector2(Mathf.Abs(far.x - origin.x), Mathf.Abs(far.y - origin.y));
		int n = Physics2D.OverlapBoxNonAlloc(center, box, 0f, Hits);
		if (n == Hits.Length)
		{
			Hits = new Collider2D[Hits.Length * 2];
			n = Physics2D.OverlapBoxNonAlloc(center, box, 0f, Hits);
		}
		for (int i = 0; i < n; i++)
		{
			Collider2D hit = Hits[i];
			if (hit == null)
				continue;
			if (!TryRoot(hit.gameObject, out GameObject root))
				continue;
			if (!ChunkOf(world, root.transform.position, out int rx, out int ry))
				continue;
			if (Key(rx, ry) != key)
				continue;
			Note(root);
			Add(root);
		}
	}

	private static void ExpandItems()
	{
		int count = Found.Count;
		for (int i = 0; i < count; i++)
		{
			GameObject go = Found[i];
			if (go != null)
				AddItemChildren(go.transform);
		}
	}

	private static void AddItemChildren(Transform t)
	{
		for (int i = 0; i < t.childCount; i++)
		{
			Transform child = t.GetChild(i);
			if (child == null)
				continue;
			if (child.GetComponent<Item>() != null)
				Add(child.gameObject);
			AddItemChildren(child);
		}
	}

	private static void Add(GameObject go)
	{
		if (go == null)
			return;
		int id = go.GetInstanceID();
		if (!Seen.Add(id))
			return;
		if (IsPlayer(go) || WallholeResident.OwnsTransform(go.transform))
			return;
		if (ElderResident.IsResident(WorldGeneration.world, go.transform))
			return;
		Found.Add(go);
	}

	private static bool TryRoot(GameObject start, out GameObject root)
	{
		root = null;
		if (start == null)
			return false;
		GameObject best = null;
		for (Transform p = start.transform; p != null; p = p.parent)
		{
			if (p.GetComponent<ChunkScript>() != null)
				break;
			if (Tracked(p.gameObject))
				best = p.gameObject;
		}
		if (best == null || IsPlayer(best) || WallholeResident.OwnsTransform(best.transform))
			return false;
		root = best;
		return true;
	}

	private static bool Tracked(GameObject go)
	{
		if (go.GetComponent<Item>() != null)
			return true;
		if (go.GetComponent<BuildingEntity>() != null)
			return true;
		if (go.GetComponent<TraderScript>() != null)
			return true;
		if (go.GetComponent<SpiderHandler>() != null)
			return true;
		if (go.GetComponent<Climbable>() != null)
			return true;
		if (go.GetComponent<OilPipeScript>() != null)
			return true;
		if (go.GetComponent<Light2D>() != null)
			return true;
		if (go.name.IndexOf("bgobj_", System.StringComparison.Ordinal) == 0)
			return true;
		Component[] parts = go.GetComponents<Component>();
		for (int i = 0; i < parts.Length; i++)
		{
			if (parts[i] != null && parts[i].GetType().Name == "BackgroundObjectIdentity")
				return true;
		}
		string name = go.name;
		if (name.IndexOf("sandvinehook", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (name.IndexOf("droppings", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		return false;
	}

	private static bool IsPlayer(GameObject go)
	{
		if (PlayerCamera.main == null || PlayerCamera.main.body == null)
			return false;
		Transform body = PlayerCamera.main.body.transform;
		for (Transform p = go.transform; p != null; p = p.parent)
		{
			if (p == body)
				return true;
		}
		return false;
	}

	private static void Forget(int id)
	{
		if (!ById.TryGetValue(id, out Slot slot))
		{
			ById.Remove(id);
			return;
		}
		ById.Remove(id);
		if (slot == null)
			return;
		if (ByChunk.TryGetValue(slot.Chunk, out List<int> ids))
			ids.Remove(id);
	}

	private static void Move(int id, Slot slot, long key)
	{
		if (ByChunk.TryGetValue(slot.Chunk, out List<int> old))
			old.Remove(id);
		slot.Chunk = key;
		List(key).Add(id);
	}

	private static List<int> List(long key)
	{
		if (!ByChunk.TryGetValue(key, out List<int> ids))
		{
			ids = new List<int>(8);
			ByChunk[key] = ids;
		}
		return ids;
	}

	private static bool ChunkOf(WorldGeneration world, Vector3 pos, out int cx, out int cy)
	{
		cx = cy = 0;
		if (world == null)
			return false;
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		cx = chunk.x;
		cy = chunk.y;
		return true;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

	private static void Decode(long key, out int x, out int y)
	{
		x = (int)(key >> 32);
		y = (int)(uint)key;
	}
}
