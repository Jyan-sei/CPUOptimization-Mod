using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ElderRegistry
{
	private static readonly HashSet<int> GoIds = new HashSet<int>();
	internal static int Count => GoIds.Count;
	internal static int FotRescanCount { get; private set; }

	internal static void Clear()
	{
		GoIds.Clear();
	}

	internal static void Register(ElderThornbackBehaviour elder)
	{
		if (!elder)
			return;
		GoIds.Add(elder.gameObject.GetInstanceID());
	}

	internal static void Unregister(ElderThornbackBehaviour elder)
	{
		if (!elder)
			return;
		GoIds.Remove(elder.gameObject.GetInstanceID());
	}

	internal static bool IsOnGameObject(GameObject go)
	{
		return go && GoIds.Contains(go.GetInstanceID());
	}

	internal static void NoteFotRescan() => FotRescanCount++;

	internal static int ConsumeFotRescan()
	{
		int n = FotRescanCount;
		FotRescanCount = 0;
		return n;
	}
}
