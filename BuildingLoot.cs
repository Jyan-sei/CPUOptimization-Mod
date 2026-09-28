using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// chest contents are BuildingEntity drop tables, not an inventory.
/// copy them on every building, empties included.
/// child items (corpse pockets, anything parented under the building) ride NestedItems
/// so they are not also loose world items.
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

		AppendUnder(build.transform, build, record.NestedItems, claimedItems, playerRoot);
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

	private static void AppendUnder(Transform root, BuildingEntity owner, List<ItemContentRecord> into, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (root == null || into == null)
			return;
		for (int i = 0; i < root.childCount; i++)
			Take(root.GetChild(i), owner, into, claimedItems, playerRoot);
	}

	private static void Take(Transform t, BuildingEntity owner, List<ItemContentRecord> into, HashSet<int> claimedItems, Transform playerRoot)
	{
		if (t == null || into == null || IsUnder(playerRoot, t))
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
			HasPose = true,
			Position = item.transform.position,
			RotationZ = item.transform.eulerAngles.z,
			LocalScale = item.transform.localScale,
			ModState = ModBehaviourState.Capture(item.gameObject),
		};
		CopyLiquids(item, record.Liquids);
		AppendChildren(item.transform, record.Children, claimedItems, playerRoot);
		return record;
	}

	private static void CopyLiquids(Item item, List<LiquidRecord> into)
	{
		if (item == null || into == null)
			return;
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

	private static bool IsUnder(Transform root, Transform node)
	{
		if (root == null || node == null)
			return false;
		return node == root || node.IsChildOf(root);
	}
}
