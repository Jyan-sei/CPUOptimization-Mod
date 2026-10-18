using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization2;

/// <summary>
/// custom structures writes onto the live object, then throws the json bag away.
/// health and the rolled drop table already sit on the building record.
/// this keeps the rest that stream respawn used to forget: full scale (flip Y
/// included), an open door, child lights, and ammo left in a mag.
/// </summary>
internal static class StructureLiveState
{
	internal sealed class LightSnap
	{
		internal string Path;
		internal int Index;
		internal bool IsLight2D;
		internal bool Enabled;
		internal float Intensity;
	}

	internal sealed class MaskSnap
	{
		internal string Path;
		internal int Index;
		internal bool Enabled;
	}

	internal sealed class BodySprite
	{
		internal string Path;
		internal Sprite Sprite;
		internal bool FlipX;
	}

	internal static void CaptureEntity(GameObject go, BuildingEntity build, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		record.LocalScale = ScaleIfCustom(go.transform);
		record.DoorOpen = DoorOpen(go, build);
		if (LifepodGroups.StandaloneFixture(build))
			record.KeepLooseFixture = true;
		CaptureLights(go, out record.Lights, out record.LightMasks);
		CaptureRolledSprite(go, record);
		CaptureCrystalValue(go, record);
		CaptureNote(go, record);
		// respawn must not treat a structure note's unset support cell as a mined wall.
		// a vanilla note's blockPlacedOn is the cell it was built on, so this leaves it.
		if (record.HasNote && build != null && build.requireGround)
		{
			WorldGeneration world = WorldGeneration.world;
			if (world != null)
			{
				Vector2Int under = world.WorldToBlockPos(go.transform.position + Vector3.down * 0.5f);
				if (build.blockPlacedOn != under)
				{
					record.RequireGround = false;
					record.HasBlockPlacedOn = true;
					record.BlockPlacedOn = under;
				}
			}
		}
	}

	/// <summary>before SetActive. seen-line flag and the crystal marker CS reads in SetUpEffects</summary>
	internal static void PrimeBeforeStart(GameObject go, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		// prefab pocket already copied into WorldItems. this instantiate must not add another pile.
		DropBakedPocket(go);
		CorpseScript corpse = go.GetComponent<CorpseScript>();
		if (corpse != null)
		{
			if (record.AnimalCorpse.HasValue)
				corpse.animalCorpse = record.AnimalCorpse.Value;
			if (record.CorpseSeen)
				FieldAccess.Set(corpse, "didComment", true);
		}
		AttachCrystalMarker(go, record.CrystalValue);
		ApplyCannon(go, record);
	}

	/// <summary>live corpse. OnWillRenderObject may have played the seen line.</summary>
	internal static void NoteCorpse(CorpseScript corpse, EntityRecord record)
	{
		if (corpse == null || record == null)
			return;
		record.AnimalCorpse = corpse.animalCorpse;
		record.CorpseSeen = FieldAccess.Get<bool>(corpse, "didComment");
	}

	/// <summary>
	/// spent is has-fired. the dish angle is child 0, written by Update while it charges.
	/// a respawned prefab has spent false and the stock dish pose, so it fires again.
	/// </summary>
	internal static void NoteCannon(GameObject go, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		SoundCannon cannon = go.GetComponent<SoundCannon>() ?? go.GetComponentInChildren<SoundCannon>(true);
		if (cannon == null)
			return;
		record.HasSoundCannon = true;
		record.CannonSpent = FieldAccess.Get<bool>(cannon, "spent");
		record.CannonCharging = FieldAccess.Get<bool>(cannon, "charging");
		record.CannonChargeTime = FieldAccess.Get<float>(cannon, "chargeTime");
		Transform barrel = cannon.transform.childCount > 0 ? cannon.transform.GetChild(0) : null;
		record.CannonBarrelZ = barrel != null ? barrel.eulerAngles.z : 0f;
	}

	internal static void ApplyCannon(GameObject go, EntityRecord record)
	{
		if (go == null || record == null || !record.HasSoundCannon)
			return;
		SoundCannon cannon = go.GetComponent<SoundCannon>() ?? go.GetComponentInChildren<SoundCannon>(true);
		if (cannon == null)
			return;
		WriteCannon(cannon, record.CannonSpent, record.CannonCharging, record.CannonChargeTime, record.CannonBarrelZ);
		CannonStateHold hold = cannon.GetComponent<CannonStateHold>();
		if (hold == null)
			hold = cannon.gameObject.AddComponent<CannonStateHold>();
		hold.Spent = record.CannonSpent;
		hold.Charging = record.CannonCharging;
		hold.ChargeTime = record.CannonChargeTime;
		hold.BarrelZ = record.CannonBarrelZ;
	}

