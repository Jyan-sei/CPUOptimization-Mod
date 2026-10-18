using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// chest contents are BuildingEntity drop tables, not an inventory.
/// copy them on every building, empties included.
/// child items are not stored on the building. writeback puts them in CaptureStore.WorldItems.
/// </summary>
internal static class BuildingLoot
{
	internal static void Capture(BuildingEntity build, EntityRecord record, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (build == null || record == null)
			return;

		record.HasDropTable = true;
		record.DropChanceMultiplier = build.dropChanceMultiplier;
		record.GuaranteedDropAmount = build.guaranteedDropAmount;
		CopyDrops(build.itemsDropOnDestroy, record.ItemsDropOnDestroy);
		CopyDrops(build.alwaysDrop, record.AlwaysDrop);
		if (build.itemCategoriesToAdd != null)
		{
			for (int i = 0; i < build.itemCategoriesToAdd.Length; i++)
			{
				if (!string.IsNullOrEmpty(build.itemCategoriesToAdd[i]))
					record.ItemCategoriesToAdd.Add(build.itemCategoriesToAdd[i]);
			}
		}
	}

	internal static void Apply(BuildingEntity build, EntityRecord record)
	{
		if (build == null || record == null || !record.HasDropTable)
			return;

		build.dropChanceMultiplier = record.DropChanceMultiplier;
		build.guaranteedDropAmount = record.GuaranteedDropAmount;
		build.itemsDropOnDestroy = ToDrops(record.ItemsDropOnDestroy);
		build.alwaysDrop = ToDrops(record.AlwaysDrop);
		build.itemCategoriesToAdd = record.ItemCategoriesToAdd != null
			? record.ItemCategoriesToAdd.ToArray()
			: new string[0];
	}

	/// <summary>items parented under a loose world item. same claim set as buildings.</summary>
	internal static void AppendChildren(Transform root, List<ItemContentRecord> into, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (root == null || into == null)
			return;
		for (int i = 0; i < root.childCount; i++)
			Take(root.GetChild(i), null, into, claimedItems, playerRoot);
	}

	private static void Take(Transform t, BuildingEntity owner, List<ItemContentRecord> into, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (t == null || into == null || PlayerCarry.Holds(t))
			return;

		BuildingEntity nested = t.GetComponent<BuildingEntity>();
		if (nested != null && nested != owner)
			return;

		Item item = t.GetComponent<Item>();
		if (item != null)
		{
			if (claimedItems != null && !claimedItems.Add(item.GetInstanceID()))
				return;
			into.Add(Fill(item, claimedItems, playerRoot));
			return;
		}

		for (int i = 0; i < t.childCount; i++)
			Take(t.GetChild(i), owner, into, claimedItems, playerRoot);
	}

	private static ItemContentRecord Fill(Item item, HashSet<int> claimedItems, Transform playerRoot)
	{
		string id = item.id;
		if (string.IsNullOrEmpty(id) && item.gameObject != null)
			id = Clean(item.gameObject.name);

		ItemContentRecord record = new ItemContentRecord
		{
			Id = id,
			Condition = item.condition,
			Battery = BatterySnap.Read(item),
			HasPose = true,
			Position = item.transform.position,
			RotationZ = item.transform.eulerAngles.z,
			LocalScale = item.transform.localScale,
			ModState = ModBehaviourState.Capture(item.gameObject),
		};
		record.HasLiquids = CopyLiquids(item, record.Liquids);
		AppendChildren(item.transform, record.Children, claimedItems, playerRoot);
		StructureLiveState.CaptureContent(item, record);
		NetHold.Link(item.gameObject, record);
		return record;
	}

	/// <summary>
	/// ghost rows never ran Awake, so stack is still null and HasLiquids stayed false.
	/// copy defaultContents so the first spawn drains and pours the prefab liquid
	/// instead of leaving Awake's pour as the only copy.
	/// </summary>
	internal static void CapturePrefabLiquids(Item item, WorldItemRecord record)
	{
		if (item == null || record == null)
			return;
		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water == null)
			return;
		record.HasLiquids = true;
		if (water.stack != null)
		{
			CopyLiquids(item, record.Liquids);
			return;
		}
		if (!(item.Stats is LiquidItemInfo info) || info.defaultContents == null)
			return;
		for (int i = 0; i < info.defaultContents.Count; i++)
		{
			LiquidStack stack = info.defaultContents[i];
			if (stack == null || string.IsNullOrEmpty(stack.liquidId) || stack.amount <= 0f)
				continue;
			record.Liquids.Add(new LiquidRecord { LiquidId = stack.liquidId, Amount = stack.amount });
		}
	}

	/// <summary>true when this is a water container. empty into means drained.</summary>
	internal static bool CopyLiquids(Item item, List<LiquidRecord> into)
	{
		if (item == null || into == null)
			return false;
		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water == null)
			return false;
		if (water.stack == null)
			return true;
		for (int i = 0; i < water.stack.Count; i++)
		{
			LiquidStack stack = water.stack[i];
			if (stack != null)
				into.Add(new LiquidRecord { LiquidId = stack.liquidId, Amount = stack.amount });
		}
		return true;
	}

	private static void CopyDrops(ItemDrop[] from, List<ItemDropRecord> into)
	{
		if (from == null || into == null)
			return;
		for (int i = 0; i < from.Length; i++)
		{
			ItemDrop drop = from[i];
			if (drop == null || string.IsNullOrEmpty(drop.id))
				continue;
			into.Add(new ItemDropRecord
			{
				Id = drop.id,
				Chance = drop.chance,
				ConditionMin = drop.conditionMin,
				ConditionMax = drop.conditionMax,
			});
		}
	}

	private static ItemDrop[] ToDrops(List<ItemDropRecord> from)
	{
		if (from == null || from.Count == 0)
			return new ItemDrop[0];
		ItemDrop[] drops = new ItemDrop[from.Count];
		for (int i = 0; i < from.Count; i++)
		{
			ItemDropRecord row = from[i];
			drops[i] = new ItemDrop
			{
				id = row.Id,
				chance = row.Chance,
				conditionMin = row.ConditionMin,
				conditionMax = row.ConditionMax,
			};
		}
		return drops;
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
