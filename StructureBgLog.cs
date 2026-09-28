using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// grep LogOutput for "bg ". one interior from gen to spawn:
/// gen SKIP never enters the arrays, GHOST is name-only, LIVE is a real object.
/// vault is a cloned tilemap. spawn is the rebuild. kill is the unload.
/// </summary>
internal static class StructureBgLog
{
	private static int _live;
	private static int _ghost;
	private static int _skip;

	internal static bool NameHasBg(string name)
	{
		return !string.IsNullOrEmpty(name)
			&& name.IndexOf("background", System.StringComparison.OrdinalIgnoreCase) >= 0;
	}

	internal static bool Watch(string name, bool hasTilemap)
	{
		return hasTilemap || NameHasBg(name);
	}

	internal static void ResetCounts()
	{
		_live = _ghost = _skip = 0;
	}

	internal static void DumpGen()
	{
		Plugin.Log.LogInfo($"bg gen totals live={_live} ghost={_ghost} skip={_skip}");
	}

	internal static void Gen(string kind, GameObject prefab, string path, Transform child, Vector3 worldPos, WorldGeneration world)
	{
		if (kind == "LIVE")
			_live++;
		else if (kind == "GHOST")
			_ghost++;
		else
			_skip++;

		Tilemap tm = child != null ? child.GetComponent<Tilemap>() : null;
		BuildingEntity build = child != null ? child.GetComponent<BuildingEntity>() : null;
		Vector2Int chunk = default;
		if (world != null)
			chunk = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));

		int tiles = -1;
		if (tm != null)
		{
			try { tiles = tm.GetUsedTilesCount(); }
			catch { tiles = -1; }
		}

		string require = build != null ? (build.requireGround ? "1" : "0") : "-";
		string health = build != null ? build.health.ToString("0.#") : "-";
		Plugin.Log.LogInfo(
			$"bg gen {kind} prefab={NameOf(prefab)} path={(string.IsNullOrEmpty(path) ? "-" : path)} " +
			$"child={NameOf(child)} chunk={chunk.x},{chunk.y} pos={worldPos.x:0.#},{worldPos.y:0.#} " +
			$"tiles={tiles} be={(build != null ? "1" : "0")} requireGround={require} health={health}");
	}

	internal static void Vault(string id, int key, bool created, GameObject source)
	{
		Plugin.Log.LogInfo(
			$"bg vault id={id} key={key} new={(created ? "1" : "0")} tiles={TileCount(source)}");
	}

	internal static void SpawnSkip(EntityRecord rec, string reason)
	{
		if (rec == null)
			return;
		Plugin.Log.LogInfo(
			$"bg spawn-skip chunk={rec.Chunk.x},{rec.Chunk.y} id={rec.Id} vault={rec.StructureVaultKey} " +
			$"path={(string.IsNullOrEmpty(rec.StructurePrefab) ? "-" : rec.StructurePrefab)} reason={reason}");
	}

	internal static void Spawned(EntityRecord rec, string via, GameObject go)
	{
		if (rec == null)
			return;

		string parent = "-";
		string active = "-";
		string require = "-";
		string health = "-";
		string hasBlock = "-";
		string block = "-";
		string blockId = "-";
		if (go != null)
		{
			active = go.activeSelf ? "1" : "0";
			parent = go.transform.parent != null ? go.transform.parent.name : "none";
			BuildingEntity build = go.GetComponent<BuildingEntity>();
			if (build != null)
			{
				require = build.requireGround ? "1" : "0";
				health = build.health.ToString("0.#");
				hasBlock = rec.HasBlockPlacedOn ? "1" : "0";
				block = build.blockPlacedOn.x + "," + build.blockPlacedOn.y;
				WorldGeneration world = WorldGeneration.world;
				if (world != null)
					blockId = world.GetBlock(build.blockPlacedOn).ToString();
			}
		}

		Plugin.Log.LogInfo(
			$"bg spawn chunk={rec.Chunk.x},{rec.Chunk.y} id={rec.Id} via={via} " +
			$"vault={rec.StructureVaultKey} path={(string.IsNullOrEmpty(rec.StructurePrefab) ? "-" : rec.StructurePrefab)} " +
			$"go={(go != null ? go.name : "null")} tiles={TileCount(go)} parent={parent} active={active} " +
			$"requireGround={require} health={health} hasBlock={hasBlock} block={block} blockId={blockId}");
	}

	internal static void Kill(GameObject go, bool originInUnload)
	{
		if (go == null)
			return;
		WorldGeneration world = WorldGeneration.world;
		Vector2Int chunk = default;
		if (world != null)
			chunk = world.BlockToChunkPos(world.WorldToBlockPos(go.transform.position));
		Plugin.Log.LogInfo(
			$"bg kill id={go.name} chunk={chunk.x},{chunk.y} tiles={TileCount(go)} originInUnload={(originInUnload ? "1" : "0")}");
	}

	private static int TileCount(GameObject go)
	{
		if (go == null)
			return -1;
		Tilemap tm = go.GetComponent<Tilemap>();
		if (tm == null)
			return -1;
		try { return tm.GetUsedTilesCount(); }
		catch { return -1; }
	}

	private static string NameOf(Object obj)
	{
		if (obj == null)
			return "-";
		return string.IsNullOrEmpty(obj.name) ? "-" : obj.name;
	}
}
