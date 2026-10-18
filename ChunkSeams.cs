using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// a thin static box on every edge where a chunk with colliders meets one without.
/// items, enemies, and anything else on the ground layer stop there.
/// world liquid does not use physics, so the fluid sim treats the far side as occupied.
/// a seam is a box, not a tilemap bake, so it costs 1 play point and never the heavy slot.
/// a frame that already spent that slot still gets the wall.
/// </summary>
internal static class ChunkSeams
{
	const int SeamCost = 1;
	const float Thick = 1f;
	const float Lip = 0.2f;

	readonly struct Edge : IEquatable<Edge>
	{
		internal readonly int X;
		internal readonly int Y;
		internal readonly int Dir;

		internal Edge(int x, int y, int dir)
		{
			X = x;
			Y = y;
			Dir = dir;
		}

		public bool Equals(Edge other) => X == other.X && Y == other.Y && Dir == other.Dir;

		public override bool Equals(object obj) => obj is Edge other && Equals(other);

		public override int GetHashCode() => (X * 397) ^ (Y * 17) ^ Dir;
	}

	sealed class Seam
	{
		internal GameObject Go;
		internal BoxCollider2D Box;
	}

	static readonly int[] DX = { 1, -1, 0, 0 };
	static readonly int[] DY = { 0, 0, 1, -1 };

	static readonly HashSet<long> Solid = new HashSet<long>();
	static readonly HashSet<Edge> Want = new HashSet<Edge>();
	static readonly HashSet<Edge> Drop = new HashSet<Edge>();
	static readonly Dictionary<Edge, Seam> Live = new Dictionary<Edge, Seam>();
	static readonly List<Seam> Free = new List<Seam>(16);
	static Transform _root;
	static bool _seeded;
	static bool _dirty;

	internal static void Install(Harmony harmony)
	{
		MethodInfo empty = AccessTools.Method(typeof(FluidManager), "Empty", new[] { typeof(Vector2Int) });
		if (empty != null)
		{
			harmony.Patch(empty, postfix: new HarmonyMethod(typeof(ChunkSeams), nameof(EmptyPostfix)));
		}
		else
			Plugin.Log.LogWarning("FluidManager.Empty(Vector2Int) was not found.");

		MethodInfo flood = AccessTools.Method(typeof(FluidManager), "FloodFillDown");
		if (flood != null)
		{
			harmony.Patch(flood, prefix: new HarmonyMethod(typeof(ChunkSeams), nameof(FloodPrefix)));
		}
		else
			Plugin.Log.LogWarning("FluidManager.FloodFillDown was not found.");
	}

	internal static void Clear()
	{
		Solid.Clear();
		Want.Clear();
		Drop.Clear();
		Live.Clear();
		Free.Clear();
		_seeded = false;
		_dirty = false;
		if (_root != null)
		{
			UnityEngine.Object.Destroy(_root.gameObject);
			_root = null;
		}
	}

	internal static void Note(int cx, int cy, bool solid)
	{
		long key = Key(cx, cy);
		if (solid)
		{
			if (Solid.Add(key))
				_dirty = true;
		}
		else if (Solid.Remove(key))
			_dirty = true;
	}

