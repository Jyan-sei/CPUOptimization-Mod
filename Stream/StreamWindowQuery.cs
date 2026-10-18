using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// read-only queries for other mods (krok opt2 registry cull and such).
/// same idea as CPUOptimization.ChunkSimQuery. stream off means "in window"
/// so callers fall through to their own distance checks via IsStreamActive.
/// </summary>
public static class StreamWindowQuery
{
	/// <summary>true when the stream is actually running (sp keep-window, or mp host union)</summary>
	public static bool IsStreamActive => StreamState.Active;

	/// <summary>
	/// true when the stream is on and worldPos is in the current keep set
	/// (sp cam window, or the mp host union).
	/// stream off returns true. dont cull.
	/// </summary>
	public static bool IsAuthoritySimWorldPos(Vector3 worldPos) =>
		StreamController.IsAuthoritySimWorldPos(worldPos);
}