	internal static void Install(Harmony harmony)
	{
		_harmony = harmony;
		// First only runs us before CUCoreLib. HarmonyX still calls every prefix.
		// A false return skips CorpseScript.Start, not their Prefix. That method is patched on its own.
		harmony.Patch(
			AccessTools.Method(typeof(CorpseScript), "Start"),
			prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(CorpseStartPrefix))
			{
				priority = Priority.First
			});
		TryPatchCuCorpse();
		TryPatchCustomLoot();
		// MP skips SoundCannon.Update and adds a tracker with its own spent flag.
		// postfix runs after that prefix, so the tracker is already on the object.
		harmony.Patch(
			AccessTools.Method(typeof(SoundCannon), "Update"),
			postfix: new HarmonyMethod(typeof(StructureLiveState), nameof(CannonUpdatePostfix))
			{
				priority = Priority.Last
			});
		// runs before the stock body flip. a prop with requireGround off is a
		// structure piece sitting on background or across a gap. dynamic drops it.
		harmony.Patch(
			AccessTools.Method(typeof(BuildingEntity), "Update"),
			prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(HoldUnsupportedPrefix))
			{
				priority = Priority.First
			});
		// wall notes never get the requireGround=false that SpawnEntity writes.
		// CheckSeating then zeros health when the foreground cell is air.
		harmony.Patch(
			AccessTools.Method(typeof(BuildingEntity), "CheckSeating"),
			prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(NoteOnAirPrefix))
			{
				priority = Priority.First
			});
		// stock Awake deletes 20% of pumps. that roll is for a pump under a lifepod.
		// a structure pump placed on its own would lose the roll again every respawn.
		harmony.Patch(
			AccessTools.Method(typeof(LifepodPump), "Awake"),
			prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(KeepStandalonePumpPrefix))
			{
				priority = Priority.First
			});
	}

	/// <summary>
	/// Custom Structures clears requireGround so CheckSeating will not delete a prop
	/// that is not standing on a foreground block. Update then makes the body dynamic
	/// while the chunk renderer is on, and a horizontal door, a charger, or a medstation
	/// falls out of the structure. traders and stalactites still need a dynamic body.
	/// </summary>
	private static void HoldUnsupportedPrefix(BuildingEntity __instance)
	{
		// a sprite shell is AddComponent<BuildingEntity>, so health is 0 and the drop
		// arrays are null. Update then tries to break it, throws, and never Destroy's it.
		if (__instance != null && __instance.health < 0.5f && __instance.itemsDropOnDestroy == null)
			__instance.health = 5f;
		if (__instance == null || __instance.animal || __instance.requireGround || __instance.ignoreBodyOptimize)
			return;
		if (__instance.GetComponent<TraderScript>() != null)
			return;
		if (__instance.GetComponent<StalactiteDropper>() != null)
			return;
		__instance.ignoreBodyOptimize = true;
		ElderResident.NoteHeldStatic(__instance);
		Rigidbody2D rb = __instance.GetComponent<Rigidbody2D>();
		if (rb != null && rb.bodyType != RigidbodyType2D.Static)
			rb.bodyType = RigidbodyType2D.Static;
	}

	private static int _wallNoteDepth;

	/// <summary>
	/// only while Custom Structures is inside TrySpawnWallNote. that spawn does not
	/// clear requireGround, and CheckSeating runs from Instantiate before the method
	/// returns. vanilla notes are not in that call, so a mined wall still deletes them.
	/// </summary>
	private static void NoteOnAirPrefix(BuildingEntity __instance)
	{
		if (_wallNoteDepth <= 0 || __instance == null || !__instance.requireGround)
			return;
		if (__instance.GetComponent<SurvivorNote>() == null
			&& __instance.GetComponentInChildren<SurvivorNote>(true) == null)
			return;
		WorldGeneration world = WorldGeneration.world;
		__instance.requireGround = false;
		if (world != null)
			__instance.blockPlacedOn = world.WorldToBlockPos(__instance.transform.position + Vector3.down * 0.5f);
		if (__instance.health < 0.5f)
			__instance.health = 5f;
	}

	private static void WallNoteEnter()
	{
		_wallNoteDepth++;
	}

	private static void WallNoteExit()
	{
		if (_wallNoteDepth > 0)
			_wallNoteDepth--;
	}

	/// <summary>false skips Awake. a pump under a lifepod still takes the 20% roll.</summary>
	private static bool KeepStandalonePumpPrefix(LifepodPump __instance)
	{
		if (__instance == null)
			return true;
		return __instance.GetComponentInParent<LifepodController>() != null;
	}

	/// <summary>plugin Update, after other mods have loaded. one try is enough once the late patches land.</summary>
	internal static void TryPatchLate()
	{
		if (!_notePatched)
			TryPatchWallNote();
		if (_lootPatched && _entityPatched && _cuCorpsePatched && _notePatched)
			return;
		TryPatchCuCorpse();
		TryPatchCustomLoot();
	}

	private static bool _notePatched;

	private static void TryPatchWallNote()
	{
		if (_harmony == null)
			return;
		System.Type helper = TypeLookup.Find("Custom_Structures.StructureHelper");
		if (helper == null)
		{
			if (TypeLookup.PluginsReady)
				_notePatched = true;
			return;
		}
		MethodInfo spawn = AccessTools.Method(helper, "TrySpawnWallNote");
		if (spawn == null)
		{
			_notePatched = true;
			return;
		}
		_harmony.Patch(
			spawn,
			prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(WallNoteEnter)),
			postfix: new HarmonyMethod(typeof(StructureLiveState), nameof(WallNoteExit)));
		_notePatched = true;
	}

	// pocket marks for this layer. a new world gen clears these.
	private static readonly HashSet<long> PocketOnce = new HashSet<long>();
	private static Harmony _harmony;
	private static bool _lootPatched;
	private static bool _entityPatched;
	private static bool _cuCorpsePatched;

	internal static void ResetLayer()
	{
		PocketOnce.Clear();
		RollsClosed = false;
	}

	/// <summary>gen bake finished. corpse Start and structure loot markers stay off for this layer.</summary>
	internal static bool RollsClosed;

	/// <summary>writeback stored this item, and it was sitting on a building. that pocket is baked.</summary>
	internal static void NotePocketFromItem(Transform item)
	{
		if (item == null)
			return;
		for (Transform p = item.parent; p != null; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return;
			if (p.GetComponent<BuildingEntity>() != null)
			{
				PocketOnce.Add(Key(p.position));
				return;
			}
		}
	}

	/// <summary>false skips vanilla Start. the loot roll already ran once during gen.</summary>
	private static bool CorpseStartPrefix(CorpseScript __instance)
	{
		if (!CorpseRollSuppressed())
			return true;
		if (RollsClosed && __instance != null)
		{
			Plugin.Log.LogInfo(
				"CPUOpt2 item corpse roll off "
				+ __instance.transform.position.x.ToString("0.0") + ","
				+ __instance.transform.position.y.ToString("0.0"));
		}
		return false;
	}

	private static bool CorpseRollSuppressed()
	{
		if (RollsClosed)
			return true;
		if (Plugin.StreamEnabled == null || !Plugin.StreamEnabled.Value)
			return false;
		WorldGeneration world = WorldGeneration.world;
		return world != null && world.generatingWorld;
	}

	private static void TryPatchCuCorpse()
	{
		if (_cuCorpsePatched || _harmony == null)
			return;
		System.Type patch = TypeLookup.Find("CUCoreLib.Patches.CorpsePatches");
		System.Reflection.MethodInfo prefix = patch != null ? AccessTools.Method(patch, "Prefix") : null;
		if (prefix == null)
			return;
		_harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(CuCorpseRollPrefix))
		{
			priority = Priority.First
		});
		_cuCorpsePatched = true;
	}

	/// <summary>
	/// false skips CUCoreLib.Patches.CorpsePatches.Prefix.
	/// that method is static, so a parameter named __instance is null. __0 is their first argument.
	/// after the gen bake this never rolls, including a corpse that is instantiated later.
	/// </summary>
	private static bool CuCorpseRollPrefix(CorpseScript __0)
	{
		// __0 is the corpse. suppressed means the gen bake already owns the pile.
		if (__0 == null)
			return !CorpseRollSuppressed();
		return !CorpseRollSuppressed();
	}

	private static void TryPatchCustomLoot()
	{
		if (_harmony == null)
			return;
		if (!_lootPatched)
		{
			System.Type reg = TypeLookup.Find("CUCoreLib.Registries.StructureRegistry");
			System.Reflection.MethodInfo spawn = reg != null ? AccessTools.Method(reg, "SpawnLootMarkers") : null;
			if (spawn != null)
			{
				_harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(CustomLootPrefix))
				{
					priority = Priority.First
				});
				_lootPatched = true;
			}
		}
		if (!_entityPatched)
		{
			System.Type helper = TypeLookup.Find("Custom_Structures.StructureHelper");
			System.Reflection.MethodInfo entity = helper != null ? AccessTools.Method(helper, "SpawnEntity") : null;
			if (entity != null)
			{
				_harmony.Patch(entity, prefix: new HarmonyMethod(typeof(StructureLiveState), nameof(CustomStructEntityPrefix))
				{
					priority = Priority.First
				});
				_entityPatched = true;
			}
		}
	}

	/// <summary>
	/// one roll for this layer. corpse loot, including ghost rows that never Start,
	/// and structure item children that the building instantiate would drop again.
	/// </summary>
	internal static void BakeLayerLoot(WorldGeneration world)
	{
		if (world == null)
			return;
		int before = CaptureStore.WorldItems.Count;
		var seen = new HashSet<long>();
		int corpses = 0;

		CorpseScript[] live = Resources.FindObjectsOfTypeAll<CorpseScript>();
		for (int i = 0; i < live.Length; i++)
		{
			CorpseScript corpse = live[i];
			if (corpse == null || !corpse.gameObject.scene.IsValid() || !corpse.gameObject.scene.isLoaded)
				continue;
			if (RollCorpse(world, corpse.animalCorpse, corpse.categories, corpse.transform.position, seen))
				corpses++;
		}

		for (int i = 0; i < CaptureStore.Entities.Count; i++)
		{
			EntityRecord er = CaptureStore.Entities[i];
			if (er == null)
				continue;
			GameObject prefab = ChunkObjects.LoadPrefab(er.Id);
			if (prefab == null)
				continue;
			CorpseScript corpse = prefab.GetComponent<CorpseScript>();
			if (corpse != null)
			{
				bool animal = er.AnimalCorpse.HasValue ? er.AnimalCorpse.Value : corpse.animalCorpse;
				if (RollCorpse(world, animal, corpse.categories, er.Position, seen))
					corpses++;
			}
			// live buildings are walked from the scene. this prefab walk is the ghost row.
			if (!er.LiveAtCapture)
				BakePrefabPocket(world, prefab, er.Position, er.RotationZ, er.ScaleNegX == true);
		}

		BuildingEntity[] builds = UnityEngine.Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < builds.Length; i++)
			BakeLivePocket(world, builds[i]);

		RollsClosed = true;
		Plugin.Log.LogInfo(
			"CPUOpt2 item bake corpses=" + corpses
			+ " items=" + (CaptureStore.WorldItems.Count - before));
	}

	private static bool RollCorpse(WorldGeneration world, bool animal, string[] categories, Vector3 pos, HashSet<long> seen)
	{
		if (seen == null || !seen.Add(Key(pos)))
			return false;
		if (animal || categories == null || categories.Length == 0)
			return true;
		int num = 0;
		float roll = UnityEngine.Random.Range(0f, 1f);
		if (roll > 0.5f)
			num++;
		if (roll > 0.85f)
			num++;
		if (roll > 0.95f)
			num++;
		for (int i = 0; i < num; i++)
		{
			string category = categories[UnityEngine.Random.Range(0, categories.Length)];
			string id = RollCorpseItemId(category);
			if (string.IsNullOrEmpty(id))
				continue;
			Vector3 drop = pos + new Vector3(UnityEngine.Random.Range(-3f, 3f), 3f, 0f);
			Vector2Int block = world.WorldToBlockPos(drop);
			CaptureStore.WorldItems.Add(new WorldItemRecord
			{
				Chunk = world.BlockToChunkPos(block),
				Id = id,
				Condition = UnityEngine.Random.Range(0f, 1f),
				Position = drop,
				RotationZ = UnityEngine.Random.Range(0f, 360f),
			});
			Vector2Int ghostChunk = world.BlockToChunkPos(block);
			ItemPath.Note("store", ghostChunk.x, ghostChunk.y, id, drop, "corpse-ghost");
		}
		return true;
	}

	private static bool _cuRollLooked;
	private static System.Reflection.MethodInfo _cuRoll;
	private static object _cuCorpsePool;
	private static bool _cuRollReady;

	private static string RollCorpseItemId(string category)
	{
		if (!_cuRollLooked)
		{
			_cuRollLooked = true;
			System.Type reg = TypeLookup.Find("CUCoreLib.Registries.DropPoolRegistry");
			System.Type pool = TypeLookup.Find("CUCoreLib.Data.DropPool");
			if (reg != null && pool != null)
			{
				_cuRoll = AccessTools.Method(reg, "TryGetRandomItemId");
				try
				{
					_cuCorpsePool = System.Enum.Parse(pool, "Corpse");
					_cuRollReady = _cuRoll != null && _cuCorpsePool != null;
				}
				catch
				{
					_cuRollReady = false;
				}
			}
		}
		if (_cuRollReady)
		{
			object[] args = new object[] { _cuCorpsePool, category, null };
			try
			{
				object ok = _cuRoll.Invoke(null, args);
				if (ok is bool hit && hit)
					return args[2] as string;
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("CPUOpt2 corpse roll failed: " + ex.Message);
			}
			return null;
		}
		if (ItemLootPool.pool != null
			&& ItemLootPool.pool.TryGetValue(category, out System.Collections.Generic.List<string> list)
			&& list != null
			&& list.Count > 0)
			return list[UnityEngine.Random.Range(0, list.Count)];
		return null;
	}

	private static void BakeLivePocket(WorldGeneration world, BuildingEntity build)
	{
		if (world == null || build == null)
			return;
		Item[] items = build.GetComponentsInChildren<Item>(true);
		bool any = false;
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null || item.transform == build.transform)
				continue;
			if (UnderItemBefore(item.transform, build.transform))
				continue;
			any = true;
			if (AlreadyStored(item.id, item.transform.position))
				continue;
			Vector2Int block = world.WorldToBlockPos(item.transform.position);
			WorldItemRecord pocket = new WorldItemRecord
			{
				Chunk = world.BlockToChunkPos(block),
				Id = string.IsNullOrEmpty(item.id) ? CleanId(item.gameObject.name) : item.id,
				Condition = item.condition,
				Battery = BatterySnap.Read(item),
				Position = item.transform.position,
				RotationZ = item.transform.eulerAngles.z,
				LocalScale = item.transform.localScale,
				// still in the scene. spawn skips until the chunk destroy clears the flag.
				LiveAtCapture = true,
			};
			NetHold.Link(item.gameObject, pocket);
			CaptureStore.WorldItems.Add(pocket);
			ItemPath.Note("store", pocket.Chunk.x, pocket.Chunk.y, pocket.Id, pocket.Position,
				"bake-live build=" + build.id + " iid=" + item.GetInstanceID() + " sceneId=" + (item.id ?? ""));
		}
		if (any)
			PocketOnce.Add(Key(build.transform.position));
	}

	private static void BakePrefabPocket(WorldGeneration world, GameObject prefab, Vector3 worldPos, float rotZ, bool negX)
	{
		if (world == null || prefab == null)
			return;
		Item[] items = prefab.GetComponentsInChildren<Item>(true);
		bool any = false;
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null || item.transform == prefab.transform)
				continue;
			if (UnderItemBefore(item.transform, prefab.transform))
				continue;
			Vector3 local = prefab.transform.InverseTransformPoint(item.transform.position);
			if (negX)
				local.x = -local.x;
			Vector3 drop = worldPos + Quaternion.Euler(0f, 0f, rotZ) * local;
			any = true;
			string id = string.IsNullOrEmpty(item.id) ? CleanId(item.gameObject.name) : item.id;
			if (AlreadyStored(id, drop))
				continue;
			Vector2Int block = world.WorldToBlockPos(drop);
			Vector2Int prefabChunk = world.BlockToChunkPos(block);
			CaptureStore.WorldItems.Add(new WorldItemRecord
			{
				Chunk = prefabChunk,
				Id = id,
				Condition = item.condition,
				Battery = BatterySnap.Read(item),
				Position = drop,
				RotationZ = rotZ + item.transform.localEulerAngles.z,
				LocalScale = item.transform.localScale,
			});
			ItemPath.Note("store", prefabChunk.x, prefabChunk.y, id, drop, "bake-prefab");
		}
		if (any)
			PocketOnce.Add(Key(worldPos));
	}

	private static bool UnderItemBefore(Transform item, Transform stop)
	{
		for (Transform p = item.parent; p != null && p != stop; p = p.parent)
		{
			if (p.GetComponent<Item>() != null)
				return true;
		}
		return false;
	}

	private static bool AlreadyStored(string id, Vector3 pos)
	{
		for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
		{
			WorldItemRecord rec = CaptureStore.WorldItems[i];
			if (rec == null)
				continue;
			if (!string.Equals(rec.Id, id, System.StringComparison.Ordinal))
				continue;
			if ((rec.Position - pos).sqrMagnitude <= 0.25f)
				return true;
		}
		return false;
	}

	private static string CleanId(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unknown";
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}

	/// <summary>false skips CUCoreLib.StructureRegistry.SpawnLootMarkers. gen already placed that pocket.</summary>
	private static bool CustomLootPrefix(Vector2 worldBase)
	{
		if (!RollsClosed)
			return true;
		Plugin.Log.LogInfo("CPUOpt2 item loot off " + worldBase.x.ToString("0.0") + "," + worldBase.y.ToString("0.0"));
		return false;
	}

	/// <summary>false skips Custom_Structures.StructureHelper.SpawnEntity. gen already placed that pocket.</summary>
	private static bool CustomStructEntityPrefix(Vector2 pos)
	{
		if (!RollsClosed)
			return true;
		Plugin.Log.LogInfo("CPUOpt2 item entity off " + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0"));
		return false;
	}

	private static void DropBakedPocket(GameObject go)
	{
		if (go == null || !PocketOnce.Contains(Key(go.transform.position)))
			return;
		if (RemovePocketItems(go.transform) <= 0)
			return;
		Plugin.Log.LogInfo(
			"CPUOpt2 item once pocket "
			+ go.transform.position.x.ToString("0.0") + ","
			+ go.transform.position.y.ToString("0.0"));
	}

	private static int RemovePocketItems(Transform root)
	{
		if (root == null)
			return 0;
		int n = 0;
		Item[] items = root.GetComponentsInChildren<Item>(true);
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null || item.transform == root)
				continue;
			bool underOtherItem = false;
			Transform p = item.transform.parent;
			while (p != null && p != root)
			{
				if (p.GetComponent<Item>() != null)
				{
					underOtherItem = true;
					break;
				}
				p = p.parent;
			}
			if (underOtherItem)
				continue;
			UnityEngine.Object.DestroyImmediate(item.gameObject);
			n++;
		}
		return n;
	}

	private static long Key(Vector3 p)
	{
		int x = Mathf.RoundToInt(p.x * 4f);
		int y = Mathf.RoundToInt(p.y * 4f);
		return ((long)x << 32) ^ (uint)y;
	}

	private static long Key(Vector2 p)
	{
		return Key(new Vector3(p.x, p.y, 0f));
	}

	private static void CannonUpdatePostfix(SoundCannon __instance)
	{
		if (__instance == null)
			return;
		CannonStateHold hold = __instance.GetComponent<CannonStateHold>();
		if (hold == null)
			return;
		WriteCannon(__instance, hold.Spent, hold.Charging, hold.ChargeTime, hold.BarrelZ);
		PushCannonTracker(__instance.gameObject, hold);
		UnityEngine.Object.Destroy(hold);
	}

	private static void WriteCannon(SoundCannon cannon, bool spent, bool charging, float chargeTime, float barrelZ)
	{
		if (cannon == null)
			return;
		FieldAccess.Set(cannon, "spent", spent);
		FieldAccess.Set(cannon, "charging", charging);
		FieldAccess.Set(cannon, "chargeTime", chargeTime);
		if (cannon.transform.childCount <= 0)
			return;
		Transform barrel = cannon.transform.GetChild(0);
		Vector3 euler = barrel.eulerAngles;
		euler.z = barrelZ;
		barrel.eulerAngles = euler;
	}

	/// <summary>Krok and Together trackers keep a second spent flag and aim the same child.</summary>
	private static void PushCannonTracker(GameObject go, CannonStateHold hold)
	{
		if (go == null || hold == null)
			return;
		Component[] parts = go.GetComponents<Component>();
		for (int i = 0; i < parts.Length; i++)
		{
			Component part = parts[i];
			if (part == null || part is SoundCannon || part is CannonStateHold)
				continue;
			string name = part.GetType().Name;
			if (name.IndexOf("SoundCannon", System.StringComparison.Ordinal) < 0)
				continue;
			if (name.IndexOf("Tracker", System.StringComparison.Ordinal) < 0)
				continue;
			FieldAccess.Set(part, "spent", hold.Spent);
			FieldAccess.Set(part, "charging", hold.Charging);
			FieldAccess.Set(part, "chargeTime", hold.ChargeTime);
		}
	}

	internal static void CaptureEnemy(GameObject go, EnemyRecord record)
	{
		if (go == null || record == null)
			return;
		record.LocalScale = ScaleIfCustom(go.transform);
	}

	internal static void CaptureItem(Item item, WorldItemRecord record)
	{
		if (item == null || record == null)
			return;
		record.AmmoRounds = ReadAmmo(item);
		ReadRolled(item.gameObject, out record.Sprite, out record.FlipX, out record.PlushIndex);
	}

	internal static void CaptureContent(Item item, ItemContentRecord record)
	{
		if (item == null || record == null)
			return;
		record.AmmoRounds = ReadAmmo(item);
		ReadRolled(item.gameObject, out record.Sprite, out record.FlipX, out record.PlushIndex);
	}

	internal static void ApplyItemVisual(GameObject go, Sprite sprite, bool? flipX, int? plushIndex)
	{
		if (go == null)
			return;
		System.Type plushType = PlushType();
		Component plush = plushType != null ? go.GetComponent(plushType) : null;
		if (plush != null && plushIndex.HasValue && _plushIndex != null)
			_plushIndex.SetValue(plush, plushIndex.Value);
		if (sprite == null && !flipX.HasValue)
			return;
		if (plush == null)
			return;
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		if (sr == null)
			return;
		if (sprite != null)
			sr.sprite = sprite;
		if (flipX.HasValue)
			sr.flipX = flipX.Value;
	}

	internal static void ReplayCrystalEffects(GameObject go)
	{
		if (go == null)
			return;
		CrystalBehaviour crystal = go.GetComponent<CrystalBehaviour>() ?? go.GetComponentInChildren<CrystalBehaviour>();
		if (crystal == null)
			return;
		System.Reflection.MethodInfo setup = HarmonyLib.AccessTools.Method(typeof(CrystalBehaviour), "SetUpEffects");
		if (setup == null)
			return;
		try
		{
			setup.Invoke(crystal, null);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"crystal effects replay failed: {ex.Message}");
		}
	}

	private static Type _tmpText;
	private static bool _tmpLooked;

	private static void CaptureNote(GameObject go, EntityRecord record)
	{
		SurvivorNote note = go.GetComponent<SurvivorNote>();
		if (note == null)
			note = go.GetComponentInChildren<SurvivorNote>(true);
		if (note == null)
			return;
		record.HasNote = true;
		record.NoteText = FieldAccess.Get<string>(note, "loreString");
		record.NoteFont = FieldAccess.Get<UnityEngine.Object>(note, "fontAsset");
		record.NoteSprite = FieldAccess.Get<Sprite>(note, "loreSprite");

		Type tmp = TmpTextType();
		if (tmp == null)
			return;
		Component[] texts = go.GetComponentsInChildren(tmp, true);
		if (texts == null || texts.Length == 0)
			return;
		PropertyInfo textProp = tmp.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
		PropertyInfo sizeProp = tmp.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
		PropertyInfo colorProp = tmp.GetProperty("color", BindingFlags.Instance | BindingFlags.Public);
		Component first = texts[0];
		if (textProp != null)
			record.NoteWallText = textProp.GetValue(first, null) as string;
		if (sizeProp != null && sizeProp.GetValue(first, null) is float size)
			record.NoteWallSize = size;
		if (colorProp != null && colorProp.GetValue(first, null) is Color color)
			record.NoteWallColor = color;
	}

	private static void ApplyNote(GameObject go, EntityRecord record)
	{
		if (!record.HasNote)
			return;
		// Start rolls a random line on the frame after SetActive, which is after this call.
		// LateUpdate writes the captured text again once that roll has happened.
		NoteHold hold = go.GetComponent<NoteHold>();
		if (hold == null)
			hold = go.AddComponent<NoteHold>();
		hold.Text = record.NoteText;
		hold.Font = record.NoteFont;
		hold.Picture = record.NoteSprite;
		hold.Wall = record.NoteWallText;
		hold.WallSize = record.NoteWallSize;
		hold.HasColor = record.NoteWallColor.HasValue;
		if (record.NoteWallColor.HasValue)
			hold.WallColor = record.NoteWallColor.Value;
		hold.Write();
	}

	internal sealed class NoteHold : MonoBehaviour
	{
		internal string Text;
		internal UnityEngine.Object Font;
		internal Sprite Picture;
		internal string Wall;
		internal float WallSize;
		internal bool HasColor;
		internal Color WallColor;
		private int _frames;

		private void LateUpdate()
		{
			Write();
			if (++_frames >= 3)
				Destroy(this);
		}

		internal void Write()
		{
			SurvivorNote note = GetComponent<SurvivorNote>();
			if (note == null)
				note = GetComponentInChildren<SurvivorNote>(true);
			if (note != null)
			{
				FieldAccess.Set(note, "loreString", Text);
				if (Font != null)
					FieldAccess.Set(note, "fontAsset", Font);
				FieldAccess.Set(note, "loreSprite", Picture);
			}
			if (Wall == null)
				return;
			Type tmp = TmpTextType();
			if (tmp == null)
				return;
			Component[] texts = gameObject.GetComponentsInChildren(tmp, true);
			if (texts == null)
				return;
			PropertyInfo textProp = tmp.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
			PropertyInfo sizeProp = tmp.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
			PropertyInfo colorProp = tmp.GetProperty("color", BindingFlags.Instance | BindingFlags.Public);
			for (int i = 0; i < texts.Length; i++)
			{
				Component row = texts[i];
				if (row == null)
					continue;
				if (textProp != null && textProp.CanWrite)
					textProp.SetValue(row, Wall, null);
				if (WallSize > 0f && sizeProp != null && sizeProp.CanWrite)
					sizeProp.SetValue(row, WallSize, null);
				if (HasColor && colorProp != null && colorProp.CanWrite)
					colorProp.SetValue(row, WallColor, null);
			}
		}
	}

	private static Type TmpTextType()
	{
		if (_tmpLooked)
			return _tmpText;
		_tmpLooked = true;
		_tmpText = AccessTools.TypeByName("TMPro.TMP_Text");
		return _tmpText;
	}

	internal static void ApplyEntity(GameObject go, EntityRecord record)
	{
		if (go == null || record == null)
			return;
		if (record.DoorOpen == true)
			OpenDoor(go);
		ApplyLights(go, record.Lights, record.LightMasks);
		ApplyScale(go.transform, record.LocalScale);
		ApplyRolledSprite(go, record);
		// Start already rolled a random locale line. put the captured note back.
		ApplyNote(go, record);
	}

	/// <summary>
	/// which Resources prefab. trader2's character field is 1 (milky), not 2.
	/// the object name is the prefab. the field is the locale index.
	/// </summary>
	internal static int ResolveTraderPrefab(TraderScript trader)
	{
		int fromName = TraderNumber(trader != null ? trader.gameObject.name : null);
		if (fromName > 0)
			return fromName;
		// 0 experiment, 1 milky, 2 dune -> trader1/2/3
		if (trader != null && trader.character >= 0 && trader.character <= 2)
			return trader.character + 1;
		return 0;
	}

	/// <summary>TraderScript.character for a traderN prefab. trader2 -> 1</summary>
	internal static int GameCharacterForPrefab(int prefabIndex)
	{
		if (prefabIndex >= 1 && prefabIndex <= 3)
			return prefabIndex - 1;
		return 0;
	}

	internal static void CaptureTraderBody(TraderScript trader, TraderRecord record)
	{
		if (trader == null || record == null)
			return;
		SpriteRenderer[] sprites = trader.GetComponentsInChildren<SpriteRenderer>(true);
		if (sprites == null || sprites.Length == 0)
			return;
		List<BodySprite> snaps = new List<BodySprite>();
		for (int i = 0; i < sprites.Length; i++)
		{
			SpriteRenderer sr = sprites[i];
			if (sr == null || sr.sprite == null)
				continue;
			snaps.Add(new BodySprite
			{
				Path = IndexPath(trader.transform, sr.transform),
				Sprite = sr.sprite,
				FlipX = sr.flipX,
			});
		}
		if (snaps.Count > 0)
			record.BodySprites = snaps;
	}

	internal static void ApplyTraderBody(TraderScript trader, TraderRecord record)
	{
		if (trader == null || record == null || record.BodySprites == null)
			return;
		for (int i = 0; i < record.BodySprites.Count; i++)
		{
			BodySprite snap = record.BodySprites[i];
			if (snap == null || snap.Sprite == null)
				continue;
			Transform t = FindIndexPath(trader.transform, snap.Path);
			if (t == null)
				continue;
			SpriteRenderer sr = t.GetComponent<SpriteRenderer>();
			if (sr == null)
				continue;
			sr.sprite = snap.Sprite;
			sr.flipX = snap.FlipX;
		}
	}

	internal static void ApplyEnemy(GameObject go, EnemyRecord record)
	{
		if (go == null || record == null)
			return;
		ApplyScale(go.transform, record.LocalScale);
	}

	internal static void ApplyAmmo(GameObject go, int? rounds)
	{
		if (go == null || !rounds.HasValue)
			return;
		AmmoScript ammo = go.GetComponent<AmmoScript>();
		if (ammo != null)
			ammo.rounds = rounds.Value;
	}

	private static Vector3? ScaleIfCustom(Transform t)
	{
		if (t == null)
			return null;
		Vector3 s = t.localScale;
		if (Mathf.Approximately(s.x, 1f) && Mathf.Approximately(s.y, 1f) && Mathf.Approximately(s.z, 1f))
			return null;
		return s;
	}

	private static void ApplyScale(Transform t, Vector3? scale)
	{
		if (t == null || !scale.HasValue)
			return;
		t.localScale = scale.Value;
	}

	/// <summary>null when this isnt a door. true when CS shoved it into the background.</summary>
	private static bool? DoorOpen(GameObject go, BuildingEntity build)
	{
		if (!IsDoor(go, build))
			return null;
		SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			if (sprites[i] != null && sprites[i].sortingOrder <= -999)
				return true;
		}
		Collider2D[] cols = go.GetComponentsInChildren<Collider2D>(true);
		for (int i = 0; i < cols.Length; i++)
		{
			if (cols[i] != null && !cols[i].enabled)
				return true;
		}
		return false;
	}

	private static bool IsDoor(GameObject go, BuildingEntity build)
	{
		if (build != null && LooksLikeDoor(build.id))
			return true;
		return go != null && LooksLikeDoor(Clean(go.name));
	}

	private static bool LooksLikeDoor(string id)
	{
		return !string.IsNullOrEmpty(id)
			&& id.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static void OpenDoor(GameObject go)
	{
		SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			if (sprites[i] == null)
				continue;
			sprites[i].color = Color.gray;
			sprites[i].sortingOrder = -999;
		}
		Collider2D[] cols = go.GetComponentsInChildren<Collider2D>(true);
		for (int i = 0; i < cols.Length; i++)
		{
			if (cols[i] != null)
				cols[i].enabled = false;
		}
		BuildingEntity build = go.GetComponent<BuildingEntity>();
		if (build != null)
			build.Backgroundify();
	}

	private static void CaptureLights(GameObject go, out List<LightSnap> lights, out List<MaskSnap> masks)
	{
		lights = null;
		masks = null;
		Light2D[] light2ds = go.GetComponentsInChildren<Light2D>(true);
		Light[] unityLights = go.GetComponentsInChildren<Light>(true);
		if ((light2ds == null || light2ds.Length == 0) && (unityLights == null || unityLights.Length == 0))
			return;

		lights = new List<LightSnap>();
		for (int i = 0; i < light2ds.Length; i++)
		{
			Light2D light = light2ds[i];
			if (light == null)
				continue;
			lights.Add(new LightSnap
			{
				Path = IndexPath(go.transform, light.transform),
				Index = ComponentIndex(light),
				IsLight2D = true,
				Enabled = light.enabled,
				Intensity = QoLLightScale.Authored(light),
			});
		}
		for (int i = 0; i < unityLights.Length; i++)
		{
			Light light = unityLights[i];
			if (light == null)
				continue;
			lights.Add(new LightSnap
			{
				Path = IndexPath(go.transform, light.transform),
				Index = ComponentIndex(light),
				IsLight2D = false,
				Enabled = light.enabled,
				Intensity = light.intensity,
			});
		}
		if (lights.Count == 0)
		{
			lights = null;
			return;
		}

		SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			SpriteRenderer sr = sprites[i];
			if (sr == null)
				continue;
			string name = sr.gameObject.name;
			if (string.IsNullOrEmpty(name) || name.IndexOf("mask", System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			if (masks == null)
				masks = new List<MaskSnap>();
			masks.Add(new MaskSnap
			{
				Path = IndexPath(go.transform, sr.transform),
				Index = ComponentIndex(sr),
				Enabled = sr.enabled,
			});
		}
	}

	private static void ApplyLights(GameObject go, List<LightSnap> lights, List<MaskSnap> masks)
	{
		if (lights != null)
		{
			for (int i = 0; i < lights.Count; i++)
			{
				LightSnap snap = lights[i];
				if (snap == null)
					continue;
				Transform t = FindIndexPath(go.transform, snap.Path);
				if (t == null)
					continue;
				if (snap.IsLight2D)
				{
					Light2D[] found = t.GetComponents<Light2D>();
					if (snap.Index < 0 || snap.Index >= found.Length)
						continue;
					found[snap.Index].enabled = snap.Enabled;
					QoLLightScale.Apply(found[snap.Index], snap.Intensity);
				}
				else
				{
					Light[] found = t.GetComponents<Light>();
					if (snap.Index < 0 || snap.Index >= found.Length)
						continue;
					found[snap.Index].enabled = snap.Enabled;
					found[snap.Index].intensity = snap.Intensity;
				}
			}
		}

		if (masks == null)
			return;
		for (int i = 0; i < masks.Count; i++)
		{
			MaskSnap snap = masks[i];
			if (snap == null)
				continue;
			Transform t = FindIndexPath(go.transform, snap.Path);
			if (t == null)
				continue;
			SpriteRenderer[] found = t.GetComponents<SpriteRenderer>();
			if (snap.Index < 0 || snap.Index >= found.Length)
				continue;
			found[snap.Index].enabled = snap.Enabled;
		}
	}

	private static int? ReadAmmo(Item item)
	{
		AmmoScript ammo = item.GetComponent<AmmoScript>();
		if (ammo == null)
			return null;
		return ammo.rounds;
	}

	private static int ComponentIndex(Component component)
	{
		if (component == null)
			return 0;
		Component[] all = component.gameObject.GetComponents(component.GetType());
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i] == component)
				return i;
		}
		return 0;
	}

	private static string IndexPath(Transform root, Transform target)
	{
		if (root == null || target == null || target == root)
			return "";
		List<int> parts = new List<int>();
		Transform cur = target;
		while (cur != null && cur != root)
		{
			Transform parent = cur.parent;
			if (parent == null)
				return "";
			parts.Add(cur.GetSiblingIndex());
			cur = parent;
		}
		if (cur != root)
			return "";
		parts.Reverse();
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		for (int i = 0; i < parts.Count; i++)
		{
			if (i > 0)
				sb.Append('/');
			sb.Append(parts[i]);
		}
		return sb.ToString();
	}

	private static Transform FindIndexPath(Transform root, string path)
	{
		if (root == null)
			return null;
		if (string.IsNullOrEmpty(path))
			return root;
		Transform cur = root;
		int start = 0;
		for (int i = 0; i <= path.Length; i++)
		{
			if (i != path.Length && path[i] != '/')
				continue;
			if (!int.TryParse(path.Substring(start, i - start), out int index))
				return null;
			if (index < 0 || index >= cur.childCount)
				return null;
			cur = cur.GetChild(index);
			start = i + 1;
		}
		return cur;
	}

	private static void CaptureRolledSprite(GameObject go, EntityRecord record)
	{
		ReadRolled(go, out record.Sprite, out record.FlipX, out record.PlushIndex);
	}

	private static void ReadRolled(GameObject go, out Sprite sprite, out bool? flipX, out int? plushIndex)
	{
		sprite = null;
		flipX = null;
		plushIndex = null;
		if (go == null)
			return;
		CorpseScript corpse = go.GetComponent<CorpseScript>();
		System.Type plushType = PlushType();
		Component plush = plushType != null ? go.GetComponent(plushType) : null;
		if (corpse == null && plush == null)
			return;
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		if (sr != null)
		{
			sprite = sr.sprite;
			flipX = sr.flipX;
		}
		if (plush == null || _plushIndex == null)
			return;
		object idx = _plushIndex.GetValue(plush);
		if (idx is int n)
			plushIndex = n;
	}

	private static void ApplyRolledSprite(GameObject go, EntityRecord record)
	{
		System.Type plushType = PlushType();
		Component plush = plushType != null ? go.GetComponent(plushType) : null;
		if (plush != null && record.PlushIndex.HasValue && _plushIndex != null)
			_plushIndex.SetValue(plush, record.PlushIndex.Value);

		if (record.Sprite == null && !record.FlipX.HasValue)
			return;
		// vines already store FlipX. only restamp a corpse or plush, whose Awake rolls a new sprite
		if (go.GetComponent<CorpseScript>() == null && plush == null)
			return;
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		if (sr == null)
			return;
		if (record.Sprite != null)
			sr.sprite = record.Sprite;
		if (record.FlipX.HasValue)
			sr.flipX = record.FlipX.Value;
	}

	private static bool _plushLooked;
	private static System.Type _plushType;
	private static System.Reflection.FieldInfo _plushIndex;

	private static System.Type PlushType()
	{
		if (_plushLooked)
			return _plushType;
		System.Type found = TypeLookup.Find("PlushScript");
		if (found == null && !TypeLookup.PluginsReady)
			return null;
		_plushLooked = true;
		_plushType = found;
		if (_plushType != null)
			_plushIndex = HarmonyLib.AccessTools.Field(_plushType, "index");
		return _plushType;
	}

	private static void CaptureCrystalValue(GameObject go, EntityRecord record)
	{
		System.Type marker = CrystalMarkerType();
		if (marker == null || _crystalValue == null)
			return;
		Component found = go.GetComponent(marker);
		if (found == null)
			return;
		record.CrystalValue = _crystalValue.GetValue(found) as string;
	}

	internal static void AttachCrystalMarker(GameObject go, string value)
	{
		if (go == null || string.IsNullOrEmpty(value))
			return;
		if (go.GetComponent<CrystalBehaviour>() == null && go.GetComponentInChildren<CrystalBehaviour>() == null)
			return;
		System.Type marker = CrystalMarkerType();
		if (marker == null || _crystalValue == null)
			return;
		Component found = go.GetComponent(marker);
		if (found == null)
			found = go.AddComponent(marker);
		_crystalValue.SetValue(found, value);
		if (_crystalInit != null)
			_crystalInit.SetValue(found, false);
	}

	private static System.Type _crystalType;
	private static bool _crystalLooked;
	private static System.Reflection.FieldInfo _crystalValue;
	private static System.Reflection.FieldInfo _crystalInit;

	private static System.Type CrystalMarkerType()
	{
		if (_crystalLooked)
			return _crystalType;
		System.Type found = TypeLookup.Find("Custom_Structures.StructureHelper+CrystalPropertiesMarker");
		if (found == null && !TypeLookup.PluginsReady)
			return null;
		_crystalLooked = true;
		_crystalType = found;
		if (_crystalType == null)
			return null;
		_crystalValue = HarmonyLib.AccessTools.Field(_crystalType, "Value");
		_crystalInit = HarmonyLib.AccessTools.Field(_crystalType, "Initialized");
		return _crystalType;
	}

	private static int TraderNumber(string name)
	{
		if (string.IsNullOrEmpty(name))
			return 0;
		for (int c = 3; c >= 1; c--)
		{
			if (name.IndexOf("trader" + c, System.StringComparison.OrdinalIgnoreCase) >= 0)
				return c;
		}
		return 0;
	}

	private static string Clean(string name)
	{
		if (string.IsNullOrEmpty(name))
			return name;
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}
}

/// <summary>one Update after spawn, so the MP tracker copies has-fired and the dish angle</summary>
internal sealed class CannonStateHold : MonoBehaviour
{
	internal bool Spent;
	internal bool Charging;
	internal float ChargeTime;
	internal float BarrelZ;
}
