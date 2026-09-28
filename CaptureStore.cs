using System.Collections.Generic;

namespace CPUOptimization2;

internal static class CaptureStore
{
	internal static readonly List<TraderRecord> Traders = new List<TraderRecord>();
	internal static readonly List<EnemyRecord> Enemies = new List<EnemyRecord>();
	internal static readonly List<EntityRecord> Entities = new List<EntityRecord>();
	internal static readonly List<WorldItemRecord> WorldItems = new List<WorldItemRecord>();
	internal static readonly List<LightRecord> Lights = new List<LightRecord>();
	internal static readonly List<ChunkTileFlipRecord> TileFlips = new List<ChunkTileFlipRecord>();

	internal static int BiomeDepth;
	internal static uint ChunkWidth;
	internal static uint ChunkHeight;
	internal static int SolidBlocks;
	internal static bool Captured;
	internal static bool TileCaptureDone;
	internal static int FramesUntilTileCapture = -1;
	/// <summary>gen ghost-place already wrote the out-of-window records before the live scan</summary>
	internal static bool GhostFilled;
	internal static int GhostEntityCount;
	internal static int GhostEnemyCount;
	internal static int GhostItemCount;
	internal static int GhostTraderCount;

	internal static void Clear()
	{
		Traders.Clear();
		Enemies.Clear();
		Entities.Clear();
		WorldItems.Clear();
		Lights.Clear();
		TileFlips.Clear();
		StructureTilemapVault.Clear();
		WallholeResident.Clear();
		ChunkBackdrop.Clear();
		ElderResident.Clear();
		ModBehaviourState.ClearRuntime();
		StreamObjectJobs.Clear();
		BiomeDepth = 0;
		ChunkWidth = 0;
		ChunkHeight = 0;
		SolidBlocks = 0;
		Captured = false;
		TileCaptureDone = false;
		FramesUntilTileCapture = -1;
		GhostFilled = false;
		GhostEntityCount = GhostEnemyCount = GhostItemCount = GhostTraderCount = 0;
	}
}