	/// <summary>one scan after the cull, so chunks that kept their gen colliders are in the set.</summary>
	internal static void Seed(WorldGeneration world)
	{
		Solid.Clear();
		if (world == null)
		{
			_seeded = false;
			_dirty = true;
			return;
		}

		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			if (ChunkTerrain.CollidersEnabled(world, x, y))
				Solid.Add(Key(x, y));
		}
		_seeded = true;
		_dirty = true;
	}

	/// <summary>
	/// takePoint spends SeamCost when the play budget still has a point.
	/// the wall is built either way. waiting a frame is how a body falls through.
	/// </summary>
	internal static void Refresh(WorldGeneration world, ref int budget, bool takePoint)
	{
		if (world == null || !_seeded || !_dirty || StreamState.Bootstrapping)
			return;

		long started = StepTrace.Stamp();
		int count = Rebuild(world);
		_dirty = false;
		int charge = 0;
		if (takePoint && budget >= SeamCost)
		{
			budget -= SeamCost;
			charge = SeamCost;
		}
		StepTrace.Mark("create", "chunk-seam", -1, -1, charge, budget, "n=" + count, started, true);
	}

	static int Rebuild(WorldGeneration world)
	{
		Want.Clear();
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		foreach (long key in Solid)
		{
			Decode(key, out int cx, out int cy);
			for (int d = 0; d < 4; d++)
			{
				int nx = cx + DX[d];
				int ny = cy + DY[d];
				if (nx >= 0 && ny >= 0 && nx < width && ny < height && Solid.Contains(Key(nx, ny)))
					continue;
				Want.Add(new Edge(cx, cy, d));
			}
		}

		Drop.Clear();
		foreach (var kv in Live)
		{
			if (!Want.Contains(kv.Key))
				Drop.Add(kv.Key);
		}
		foreach (Edge edge in Drop)
		{
			if (!Live.TryGetValue(edge, out Seam seam))
				continue;
			Live.Remove(edge);
			if (seam.Go != null)
				seam.Go.SetActive(false);
			Free.Add(seam);
		}

		EnsureRoot(world);
		foreach (Edge edge in Want)
		{
			if (Live.ContainsKey(edge))
				continue;
			Seam seam = Take();
			Place(world, seam, edge);
			Live[edge] = seam;
		}
		return Live.Count;
	}

	static Seam Take()
	{
		for (int i = Free.Count - 1; i >= 0; i--)
		{
			Seam seam = Free[i];
			Free.RemoveAt(i);
			if (seam != null && seam.Go != null)
				return seam;
		}

		GameObject go = new GameObject("ChunkSeam");
		go.layer = 6;
		go.tag = "BlockGround";
		go.transform.SetParent(_root, false);
		BoxCollider2D box = go.AddComponent<BoxCollider2D>();
		box.isTrigger = false;
		return new Seam { Go = go, Box = box };
	}

	static void Place(WorldGeneration world, Seam seam, Edge edge)
	{
		float size = WorldGeneration.CHUNKSIZE;
		float minX = (edge.X - world.chunkWidth * 0.5f) * size;
		float minY = (edge.Y - world.chunkHeight * 0.5f) * size;
		Vector2 center;
		Vector2 box;
		if (edge.Dir == 0)
		{
			center = new Vector2(minX + size + Thick * 0.5f - Lip, minY + size * 0.5f);
			box = new Vector2(Thick, size + Thick);
		}
		else if (edge.Dir == 1)
		{
			center = new Vector2(minX - Thick * 0.5f + Lip, minY + size * 0.5f);
			box = new Vector2(Thick, size + Thick);
		}
		else if (edge.Dir == 2)
		{
			center = new Vector2(minX + size * 0.5f, minY + size + Thick * 0.5f - Lip);
			box = new Vector2(size + Thick, Thick);
		}
		else
		{
			center = new Vector2(minX + size * 0.5f, minY - Thick * 0.5f + Lip);
			box = new Vector2(size + Thick, Thick);
		}

		seam.Go.SetActive(true);
		seam.Go.transform.position = center;
		seam.Box.size = box;
		seam.Box.offset = Vector2.zero;
	}

	static void EnsureRoot(WorldGeneration world)
	{
		if (_root != null)
			return;
		GameObject go = new GameObject("CPUOptimization2_ChunkSeams");
		Transform parent = world.worldGrid != null ? world.worldGrid.transform : null;
		if (parent != null)
			go.transform.SetParent(parent, false);
		_root = go.transform;
	}

	static bool CellBlocked(Vector2Int block)
	{
		if (!_seeded || !StreamState.Active || StreamState.Bootstrapping)
			return false;
		WorldGeneration world = WorldGeneration.world;
		if (world == null)
			return false;
		Vector2Int chunk = world.BlockToChunkPos(block);
		return !Solid.Contains(Key(chunk.x, chunk.y));
	}

	static void EmptyPostfix(Vector2Int pos, ref bool __result)
	{
		if (!__result)
			return;
		if (CellBlocked(pos))
			__result = false;
	}

	static bool FloodPrefix(int x, int y)
	{
		return !CellBlocked(new Vector2Int(x, y));
	}

	static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

	static void Decode(long key, out int x, out int y)
	{
		x = (int)(key >> 32);
		y = (int)(uint)key;
	}
}
