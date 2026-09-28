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

		WritebackMatching(world, keys);
		DestroyMatching(world, keys);
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
		DestroyMatching(world, keys);
	}

	/// <summary>
	/// append live objects into CaptureStore then destroy. doesnt wipe ghost records that are already there
	/// </summary>
	internal static void AppendWritebackAndDestroy(WorldGeneration world, HashSet<long> chunkKeys)
	{
		if (chunkKeys == null || chunkKeys.Count == 0)
			return;
		WritebackMatching(world, chunkKeys);
		DestroyMatching(world, chunkKeys);
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
		WritebackMatching(world, chunkKeys);

	internal static void RemoveChunkSlicePublic(int cx, int cy) => RemoveChunkSlice(cx, cy);

	/// <summary>gather GOs DestroyMatching would kill. paced StreamObjectJobs uses this</summary>
	internal static List<GameObject> CollectDestroyTargets(WorldGeneration world, HashSet<long> chunkKeys)
	{
		var kill = new List<GameObject>();
		Transform playerRoot = PlayerRoot();

		BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < buildings.Length; i++)
		{
			BuildingEntity build = buildings[i];
			if (build == null || !InKeys(world, build.transform.position, chunkKeys, out _, out _))
				continue;
			if (IsUnder(playerRoot, build.transform))
				continue;
			if (ElderResident.IsResident(world, build.transform))
				continue;
			// pod interiors are tilemaps. CollectStructureTilemaps decides if they stay
			if (StructureTilemapVault.IsStructureTilemap(build.transform, world))
				continue;
			kill.Add(build.gameObject);
		}

		Item[] items = Object.FindObjectsOfType<Item>();
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null || !InKeys(world, item.transform.position, chunkKeys, out _, out _))
				continue;
			if (IsUnder(playerRoot, item.transform))
				continue;
			if (ElderResident.IsResident(world, item.transform))
				continue;
			kill.Add(item.gameObject);
		}

		Climbable[] climbables = Object.FindObjectsOfType<Climbable>();
		for (int i = 0; i < climbables.Length; i++)
		{
			Climbable climb = climbables[i];
			if (climb == null || !InKeys(world, climb.transform.position, chunkKeys, out _, out _))
				continue;
			if (ElderResident.IsResident(world, climb.transform))
				continue;
			kill.Add(climb.gameObject);
		}

		TraderScript[] traders = Object.FindObjectsOfType<TraderScript>();
		for (int i = 0; i < traders.Length; i++)
		{
			TraderScript trader = traders[i];
			if (trader == null || !InKeys(world, trader.transform.position, chunkKeys, out _, out _))
				continue;
			kill.Add(trader.gameObject);
		}

		CollectStructureTilemaps(world, chunkKeys, kill);
		CollectNamed(world, chunkKeys, "sandvinehook", kill);
		CollectNamed(world, chunkKeys, "droppings", kill);
		CollectOilPipes(world, chunkKeys, kill);
		CollectLights(world, chunkKeys, kill);
		return kill;
	}

	private static void WritebackMatching(WorldGeneration world, HashSet<long> chunkKeys)
	{
		Transform playerRoot = PlayerRoot();
		HashSet<int> claimedItems = new HashSet<int>();
		HashSet<int> claimedBuildings = new HashSet<int>();

		TraderScript[] traders = Object.FindObjectsOfType<TraderScript>();
		for (int i = 0; i < traders.Length; i++)
		{
			TraderScript trader = traders[i];
			if (trader == null || !InKeys(world, trader.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			BuildingEntity build = FieldAccess.Get<BuildingEntity>(trader, "build")
				?? trader.GetComponent<BuildingEntity>();
			if (build != null)
				claimedBuildings.Add(build.GetInstanceID());
			CaptureStore.Traders.Add(FillTrader(cx, cy, trader, build));
		}

		SpiderHandler[] spiders = Object.FindObjectsOfType<SpiderHandler>();
		for (int i = 0; i < spiders.Length; i++)
		{
			SpiderHandler spider = spiders[i];
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
			CaptureStore.Enemies.Add(new EnemyRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = IdOf(spider.gameObject, build),
				Health = build != null ? build.health : 0f,
				RequireGround = build == null || build.requireGround,
				Position = spider.transform.position,
				RotationZ = spider.transform.eulerAngles.z,
				ScaleNegX = spider.transform.localScale.x < 0f ? true : (bool?)null,
			});
		}

		BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < buildings.Length; i++)
		{
			BuildingEntity build = buildings[i];
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
				CaptureStore.Enemies.Add(new EnemyRecord
				{
					Chunk = new Vector2Int(cx, cy),
					Id = IdOf(build.gameObject, build),
					Health = build.health,
					RequireGround = build.requireGround,
					Position = build.transform.position,
					RotationZ = build.transform.eulerAngles.z,
					ScaleNegX = build.transform.localScale.x < 0f ? true : (bool?)null,
				});
				continue;
			}

			claimedBuildings.Add(build.GetInstanceID());
			EntityRecord er = BaseEntity(cx, cy, build.gameObject, build);
			CaptureVineHook(build.gameObject, er);
			ResolveStructurePrefab(build.gameObject, er);
			AttachStructureTilemapVault(world, build.gameObject, er);
			AttachExtras(build, er, claimedItems, playerRoot);
			er.ModState = ModBehaviourState.Capture(build.gameObject);
			CaptureStore.Entities.Add(er);
		}

		Climbable[] climbables = Object.FindObjectsOfType<Climbable>();
		for (int i = 0; i < climbables.Length; i++)
		{
			Climbable climb = climbables[i];
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
			CaptureStore.Entities.Add(er);
		}

		WritebackStructureTilemaps(world, chunkKeys, claimedBuildings, claimedItems, playerRoot);

		Item[] items = Object.FindObjectsOfType<Item>();
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null || claimedItems.Contains(item.GetInstanceID()))
				continue;
			if (!InKeys(world, item.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			if (IsUnder(playerRoot, item.transform))
				continue;
			if (item.transform.parent != null && item.transform.parent.GetComponent<Item>() != null)
				continue;
			WorldItemRecord wir = new WorldItemRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = string.IsNullOrEmpty(item.id) ? Clean(item.gameObject.name) : item.id,
				Condition = item.condition,
				Position = item.transform.position,
				RotationZ = item.transform.eulerAngles.z,
				LocalScale = item.transform.localScale,
			};
			CopyLiquids(item, wir.Liquids);
			BuildingLoot.AppendChildren(item.transform, wir.Children, claimedItems, playerRoot);
			wir.ModState = ModBehaviourState.Capture(item.gameObject);
			CaptureStore.WorldItems.Add(wir);
		}

		// wallholes are WallholeResident. never writeback, destroy, or respawn them
		WritebackNamed(world, chunkKeys, "sandvinehook");
		WritebackOilPipes(world, chunkKeys);
		WritebackLights(world, chunkKeys, claimedBuildings);
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
			if (be != null)
				BuildingLoot.Capture(be, rec, claimedItems, playerRoot);
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
	}

	private static TraderRecord FillTrader(int cx, int cy, TraderScript trader, BuildingEntity build)
	{
		TraderRecord record = new TraderRecord
		{
			Chunk = new Vector2Int(cx, cy),
			Position = trader.transform.position,
			Health = build != null ? build.health : 0f,
			Character = trader.character,
			Reputation = trader.reputation,
			Hostility = trader.hostility,
			ValueGiven = trader.valueGiven,
			MoveRangeMin = trader.MoveRange.min,
			MoveRangeMax = trader.MoveRange.max,
			FarEnoughToMove = trader.farEnoughToMove,
			DidMove = trader.didMove,
		};
		if (trader.items != null)
		{
			for (int j = 0; j < trader.items.Count; j++)
			{
				if (trader.items[j] != null && !string.IsNullOrEmpty(trader.items[j].id))
					record.ItemIds.Add(trader.items[j].id);
			}
		}
		if (record.MoveRangeMin == 0f && record.MoveRangeMax == 0f)
		{
			float x = trader.transform.position.x;
			record.MoveRangeMin = x - 5f;
			record.MoveRangeMax = x + 5f;
		}
		return record;
	}

	private static void ResolveStructurePrefab(GameObject go, EntityRecord record)
	{
		if (StructureCatalog.TryResolve(go, record.Id, out string path))
			record.StructurePrefab = path;
	}

	private static void DestroyMatching(WorldGeneration world, HashSet<long> chunkKeys)
	{
		StreamState.SuppressBuildingDrops = true;
		try
		{
			List<GameObject> kill = CollectDestroyTargets(world, chunkKeys);
			for (int i = 0; i < kill.Count; i++)
			{
				if (kill[i] != null)
				{
					AltarBlessing.Suppress(kill[i]);
					Object.Destroy(kill[i]);
				}
			}
		}
		finally
		{
			StreamState.SuppressBuildingDrops = false;
		}
	}

	private static void CollectStructureTilemaps(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill)
	{
		if (world.worldGrid == null)
			return;
		Transform grid = world.worldGrid.transform;
		for (int i = 0; i < grid.childCount; i++)
		{
			Transform child = grid.GetChild(i);
			if (child == null || !StructureTilemapVault.IsStructureTilemap(child, world))
				continue;
			// corner-pinned. tiles are not on chunk 0,0. leave the object where the mod put it
			if (StructureTilemapVault.IsPinnedCornerTilemap(child))
				continue;
			if (KeepStructureTilemap(world, child.gameObject, chunkKeys))
				continue;
			bool originHere = InKeys(world, child.position, chunkKeys, out _, out _);
			if (!originHere && !StructureTilemapHitsUnload(world, child.gameObject, chunkKeys))
				continue;
			if (StructureBgLog.Watch(child.name, true))
				StructureBgLog.Kill(child.gameObject, originHere);
			kill.Add(child.gameObject);
		}
	}

	/// <summary>
	/// crate/mini/life pod backgrounds are one tilemap whose tiles cross chunk edges.
	/// dont kill it while any of those chunks are still on screen.
	/// </summary>
	private static bool KeepStructureTilemap(WorldGeneration world, GameObject go, HashSet<long> unloadKeys)
	{
		if (go == null || !StructureTilemapVault.IsStructureTilemap(go.transform, world))
			return false;
		Tilemap tm = go.GetComponent<Tilemap>();
		if (tm == null)
			return false;
		if (StreamState.Active)
			return StructureTilemapVault.CoversDesiredChunk(world, tm);
		// gen cull runs before the stream is active. the unload set is everything
		// outside the window, so a pod that still sticks into the window stays
		return StructureTilemapVault.CoversAny(world, tm, (x, y) => !unloadKeys.Contains(Key(x, y)));
	}

	private static bool StructureTilemapHitsUnload(WorldGeneration world, GameObject go, HashSet<long> unloadKeys)
	{
		Tilemap tm = go.GetComponent<Tilemap>();
		if (tm == null)
			return false;
		return StructureTilemapVault.CoversAny(world, tm, (x, y) => unloadKeys.Contains(Key(x, y)));
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

	private static void CollectNamed(WorldGeneration world, HashSet<long> chunkKeys, string key, List<GameObject> kill)
	{
		Transform[] all = Object.FindObjectsOfType<Transform>();
		for (int i = 0; i < all.Length; i++)
		{
			Transform t = all[i];
			if (t == null)
				continue;
			if (Clean(t.name).IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			if (!InKeys(world, t.position, chunkKeys, out _, out _))
				continue;
			kill.Add(t.gameObject);
		}
	}

	private static void CollectLights(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill)
	{
		Light2D ambient = world.ambientLight;
		Light2D[] lights = Object.FindObjectsOfType<Light2D>();
		Transform playerRoot = PlayerRoot();
		for (int i = 0; i < lights.Length; i++)
		{
			Light2D light = lights[i];
			if (!WorldScanner.IsStandaloneWorldLight(light, ambient, playerRoot))
				continue;
			if (!InKeys(world, light.transform.position, chunkKeys, out _, out _))
				continue;
			kill.Add(light.gameObject);
		}
	}

	private static void WritebackNamed(WorldGeneration world, HashSet<long> chunkKeys, string key)
	{
		Transform[] all = Object.FindObjectsOfType<Transform>();
		for (int i = 0; i < all.Length; i++)
		{
			Transform t = all[i];
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
			CaptureStore.Entities.Add(new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = name,
				Position = t.position,
				RotationZ = t.eulerAngles.z,
				Color = sr != null ? sr.color : (Color?)null,
				FlipX = sr != null ? sr.flipX : (bool?)null,
				Pitch = audio != null ? audio.pitch : (float?)null,
				LengthScale = t.localScale.x,
			});
		}
	}

	/// <summary>spawn one matching trader from Index on. bumps Index. false means the list is done</summary>
	internal static bool SpawnNextTrader(int cx, int cy, ref int index)
	{
		while (index < CaptureStore.Traders.Count)
		{
			TraderRecord rec = CaptureStore.Traders[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			GameObject prefab = Resources.Load<GameObject>("trader" + rec.Character);
			if (prefab == null)
			{
				Plugin.Log.LogWarning($"stream spawn missing trader{rec.Character}");
				return true;
			}
			GameObject go = Object.Instantiate(prefab, rec.Position, Quaternion.identity);
			ScreenCull.RegisterGameObject(go);
			TraderScript trader = go.GetComponent<TraderScript>();
			BuildingEntity build = go.GetComponent<BuildingEntity>();
			if (build != null)
			{
				build.health = rec.Health;
				if (rec.Hostility >= 100f)
					build.cantHit = false;
			}
			if (trader != null)
			{
				StreamState.SkipTraderStart.Add(trader.GetInstanceID());
				trader.character = rec.Character;
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
				FieldAccess.Set(trader, "desiredPos", (Vector2)rec.Position);

				if (rec.ItemIds != null && rec.ItemIds.Count > 0)
				{
					trader.items = new List<TraderItem>();
					for (int j = 0; j < rec.ItemIds.Count; j++)
					{
						string id = rec.ItemIds[j];
						if (string.IsNullOrEmpty(id))
							continue;
						ItemInfo info = Item.GetItem(id);
						trader.items.Add(new TraderItem
						{
							id = id,
							bought = false,
							value = info != null ? info.DefaultValue() : 0,
							preference = TraderScript.TraderItemPreference.Indifferent,
						});
					}
				}
			}
			return true;
		}
		return false;
	}

	internal static bool SpawnNextEnemy(int cx, int cy, ref int index)
	{
		while (index < CaptureStore.Enemies.Count)
		{
			EnemyRecord rec = CaptureStore.Enemies[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			GameObject go = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ, inactive: true);
			if (go == null)
				return true;
			WakeSeating(go, rec.RequireGround, rec.Health, false, default);
			if (rec.ScaleNegX == true)
			{
				Vector3 s = go.transform.localScale;
				go.transform.localScale = new Vector3(-Mathf.Abs(s.x), s.y, s.z);
			}
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

		GameObject loaded = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ, inactive: true);
		if (loaded != null)
			via = via == "none" ? "resources" : via + "+resources";
		return loaded;
	}

	internal static bool SpawnNextEntity(int cx, int cy, ref int index)
	{
		while (index < CaptureStore.Entities.Count)
		{
			EntityRecord rec = CaptureStore.Entities[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			if (WallholeResident.IsWallholeName(rec.Id))
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
				return true;
			}

			GameObject go = SpawnEntityObject(rec, out string via);
			if (go == null)
			{
				if (watchBg)
					StructureBgLog.Spawned(rec, via, null);
				return true;
			}
			if (rec.MagmaPipe)
				ApplyMagmaPipe(go);
			// still inactive. CheckSeating runs from OnBecameVisible and zeros health if requireGround is on
			WakeSeating(go, rec.RequireGround, rec.Health, rec.HasBlockPlacedOn, rec.BlockPlacedOn);
			if (watchBg || go.GetComponent<Tilemap>() != null)
				StructureBgLog.Spawned(rec, via, go);
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
					sr.size = rec.SpriteSize.Value;
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
			BuildingLoot.Apply(go.GetComponent<BuildingEntity>(), rec);
			SpawnItemTree(go.transform, rec.NestedItems);
			return true;
		}
		return false;
	}

	private static void WritebackLights(WorldGeneration world, HashSet<long> chunkKeys, HashSet<int> claimedBuildings)
	{
		_ = claimedBuildings;
		Light2D ambient = world.ambientLight;
		Light2D[] lights = Object.FindObjectsOfType<Light2D>();
		Transform playerRoot = PlayerRoot();
		for (int i = 0; i < lights.Length; i++)
		{
			Light2D light = lights[i];
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
					go = Object.Instantiate(prefab, rec.Position, Quaternion.Euler(0f, 0f, rec.RotationZ));
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
			light.intensity = rec.Intensity;
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

	internal static bool SpawnNextWorldItem(int cx, int cy, ref int index)
	{
		while (index < CaptureStore.WorldItems.Count)
		{
			WorldItemRecord rec = CaptureStore.WorldItems[index++];
			if (rec.Chunk.x != cx || rec.Chunk.y != cy)
				continue;
			GameObject go = InstantiatePrefab(rec.Id, rec.Position, rec.RotationZ);
			if (go == null)
				return true;
			if (rec.LocalScale.HasValue)
				go.transform.localScale = rec.LocalScale.Value;
			Item item = go.GetComponent<Item>();
			if (item != null)
				item.condition = rec.Condition;
			WaterContainerItem water = go.GetComponent<WaterContainerItem>();
			if (water != null && rec.Liquids != null)
			{
				for (int L = 0; L < rec.Liquids.Count; L++)
					water.AddLiquid(rec.Liquids[L].LiquidId, rec.Liquids[L].Amount);
			}
			// spawn scripts are added in Item.Start, which has not run yet
			ModBehaviourState.Expect(go, rec.ModState);
			SpawnItemTree(go.transform, rec.Children);
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
			_registryLooked = true;
			System.Type type = AccessTools.TypeByName("CUCoreLib.Helpers.CustomInstantiate");
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

	private static GameObject InstantiatePrefab(string id, Vector3 pos, float rotZ, bool inactive = false)
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
		if (prefab == null)
		{
			if (id.IndexOf("climbingrope", System.StringComparison.OrdinalIgnoreCase) >= 0)
			{
				GameObject rope = Utils.Create(id, (Vector2)pos, rotZ);
				ScreenCull.RegisterGameObject(rope);
				return rope;
			}
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
			return Object.Instantiate(prefab, pos, rot);
		}
		finally
		{
			if (was)
				prefab.SetActive(true);
		}
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
		Vector3 pos = child.HasPose ? child.Position : parent.position;
		float rot = child.HasPose ? child.RotationZ : parent.eulerAngles.z;
		GameObject itemGo = InstantiatePrefab(child.Id, pos, rot);
		if (itemGo == null)
			return;
		if (child.LocalScale.HasValue)
			itemGo.transform.localScale = child.LocalScale.Value;
		itemGo.transform.SetParent(parent, true);
		Item item = itemGo.GetComponent<Item>();
		if (item != null)
			item.condition = child.Condition;
		WaterContainerItem water = itemGo.GetComponent<WaterContainerItem>();
		if (water != null && child.Liquids != null)
		{
			for (int L = 0; L < child.Liquids.Count; L++)
			{
				if (child.Liquids[L] == null)
					continue;
				water.AddLiquid(child.Liquids[L].LiquidId, child.Liquids[L].Amount);
			}
		}
		ModBehaviourState.Expect(itemGo, child.ModState);
		SpawnItemTree(itemGo.transform, child.Children);
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
			er.AnimalCorpse = corpse.animalCorpse;

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

	private static void CopyLiquids(Item item, List<LiquidRecord> into)
	{
		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water == null || water.stack == null)
			return;
		for (int i = 0; i < water.stack.Count; i++)
		{
			LiquidStack stack = water.stack[i];
			if (stack != null)
				into.Add(new LiquidRecord { LiquidId = stack.liquidId, Amount = stack.amount });
		}
	}

	private static void RemoveChunkSlice(int cx, int cy)
	{
		CaptureStore.Traders.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.Enemies.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.Entities.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.WorldItems.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
		CaptureStore.Lights.RemoveAll(r => r.Chunk.x == cx && r.Chunk.y == cy);
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
		if (ResolveVinePrefab(record.Id) != "Special/sandvinehook"
			&& Clean(go.name).IndexOf("sandvinehook", System.StringComparison.OrdinalIgnoreCase) < 0)
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
		System.Type tag = AccessTools.TypeByName("SuperSecret.MagmaPipeTag");
		if (tag == null)
			return;
		go.AddComponent(tag);
	}

	private static void CollectOilPipes(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill)
	{
		OilPipeScript[] pipes = Object.FindObjectsOfType<OilPipeScript>();
		for (int i = 0; i < pipes.Length; i++)
		{
			OilPipeScript pipe = pipes[i];
			if (pipe == null || !InKeys(world, pipe.transform.position, chunkKeys, out _, out _))
				continue;
			kill.Add(pipe.gameObject);
		}
	}

	private static void WritebackOilPipes(WorldGeneration world, HashSet<long> chunkKeys)
	{
		OilPipeScript[] pipes = Object.FindObjectsOfType<OilPipeScript>();
		for (int i = 0; i < pipes.Length; i++)
		{
			OilPipeScript pipe = pipes[i];
			if (pipe == null || !InKeys(world, pipe.transform.position, chunkKeys, out int cx, out int cy))
				continue;
			CaptureStore.Entities.Add(new EntityRecord
			{
				Chunk = new Vector2Int(cx, cy),
				Id = "oilpipe",
				Position = pipe.transform.position,
				RotationZ = pipe.transform.eulerAngles.z,
				MagmaPipe = IsMagmaPipe(pipe.gameObject),
			});
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

	private static bool IsUnder(Transform root, Transform node)
	{
		if (root == null || node == null)
			return false;
		return node == root || node.IsChildOf(root);
	}
}
