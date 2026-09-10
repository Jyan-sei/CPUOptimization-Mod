using System;

namespace CPUOptimization.Features.ChunkSim;

[Flags]
internal enum ItemSiblingOptFlags : byte
{
	None = 0,
	WaterContainer = 1,
	Battery = 2,
	Light = 4,
}
