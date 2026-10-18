using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// inactive clones of structure tilemaps (backgrounds etc) under worldGrid.
/// not chunk tilemaps, and alot of them have no Resources.Load id.
/// StructureCatalog covers named prefab kids. this covers the rest, one template per
/// fingerprint. live copies stay until the layer changes.
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
	/// structure tilemaps never got a manual culling box. auto bounds stay tiny,
	/// so the interior drops once the camera leaves the transform, starting pod included.
	/// chunk unload does not destroy these. the bounds have to keep drawing.
	/// </summary>
	internal static void WakeRenderer(GameObject go)
	{
		if (go == null)
			return;
		Tilemap tm = go.GetComponent<Tilemap>();
		if (tm != null)
		{
			tm.CompressBounds();
			tm.RefreshAllTiles();
		}
		TilemapRenderer renderer = go.GetComponent<TilemapRenderer>();
		if (renderer == null)
			return;
		renderer.detectChunkCullingBounds = TilemapRenderer.DetectChunkCullingBounds.Manual;
		renderer.chunkCullingBounds = CullingBounds(go);
		renderer.enabled = false;
		renderer.enabled = true;
	}

	/// <summary>
	/// pod interiors sit on their own transform, so 256 covers them.
	/// StructureBackgroundTilemap is parented at the map corner and paints in cell space.
	/// a 256 box around that corner never reaches a structure, and those tiles never draw.
	/// </summary>
	private static Vector3 CullingBounds(GameObject go)
	{
		if (!IsPinnedCornerTilemap(go.transform))
			return new Vector3(256f, 256f, 8f);
		WorldGeneration world = WorldGeneration.world;
		float span = 8192f;
		if (world != null)
		{
			float w = world.halfWidth * 2f;
			float h = world.halfHeight * 2f;
			if (w > span)
				span = w;
			if (h > span)
				span = h;
		}
		return new Vector3(span + 64f, span + 64f, 8f);
	}

	internal static void WakeAll(WorldGeneration world)
	{
		if (world == null)
			return;
		// not only worldGrid kids. a pod interior can sit under the pod root, and tutorial
		// skips capture so the grid walk never ran.
		Tilemap[] maps = Object.FindObjectsOfType<Tilemap>();
		for (int i = 0; i < maps.Length; i++)
		{
			Tilemap tm = maps[i];
			if (tm == null || tm.GetComponent<ChunkScript>() != null)
				continue;
			WakeRenderer(tm.gameObject);
		}
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
	/// transform.position is always chunk 0,0. leave it where the mod put it.
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
