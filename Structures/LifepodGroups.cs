using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// lifepod button, controller, heater, and shower are one prefab child.
/// capture that parent once. the four fixture ids are not their own rows.
/// the pump is not in this group.
/// </summary>
internal static class LifepodGroups
{
	private static readonly List<GameObject> _roster = new List<GameObject>(32);

	internal static bool IsLooseFixtureId(string id)
	{
		return Same(id, "lifepodbuttonheat")
			|| Same(id, "lifepodbuttonshower")
			|| Same(id, "lifepodheater")
			|| Same(id, "lifepodshower");
	}

	/// <summary>heater or shower placed on its own, not a child of a lifepod.</summary>
	internal static bool StandaloneFixture(BuildingEntity build)
	{
		if (build == null || !IsLooseFixtureId(build.id))
			return false;
		return RootOf(build.transform) == null;
	}

	internal static bool IsPump(BuildingEntity build)
	{
		if (build == null)
			return false;
		if (build.GetComponent<LifepodPump>() != null)
			return true;
		return Same(build.id, "lifepodpump") || Same(build.gameObject.name, "lifepodpump");
	}

	/// <summary>this building is part of a lifepod that chunk unload does not destroy.</summary>
	internal static bool HostsPocket(BuildingEntity build)
	{
		return build != null && RootOf(build.transform) != null;
	}

	/// <summary>
	/// this building sits under a lifepod parent. claim the parent and the nested fixtures.
	/// caller writes one row for the parent.
	/// </summary>
	internal static bool TryGroup(BuildingEntity build, HashSet<int> claimed, out Transform root)
	{
		root = null;
		if (build == null || IsPump(build))
			return false;
		root = RootOf(build.transform);
		if (root == null)
			return false;
		Claim(root, claimed);
		return true;
	}

	internal static EntityRecord BuildRecord(WorldGeneration world, Transform root)
	{
		BuildingEntity build = root.GetComponent<BuildingEntity>();
		string id = Clean(root.name);
		EntityRecord rec = new EntityRecord
		{
			Chunk = world.BlockToChunkPos(world.WorldToBlockPos(root.position)),
			Id = id,
			Health = build != null ? build.health : 0f,
			RequireGround = build == null || build.requireGround,
			Position = root.position,
			RotationZ = root.eulerAngles.z,
			ScaleNegX = root.localScale.x < 0f ? true : (bool?)null,
			HasBlockPlacedOn = build != null,
			BlockPlacedOn = build != null ? build.blockPlacedOn : default,
			LifepodGroup = true,
			StructurePrefab = "Lifepod",
		};
		if (StructureCatalog.TryResolve(root.gameObject, id, out string path) && !string.IsNullOrEmpty(path))
			rec.StructurePrefab = path;
		ReadState(root, out rec.LifepodHeatState, out rec.LifepodShowerSpent);
		return rec;
	}

	internal static void Stamp(Transform root, EntityRecord record, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (root == null || record == null)
			return;
		BuildingEntity build = root.GetComponent<BuildingEntity>();
		ChunkObjects.CaptureVineHook(root.gameObject, record);
		StructureLiveState.CaptureEntity(root.gameObject, build, record);
		record.ModState = ModBehaviourState.Capture(root.gameObject);
		if (build == null)
			return;
		BuildingLoot.Capture(build, record, claimedItems, playerRoot);
	}

	/// <summary>true when some piece of this parent is still in a loaded chunk</summary>
	internal static bool AnyPieceLoaded(Transform root, WorldGeneration world, HashSet<long> unloadKeys)
	{
		if (root == null || world == null)
			return false;
		if (ChunkStillLoaded(world, root.position, unloadKeys))
			return true;

		BuildingEntity[] builds = root.GetComponentsInChildren<BuildingEntity>(true);
		for (int i = 0; i < builds.Length; i++)
		{
			BuildingEntity b = builds[i];
			if (b == null || IsPump(b))
				continue;
			if (ChunkStillLoaded(world, b.transform.position, unloadKeys))
				return true;
		}

		if (PieceLoaded<LifepodController>(root, world, unloadKeys))
			return true;
		if (PieceLoaded<LifepodButton>(root, world, unloadKeys))
			return true;
		if (PieceLoaded<Heater>(root, world, unloadKeys))
			return true;
		if (PieceLoaded<LifepodShower>(root, world, unloadKeys))
			return true;
		return false;
	}

