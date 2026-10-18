using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// optional. Krok virtual containers ship off, so this ships off.
/// when on, a bag's contents stay on that bag's record. a loose row with the
/// same id within 12 units is the copy that popped out beside the bag, and it
/// is not stored or spawned again. the bag itself still round-trips.
/// </summary>
internal static class BagVirt
{
	internal const float HoldReachSqr = 144f;

	internal static bool Active =>
		Plugin.VirtualBagContents != null && Plugin.VirtualBagContents.Value;

	internal static bool IsParked(Item item)
	{
		if (item == null)
			return false;
		Transform t = item.transform.parent;
		for (int guard = 0; t != null && guard < 6; guard++, t = t.parent)
		{
			string n = t.name;
			if (n == "Opt2_VirtualItemCache" || n == "Opt2_VirtualUseSlot")
				return true;
		}
		return false;
	}

	/// <summary>
	/// drop loose rows that duplicate a child already stored on a bag in this chunk.
	/// fresh rows are removed. old rows are marked used so the merge does not keep them.
	/// </summary>
	internal static void DropLooseCopies(
		List<WorldItemRecord> fresh,
		List<WorldItemRecord> old,
		List<bool> used)
	{
		if (!Active || fresh == null)
			return;
		for (int i = fresh.Count - 1; i >= 0; i--)
		{
			if (BesideBag(fresh[i], fresh, old, i, -1))
				fresh.RemoveAt(i);
		}
		if (old == null || used == null)
			return;
		for (int i = 0; i < old.Count; i++)
		{
			if (used[i])
				continue;
			if (BesideBag(old[i], fresh, old, -1, i))
				used[i] = true;
		}
	}

	/// <summary>
	/// true when some other stored row in this chunk already has this id in its contents.
	/// caller removes the row so SpawnNextWorldItem does not put it beside the bag.
	/// </summary>
	internal static bool IsStoredBesideBag(WorldItemRecord rec)
	{
		if (!Active || rec == null || string.IsNullOrEmpty(rec.Id))
			return false;
		List<WorldItemRecord> items = CaptureStore.WorldItems;
		for (int i = 0; i < items.Count; i++)
		{
			WorldItemRecord bag = items[i];
			if (bag == null || ReferenceEquals(bag, rec))
				continue;
			if (bag.Chunk.x != rec.Chunk.x || bag.Chunk.y != rec.Chunk.y)
				continue;
			if ((bag.Position - rec.Position).sqrMagnitude > HoldReachSqr)
				continue;
			if (TreeHasId(bag.Children, rec.Id))
				return true;
		}
		return false;
	}

	internal static bool UnderContainer(Transform t)
	{
		for (int guard = 0; t != null && guard < 8; guard++, t = t.parent)
		{
			if (t.GetComponent<Container>() != null)
				return true;
		}
		return false;
	}

	private static bool BesideBag(
		WorldItemRecord rec,
		List<WorldItemRecord> fresh,
		List<WorldItemRecord> old,
		int skipFresh,
		int skipOld)
	{
		if (rec == null || string.IsNullOrEmpty(rec.Id))
			return false;
		if (fresh != null)
		{
			for (int i = 0; i < fresh.Count; i++)
			{
				if (i == skipFresh)
					continue;
				if (Holds(fresh[i], rec))
					return true;
			}
		}
		if (old != null)
		{
			for (int i = 0; i < old.Count; i++)
			{
				if (i == skipOld)
					continue;
				if (Holds(old[i], rec))
					return true;
			}
		}
		return false;
	}

	private static bool Holds(WorldItemRecord bag, WorldItemRecord rec)
	{
		if (bag == null || rec == null || ReferenceEquals(bag, rec))
			return false;
		if (bag.Children == null || bag.Children.Count == 0)
			return false;
		if ((bag.Position - rec.Position).sqrMagnitude > HoldReachSqr)
			return false;
		return TreeHasId(bag.Children, rec.Id);
	}

	private static bool TreeHasId(List<ItemContentRecord> items, string id)
	{
		if (items == null || string.IsNullOrEmpty(id))
			return false;
		for (int i = 0; i < items.Count; i++)
		{
			ItemContentRecord child = items[i];
			if (child == null)
				continue;
			if (string.Equals(child.Id ?? "", id, System.StringComparison.Ordinal))
				return true;
			if (TreeHasId(child.Children, id))
				return true;
		}
		return false;
	}
}
