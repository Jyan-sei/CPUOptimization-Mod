using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// loading screen off: the block array still fills in order, but only the spawn
/// window is painted. ground queries during gen read worldBlocks, so structures
/// keep their spots without a collider on every chunk.
/// chunks outside that window paint the first time the stream shows them.
/// </summary>
internal static class InstantLoad
{
	private static bool _genArmed;
	private static bool _defer;
	private static bool _allowPaint;
	private static readonly HashSet<long> _painted = new HashSet<long>();
	private static int _groundMask = int.MinValue;
	private static BoxCollider2D _probe;

	internal static bool Active =>
		Plugin.LayerLoadOn
		&& _genArmed
		&& !_allowPaint
		&& Plugin.StreamEnabled != null && Plugin.StreamEnabled.Value
		&& Plugin.LoadingScreen != null && !Plugin.LoadingScreen.Value
		&& MpSession.AllowStream
		&& WorldGeneration.world != null
		&& WorldGeneration.world.generatingWorld
		&& WorldGeneration.world.biomeOverride != WorldGeneration.OverrideSceneType.Tutorial;

	private static bool SuppressPaint => Active && !_allowPaint;

	internal static void OnGenBegan()
	{
		if (_genArmed)
			return;
		_genArmed = true;
		_defer = false;
		_allowPaint = false;
		_painted.Clear();
		HideProbe();
	}

