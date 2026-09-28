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

		StructureCatalog.EnsureIndex();
		ChunkBackdrop.Sample(world);
		// cull already ran. IsResident kept them alive. this keeps them out of the arrays.
		ElderResident.Discover(world, claimedBuildings, claimedTransforms);
		CaptureTraders(world, claimedBuildings);
		CaptureEnemies(world, claimedBuildings);
		CaptureBuildingEntities(world, claimedBuildings, claimedTransforms, claimedItems, playerRoot);
		CaptureStructureTilemaps(world, claimedTransforms, claimedItems, playerRoot);
		CaptureClimbablesWithoutBuilding(world, claimedTransforms);
		// wallholes stay resident. not streamed as EntityRecords
		WallholeResident.Discover(world, claimedTransforms);
		CaptureNamedEntities(world, claimedTransforms, "sandvinehook");
		CaptureOilPipes(world, claimedTransforms);
		CaptureWorldItems(world, claimedItems, playerRoot);
		CaptureLights(world, claimedBuildings, claimedTransforms, playerRoot);

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

			CaptureStore.Enemies.Add(new EnemyRecord
			{
				Chunk = ChunkAt(world, spider.transform.position),
				Id = PrefabName(spider.gameObject, build),
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
			if (build == null || !build.animal || claimedBuildings.Contains(build.GetInstanceID()))
				continue;
			if (ElderResident.IsResident(world, build.transform))
			{
				claimedBuildings.Add(build.GetInstanceID());
				continue;
			}

			claimedBuildings.Add(build.GetInstanceID());
			CaptureStore.Enemies.Add(new EnemyRecord
			{
				Chunk = ChunkAt(world, build.transform.position),
				Id = PrefabName(build.gameObject, build),
				Health = build.health,
				RequireGround = build.requireGround,
				Position = build.transform.position,
				RotationZ = build.transform.eulerAngles.z,
				ScaleNegX = build.transform.localScale.x < 0f ? true : (bool?)null,
			});
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

			ScrapEaterScript eater = build.GetComponent<ScrapEaterScript>();
			if (eater != null)
				record.ScrapAmount = eater.scrapAmount;

			record.ModState = ModBehaviourState.Capture(build.gameObject);
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
				TraderItem item = trader.items[j];
				if (item != null && !string.IsNullOrEmpty(item.id))
					record.ItemIds.Add(item.id);
			}
		}

		if (record.MoveRangeMin == 0f && record.MoveRangeMax == 0f)
		{
			float x = trader.transform.position.x;
			record.MoveRangeMin = x - 5f;
			record.MoveRangeMax = x + 5f;
			Plugin.Log.LogWarning(
				$"capture trader{record.Character} MoveRange was 0,0 — stored fallback ±5 at x={x:0.#}");
		}

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

		record.AnimalCorpse = corpse.animalCorpse;
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
			EntityRecord record = new EntityRecord
			{
				Chunk = ChunkAt(world, climb.transform.position),
				Id = CleanName(climb.gameObject.name),
				Position = climb.transform.position,
				RotationZ = climb.transform.eulerAngles.z,
			};
			FillRopeFields(climb, record);
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
			CaptureStore.Entities.Add(new EntityRecord
			{
				Chunk = ChunkAt(world, t.position),
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
		if (IsUnder(playerRoot, light.transform))
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
			Intensity = light.intensity,
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
		Item[] items = Object.FindObjectsOfType<Item>();
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null)
				continue;
			if (claimedItems.Contains(item.GetInstanceID()))
				continue;
			if (IsUnder(playerRoot, item.transform))
				continue;

			Item parentItem = item.transform.parent != null
				? item.transform.parent.GetComponent<Item>()
				: null;
			if (parentItem != null)
				continue;

			claimedItems.Add(item.GetInstanceID());
			WorldItemRecord record = new WorldItemRecord
			{
				Chunk = ChunkAt(world, item.transform.position),
				Id = PrefabName(item.gameObject, null, item.id),
				Condition = item.condition,
				Position = item.transform.position,
				RotationZ = item.transform.eulerAngles.z,
				LocalScale = item.transform.localScale,
			};
			CopyLiquids(item, record.Liquids);
			BuildingLoot.AppendChildren(item.transform, record.Children, claimedItems, playerRoot);
			record.ModState = ModBehaviourState.Capture(item.gameObject);
			CaptureStore.WorldItems.Add(record);
		}
	}

	private static void CopyLiquids(Item item, List<LiquidRecord> into)
	{
		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water == null || water.stack == null)
			return;
		for (int i = 0; i < water.stack.Count; i++)
		{
			LiquidStack stack = water.stack[i];
			if (stack == null)
				continue;
			into.Add(new LiquidRecord { LiquidId = stack.liquidId, Amount = stack.amount });
		}
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
			CaptureStore.Entities.Add(new EntityRecord
			{
				Chunk = ChunkAt(world, pipe.transform.position),
				Id = "oilpipe",
				Position = pipe.transform.position,
				RotationZ = pipe.transform.eulerAngles.z,
				MagmaPipe = ChunkObjects.IsMagmaPipe(pipe.gameObject),
			});
		}
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

	private static bool IsUnder(Transform root, Transform node)
	{
		if (root == null || node == null)
			return false;
		return node == root || node.IsChildOf(root);
	}
}
