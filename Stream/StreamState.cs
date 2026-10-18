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
	/// <summary>
	/// stock Start resets rep and rebuilds the shop. put the capture back after it.
	/// the original still has to run so krok can add the tracker and Talker can make the name text
	/// </summary>
	internal static readonly System.Collections.Generic.Dictionary<int, TraderSpawnState> TraderRestore
		= new System.Collections.Generic.Dictionary<int, TraderSpawnState>();

	internal sealed class TraderSpawnState
	{
		internal int GameCharacter;
		internal float Reputation;
		internal float Hostility;
		/// <summary>already introduced. Start does not clear this, but the prefab is false and OnWillRender calls MeetPlayer</summary>
		internal bool StartedConvo;
		internal int ValueGiven;
		internal int TotalValueGiven;
		/// <summary>replace the shop Start just rolled, including an empty list</summary>
		internal bool HasItems;
		internal bool FarEnoughToMove;
		internal bool DidMove;
		internal float MoveRangeMin;
		internal float MoveRangeMax;
		internal float HeightMult;
		internal float RotationZ;
		internal Vector3 LocalScale;
		internal System.Collections.Generic.List<TraderItemSnap> Items;
	}

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
		TraderRestore.Clear();
		// window policy stays locked for the run. ResetForNewGenerate clears it
	}

	internal static void ResetForNewGenerate()
	{
		Reset();
		StreamWindowPolicy.Reset();
	}
}
