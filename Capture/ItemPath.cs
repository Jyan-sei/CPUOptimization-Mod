using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// one line per item decision. prefix is CPUOpt2 path.
/// store is the row write, tear is the scene object leaving, build is the chunk coming back.
/// </summary>
internal static class ItemPath
{
	internal static string Source = "";

	internal static void Note(string phase, int cx, int cy, string id, Vector3 pos, string detail)
	{
		Plugin.Log.LogInfo(
			"CPUOpt2 path " + phase
			+ " " + cx + "," + cy
			+ (string.IsNullOrEmpty(Source) ? "" : " src=" + Source)
			+ " id=" + (string.IsNullOrEmpty(id) ? "?" : id)
			+ " pos=" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0")
			+ (string.IsNullOrEmpty(detail) ? "" : " " + detail));
	}

	internal static void Sum(string phase, int cx, int cy, string detail)
	{
		Plugin.Log.LogInfo(
			"CPUOpt2 path " + phase
			+ " " + cx + "," + cy
			+ (string.IsNullOrEmpty(Source) ? "" : " src=" + Source)
			+ " " + detail);
	}

	/// <summary>scene items with this id within 4 units, including inactive. ignore is the object we already own.</summary>
	internal static int Nearby(string id, Vector3 pos, GameObject ignore)
	{
		if (string.IsNullOrEmpty(id))
			return 0;
		return ChunkRoster.CountItems(id, pos, ignore, 16f);
	}
}