	/// <summary>
	/// part of a lifepod parent. the pod stays for the layer, so this only claims the pieces
	/// and does not put the parent on the kill list.
	/// </summary>
	internal static bool ConsumeDestroy(
		BuildingEntity build,
		WorldGeneration world,
		HashSet<long> unloadKeys,
		HashSet<int> handledRoots,
		List<GameObject> kill)
	{
		if (build == null || IsPump(build))
			return false;
		Transform root = RootOf(build.transform);
		if (root == null)
			return false;
		if (!handledRoots.Add(root.GetInstanceID()))
			return true;
		// the pod stays once it is built. chunk unload does not take the parent down.
		return true;
	}

	internal static void RefreshStored(Transform root)
	{
		if (root == null)
			return;
		ReadState(root, out int heat, out bool spent);
		string id = Clean(root.name);
		Vector3 pos = root.position;
		List<EntityRecord> list = CaptureStore.Entities;
		for (int i = 0; i < list.Count; i++)
		{
			EntityRecord rec = list[i];
			if (rec == null || !rec.LifepodGroup)
				continue;
			if (!string.Equals(rec.Id, id, System.StringComparison.OrdinalIgnoreCase))
				continue;
			if ((rec.Position - pos).sqrMagnitude >= 0.25f)
				continue;
			rec.LifepodHeatState = heat;
			rec.LifepodShowerSpent = spent;
			return;
		}
	}

	internal static bool AlreadyLive(EntityRecord rec)
	{
		if (rec == null || !rec.LifepodGroup)
			return false;
		ChunkRoster.CopyAll(_roster);
		for (int i = 0; i < _roster.Count; i++)
		{
			LifepodController ctrl = _roster[i] != null ? _roster[i].GetComponent<LifepodController>() : null;
			if (ctrl == null)
				continue;
			Transform root = RootOf(ctrl.transform);
			if (root == null)
				continue;
			if ((root.position - rec.Position).sqrMagnitude < 0.25f)
				return true;
		}
		return false;
	}

	/// <summary>prefab child can contain a pump. the pump row spawns that on its own</summary>
	internal static void StripNestedPumps(GameObject go)
	{
		if (go == null)
			return;
		LifepodPump[] pumps = go.GetComponentsInChildren<LifepodPump>(true);
		for (int i = pumps.Length - 1; i >= 0; i--)
		{
			LifepodPump pump = pumps[i];
			if (pump == null || pump.gameObject == go)
				continue;
			Object.DestroyImmediate(pump.gameObject);
		}
	}

	/// <summary>after Start. Start always writes the hot tooltip and ToggleHeatState would bump the mode</summary>
	internal static void ApplySaved(GameObject go, EntityRecord rec)
	{
		if (go == null || rec == null || !rec.LifepodGroup)
			return;
		LifepodController ctrl = go.GetComponent<LifepodController>()
			?? go.GetComponentInChildren<LifepodController>(true);
		if (ctrl == null)
			return;

		int heat = rec.LifepodHeatState;
		if (heat < 0)
			heat = 0;
		if (heat > 2)
			heat = 2;
		ctrl.heatState = heat;

		if (heat == 0)
		{
			if (ctrl.heater != null)
			{
				ctrl.heater.desiredTemp = 39f;
				ctrl.heater.enabled = true;
			}
			if (ctrl.heatSprite != null)
				ctrl.heatSprite.color = ctrl.hotColor;
			SetHeatDesc(ctrl, "heaterhot");
		}
		else if (heat == 1)
		{
			if (ctrl.heater != null)
			{
				ctrl.heater.desiredTemp = 33f;
				ctrl.heater.enabled = true;
			}
			if (ctrl.heatSprite != null)
				ctrl.heatSprite.color = ctrl.coldColor;
			SetHeatDesc(ctrl, "heatercold");
		}
		else
		{
			if (ctrl.heater != null)
				ctrl.heater.enabled = false;
			if (ctrl.heatSprite != null)
				ctrl.heatSprite.color = ctrl.baseColor;
			SetHeatDesc(ctrl, "heateroff");
		}

		if (ctrl.shower != null && rec.LifepodShowerSpent)
			ctrl.shower.activated = true;
		if (ctrl.disinfectSprite != null)
			ctrl.disinfectSprite.color = rec.LifepodShowerSpent ? ctrl.offColor : ctrl.onColor;
	}

