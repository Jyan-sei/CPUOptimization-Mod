using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class StreamState
{
	internal static bool Active;
	/// <summary>load-screen cull is still packing the world into arrays plus the live window</summary>
	internal static bool Bootstrapping;
	internal static bool SuppressBuildingDrops;
	internal static TilemapRenderer DummyRenderer;
	internal static readonly System.Collections.Generic.HashSet<int> SkipTraderStart
		= new System.Collections.Generic.HashSet<int>();

	internal static void EnsureDummy()
	{
		if (DummyRenderer != null)
			return;

		GameObject go = new GameObject("CPUOptimization2_DummyChunkRenderer");
		Object.DontDestroyOnLoad(go);
		go.hideFlags = HideFlags.HideAndDontSave;
		DummyRenderer = go.AddComponent<TilemapRenderer>();
		DummyRenderer.enabled = false;
	}

	internal static void Reset()
	{
		Active = false;
		Bootstrapping = false;
		SuppressBuildingDrops = false;
		SkipTraderStart.Clear();
		// window policy stays locked for the run. ResetForNewGenerate clears it
	}

	internal static void ResetForNewGenerate()
	{
		Reset();
		StreamWindowPolicy.Reset();
	}
}
