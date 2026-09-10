using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal enum BuildingSimClass
{
	None,
	Elder,
	Dynamic,
	Static
}

// incremental buildingentity registry - no findobjectsbytype in telemetry
internal static class BuildingSimRegistry
{
	private static readonly List<BuildingEntity> Buildings = new List<BuildingEntity>(768);
	private static readonly HashSet<BuildingEntity> Known = new HashSet<BuildingEntity>();

	internal static int ElderCount { get; private set; }
	internal static int DynamicCount { get; private set; }
	internal static int StaticCount { get; private set; }

	internal static int Count => Buildings.Count;

	internal static IReadOnlyList<BuildingEntity> All => Buildings;

	internal static void Register(BuildingEntity building)
	{
		if (!building || !Known.Add(building))
			return;
		Buildings.Add(building);
	}

	internal static void Unregister(BuildingEntity building)
	{
		if (!building)
			return;
		if (!Known.Remove(building))
			return;
		Buildings.Remove(building);
	}

	internal static void SetClass(BuildingSimClass oldClass, BuildingSimClass newClass)
	{
		if (oldClass == newClass)
			return;

		Decrement(oldClass);
		Increment(newClass);
	}

	internal static void ResetCounts()
	{
		ElderCount = 0;
		DynamicCount = 0;
		StaticCount = 0;
	}

	internal static void Clear()
	{
		Buildings.Clear();
		Known.Clear();
		ResetCounts();
	}

	private static void Increment(BuildingSimClass cls)
	{
		switch (cls)
		{
			case BuildingSimClass.Elder:
				ElderCount++;
				break;
			case BuildingSimClass.Dynamic:
				DynamicCount++;
				break;
			case BuildingSimClass.Static:
				StaticCount++;
				break;
		}
	}

	private static void Decrement(BuildingSimClass cls)
	{
		switch (cls)
		{
			case BuildingSimClass.Elder:
				ElderCount--;
				break;
			case BuildingSimClass.Dynamic:
				DynamicCount--;
				break;
			case BuildingSimClass.Static:
				StaticCount--;
				break;
		}
	}
}
