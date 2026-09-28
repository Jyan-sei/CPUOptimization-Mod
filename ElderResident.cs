using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// elder thornbacks (layer 4), elder wall striders (layer 5, New Descents), and
/// Telchak graboids stay alive. no capture, no destroy, no rebuild. they roam,
/// so the stream has to keep up.
///
/// graboids register on Awake, not only at capture. Telchak 1.4.2 places one
/// during sand entity-gen when the megabattery recipe is already made. the hook
/// still catches a spawn after that pass, so a mid-layer place is not missed.
/// Discover is the backup for one that already exists when capture runs.
///
/// chew is SpiderHandler.OnCollisionStay2D -> CheckForBlockDamage. that needs the
/// chunk TilemapCollider2D and its static rigidbody simulated. DamageBlock then asks
/// GetClosestChunk for the break sprite before SetBlock, so the tilemap has to exist.
/// BuildingEntity.Update forces the body Static when that chunk's renderer is off,
/// which is the whole sim. nearest 2 chunks they overlap stay up. renderer included.
/// </summary>
internal static class ElderResident
{
	private sealed class Tracked
	{
		internal Transform Root;
		internal readonly List<Transform> Parts = new List<Transform>();
		internal readonly List<int> PartIds = new List<int>();
	}

	private static readonly List<Tracked> Elders = new List<Tracked>(4);
	private static readonly HashSet<int> OwnedTransforms = new HashSet<int>();
	private static readonly HashSet<int> ProtectedRenderers = new HashSet<int>();
	private static readonly List<Vector2Int> Occupied = new List<Vector2Int>(8);

	private static bool _striderLooked;
	private static System.Type _striderType;
	private static bool _graboidLooked;
	private static System.Type _graboidType;
	private static bool _graboidHooked;
	private static bool _graboidAbsent;
	private static bool _sawUpdate;

	internal static int Count
	{
		get
		{
			int n = 0;
			for (int i = 0; i < Elders.Count; i++)
			{
				if (Elders[i] != null && Elders[i].Root != null)
					n++;
			}
			return n;
		}
	}

	internal static void Clear()
	{
		Elders.Clear();
		OwnedTransforms.Clear();
		ProtectedRenderers.Clear();
	}

	internal static void Discover(WorldGeneration world, HashSet<int> claimedBuildings, HashSet<int> claimedTransforms)
	{
		if (world == null)
			return;

		EnsureStriderType();
		int before = Count;

		if (world.biomeDepth == 4)
		{
			ElderThornbackBehaviour[] backs = Object.FindObjectsOfType<ElderThornbackBehaviour>();
			for (int i = 0; i < backs.Length; i++)
			{
				if (backs[i] != null)
					Track(backs[i].transform, claimedBuildings, claimedTransforms);
			}
		}

		if (world.biomeDepth == 5 && _striderType != null)
		{
			Object[] striders = Object.FindObjectsOfType(_striderType);
			for (int i = 0; i < striders.Length; i++)
			{
				Component c = striders[i] as Component;
				if (c != null)
					Track(c.transform, claimedBuildings, claimedTransforms);
			}
		}

		// once per capture. later spawns come through the Awake hook, not another scan.
		EnsureGraboidType();
		if (_graboidType != null)
		{
			Object[] graboids = Object.FindObjectsOfType(_graboidType);
			for (int i = 0; i < graboids.Length; i++)
			{
				Component c = graboids[i] as Component;
				if (c != null)
					Track(c.transform, claimedBuildings, claimedTransforms);
			}
		}

		int added = Count - before;
		if (added > 0 || Count > 0)
		{
			Plugin.Log.LogInfo(
				$"elder resident — depth={world.biomeDepth} tracked={Count} newlyRegistered={added}");
		}
	}

