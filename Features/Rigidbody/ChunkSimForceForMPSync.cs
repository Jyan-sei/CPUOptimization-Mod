using CPUOptimization.Features.Mp;

namespace CPUOptimization.Features.ChunkSim;

// disable trap forceformp off-window (same as sound cannon / building disable)
internal static class ChunkSimForceForMPSync
{
	internal static void SyncAll()
	{
		if (!ChunkSimState.IsActive)
			return;

		ForceForMPScheduler.SyncAllEnabledStates();
	}
}
