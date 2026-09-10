using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ChunkSimUpdateSkip
{
	internal static bool ShouldRunAt(MonoBehaviour instance)
	{
		if (!ChunkSimState.IsActive)
			return true;
		return ChunkSimState.ShouldSimulatePresentationWorldPos(instance.transform.position);
	}

	internal static bool ShouldRunBuildingUpdate(BuildingEntity building)
	{
		if (!ChunkSimState.IsActive)
			return true;
		if (building.GetComponent<ElderThornbackBehaviour>())
			return true;
		if (building.ignoreBodyOptimize)
			return true;
		return ChunkSimState.ShouldSimulatePresentationWorldPos(building.transform.position);
	}
}