	/// <summary>true for the body and any part, including a segment that unparented after discover</summary>
	internal static bool IsResident(WorldGeneration world, Transform t)
	{
		if (t == null)
			return false;
		if (OwnsTransform(t))
			return true;
		if (world == null)
			return false;

		EnsureStriderType();
		EnsureGraboidType();
		int depth = world.biomeDepth;
		// graboids are a sand-layer boss. other depths skip the component walk.
		if (depth != 2 && depth != 4 && depth != 5)
			return false;

		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			if (depth == 4 && cur.GetComponent<ElderThornbackBehaviour>() != null)
				return true;
			if (depth == 5 && _striderType != null && cur.GetComponent(_striderType) != null)
				return true;
			if (depth == 2 && IsGraboid(cur))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Telchak may load after us. first Update is late enough that every plugin has awoken.
	/// a miss then means the graboid type is not in this game.
	/// </summary>
	internal static void TryHookSpawn(Harmony harmony)
	{
		if (_graboidHooked || _graboidAbsent || harmony == null)
			return;

		EnsureGraboidType();
		if (_graboidType == null)
		{
			if (_sawUpdate)
				_graboidAbsent = true;
			return;
		}

		MethodInfo awake = AccessTools.Method(_graboidType, "Awake");
		if (awake == null)
		{
			_graboidAbsent = true;
			Plugin.Log.LogWarning("GraboidBehaviour has no Awake. mid-layer graboids only register at capture.");
			return;
		}

		harmony.Patch(awake, postfix: new HarmonyMethod(typeof(ElderResident), nameof(OnGraboidAwake)));
		_graboidHooked = true;
		Plugin.Log.LogInfo("graboid resident — spawn hook on");
	}

	internal static void NotifyUpdate()
	{
		_sawUpdate = true;
	}

	private static void OnGraboidAwake(Component __instance)
	{
		if (__instance != null)
			Track(__instance.transform, null, null);
	}

	internal static bool OwnsTransform(Transform t)
	{
		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			if (OwnedTransforms.Contains(cur.GetInstanceID()))
				return true;
		}
		return false;
	}

	internal static void ClearProtectedRenderers()
	{
		ProtectedRenderers.Clear();
	}

	internal static void ProtectRenderer(Renderer r)
	{
		if (r != null)
			ProtectedRenderers.Add(r.GetInstanceID());
	}

	internal static bool ProtectsRenderer(Renderer r)
	{
		return r != null && ProtectedRenderers.Contains(r.GetInstanceID());
	}

	/// <summary>up to 2 chunks per elder: ones their body overlaps, plus the near neighbor when they only sit in one</summary>
	internal static void CollectPins(WorldGeneration world, List<Vector2Int> pins)
	{
		pins.Clear();
		if (world == null)
			return;

		for (int i = Elders.Count - 1; i >= 0; i--)
		{
			Tracked elder = Elders[i];
			if (elder == null || elder.Root == null)
			{
				if (elder != null)
					Untrack(elder);
				Elders.RemoveAt(i);
				continue;
			}

			Occupied.Clear();
			AddChunk(world, elder.Root.position);
			for (int p = 0; p < elder.Parts.Count; p++)
			{
				Transform part = elder.Parts[p];
				if (part == null)
					continue;
				AddChunk(world, part.position);
				Collider2D col = part.GetComponent<Collider2D>();
				if (col == null)
					continue;
				Bounds b = col.bounds;
				AddChunk(world, b.min);
				AddChunk(world, b.max);
			}

			if (Occupied.Count == 0)
				continue;

			Vector3 origin = elder.Root.position;
			SortByDistance(world, origin);
			int keep = Occupied.Count < 2 ? Occupied.Count : 2;
			for (int k = 0; k < keep; k++)
				AddPin(pins, Occupied[k]);

			if (Occupied.Count == 1)
			{
				Vector2Int neighbor = NearestNeighbor(world, origin, Occupied[0]);
				AddPin(pins, neighbor);
			}
		}
	}

