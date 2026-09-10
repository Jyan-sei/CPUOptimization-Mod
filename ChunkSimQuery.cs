using CPUOptimization.Features.ChunkSim;
using UnityEngine;

namespace CPUOptimization;

// public read-only queries for other mods (e.g. krokmpopt2 registry cull)
public static class ChunkSimQuery
{
	// true when cpuopt chunk-sim active and worldPos is in authority sim window (mp union or sp 2x2)
	public static bool IsAuthoritySimWorldPos(Vector3 worldPos) =>
		ChunkSimState.ShouldSimulateAuthorityWorldPos(worldPos);

	// true when chunk-sim enabled and a window is tracked
	public static bool IsChunkSimActive => ChunkSimState.IsActive;
}
