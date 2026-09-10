using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal sealed class ChunkSimTrackState
{
	internal Rigidbody2D Rb;
	internal bool IsElder;
	internal bool IgnoreBodyOptimize;
	internal BuildingSimClass BuildingClass = BuildingSimClass.None;
	internal bool ItemLastInSim;
	internal bool ItemInitialized;
	internal bool ItemDisabledByOpt;
	internal ItemSiblingOptFlags ItemSiblingsDisabledByOpt;
	internal bool BuildingDisabledByOpt;
	internal bool BuildingDestroyQueued;
	internal float ItemDecayAccum;
	internal BuildingEntity IkBuilding;
	internal SpiderHandler IkSpider;
	internal int IkGateRevision = -1;
	internal bool IkGateAllow;
	internal bool CannonDisabledByOpt;
	internal bool CannonTrackerDisabledByOpt;
	internal Behaviour CannonTracker;
}

// per-instance cache keyed by unity instance id
internal static class ChunkSimTrackTable
{
	private static readonly Dictionary<int, ChunkSimTrackState> States = new Dictionary<int, ChunkSimTrackState>(1024);

	internal static ChunkSimTrackState Get(Object obj)
	{
		if (!obj)
			return null;

		int id = obj.GetInstanceID();
		if (!States.TryGetValue(id, out ChunkSimTrackState state))
		{
			state = new ChunkSimTrackState();
			States[id] = state;
		}

		return state;
	}

	internal static void Remove(Object obj)
	{
		if (!obj)
			return;
		States.Remove(obj.GetInstanceID());
	}

	internal static void Clear()
	{
		States.Clear();
	}
}
