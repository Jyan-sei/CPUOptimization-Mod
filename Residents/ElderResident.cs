using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// elder thornbacks (layer 4), elder wall striders (layer 5, New Descents), and
/// Telchak graboids stay alive. no capture, no destroy, no rebuild. they roam,
/// so the stream has to keep up.
///
/// a BuildingEntity with ignoreBodyOptimize stays too, when it is not a stock
/// prefab. mod bosses tick that flag and are left resident. stock prefabs that
/// use the same flag (stalactites and their variants) still stream. thornbackelder
/// is the stock id that stays anyway. a prop we froze ourselves is not a boss:
/// pinning those held every desert chunk the vines sat on.
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
/// which is the whole sim. nearest 2 chunks keep their collider. the tilemap
/// draws only while that chunk is inside the camera window.
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
	private static System.Type _graboidType;
	private static bool _graboidHooked;
	private static bool _graboidAbsent;
	private static bool _bodyHooked;
	private static readonly Dictionary<string, bool> VanillaIds = new Dictionary<string, bool>(32);
	private static readonly HashSet<string> KeptIds = new HashSet<string>();
	// HoldUnsupported sets ignoreBodyOptimize so a prop stays static. that is not a
	// roaming boss, and pinning one per vine kept the desert's chunks awake.
	private static readonly HashSet<int> HeldStatic = new HashSet<int>();

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
		HeldStatic.Clear();
	}

	/// <summary>we forced this body static. ShouldKeep must not pin it.</summary>
	internal static void NoteHeldStatic(BuildingEntity build)
	{
		if (build != null)
			HeldStatic.Add(build.GetInstanceID());
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

		// flag is already set: stalactite Start ran during gen, mod prefabs ship it serialized.
		BuildingEntity[] bodies = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < bodies.Length; i++)
		{
			BuildingEntity build = bodies[i];
			if (ShouldKeep(build))
				TrackKept(build, claimedBuildings, claimedTransforms);
		}

		int added = Count - before;
		if (added > 0 || Count > 0)
		{
			Plugin.Log.LogInfo(
				$"elder resident - depth={world.biomeDepth} tracked={Count} newlyRegistered={added}");
		}
	}

	/// <summary>true for the body and any part, including a segment that unparented after discover</summary>
	internal static bool IsResident(WorldGeneration world, Transform t)
	{
		if (t == null)
			return false;
		if (OwnsTransform(t))
			return true;

		// cull runs before Discover. the walk is what keeps a flagged boss alive on the first pass.
		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			if (ShouldKeep(cur.GetComponent<BuildingEntity>()))
				return true;
		}

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
	/// Telchak may load after us. a miss before the first Update is not final.
	/// after that, GraboidBehaviour is either hooked or absent. no per-entity scan.
	/// </summary>
	internal static void TryHookSpawn(Harmony harmony)
	{
		EnsureBodyHook(harmony);
		if (_graboidHooked || _graboidAbsent || harmony == null)
			return;

		EnsureGraboidType();
		if (_graboidType == null)
			return;

		MethodInfo awake = AccessTools.Method(_graboidType, "Awake");
		if (awake == null)
		{
			_graboidAbsent = true;
			Plugin.Log.LogWarning("GraboidBehaviour has no Awake. mid-layer graboids only register at capture.");
			return;
		}

		harmony.Patch(awake, postfix: new HarmonyMethod(typeof(ElderResident), nameof(OnGraboidAwake)));
		_graboidHooked = true;
		Plugin.Log.LogInfo("graboid resident - spawn hook on");
	}

	private static void OnGraboidAwake(Component __instance)
	{
		if (__instance != null)
			Track(__instance.transform, null, null);
	}

	private static void EnsureBodyHook(Harmony harmony)
	{
		if (_bodyHooked || harmony == null)
			return;
		MethodInfo start = AccessTools.Method(typeof(BuildingEntity), "Start");
		if (start == null)
			return;
		harmony.Patch(start, postfix: new HarmonyMethod(typeof(ElderResident), nameof(OnBuildingStart)));
		_bodyHooked = true;
		Plugin.Log.LogInfo("body resident - BuildingEntity.Start hook on");
	}

	private static readonly AccessTools.FieldRef<BuildingEntity, Rigidbody2D> Body
		= AccessTools.FieldRefAccess<BuildingEntity, Rigidbody2D>("rb");

	/// <summary>
	/// stock sets the body Static when the chunk renderer is off. an off-window
	/// elder still has its collider, so the body has to stay Dynamic.
	/// runs after BuildingEntity.Update.
	/// </summary>
	internal static void KeepBodiesMoving()
	{
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists || Time.timeScale > 5f)
			return;
		for (int i = 0; i < Elders.Count; i++)
		{
			Tracked elder = Elders[i];
			if (elder == null || elder.Root == null)
				continue;
			BuildingEntity build = elder.Root.GetComponent<BuildingEntity>();
			if (build == null)
				continue;
			Rigidbody2D rb = Body(build);
			if (rb == null || rb.bodyType == RigidbodyType2D.Dynamic)
				continue;
			TilemapRenderer renderer = world.GetClosestChunkRenderer(world.WorldToBlockPos(elder.Root.position));
			if (renderer != null && renderer.enabled)
				continue;
			rb.bodyType = RigidbodyType2D.Dynamic;
		}
	}

	private static void OnBuildingStart(BuildingEntity __instance)
	{
		if (ShouldKeep(__instance))
			TrackKept(__instance, null, null);
	}

	/// <summary>
	/// structure children outside the keep window are ghosted at gen. a keeper has to be
	/// placed live or the stream is the only copy, which is the cull modders are opting out of.
	/// </summary>
	internal static bool KeepLive(Transform t)
	{
		return t != null && ShouldKeep(t.GetComponent<BuildingEntity>());
	}

	private static bool ShouldKeep(BuildingEntity build)
	{
		if (build == null)
			return false;
		if (IsThornbackElder(build))
			return true;
		if (!build.ignoreBodyOptimize)
			return false;
		if (HeldStatic.Contains(build.GetInstanceID()))
			return false;
		// stock droppers set the flag in Start. variants share the component and the id.
		if (IsStalactiteFamily(build))
			return false;
		if (IsVanillaId(build.id))
			return false;
		return true;
	}

	private static bool IsThornbackElder(BuildingEntity build)
	{
		if (IdIs(build.id, "thornbackelder"))
			return true;
		string name = build.gameObject != null ? build.gameObject.name : null;
		return IdIs(name, "thornbackelder");
	}

	private static bool IsStalactiteFamily(BuildingEntity build)
	{
		if (Contains(build.id, "stalactite"))
			return true;
		string name = build.gameObject != null ? build.gameObject.name : null;
		if (Contains(name, "stalactite"))
			return true;
		return build.GetComponent<StalactiteDropper>() != null;
	}

	private static bool IsVanillaId(string id)
	{
		if (string.IsNullOrEmpty(id))
			return false;
		if (VanillaIds.TryGetValue(id, out bool known))
			return known;
		// stock resources only. a mod asset bundle does not answer Resources.Load.
		bool vanilla = Resources.Load(id) != null;
		VanillaIds[id] = vanilla;
		return vanilla;
	}

	private static bool IdIs(string value, string id)
	{
		if (string.IsNullOrEmpty(value))
			return false;
		if (string.Equals(value, id, System.StringComparison.OrdinalIgnoreCase))
			return true;
		// Instantiate names the clone "thornbackelder(Clone)"
		return value.StartsWith(id, System.StringComparison.OrdinalIgnoreCase)
			&& value.Length > id.Length
			&& value[id.Length] == '(';
	}

	private static bool Contains(string value, string part)
	{
		return !string.IsNullOrEmpty(value)
			&& value.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static void TrackKept(BuildingEntity build, HashSet<int> claimedBuildings, HashSet<int> claimedTransforms)
	{
		if (build == null)
			return;
		string id = string.IsNullOrEmpty(build.id) ? build.gameObject.name : build.id;
		if (!string.IsNullOrEmpty(id) && KeptIds.Add(id))
			Plugin.Log.LogInfo("body resident - keep id=" + id);
		Track(build.transform, claimedBuildings, claimedTransforms);
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
		System.Type found = TypeLookup.Find("NewDescents.ElderWallStriderBehaviour");
		if (found == null && !TypeLookup.PluginsReady)
			return;
		_striderLooked = true;
		_striderType = found;
	}

	private static void EnsureGraboidType()
	{
		if (_graboidType != null || _graboidAbsent)
			return;
		_graboidType = TypeLookup.Find("GraboidBehaviour");
		// miss stays a miss once every plugin has had Awake. before that, Find is cached anyway.
		if (_graboidType == null && TypeLookup.PluginsReady)
			_graboidAbsent = true;
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
