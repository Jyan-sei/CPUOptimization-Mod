using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class WorldScanner
{
	/// <param name="mergeLiveOnly">keep ghost records, only append stuff still alive in the window</param>
	internal static void Capture(WorldGeneration world, bool mergeLiveOnly = false)
	{
		if (!mergeLiveOnly)
			CaptureStore.Clear();

		CaptureStore.BiomeDepth = world.biomeDepth;
		CaptureStore.ChunkWidth = world.chunkWidth;
		CaptureStore.ChunkHeight = world.chunkHeight;
		CaptureStore.SolidBlocks = CountSolidBlocks(world);

		Transform playerRoot = null;
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			playerRoot = PlayerCamera.main.body.transform;

		HashSet<int> claimedItems = new HashSet<int>();
		HashSet<int> claimedBuildings = new HashSet<int>();
		HashSet<int> claimedTransforms = new HashSet<int>();

		int traders0 = CaptureStore.Traders.Count;
		int enemies0 = CaptureStore.Enemies.Count;
		int entities0 = CaptureStore.Entities.Count;
		int items0 = CaptureStore.WorldItems.Count;
		int lights0 = CaptureStore.Lights.Count;

		StructureCatalog.EnsureIndex();
		ChunkBackdrop.Sample(world);
		// cull already ran. IsResident kept them alive. this keeps them out of the arrays.
		ElderResident.Discover(world, claimedBuildings, claimedTransforms);
		CaptureTraders(world, claimedBuildings);
		CaptureEnemies(world, claimedBuildings);
		CaptureBuildingEntities(world, claimedBuildings, claimedTransforms, claimedItems, playerRoot);
		CaptureBackgroundShells(world, claimedTransforms);
		CaptureStructureTilemaps(world, claimedTransforms, claimedItems, playerRoot);
		CaptureClimbablesWithoutBuilding(world, claimedTransforms);
		// wallholes stay resident. not streamed as EntityRecords
		WallholeResident.Discover(world, claimedTransforms);
		CaptureNamedEntities(world, claimedTransforms, "sandvinehook");
		CaptureOilPipes(world, claimedTransforms);
		CaptureWorldItems(world, claimedItems, playerRoot);
		CaptureLights(world, claimedBuildings, claimedTransforms, playerRoot);
		// starting pod is already live. give its interior a manual culling box before play
		StructureTilemapVault.WakeAll(world);

		// rows added here are objects still in the scene. ghost rows from gen stay unmarked
		// so the spawn window can instantiate them. gen's camera is not the final spawn.
		if (mergeLiveOnly)
			MarkLiveTail(traders0, enemies0, entities0, items0, lights0);

		CaptureStore.Captured = true;
		Plugin.Log.LogInfo(
			$"capture filled traders={CaptureStore.Traders.Count} enemies={CaptureStore.Enemies.Count} " +
			$"entities={CaptureStore.Entities.Count} worldItems={CaptureStore.WorldItems.Count} " +
			$"lights={CaptureStore.Lights.Count} wallholesResident={WallholeResident.Count} " +
			$"elders={ElderResident.Count}" +
			(CaptureStore.GhostFilled
				? $" (ghost during gen: e={CaptureStore.GhostEntityCount} n={CaptureStore.GhostEnemyCount} " +
				  $"i={CaptureStore.GhostItemCount} t={CaptureStore.GhostTraderCount})"
				: ""));
	}

	private static void MarkLiveTail(int traders0, int enemies0, int entities0, int items0, int lights0)
	{
		for (int i = traders0; i < CaptureStore.Traders.Count; i++)
			CaptureStore.Traders[i].LiveAtCapture = true;
		for (int i = enemies0; i < CaptureStore.Enemies.Count; i++)
			CaptureStore.Enemies[i].LiveAtCapture = true;
		for (int i = entities0; i < CaptureStore.Entities.Count; i++)
			CaptureStore.Entities[i].LiveAtCapture = true;
		for (int i = items0; i < CaptureStore.WorldItems.Count; i++)
			CaptureStore.WorldItems[i].LiveAtCapture = true;
		for (int i = lights0; i < CaptureStore.Lights.Count; i++)
			CaptureStore.Lights[i].LiveAtCapture = true;
	}

	private static void CaptureTraders(WorldGeneration world, HashSet<int> claimedBuildings)
	{
		TraderScript[] traders = Object.FindObjectsOfType<TraderScript>();
		for (int i = 0; i < traders.Length; i++)
		{
			TraderScript trader = traders[i];
			if (trader == null)
				continue;

			BuildingEntity build = FieldAccess.Get<BuildingEntity>(trader, "build")
				?? trader.GetComponent<BuildingEntity>();
			if (build != null)
				claimedBuildings.Add(build.GetInstanceID());

			TraderRecord record = FillTrader(world, trader, build);
			ChunkRoster.Note(trader.gameObject);
			CaptureStore.Traders.Add(record);
		}
	}

	private static void CaptureEnemies(WorldGeneration world, HashSet<int> claimedBuildings)
	{
		SpiderHandler[] spiders = Object.FindObjectsOfType<SpiderHandler>();
		for (int i = 0; i < spiders.Length; i++)
		{
			SpiderHandler spider = spiders[i];
			if (spider == null)
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

			EnemyRecord spiderRec = new EnemyRecord
			{
				Chunk = ChunkAt(world, spider.transform.position),
				Id = PrefabName(spider.gameObject, build),
				Health = build != null ? build.health : 0f,
				RequireGround = build == null || build.requireGround,
				Position = spider.transform.position,
				RotationZ = spider.transform.eulerAngles.z,
				ScaleNegX = spider.transform.localScale.x < 0f ? true : (bool?)null,
			};
			ChunkRoster.Note(spider.gameObject);
			StructureLiveState.CaptureEnemy(spider.gameObject, spiderRec);
			CaptureStore.Enemies.Add(spiderRec);
		}

		BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < buildings.Length; i++)
		{
			BuildingEntity build = buildings[i];
			if (build == null || !build.animal || claimedBuildings.Contains(build.GetInstanceID()))
				continue;
			if (ElderResident.IsResident(world, build.transform))
			{
				claimedBuildings.Add(build.GetInstanceID());
				continue;
			}

			claimedBuildings.Add(build.GetInstanceID());
			EnemyRecord animalRec = new EnemyRecord
			{
				Chunk = ChunkAt(world, build.transform.position),
				Id = PrefabName(build.gameObject, build),
				Health = build.health,
				RequireGround = build.requireGround,
				Position = build.transform.position,
				RotationZ = build.transform.eulerAngles.z,
				ScaleNegX = build.transform.localScale.x < 0f ? true : (bool?)null,
			};
			ChunkRoster.Note(build.gameObject);
			StructureLiveState.CaptureEnemy(build.gameObject, animalRec);
			CaptureStore.Enemies.Add(animalRec);
		}
	}

	private static void CaptureBuildingEntities(
		WorldGeneration world,
		HashSet<int> claimedBuildings,
		HashSet<int> claimedTransforms,
		HashSet<int> claimedItems,
		Transform playerRoot)
	{
		BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < buildings.Length; i++)
		{
			BuildingEntity build = buildings[i];
			if (build == null)
				continue;
			int id = build.GetInstanceID();
			if (claimedBuildings.Contains(id))
				continue;
			if (ElderResident.IsResident(world, build.transform))
			{
				claimedBuildings.Add(id);
				continue;
			}

			if (LifepodGroups.TryGroup(build, claimedBuildings, out Transform groupRoot))
			{
				EntityRecord grouped = LifepodGroups.BuildRecord(world, groupRoot);
				LifepodGroups.Stamp(groupRoot, grouped, claimedItems, playerRoot);
				claimedTransforms.Add(groupRoot.GetInstanceID());
				CaptureStore.Entities.Add(grouped);
				continue;
			}

			claimedBuildings.Add(id);
			claimedTransforms.Add(build.transform.GetInstanceID());

			EntityRecord record = new EntityRecord
			{
				Chunk = ChunkAt(world, build.transform.position),
				Id = PrefabName(build.gameObject, build),
				Health = build.health,
				RequireGround = build.requireGround,
				Position = build.transform.position,
				RotationZ = build.transform.eulerAngles.z,
				// turret, stalactite, etc face by flipping localScale.x. rotation stays 0
				ScaleNegX = build.transform.localScale.x < 0f ? true : (bool?)null,
				HasBlockPlacedOn = true,
				BlockPlacedOn = build.blockPlacedOn,
			};
			ChunkObjects.CaptureVineHook(build.gameObject, record);
			ResolveStructurePrefab(build.gameObject, record);
			AttachStructureTilemapVault(world, build.gameObject, record);

			AttachCrystal(build, record);
			AttachClimbable(build.gameObject, record);
			AttachCorpse(build, record);
			BuildingLoot.Capture(build, record, claimedItems, playerRoot);

			StalactiteDropper dropper = build.GetComponent<StalactiteDropper>();
			if (dropper != null)
				record.StalactiteDropped = FieldAccess.Get<bool>(dropper, "dropped");

			StructureLiveState.NoteCannon(build.gameObject, record);

			ScrapEaterScript eater = build.GetComponent<ScrapEaterScript>();
			if (eater != null)
				record.ScrapAmount = eater.scrapAmount;

			record.ModState = ModBehaviourState.Capture(build.gameObject);
			ChunkRoster.Note(build.gameObject);
			StructureLiveState.CaptureEntity(build.gameObject, build, record);
			CaptureStore.Entities.Add(record);
		}
	}

	/// <summary>
	/// structure tilemaps under worldGrid, not chunk tilemaps. catalog when we know it,
	/// otherwise StructureTilemapVault so stream restore isnt lifepod-only
	/// </summary>
	private static void CaptureStructureTilemaps(WorldGeneration world, HashSet<int> claimedTransforms, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (world.worldGrid == null)
			return;

		Transform grid = world.worldGrid.transform;
		for (int i = 0; i < grid.childCount; i++)
		{
			Transform child = grid.GetChild(i);
			if (child == null)
				continue;
			int tid = child.GetInstanceID();
			if (claimedTransforms.Contains(tid))
				continue;
			if (!StructureTilemapVault.IsStructureTilemap(child, world))
				continue;
			if (StructureTilemapVault.IsPinnedCornerTilemap(child))
				continue;

			string id = CleanName(child.name);
			claimedTransforms.Add(tid);
			BuildingEntity seated = child.GetComponent<BuildingEntity>();
			EntityRecord record = new EntityRecord
			{
				Chunk = ChunkAt(world, child.position),
				Id = id,
				Health = seated != null ? seated.health : 0f,
				RequireGround = seated == null || seated.requireGround,
				Position = child.position,
				RotationZ = child.eulerAngles.z,
				HasBlockPlacedOn = seated != null,
				BlockPlacedOn = seated != null ? seated.blockPlacedOn : default,
			};
			if (StructureCatalog.TryResolve(child.gameObject, id, out string prefabPath))
				record.StructurePrefab = prefabPath;
			record.StructureVaultKey = StructureTilemapVault.EnsureTemplate(child.gameObject, id);
			if (seated != null)
				BuildingLoot.Capture(seated, record, claimedItems, playerRoot);
			ChunkRoster.Note(child.gameObject);
			StructureLiveState.CaptureEntity(child.gameObject, seated, record);
			CaptureStore.Entities.Add(record);
		}
	}

	private static void ResolveStructurePrefab(GameObject go, EntityRecord record)
	{
		if (StructureCatalog.TryResolve(go, record.Id, out string path))
			record.StructurePrefab = path;
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

	private static TraderRecord FillTrader(WorldGeneration world, TraderScript trader, BuildingEntity build)
	{
		TraderRecord record = new TraderRecord
		{
			Chunk = ChunkAt(world, trader.transform.position),
			Position = trader.transform.position,
			Health = build != null ? build.health : 0f,
			RequireGround = build == null || build.requireGround,
			HasBlockPlacedOn = build != null,
			BlockPlacedOn = build != null ? build.blockPlacedOn : default,
			StartedConvo = trader.startedConvo,
			Character = StructureLiveState.ResolveTraderPrefab(trader),
			Reputation = trader.reputation,
			Hostility = trader.hostility,
			ValueGiven = trader.valueGiven,
			TotalValueGiven = trader.totalValueGiven,
			HasItems = trader.items != null,
			MoveRangeMin = trader.MoveRange.min,
			MoveRangeMax = trader.MoveRange.max,
			FarEnoughToMove = trader.farEnoughToMove,
			DidMove = trader.didMove,
			HeightMult = trader.heightMult,
			RotationZ = trader.transform.eulerAngles.z,
			LocalScale = trader.transform.localScale,
		};

		if (trader.items != null)
		{
			for (int j = 0; j < trader.items.Count; j++)
			{
				TraderItem item = trader.items[j];
				if (item == null || string.IsNullOrEmpty(item.id))
					continue;
				record.Items.Add(new TraderItemSnap
				{
					Id = item.id,
					Bought = item.bought,
					Value = item.value,
					Preference = item.preference,
				});
			}
		}

		if (record.MoveRangeMin == 0f && record.MoveRangeMax == 0f)
		{
			float x = trader.transform.position.x;
			record.MoveRangeMin = x - 5f;
			record.MoveRangeMax = x + 5f;
			Plugin.Log.LogWarning(
				$"capture trader{record.Character} MoveRange was 0,0 - stored fallback ±5 at x={x:0.#}");
		}

		StructureLiveState.CaptureTraderBody(trader, record);
		return record;
	}

	private static void AttachCrystal(BuildingEntity build, EntityRecord record)
	{
		CrystalBehaviour crystal = build.GetComponent<CrystalBehaviour>();
		if (crystal == null)
			crystal = build.GetComponentInChildren<CrystalBehaviour>();
		if (crystal == null)
			return;

		record.CrystalSize = crystal.crystalSize;
		IList effects = FieldAccess.Get<IList>(crystal, "effects");
		if (effects == null)
			return;
		for (int j = 0; j < effects.Count; j++)
		{
			object effect = effects[j];
			if (effect != null)
				record.EffectNames.Add(effect.GetType().Name);
		}
	}

	private static void AttachClimbable(GameObject go, EntityRecord record)
	{
		Climbable climb = go.GetComponent<Climbable>();
		if (climb == null)
			climb = go.GetComponentInChildren<Climbable>();
		if (climb == null)
			return;

		FillRopeFields(climb, record);
	}

	private static void AttachCorpse(BuildingEntity build, EntityRecord record)
	{
		CorpseScript corpse = build.GetComponent<CorpseScript>();
		if (corpse == null)
			return;

		StructureLiveState.NoteCorpse(corpse, record);
	}

	private static void CaptureClimbablesWithoutBuilding(WorldGeneration world, HashSet<int> claimedTransforms)
	{
		Climbable[] climbables = Object.FindObjectsOfType<Climbable>();
		for (int i = 0; i < climbables.Length; i++)
		{
			Climbable climb = climbables[i];
			if (climb == null)
				continue;
			int tid = climb.transform.GetInstanceID();
			if (claimedTransforms.Contains(tid))
				continue;
			if (climb.GetComponentInParent<BuildingEntity>() != null)
				continue;

			claimedTransforms.Add(tid);
			ChunkRoster.Note(climb.gameObject);
			EntityRecord record = new EntityRecord
			{
				Chunk = ChunkAt(world, climb.transform.position),
				Id = CleanName(climb.gameObject.name),
				Position = climb.transform.position,
				RotationZ = climb.transform.eulerAngles.z,
			};
			FillRopeFields(climb, record);
			StructureLiveState.CaptureEntity(climb.gameObject, null, record);
			CaptureStore.Entities.Add(record);
		}
	}

	private static void FillRopeFields(Climbable climb, EntityRecord record)
	{
		if (climb.points != null && climb.points.Count >= 1)
			record.RopePointA = climb.points[0];
		if (climb.points != null && climb.points.Count >= 2)
			record.RopePointB = climb.points[1];

		float lengthScale = 1f;
		if (climb.transform.childCount > 0)
			lengthScale = climb.transform.GetChild(0).localScale.y;
		record.LengthScale = lengthScale;
		record.DownwardsVelocity = climb.downwardsVelocity;

		SpriteRenderer sr = climb.GetComponent<SpriteRenderer>();
		if (sr != null)
		{
			record.Color = sr.color;
			record.FlipX = sr.flipX;
			if (sr.drawMode == SpriteDrawMode.Tiled || sr.drawMode == SpriteDrawMode.Sliced)
				record.SpriteSize = sr.size;
		}
		record.RootScaleX = climb.transform.localScale.x;
	}

	private static void CaptureNamedEntities(WorldGeneration world, HashSet<int> claimedTransforms, string nameKey)
	{
		Transform[] all = Object.FindObjectsOfType<Transform>();
		for (int i = 0; i < all.Length; i++)
		{
			Transform t = all[i];
			if (t == null)
				continue;
			int tid = t.GetInstanceID();
			if (claimedTransforms.Contains(tid))
				continue;

			string name = CleanName(t.name);
			if (name.IndexOf(nameKey, System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			if (t.GetComponent<Climbable>() != null)
				continue;
			if (t.GetComponentInParent<BuildingEntity>() != null && t.GetComponent<BuildingEntity>() == null)
				continue;

			claimedTransforms.Add(tid);
			SpriteRenderer sr = t.GetComponent<SpriteRenderer>();
			AudioSource audio = t.GetComponent<AudioSource>();
			EntityRecord named = new EntityRecord
			{
				Chunk = ChunkAt(world, t.position),
				Id = name,
				Position = t.position,
				RotationZ = t.eulerAngles.z,
				Color = sr != null ? sr.color : (Color?)null,
				FlipX = sr != null ? sr.flipX : (bool?)null,
				Pitch = audio != null ? audio.pitch : (float?)null,
				LengthScale = t.localScale.x,
			};
			ChunkRoster.Note(t.gameObject);
			StructureLiveState.CaptureEntity(t.gameObject, t.GetComponent<BuildingEntity>(), named);
			CaptureStore.Entities.Add(named);
		}
	}

	/// <summary>
	/// free-floating light2d only. lights under buildings/items/traders/props ride those records
	/// (wallhole kids named "Light 2D (N)" etc)
	/// </summary>
	private static void CaptureLights(
		WorldGeneration world,
		HashSet<int> claimedBuildings,
		HashSet<int> claimedTransforms,
		Transform playerRoot)
	{
		_ = claimedBuildings;
		Light2D ambient = world.ambientLight;
		Light2D[] lights = Object.FindObjectsOfType<Light2D>();
		for (int i = 0; i < lights.Length; i++)
		{
			Light2D light = lights[i];
			if (!IsStandaloneWorldLight(light, ambient, playerRoot, claimedTransforms))
				continue;

			int tid = light.transform.GetInstanceID();
			if (!claimedTransforms.Add(tid))
				continue;

			ChunkRoster.Note(light.gameObject);
			CaptureStore.Lights.Add(FillLight(world, light));
		}
	}

	/// <summary>
	/// free-floating world lights we stream ourselves. ambient / player / building / item /
	/// trader / enemy / wallhole kids return false. those used to spam 900+ "Light 2D (N)" rows
	/// </summary>
	internal static bool IsStandaloneWorldLight(
		Light2D light,
		Light2D ambient,
		Transform playerRoot,
		HashSet<int> claimedTransforms = null)
	{
		if (light == null || light == ambient)
			return false;
		if (PlayerCarry.Holds(light.transform))
			return false;
		if (light.GetComponentInParent<BuildingEntity>() != null)
			return false;
		if (light.GetComponentInParent<TraderScript>() != null)
			return false;
		if (light.GetComponentInParent<Item>() != null)
			return false;
		if (light.GetComponentInParent<SpiderHandler>() != null)
			return false;
		if (WallholeResident.OwnsTransform(light.transform))
			return false;

		// parent already got captured as an entity or structure
		if (claimedTransforms != null)
		{
			for (Transform t = light.transform.parent; t != null; t = t.parent)
			{
				if (claimedTransforms.Contains(t.GetInstanceID()))
					return false;
			}
		}

		// nested under gameplay stuff. that object's spawn/destroy owns the light.
		// only keep root / chunk / world / grid kids
		Transform parent = light.transform.parent;
		if (parent == null)
			return true;
		if (parent.GetComponent<ChunkScript>() != null)
			return true;
		if (parent.GetComponent<WorldGeneration>() != null)
			return true;
		if (parent.GetComponent<Grid>() != null)
			return true;
		string pn = CleanName(parent.name);
		if (pn.Equals("Lights", System.StringComparison.OrdinalIgnoreCase)
			|| pn.Equals("LightRoot", System.StringComparison.OrdinalIgnoreCase))
			return true;
		return false;
	}

	internal static LightRecord FillLight(WorldGeneration world, Light2D light)
	{
		return new LightRecord
		{
			Chunk = ChunkAt(world, light.transform.position),
			Id = CleanName(light.gameObject.name),
			Position = light.transform.position,
			RotationZ = light.transform.eulerAngles.z,
			Intensity = QoLLightScale.Authored(light),
			Color = light.color,
			OuterRadius = light.pointLightOuterRadius,
			InnerRadius = light.pointLightInnerRadius,
			LightType = (int)light.lightType,
			FalloffIntensity = light.falloffIntensity,
			ShadowEnabled = light.shadowsEnabled,
		};
	}

	private static void CaptureWorldItems(WorldGeneration world, HashSet<int> claimedItems, Transform playerRoot)
	{
		List<Item> items = new List<Item>();
		ChunkObjects.CollectSceneItems(items);
		for (int i = 0; i < items.Count; i++)
		{
			Item item = items[i];
			if (item == null)
				continue;
			ChunkRoster.Note(item.gameObject);
			if (claimedItems.Contains(item.GetInstanceID()))
				continue;
			if (PlayerCarry.Holds(item.transform))
				continue;
			if (ChunkObjects.UnderItem(item.transform))
				continue;

			claimedItems.Add(item.GetInstanceID());
			WorldItemRecord linked = NetHold.Linked(item.gameObject);
			if (linked != null)
			{
				linked.Chunk = ChunkAt(world, item.transform.position);
				linked.Id = PrefabName(item.gameObject, null, item.id);
				linked.Condition = item.condition;
				linked.Battery = BatterySnap.Read(item);
				linked.Position = item.transform.position;
				linked.RotationZ = item.transform.eulerAngles.z;
				linked.LocalScale = item.transform.localScale;
				linked.LiveAtCapture = true;
				if (!CaptureStore.WorldItems.Contains(linked))
					CaptureStore.WorldItems.Add(linked);
				ItemPath.Note("store", linked.Chunk.x, linked.Chunk.y, linked.Id, linked.Position,
					"capture-keep iid=" + item.GetInstanceID() + " net=" + (linked.HasNetId ? linked.NetId.ToString() : "-"));
				continue;
			}
			WorldItemRecord record = new WorldItemRecord
			{
				Chunk = ChunkAt(world, item.transform.position),
				Id = PrefabName(item.gameObject, null, item.id),
				Condition = item.condition,
				Battery = BatterySnap.Read(item),
				Position = item.transform.position,
				RotationZ = item.transform.eulerAngles.z,
				LocalScale = item.transform.localScale,
			};
			CopyLiquids(item, record);
			BuildingLoot.AppendChildren(item.transform, record.Children, claimedItems, playerRoot);
			record.ModState = ModBehaviourState.Capture(item.gameObject);
			StructureLiveState.CaptureItem(item, record);
			NetHold.Link(item.gameObject, record);
			CaptureStore.WorldItems.Add(record);
			ItemPath.Note("store", record.Chunk.x, record.Chunk.y, record.Id, record.Position,
				"capture iid=" + item.GetInstanceID() + " sceneId=" + (item.id ?? ""));
		}
	}

	private static void CopyLiquids(Item item, WorldItemRecord record)
	{
		if (record == null)
			return;
		record.HasLiquids = BuildingLoot.CopyLiquids(item, record.Liquids);
	}

	private static int CountSolidBlocks(WorldGeneration world)
	{
		FieldInfo field = AccessTools.Field(typeof(WorldGeneration), "worldBlocks");
		ushort[,] blocks = field?.GetValue(world) as ushort[,];
		if (blocks == null)
			return 0;

		int count = 0;
		int w = blocks.GetLength(0);
		int h = blocks.GetLength(1);
		for (int x = 0; x < w; x++)
		for (int y = 0; y < h; y++)
		{
			if (blocks[x, y] != 0)
				count++;
		}
		return count;
	}

	private static Vector2Int ChunkAt(WorldGeneration world, Vector3 worldPos)
	{
		Vector2Int block = world.WorldToBlockPos(worldPos);
		return world.BlockToChunkPos(block);
	}

	private static string PrefabName(GameObject go, BuildingEntity build, string fallbackId = null)
	{
		if (build != null)
			return ChunkObjects.PrefabIdFor(go, build);
		if (!string.IsNullOrEmpty(fallbackId))
			return fallbackId;
		return ChunkObjects.PrefabIdFor(go, null);
	}

	private static void CaptureOilPipes(WorldGeneration world, HashSet<int> claimedTransforms)
	{
		OilPipeScript[] pipes = Object.FindObjectsOfType<OilPipeScript>();
		for (int i = 0; i < pipes.Length; i++)
		{
			OilPipeScript pipe = pipes[i];
			if (pipe == null)
				continue;
			int tid = pipe.transform.GetInstanceID();
			if (claimedTransforms.Contains(tid))
				continue;
			claimedTransforms.Add(tid);
			ChunkRoster.Note(pipe.gameObject);
			EntityRecord pipeRec = new EntityRecord
			{
				Chunk = ChunkAt(world, pipe.transform.position),
				Id = "oilpipe",
				Position = pipe.transform.position,
				RotationZ = pipe.transform.eulerAngles.z,
				MagmaPipe = ChunkObjects.IsMagmaPipe(pipe.gameObject),
			};
			StructureLiveState.CaptureEntity(pipe.gameObject, pipe.GetComponent<BuildingEntity>(), pipeRec);
			CaptureStore.Entities.Add(pipeRec);
		}
	}

	/// <summary>
	/// Custom Structures decorative props (pipeoutside, arrowtip, and a podbutton
	/// with no use binding) are a sprite plus BackgroundObjectIdentity. no building,
	/// so the building scan never stores them and chunk unload cannot put them back.
	/// interactive ones already have a BuildingEntity and take the normal row.
	/// </summary>
	private static void CaptureBackgroundShells(WorldGeneration world, HashSet<int> claimedTransforms)
	{
		List<GameObject> shells = BackgroundShells();
		for (int i = 0; i < shells.Count; i++)
		{
			GameObject go = shells[i];
			if (go == null)
				continue;
			int tid = go.transform.GetInstanceID();
			if (!claimedTransforms.Add(tid))
				continue;
			EntityRecord rec = ShellRecord(world, go);
			ChunkRoster.Note(go);
			StructureLiveState.CaptureEntity(go, null, rec);
			rec.ModState = ModBehaviourState.Capture(go);
			CaptureStore.Entities.Add(rec);
		}
	}

	internal static void WritebackBackgroundShells(WorldGeneration world, HashSet<long> chunkKeys)
	{
		if (world == null || chunkKeys == null)
			return;
		List<GameObject> shells = BackgroundShells();
		for (int i = 0; i < shells.Count; i++)
		{
			GameObject go = shells[i];
			if (go == null || !ChunkObjects.InChunkKeys(world, go.transform.position, chunkKeys))
				continue;
			EntityRecord rec = ShellRecord(world, go);
			ChunkRoster.Note(go);
			StructureLiveState.CaptureEntity(go, null, rec);
			rec.ModState = ModBehaviourState.Capture(go);
			CaptureStore.Entities.Add(rec);
		}
	}

	internal static void AddBackgroundShellTargets(WorldGeneration world, HashSet<long> chunkKeys, List<GameObject> kill, HashSet<int> seen)
	{
		if (world == null || chunkKeys == null || kill == null)
			return;
		List<GameObject> shells = BackgroundShells();
		for (int i = 0; i < shells.Count; i++)
		{
			GameObject go = shells[i];
			if (go == null || !ChunkObjects.InChunkKeys(world, go.transform.position, chunkKeys))
				continue;
			if (seen != null && !seen.Add(go.GetInstanceID()))
				continue;
			kill.Add(go);
		}
	}

	private static readonly List<GameObject> _shells = new List<GameObject>(16);
	private static System.Type _shellType;
	private static bool _shellLooked;
	private static FieldInfo _shellIdField;
	private static PropertyInfo _shellIdProp;

	private static List<GameObject> BackgroundShells()
	{
		_shells.Clear();
		if (!_shellLooked)
		{
			_shellLooked = true;
			_shellType = TypeLookup.Find("Custom_Structures.BackgroundObjectIdentity");
			if (_shellType != null)
			{
				_shellIdField = _shellType.GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_shellIdProp = _shellType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
		}
		if (_shellType == null)
			return _shells;
		UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(_shellType);
		for (int i = 0; i < found.Length; i++)
		{
			Component part = found[i] as Component;
			if (part == null)
				continue;
			GameObject go = part.gameObject;
			if (!ChunkRoster.InActiveScene(go))
				continue;
			if (go.GetComponent<BuildingEntity>() != null)
				continue;
			_shells.Add(go);
		}
		return _shells;
	}

	private static EntityRecord ShellRecord(WorldGeneration world, GameObject go)
	{
		string id = ShellId(go);
		return new EntityRecord
		{
			Chunk = ChunkAt(world, go.transform.position),
			Id = string.IsNullOrEmpty(id) ? CleanName(go.name) : id,
			Health = 5f,
			RequireGround = false,
			Position = go.transform.position,
			RotationZ = go.transform.eulerAngles.z,
			LocalScale = go.transform.localScale,
		};
	}

	private static string ShellId(GameObject go)
	{
		Component part = _shellType != null ? go.GetComponent(_shellType) : null;
		if (part != null)
		{
			if (_shellIdField != null && _shellIdField.GetValue(part) is string field && !string.IsNullOrEmpty(field))
				return field;
			if (_shellIdProp != null && _shellIdProp.GetValue(part, null) is string prop && !string.IsNullOrEmpty(prop))
				return prop;
		}
		string name = CleanName(go.name);
		const string prefix = "bgobj_";
		if (name.StartsWith(prefix, System.StringComparison.Ordinal))
			return name.Substring(prefix.Length);
		return name;
	}

	private static string CleanName(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unknown";
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}

}
