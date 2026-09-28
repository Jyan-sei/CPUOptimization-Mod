using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// inactive clones of structure tilemaps (backgrounds etc) under worldGrid.
/// not chunk tilemaps, and alot of them have no Resources.Load id.
/// StructureCatalog covers named prefab kids. this covers the rest, one template per
/// fingerprint, shared across chunks so writeback/destroy can free the live copies.
/// </summary>
internal static class StructureTilemapVault
{
	private static Transform _root;
	private static readonly List<GameObject> Templates = new List<GameObject>(32);
	private static readonly Dictionary<string, int> FingerprintToKey = new Dictionary<string, int>();

	internal static void Clear()
	{
		for (int i = 0; i < Templates.Count; i++)
		{
			if (Templates[i] != null)
				Object.Destroy(Templates[i]);
		}
		Templates.Clear();
		FingerprintToKey.Clear();
	}

	/// <summary>stash a template if its new. returns the vault key (>= 0)</summary>
	internal static int EnsureTemplate(GameObject live, string id)
	{
		if (live == null)
			return -1;

		string fingerprint = Fingerprint(live, id);
		if (FingerprintToKey.TryGetValue(fingerprint, out int existing))
		{
			if (StructureBgLog.Watch(id, true))
				StructureBgLog.Vault(id, existing, false, live);
			return existing;
		}

		EnsureRoot();
		GameObject clone = Object.Instantiate(live);
		clone.name = string.IsNullOrEmpty(id) ? live.name : id;
		clone.SetActive(false);
		clone.transform.SetParent(_root, false);
		clone.hideFlags = HideFlags.HideAndDontSave;

		int key = Templates.Count;
		Templates.Add(clone);
		FingerprintToKey[fingerprint] = key;
		if (StructureBgLog.Watch(id, true))
			StructureBgLog.Vault(id, key, true, live);
		return key;
	}

	internal static GameObject Instantiate(int key, Vector3 position, float rotationZ, Transform worldGrid)
	{
		if (key < 0 || key >= Templates.Count)
			return null;
		GameObject template = Templates[key];
		if (template == null)
			return null;

		GameObject go = Object.Instantiate(template, position, Quaternion.Euler(0f, 0f, rotationZ));
		go.hideFlags = HideFlags.None;
		if (go.GetComponent<Tilemap>() != null && worldGrid != null)
			go.transform.SetParent(worldGrid, true);
		return go;
	}

	/// <summary>
	/// pod interiors are big tilemaps. the transform sits on one chunk, the tiles spill into the next.
	/// keep it while any of those chunks are still in the window.
	/// </summary>
	internal static bool CoversDesiredChunk(WorldGeneration world, Tilemap tm)
	{
		return CoversAny(world, tm, (x, y) => StreamController.IsChunkDesired(x, y));
	}

	/// <summary>true if any in-bounds chunk under the tilemap matches</summary>
	internal static bool CoversAny(WorldGeneration world, Tilemap tm, System.Func<int, int, bool> match)
	{
		if (world == null || tm == null || match == null)
			return false;

		BoundsInt cells = tm.cellBounds;
		Vector3 a = tm.transform.TransformPoint(new Vector3(cells.xMin, cells.yMin, 0f));
		Vector3 b = tm.transform.TransformPoint(new Vector3(cells.xMax, cells.yMin, 0f));
		Vector3 c = tm.transform.TransformPoint(new Vector3(cells.xMin, cells.yMax, 0f));
		Vector3 d = tm.transform.TransformPoint(new Vector3(cells.xMax, cells.yMax, 0f));

		float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
		float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
		float minY = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y));
		float maxY = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
		if (!TryGetCoveredChunkRect(world, minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1))
			return false;

		for (int x = x0; x <= x1; x++)
		for (int y = y0; y <= y1; y++)
		{
			if (match(x, y))
				return true;
		}
		return false;
	}

	private static bool TryGetCoveredChunkRect(
		WorldGeneration world, float minX, float minY, float maxX, float maxY,
		out int x0, out int y0, out int x1, out int y1)
	{
		x0 = y0 = x1 = y1 = 0;
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		if (width <= 0 || height <= 0)
			return false;

		Vector2Int c0 = world.BlockToChunkPos(world.WorldToBlockPos(new Vector2(minX, minY)));
		Vector2Int c1 = world.BlockToChunkPos(world.WorldToBlockPos(new Vector2(maxX, maxY)));
		int rawX0 = Mathf.Min(c0.x, c1.x);
		int rawX1 = Mathf.Max(c0.x, c1.x);
		int rawY0 = Mathf.Min(c0.y, c1.y);
		int rawY1 = Mathf.Max(c0.y, c1.y);
		if (rawX1 < 0 || rawY1 < 0 || rawX0 >= width || rawY0 >= height)
			return false;

		x0 = Mathf.Clamp(rawX0, 0, width - 1);
		x1 = Mathf.Clamp(rawX1, 0, width - 1);
		y0 = Mathf.Clamp(rawY0, 0, height - 1);
		y1 = Mathf.Clamp(rawY1, 0, height - 1);
		return true;
	}

	internal static bool IsStructureTilemap(Transform child, WorldGeneration world)
	{
		if (child == null || world == null || world.worldGrid == null)
			return false;
		if (child.GetComponent<ChunkScript>() != null)
			return false;
		if (child.GetComponent<Tilemap>() == null)
			return false;
		return child.parent == world.worldGrid.transform;
	}

	/// <summary>
	/// Custom Structures parks the transform at the world corner and SetTiles in cell space.
	/// transform.position is always chunk 0,0, so a stream kill there deletes every interior
	/// and the record only comes back when that corner chunk loads.
	/// </summary>
	internal static bool IsPinnedCornerTilemap(Transform child)
	{
		if (child == null)
			return false;
		string name = child.name;
		const string clone = "(Clone)";
		if (!string.IsNullOrEmpty(name) && name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name == "StructureBackgroundTilemap" || name == "HiddenForegroundTilemap";
	}

	private static void EnsureRoot()
	{
		if (_root != null)
			return;
		GameObject go = new GameObject("CPUOptimization2_StructureTilemapVault");
		Object.DontDestroyOnLoad(go);
		go.hideFlags = HideFlags.HideAndDontSave;
		_root = go.transform;
	}

	private static string Fingerprint(GameObject live, string id)
	{
		Tilemap tm = live.GetComponent<Tilemap>();
		int used = 0;
		int sx = 0;
		int sy = 0;
		if (tm != null)
		{
			BoundsInt b = tm.cellBounds;
			sx = b.size.x;
			sy = b.size.y;
			used = tm.GetUsedTilesCount();
		}
		Vector3 s = live.transform.localScale;
		// Map is on lifepod and biocontainer. same id + tile count would share the wrong art
		int mix = 0;
		int area = sx * sy;
		if (tm != null && area > 0 && area <= 256)
		{
			for (int x = tm.cellBounds.xMin; x < tm.cellBounds.xMax; x++)
			for (int y = tm.cellBounds.yMin; y < tm.cellBounds.yMax; y++)
			{
				Vector3Int cell = new Vector3Int(x, y, 0);
				if (!tm.HasTile(cell))
					continue;
				TileBase tile = tm.GetTile(cell);
				mix = unchecked(mix * 31 + x * 17 + y);
				if (tile != null && tile.name != null)
					mix ^= tile.name.GetHashCode();
			}
		}
		return (id ?? "unknown") + "|" + sx + "x" + sy + "|t" + used
			+ "|s" + s.x.ToString("0.###") + "," + s.y.ToString("0.###")
			+ "|m" + mix.ToString();
	}
}