	internal static void OnGenFinished()
	{
		_genArmed = false;
		_allowPaint = false;
		HideProbe();
	}

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "UpdateWorld"),
			prefix: new HarmonyMethod(typeof(InstantLoad), nameof(AllowPaintPrefix)));
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "UpdateChunk", new[] { typeof(Vector2Int) }),
			prefix: new HarmonyMethod(typeof(InstantLoad), nameof(AllowPaintPrefix)));

		MethodInfo place = AccessTools.Method(typeof(WorldGeneration), "WorldPlacePlayer", System.Type.EmptyTypes);
		MethodInfo move = place != null ? AccessTools.EnumeratorMoveNext(place) : null;
		if (move != null)
		{
			harmony.Patch(move, postfix: new HarmonyMethod(typeof(InstantLoad), nameof(AfterPlace)));
		}
		else
			Plugin.Log.LogError("WorldPlacePlayer iterator was not found.");

		PatchGround(harmony, AccessTools.Method(typeof(Physics2D), "Raycast", new[]
		{
			typeof(Vector2), typeof(Vector2), typeof(float), typeof(int)
		}), nameof(RaycastPrefix), nameof(RaycastPostfix));
		PatchGround(harmony, AccessTools.Method(typeof(Physics2D), "OverlapPoint", new[]
		{
			typeof(Vector2), typeof(int)
		}), nameof(OverlapPointPrefix), null);
		PatchGround(harmony, AccessTools.Method(typeof(Physics2D), "OverlapBox", new[]
		{
			typeof(Vector2), typeof(Vector2), typeof(float), typeof(int)
		}), nameof(OverlapBoxPrefix), null);

		harmony.Patch(
			AccessTools.Method(typeof(GlobalDark), "Darken"),
			prefix: new HarmonyMethod(typeof(InstantLoad), nameof(SkipDarkenPrefix)),
			postfix: new HarmonyMethod(typeof(InstantLoad), nameof(ClearDarkenPostfix)));
		harmony.Patch(
			AccessTools.Method(typeof(GlobalDark), "Update"),
			prefix: new HarmonyMethod(typeof(InstantLoad), nameof(HoldClearPrefix)));
	}

	private static FieldInfo _darkeningField;
	private static FieldInfo _darkProgressField;

	// the fade paints in Update from a flag Darken sets. skip the flag and clear the image.
	private static bool SuppressReveal()
	{
		if (Plugin.LoadingScreen != null && Plugin.LoadingScreen.Value)
			return false;
		return LayerLoadLog.Open;
	}

	private static bool SkipDarkenPrefix(GlobalDark __instance)
	{
		if (!SuppressReveal())
			return true;
		ClearDark(__instance);
		Plugin.Log.LogInfo("layer load darken skipped");
		return false;
	}

	private static void ClearDarkenPostfix(GlobalDark __instance)
	{
		if (!SuppressReveal())
			return;
		ClearDark(__instance);
	}

	private static void HoldClearPrefix(GlobalDark __instance)
	{
		if (!SuppressReveal())
			return;
		ClearDark(__instance);
	}

	private static void ClearDark(GlobalDark dark)
	{
		if (dark == null)
			return;
		if (_darkeningField == null)
			_darkeningField = AccessTools.Field(typeof(GlobalDark), "darkening");
		if (_darkProgressField == null)
			_darkProgressField = AccessTools.Field(typeof(GlobalDark), "darkProgress");
		if (_darkeningField != null)
			_darkeningField.SetValue(dark, false);
		if (_darkProgressField != null)
			_darkProgressField.SetValue(dark, 0f);
		if (dark.image != null)
		{
			Color c = dark.image.color;
			c.a = 0f;
			dark.image.color = c;
		}
	}

	internal static void NotePainted(int cx, int cy)
	{
		_painted.Add(Key(cx, cy));
	}

	/// <summary>columns written after the spawn paint. the tilemap still shows the old air.</summary>
	internal static void RefreshPainted(WorldGeneration world, int blockX0, int blockX1)
	{
		if (!_defer || world == null || blockX1 <= blockX0)
			return;
		int size = WorldGeneration.CHUNKSIZE;
		if (size <= 0)
			return;
		int cx0 = blockX0 / size;
		int cx1 = (blockX1 - 1) / size;
		int chunkW = (int)world.chunkWidth;
		int chunkH = (int)world.chunkHeight;
		_allowPaint = true;
		try
		{
			for (int cx = cx0; cx <= cx1; cx++)
			{
				if (cx < 0 || cx >= chunkW)
					continue;
				for (int cy = 0; cy < chunkH; cy++)
				{
					if (!_painted.Contains(Key(cx, cy)))
						continue;
					world.UpdateChunk(new Vector2Int(cx, cy));
				}
			}
		}
		finally
		{
			_allowPaint = false;
		}
	}

	/// <summary>a chunk the stream is about to show. paint it once from the block array.</summary>
	internal static void EnsurePainted(WorldGeneration world, int cx, int cy)
	{
		if (!_defer || world == null)
			return;
		long key = Key(cx, cy);
		if (_painted.Contains(key))
			return;
		_painted.Add(key);
		_allowPaint = true;
		try
		{
			world.UpdateChunk(new Vector2Int(cx, cy));
		}
		finally
		{
			_allowPaint = false;
		}
	}

	internal static bool SolidAt(WorldGeneration world, Vector2 worldPos)
	{
		if (world == null)
			return false;
		Vector2Int block = world.WorldToBlockPos(worldPos);
		return Solid(world, block.x, block.y);
	}

	internal static bool Ray(WorldGeneration world, Vector2 origin, Vector2 direction, float distance, out Vector2 point)
	{
		point = origin;
		if (world == null || direction.sqrMagnitude < 0.000001f || distance <= 0f)
			return false;
		direction.Normalize();
		if (SolidAt(world, origin))
			return true;

		float traveled = 0f;
		Vector2 p = origin;
		int guard = 0;
		while (traveled < distance && guard++ < 4096)
		{
			Vector2Int cell = world.WorldToBlockPos(p);
			Vector2 center = world.BlockToWorldPos(cell);
			float dx = NextBoundary(p.x, center.x, direction.x);
			float dy = NextBoundary(p.y, center.y, direction.y);
			float step = dx < dy ? dx : dy;
			if (step < 0.0001f)
				step = 0.0001f;
			if (traveled + step > distance)
				return false;
			traveled += step;
			p += direction * step;
			// step onto the far side of the boundary, into the next cell
			p += direction * 0.001f;
			traveled += 0.001f;
			if (SolidAt(world, p))
			{
				point = p;
				return true;
			}
		}
		return false;
	}

	private static void PatchGround(Harmony harmony, MethodInfo method, string prefix, string postfix)
	{
		if (method == null)
		{
			Plugin.Log.LogWarning("instant load missed a Physics2D method (" + prefix + ")");
			return;
		}
		harmony.Patch(
			method,
			prefix: new HarmonyMethod(typeof(InstantLoad), prefix),
			postfix: postfix != null ? new HarmonyMethod(typeof(InstantLoad), postfix) : null);
	}

	private static bool AllowPaintPrefix()
	{
		return !SuppressPaint;
	}

	private static void AfterPlace(object __instance, bool __result)
	{
		if (__result || !Active)
			return;
		WorldGeneration world = __instance as WorldGeneration;
		if (world == null)
		{
			FieldInfo self = __instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			world = self?.GetValue(__instance) as WorldGeneration;
		}
		if (world == null || PlayerCamera.main == null || PlayerCamera.main.body == null)
			return;

		Vector3 pos = PlayerCamera.main.body.transform.position;
		StreamController.GetWindowAround(world, pos, out int x0, out int y0, out int x1, out int y1);
		int n = 0;
		_allowPaint = true;
		try
		{
			for (int x = x0; x <= x1; x++)
			for (int y = y0; y <= y1; y++)
			{
				world.UpdateChunk(new Vector2Int(x, y));
				_painted.Add(Key(x, y));
				n++;
				EnableCollider(world, x, y);
			}
		}
		finally
		{
			_allowPaint = false;
		}
		_defer = true;
		Physics2D.SyncTransforms();

		if (world.loadingObject != null)
			world.loadingObject.SetActive(false);
		LayerLoadLog.NoteScreenDropped();
		Plugin.Log.LogInfo(
			"layer load instant paint chunks=" + n
			+ " window=" + x0 + "," + y0 + ".." + x1 + "," + y1);
	}

	private static void EnableCollider(WorldGeneration world, int cx, int cy)
	{
		Tilemap[,] chunks = WorldArrays.Chunks(world);
		if (chunks == null || cx < 0 || cy < 0 || cx >= chunks.GetLength(0) || cy >= chunks.GetLength(1))
			return;
		Tilemap map = chunks[cx, cy];
		if (map == null)
			return;
		TilemapCollider2D col = map.GetComponent<TilemapCollider2D>();
		if (col != null && !col.enabled)
			col.enabled = true;
		Rigidbody2D rb = map.GetComponent<Rigidbody2D>();
		if (rb != null)
			rb.simulated = true;
	}

	private static bool RaycastPrefix(Vector2 origin, Vector2 direction, float distance, int layerMask, ref RaycastHit2D __result)
	{
		if (!GroundOnly(layerMask))
			return true;
		if (!Ray(WorldGeneration.world, origin, direction, distance, out Vector2 point))
		{
			__result = default;
			return false;
		}
		WorldGeneration world = WorldGeneration.world;
		Vector2Int cell = world.WorldToBlockPos(point);
		// inset from the cell edge so the hit lands in this block, not the next one
		PlaceProbe(world.BlockToWorldPos(cell));
		return true;
	}

	private static void RaycastPostfix()
	{
		HideProbe();
	}

	private static bool OverlapPointPrefix(Vector2 point, int layerMask, ref Collider2D __result)
	{
		if (!GroundOnly(layerMask))
			return true;
		if (!SolidAt(WorldGeneration.world, point))
		{
			__result = null;
			return false;
		}
		__result = PlaceProbe(point);
		HideProbe();
		return false;
	}

	private static bool OverlapBoxPrefix(Vector2 point, Vector2 size, float angle, int layerMask, ref Collider2D __result)
	{
		if (!GroundOnly(layerMask) || Mathf.Abs(angle) > 0.01f)
			return true;
		if (!BoxHitsSolid(WorldGeneration.world, point, size))
		{
			__result = null;
			return false;
		}
		__result = PlaceProbe(point);
		HideProbe();
		return false;
	}

	private static bool BoxHitsSolid(WorldGeneration world, Vector2 center, Vector2 size)
	{
		if (world == null)
			return false;
		Vector2 half = size * 0.5f;
		Vector2Int a = world.WorldToBlockPos(center - half);
		Vector2Int b = world.WorldToBlockPos(center + half);
		int x0 = Mathf.Min(a.x, b.x);
		int x1 = Mathf.Max(a.x, b.x);
		int y0 = Mathf.Min(a.y, b.y);
		int y1 = Mathf.Max(a.y, b.y);
		for (int x = x0; x <= x1; x++)
		for (int y = y0; y <= y1; y++)
		{
			if (Solid(world, x, y))
				return true;
		}
		return false;
	}

	private static bool Solid(WorldGeneration world, int x, int y)
	{
		ushort[,] blocks = WorldArrays.WorldBlocks(world);
		if (blocks == null)
			return false;
		if (x < 0 || y < 0 || x >= blocks.GetLength(0) || y >= blocks.GetLength(1))
			return false;
		ushort id = blocks[x, y];
		if (id == 0 || world.tiles == null || id >= world.tiles.Length)
			return false;
		Tile tile = world.tiles[id] as Tile;
		if (tile == null)
			return true;
		return tile.colliderType != Tile.ColliderType.None;
	}

	private static bool GroundOnly(int layerMask)
	{
		if (!Active)
			return false;
		if (_groundMask == int.MinValue)
			_groundMask = LayerMask.GetMask("Ground");
		if (_groundMask == 0)
			return false;
		return (layerMask & ~_groundMask) == 0 && (layerMask & _groundMask) != 0;
	}

	private static float NextBoundary(float pos, float center, float dir)
	{
		if (dir > 0.001f)
			return ((center + 0.5f) - pos) / dir;
		if (dir < -0.001f)
			return ((center - 0.5f) - pos) / dir;
		return float.MaxValue;
	}

	private static Collider2D PlaceProbe(Vector2 worldPos)
	{
		if (_probe == null)
		{
			var go = new GameObject("CPUOpt2GroundProbe");
			go.hideFlags = HideFlags.HideAndDontSave;
			int layer = LayerMask.NameToLayer("Ground");
			if (layer >= 0)
				go.layer = layer;
			_probe = go.AddComponent<BoxCollider2D>();
			_probe.size = new Vector2(0.96f, 0.96f);
		}
		_probe.transform.position = worldPos;
		_probe.enabled = true;
		return _probe;
	}

	private static void HideProbe()
	{
		if (_probe != null)
			_probe.enabled = false;
	}

	private static long Key(int x, int y)
	{
		return ((long)x << 32) | (uint)y;
	}
}
