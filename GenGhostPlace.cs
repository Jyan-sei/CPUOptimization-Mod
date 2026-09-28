using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// during gen we only need Ground colliders for the placement casts.
/// outside the cam window, skip Instantiate and write capture records instead.
/// </summary>
internal static class GenGhostPlace
{
	private static bool _armed;
	private static int _keepX0, _keepY0, _keepX1, _keepY1;
	private static int _ghosted;
	private static int _placedLive;

	internal static void Apply(Harmony harmony)
	{
		// after qol's seed prefix (Normal). returning false here skips later prefixes
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "DistributeEntities"),
			prefix: new HarmonyMethod(typeof(GenGhostPlace), nameof(DistributeEntitiesPrefix))
			{
				priority = QolSeed.AfterOtherPrefixes
			});

		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "GenerateEntityAtPos"),
			prefix: new HarmonyMethod(typeof(GenGhostPlace), nameof(GenerateEntityAtPosPrefix)));

		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "DistributeMiniBarrels"),
			postfix: new HarmonyMethod(typeof(GenGhostPlace), nameof(DistributeMiniBarrelsPostfix)));

		// UpdateWorld already painted the tiles, bake colliders before placement casts
		harmony.Patch(
			AccessTools.Method(typeof(WorldGeneration), "UpdateWorld"),
			postfix: new HarmonyMethod(typeof(GenGhostPlace), nameof(UpdateWorldPostfix)));
	}

	internal static bool Active =>
		_armed
		&& Plugin.StreamEnabled != null
		&& Plugin.StreamEnabled.Value
		&& WorldGeneration.world != null
		&& WorldGeneration.world.generatingWorld;

	private static void ArmFromPlayer(WorldGeneration world)
	{
		if (world == null)
			return;

		Vector3 cam = Vector3.zero;
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			cam = PlayerCamera.main.body.transform.position;
		else if (PlayerCamera.main != null)
			cam = PlayerCamera.main.transform.position;

		StreamController.GetWindowAround(world, cam, out _keepX0, out _keepY0, out _keepX1, out _keepY1);
		_armed = true;

		if (!CaptureStore.GhostFilled)
		{
			CaptureStore.BiomeDepth = world.biomeDepth;
			CaptureStore.ChunkWidth = world.chunkWidth;
			CaptureStore.ChunkHeight = world.chunkHeight;
		}
	}

	internal static void Reset()
	{
		_armed = false;
		_ghosted = _placedLive = 0;
		StructureBgLog.ResetCounts();
	}

	internal static void LogSummary()
	{
		if (_ghosted != 0 || _placedLive != 0)
		{
			Plugin.Log.LogInfo(
				$"gen ghost-place — skipped Instantiates outside {StreamWindowPolicy.FormatSpans()}: ghosted={_ghosted} livePlaced={_placedLive} " +
				$"keep={_keepX0},{_keepY0}..{_keepX1},{_keepY1}");
		}
		StructureBgLog.DumpGen();
	}

	private static bool InKeepWindow(WorldGeneration world, Vector3 worldPos)
	{
		Vector2Int c = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));
		return c.x >= _keepX0 && c.x <= _keepX1 && c.y >= _keepY0 && c.y <= _keepY1;
	}

	private static void UpdateWorldPostfix(WorldGeneration __instance)
	{
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return;
		if (__instance == null || !__instance.generatingWorld)
			return;

		ArmFromPlayer(__instance);
		EnsureColliders(__instance);
	}

	internal static void EnsureColliders(WorldGeneration world)
	{
		Tilemap[,] chunks = WorldArrays.Chunks(world);
		if (chunks == null)
			return;

		int w = chunks.GetLength(0);
		int h = chunks.GetLength(1);
		for (int x = 0; x < w; x++)
		for (int y = 0; y < h; y++)
		{
			Tilemap map = chunks[x, y];
			if (map == null)
				continue;
			TilemapCollider2D col = map.GetComponent<TilemapCollider2D>();
			if (col != null && !col.enabled)
				col.enabled = true;
			Rigidbody2D rb = map.GetComponent<Rigidbody2D>();
			if (rb != null)
				rb.simulated = true;
		}
		Physics2D.SyncTransforms();
	}

	private static bool DistributeEntitiesPrefix(
		WorldGeneration __instance,
		GameObject basObj,
		float minPerChunk,
		float maxPerChunk,
		float spawnYOffset,
		float randomRotation,
		float spawnYOffsetDeviation,
		bool spawnInGround,
		bool randomFlip,
		WorldGeneration.PlaceCheckDelegate checkFunc,
		bool isTrap,
		Vector2 dir,
		bool forceFlip)
	{
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return true;
		if (__instance == null || !__instance.generatingWorld || basObj == null)
			return true;

		QolSeed.NoteIfActive();
		ArmFromPlayer(__instance);
		EnsureColliders(__instance);

		if (dir == Vector2.zero)
			dir = Vector2.down;

		float attempts = (float)(__instance.chunkWidth * __instance.chunkHeight)
			* UnityEngine.Random.Range(minPerChunk, maxPerChunk);
		int ground = LayerMask.GetMask("Ground");
		float trapYMax = (float)__instance.halfHeight;
		if (isTrap && PlayerCamera.main != null && PlayerCamera.main.body != null)
			trapYMax = PlayerCamera.main.body.transform.position.y - 5f;

		for (int i = 0; i < attempts; i++)
		{
			Vector2 probe = new Vector2(
				UnityEngine.Random.Range(0L - (long)__instance.halfWidth, __instance.halfWidth),
				UnityEngine.Random.Range(0L - (long)__instance.halfHeight, trapYMax));

			if (!(!Physics2D.OverlapPoint(probe, ground) || spawnInGround))
				continue;

			RaycastHit2D hit = Physics2D.Raycast(probe, dir, WorldGeneration.CHUNKSIZE, ground);
			if (!hit)
				continue;
			if (Mathf.Abs(hit.point.x) >= (float)__instance.halfWidth - 1f
				|| Mathf.Abs(hit.point.y) >= (float)__instance.halfHeight - 1f)
				continue;
			if (checkFunc != null && !checkFunc(__instance.WorldToBlockPos(hit.point - Vector2.up * 0.5f)))
				continue;

			float offset = UnityEngine.Random.Range(
				spawnYOffset - spawnYOffsetDeviation,
				spawnYOffset + spawnYOffsetDeviation);
			Vector3 pos = hit.point - dir * offset;
			float rotZ = basObj.transform.eulerAngles.z
				+ UnityEngine.Random.Range(0f - randomRotation, randomRotation);
			bool negX = forceFlip || (randomFlip && UnityEngine.Random.Range(0f, 1f) > 0.5f);

			Vector2Int support = __instance.WorldToBlockPos(hit.point + dir * 0.5f);

			if (InKeepWindow(__instance, pos))
			{
				GameObject go = UnityEngine.Object.Instantiate(
					basObj, pos, Quaternion.Euler(0f, 0f, rotZ));
				BuildingEntity build = go.GetComponent<BuildingEntity>();
				if (build != null)
				{
					build.blockPlacedOn = support;
					if (build.requireGround)
					{
						__instance.ChunkUpdated[support.x / WorldGeneration.CHUNKSIZE, support.y / WorldGeneration.CHUNKSIZE]
							.AddListener(build.CheckSeating);
					}
				}
				if (negX)
					go.transform.localScale = new Vector3(-1f, 1f, 1f);
				_placedLive++;
			}
			else
			{
				GhostPrefab(__instance, basObj, pos, rotZ, negX, support);
				_ghosted++;
			}
		}

		return false;
	}

	private static bool GenerateEntityAtPosPrefix(WorldGeneration __instance, Vector2 pos, GameObject basObj)
	{
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return true;
		if (__instance == null || !__instance.generatingWorld || basObj == null)
			return true;

		ArmFromPlayer(__instance);

		Vector2 origin = pos - Vector2.one * 0.5f;
		string prefabPath = StructureCatalog.PathForInstance(basObj);
		foreach (Transform child in basObj.transform)
		{
			bool tilemap = child.GetComponent<Tilemap>() != null;
			bool placedKind = child.GetComponent<Item>() != null
				|| child.GetComponent<BuildingEntity>() != null
				|| child.gameObject.name == "DOSPAWN";
			// tilemap with no entity. stock skips these. Back (medical) is a real interior.
			// Map on lifepod/biocontainer is the block stamp, already in worldBlocks.
			bool interior = tilemap && !placedKind && !StructureCatalog.IsBlockStamp(basObj, child);
			Vector3 worldPos = origin + (Vector2)child.localPosition;
			if (!placedKind && !interior)
			{
				if (StructureBgLog.Watch(child.name, tilemap))
					StructureBgLog.Gen("SKIP", basObj, prefabPath, child, worldPos, __instance);
				continue;
			}

			float rotZ = child.localRotation.eulerAngles.z;

			if (InKeepWindow(__instance, worldPos))
			{
				GameObject go = UnityEngine.Object.Instantiate(
					child.gameObject, worldPos, child.localRotation);
				if (go.GetComponent<Tilemap>() != null && __instance.worldGrid != null)
					go.transform.SetParent(__instance.worldGrid.transform, true);
				if (StructureBgLog.Watch(child.name, tilemap))
					StructureBgLog.Gen("LIVE", basObj, prefabPath, child, worldPos, __instance);
				_placedLive++;
			}
			else
			{
				string id = Clean(child.name);
				// keep THIS pod's prefab. "background" also lives on lifepod and first-match grabs that
				if (StructureBgLog.Watch(child.name, tilemap))
					StructureBgLog.Gen("GHOST", basObj, prefabPath, child, worldPos, __instance);

				Vector2Int chunk = __instance.BlockToChunkPos(__instance.WorldToBlockPos(worldPos));
				BuildingEntity build = child.GetComponent<BuildingEntity>();
				Item item = child.GetComponent<Item>();
				if (item != null && build == null)
				{
					CaptureStore.WorldItems.Add(new WorldItemRecord
					{
						Chunk = chunk,
						Id = string.IsNullOrEmpty(item.id) ? id : item.id,
						Condition = item.condition,
						Position = worldPos,
						RotationZ = rotZ,
						LocalScale = child.localScale,
					});
					CaptureStore.GhostItemCount++;
				}
				else
				{
					CaptureStore.Entities.Add(new EntityRecord
					{
						Chunk = chunk,
						Id = id,
						Health = build != null ? build.health : 0f,
						// no BuildingEntity on Map/Back. requireGround would be a lie and seating never runs
						RequireGround = interior ? false : (build == null || build.requireGround),
						Position = worldPos,
						RotationZ = rotZ,
						StructurePrefab = prefabPath,
					});
					CaptureStore.GhostEntityCount++;
				}
				CaptureStore.GhostFilled = true;
				_ghosted++;
			}
		}

		return false;
	}

	private static void DistributeMiniBarrelsPostfix(WorldGeneration __instance)
	{
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return;
		if (__instance == null)
			return;
		ArmFromPlayer(__instance);
		CullLiveOutsideWindow(__instance);
	}

	/// <summary>
	/// catch Instantiates that skipped DistributeEntities (bandages, ropes, remote traders, mini-barrels)
	/// </summary>
	internal static void CullLiveOutsideWindow(WorldGeneration world)
	{
		if (world == null)
			return;
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return;

		ArmFromPlayer(world);

		System.Collections.Generic.HashSet<long> outside = new System.Collections.Generic.HashSet<long>();
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			if (x < _keepX0 || x > _keepX1 || y < _keepY0 || y > _keepY1)
				outside.Add(((long)x << 32) ^ (uint)y);
		}

		if (outside.Count == 0)
			return;

		// append live stuff into the arrays then destroy. dont wipe ghost slices
		ChunkObjects.AppendWritebackAndDestroy(world, outside);
	}

	private static void GhostPrefab(WorldGeneration world, GameObject prefab, Vector3 pos, float rotZ, bool negX, Vector2Int supportBlock)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		string id = PrefabId(prefab);
		BuildingEntity build = prefab.GetComponent<BuildingEntity>();
		TraderScript trader = prefab.GetComponent<TraderScript>();
		Item item = prefab.GetComponent<Item>();
		SpiderHandler spider = prefab.GetComponent<SpiderHandler>();

		CaptureStore.GhostFilled = true;

		if (trader != null)
		{
			int character = trader.character;
			if (character <= 0)
			{
				// trader1/2/3
				for (int c = 1; c <= 3; c++)
				{
					if (prefab.name.IndexOf(c.ToString(), StringComparison.Ordinal) >= 0)
					{
						character = c;
						break;
					}
				}
			}
			float x = pos.x;
			CaptureStore.Traders.Add(new TraderRecord
			{
				Chunk = chunk,
				Position = pos,
				Health = build != null ? build.health : 0f,
				Character = character > 0 ? character : 1,
				Reputation = 100f,
				Hostility = 0f,
				ValueGiven = 0,
				MoveRangeMin = x - 5f,
				MoveRangeMax = x + 5f,
			});
			CaptureStore.GhostTraderCount++;
			return;
		}

		if (spider != null || (build != null && build.animal))
		{
			CaptureStore.Enemies.Add(new EnemyRecord
			{
				Chunk = chunk,
				Id = id,
				Health = build != null ? build.health : 0f,
				RequireGround = build == null || build.requireGround,
				Position = pos,
				RotationZ = rotZ,
				ScaleNegX = negX ? true : (bool?)null,
			});
			CaptureStore.GhostEnemyCount++;
			return;
		}

		if (item != null && build == null)
		{
			CaptureStore.WorldItems.Add(new WorldItemRecord
			{
				Chunk = chunk,
				Id = string.IsNullOrEmpty(item.id) ? id : item.id,
				Condition = item.condition,
				Position = pos,
				RotationZ = rotZ,
				LocalScale = negX ? new Vector3(-1f, 1f, 1f) : prefab.transform.localScale,
			});
			CaptureStore.GhostItemCount++;
			return;
		}

		CaptureStore.Entities.Add(new EntityRecord
		{
			Chunk = chunk,
			Id = id,
			Health = build != null ? build.health : 0f,
			RequireGround = build == null || build.requireGround,
			Position = pos,
			RotationZ = rotZ,
			ScaleNegX = negX ? true : (bool?)null,
			HasBlockPlacedOn = build != null,
			BlockPlacedOn = supportBlock,
		});
		CaptureStore.GhostEntityCount++;
	}

	private static string PrefabId(GameObject prefab)
	{
		return ChunkObjects.PrefabIdFor(prefab, prefab != null ? prefab.GetComponent<BuildingEntity>() : null);
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