	private static void Track(Transform root, HashSet<int> claimedBuildings, HashSet<int> claimedTransforms)
	{
		if (root == null || OwnedTransforms.Contains(root.GetInstanceID()))
			return;

		Tracked elder = new Tracked { Root = root };
		Transform[] nodes = root.GetComponentsInChildren<Transform>(true);
		for (int i = 0; i < nodes.Length; i++)
		{
			Transform n = nodes[i];
			if (n == null)
				continue;
			int nid = n.GetInstanceID();
			OwnedTransforms.Add(nid);
			elder.PartIds.Add(nid);
			elder.Parts.Add(n);
			if (claimedTransforms != null)
				claimedTransforms.Add(n.GetInstanceID());
			BuildingEntity build = n.GetComponent<BuildingEntity>();
			if (build != null && claimedBuildings != null)
				claimedBuildings.Add(build.GetInstanceID());
		}
		Elders.Add(elder);
	}

	private static void Untrack(Tracked elder)
	{
		for (int i = 0; i < elder.PartIds.Count; i++)
			OwnedTransforms.Remove(elder.PartIds[i]);
	}

	private static bool IsGraboid(Transform cur)
	{
		if (_graboidType != null && cur.GetComponent(_graboidType) != null)
			return true;
		BuildingEntity build = cur.GetComponent<BuildingEntity>();
		return build != null && build.id == "graboid";
	}

	private static void EnsureStriderType()
	{
		if (_striderLooked)
			return;
		_striderLooked = true;
		_striderType = AccessTools.TypeByName("NewDescents.ElderWallStriderBehaviour");
	}

	private static void EnsureGraboidType()
	{
		if (_graboidLooked)
			return;
		_graboidType = AccessTools.TypeByName("GraboidBehaviour");
		// a miss in Awake just means Telchak has not loaded yet. latch it after the first Update.
		if (_graboidType != null || _sawUpdate)
			_graboidLooked = true;
	}

	private static void AddChunk(WorldGeneration world, Vector3 worldPos)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));
		for (int i = 0; i < Occupied.Count; i++)
		{
			if (Occupied[i] == chunk)
				return;
		}
		Occupied.Add(chunk);
	}

	private static void AddPin(List<Vector2Int> pins, Vector2Int chunk)
	{
		for (int i = 0; i < pins.Count; i++)
		{
			if (pins[i] == chunk)
				return;
		}
		pins.Add(chunk);
	}

	private static void SortByDistance(WorldGeneration world, Vector3 origin)
	{
		for (int i = 0; i < Occupied.Count; i++)
		{
			int best = i;
			float bestD = Dist(world, origin, Occupied[i]);
			for (int j = i + 1; j < Occupied.Count; j++)
			{
				float d = Dist(world, origin, Occupied[j]);
				if (d < bestD)
				{
					bestD = d;
					best = j;
				}
			}
			if (best != i)
			{
				Vector2Int tmp = Occupied[i];
				Occupied[i] = Occupied[best];
				Occupied[best] = tmp;
			}
		}
	}

	private static float Dist(WorldGeneration world, Vector3 origin, Vector2Int chunk)
	{
		Vector2 center = ChunkCenter(world, chunk.x, chunk.y);
		return (center - (Vector2)origin).sqrMagnitude;
	}

	private static Vector2Int NearestNeighbor(WorldGeneration world, Vector3 origin, Vector2Int home)
	{
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		Vector2Int best = home;
		float bestD = float.MaxValue;
		int[] dx = { 1, -1, 0, 0 };
		int[] dy = { 0, 0, 1, -1 };
		for (int i = 0; i < 4; i++)
		{
			int nx = home.x + dx[i];
			int ny = home.y + dy[i];
			if (nx < 0 || ny < 0 || nx >= width || ny >= height)
				continue;
			float d = Dist(world, origin, new Vector2Int(nx, ny));
			if (d < bestD)
			{
				bestD = d;
				best = new Vector2Int(nx, ny);
			}
		}
		return best;
	}

	private static Vector2 ChunkCenter(WorldGeneration world, int cx, int cy)
	{
		float w = world.chunkWidth;
		float h = world.chunkHeight;
		return new Vector2(
			(cx - w * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE,
			(cy - h * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE);
	}
}