	private static void SetHeatDesc(LifepodController ctrl, string modeKey)
	{
		if (ctrl.heatButton == null || string.IsNullOrEmpty(ctrl.heatButton.id))
			return;
		string dsc = Locale.GetBuilding(ctrl.heatButton.id + "dsc");
		string mode = Locale.GetOther(modeKey);
		if (dsc == null || mode == null)
			return;
		ctrl.heatButton.description = dsc.Replace("<>", mode);
	}

	private static void ReadState(Transform root, out int heat, out bool spent)
	{
		heat = 0;
		spent = false;
		LifepodController ctrl = root.GetComponent<LifepodController>()
			?? root.GetComponentInChildren<LifepodController>(true);
		if (ctrl == null)
			return;
		heat = ctrl.heatState;
		if (heat < 0)
			heat = 0;
		if (heat > 2)
			heat = 2;
		if (ctrl.shower != null)
			spent = ctrl.shower.activated;
	}

	private static void Claim(Transform root, HashSet<int> claimed)
	{
		if (claimed == null)
			return;
		BuildingEntity[] all = root.GetComponentsInChildren<BuildingEntity>(true);
		for (int i = 0; i < all.Length; i++)
		{
			BuildingEntity b = all[i];
			if (b == null || IsPump(b))
				continue;
			claimed.Add(b.GetInstanceID());
		}
	}

	private static void DetachPumps(Transform root)
	{
		LifepodPump[] pumps = root.GetComponentsInChildren<LifepodPump>(true);
		for (int i = 0; i < pumps.Length; i++)
		{
			LifepodPump pump = pumps[i];
			if (pump == null || pump.transform == root)
				continue;
			pump.transform.SetParent(null, true);
		}
	}

	private static bool PieceLoaded<T>(Transform root, WorldGeneration world, HashSet<long> unloadKeys) where T : Component
	{
		T[] parts = root.GetComponentsInChildren<T>(true);
		for (int i = 0; i < parts.Length; i++)
		{
			if (parts[i] == null)
				continue;
			if (ChunkStillLoaded(world, parts[i].transform.position, unloadKeys))
				return true;
		}
		return false;
	}

	/// <summary>stream on: still in the keep window. gen cull: not in the unload set</summary>
	private static bool ChunkStillLoaded(WorldGeneration world, Vector3 pos, HashSet<long> unloadKeys)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		if (StreamState.Active)
			return StreamController.IsChunkDesired(chunk.x, chunk.y);
		if (unloadKeys == null)
			return true;
		return !unloadKeys.Contains(Key(chunk.x, chunk.y));
	}

	/// <summary>button's parent, the object that also has the controller under it</summary>
	private static Transform RootOf(Transform t)
	{
		if (t == null)
			return null;
		if (t.GetComponent<LifepodPump>() != null)
			return null;
		if (IsPump(t.GetComponent<BuildingEntity>()))
			return null;

		Transform cur = t;
		for (int depth = 0; depth < 5 && cur != null; depth++)
		{
			LifepodButton btn = cur.GetComponentInChildren<LifepodButton>(true);
			if (btn != null && btn.transform.parent != null)
			{
				Transform parent = btn.transform.parent;
				if (parent.GetComponentInChildren<LifepodController>(true) != null
					&& (t == parent || t.IsChildOf(parent)))
					return parent;
			}
			if (cur.parent == null || cur.parent.GetComponent<LifepodPump>() != null)
				break;
			cur = cur.parent;
		}
		return null;
	}

	private static bool Same(string value, string id)
	{
		if (string.IsNullOrEmpty(value))
			return false;
		return Clean(value).Equals(id, System.StringComparison.OrdinalIgnoreCase);
	}

	private static string Clean(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "";
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
