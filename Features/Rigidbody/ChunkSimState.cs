namespace CPUOptimization.Features.ChunkSim;

internal static class ChunkSimState
{
	internal static ChunkSimWindow Current { get; private set; }
	internal static ChunkSimUnionWindow CurrentUnion { get; private set; }
	internal static bool UseMpUnion { get; private set; }

	// mp clients: presentation/update skips use local 2x2 instead of union
	internal static bool UseClientLocalPresentation { get; private set; }

	// bumps when active chunk set changes (refresh cached ik gates)
	internal static int WindowRevision { get; private set; }

	internal static bool IsActive =>
		Plugin.Enabled != null && Plugin.Enabled.Value
		&& Plugin.ChunkSimEnabled != null && Plugin.ChunkSimEnabled.Value
		&& HasActiveWindow;

	private static bool HasActiveWindow =>
		UseMpUnion ? CurrentUnion != null : Current != null;

	internal static void SetWindow(ChunkSimWindow window)
	{
		UseMpUnion = false;
		UseClientLocalPresentation = false;
		CurrentUnion = null;
		bool changed = Current == null || window == null || !Current.MatchesAnchor(window);
		Current = window;
		if (changed)
			WindowRevision++;
	}

	internal static void SetUnionWindow(ChunkSimUnionWindow union)
	{
		UseMpUnion = true;
		UseClientLocalPresentation = false;
		Current = null;
		bool changed = CurrentUnion == null || union == null || !CurrentUnion.Matches(union);
		CurrentUnion = union;
		if (changed)
			WindowRevision++;
	}

	internal static void SetClientLocalWindow(ChunkSimWindow window)
	{
		UseMpUnion = false;
		UseClientLocalPresentation = true;
		CurrentUnion = null;
		bool changed = Current == null || window == null || !Current.MatchesAnchor(window);
		Current = window;
		if (changed)
			WindowRevision++;
	}

	internal static void Clear()
	{
		Current = null;
		CurrentUnion = null;
		UseMpUnion = false;
		UseClientLocalPresentation = false;
		WindowRevision = 0;
	}

	// host colliders, item/building rb sync, krokmp host gates
	internal static bool ShouldSimulateAuthorityWorldPos(UnityEngine.Vector3 worldPos)
	{
		if (!IsActive)
			return true;
		WorldGeneration world = WorldGeneration.world;
		if (!world || !world.worldExists)
			return true;

		if (UseMpUnion && CurrentUnion != null)
			return CurrentUnion.ContainsWorldPos(world, worldPos);

		return Current != null && Current.ContainsWorldPos(world, worldPos);
	}

	// update skips, ik, particles - local 2x2 on mp clients when enabled
	internal static bool ShouldSimulatePresentationWorldPos(UnityEngine.Vector3 worldPos)
	{
		if (!IsActive)
			return true;
		WorldGeneration world = WorldGeneration.world;
		if (!world || !world.worldExists)
			return true;

		if (UseClientLocalPresentation && Current != null)
			return Current.ContainsWorldPos(world, worldPos);

		return ShouldSimulateAuthorityWorldPos(worldPos);
	}

	// legacy alias for authority sim
	internal static bool ShouldSimulateWorldPos(UnityEngine.Vector3 worldPos) =>
		ShouldSimulateAuthorityWorldPos(worldPos);

	internal static int ActiveChunkCount =>
		UseMpUnion && CurrentUnion != null ? CurrentUnion.ChunkCount : 4;

	internal static string FormatActiveChunks()
	{
		if (UseMpUnion && CurrentUnion != null)
			return CurrentUnion.FormatChunks();
		if (UseClientLocalPresentation)
			return Current != null ? Current.FormatChunks() + " (local)" : "-";
		return Current != null ? Current.FormatChunks() : "-";
	}

	internal static string FormatSimMode()
	{
		if (UseClientLocalPresentation)
			return "mp-local";
		if (UseMpUnion)
			return "mp-union";
		return "sp-2x2";
	}
}
