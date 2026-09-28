using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// a dead player's ragdoll has to keep the ground under it.
/// the spectator cam and other players are what the stream window follows, so the
/// corpse chunk unloads and the body falls through. hold those colliders until
/// the body is alive again, destroyed, or the player drops off Krok's dead list.
/// </summary>
internal static class PlayerCorpseGround
{
	private static readonly List<Body> Bodies = new List<Body>(8);
	private static readonly List<Vector2Int> Occupied = new List<Vector2Int>(16);

	internal static void AppendPins(WorldGeneration world, List<Vector2Int> pins)
	{
		if (world == null || pins == null)
			return;

		MpSession.CollectDeadPlayerBodies(Bodies);
		for (int i = 0; i < Bodies.Count; i++)
		{
			Body body = Bodies[i];
			if (body == null || body.alive)
				continue;

			Occupied.Clear();
			AddChunk(world, body.transform.position);
			Limb[] limbs = body.limbs;
			if (limbs != null)
			{
				for (int L = 0; L < limbs.Length; L++)
				{
					Limb limb = limbs[L];
					if (limb != null)
						AddChunk(world, limb.transform.position);
				}
			}

			if (Occupied.Count == 0)
				continue;

			for (int c = 0; c < Occupied.Count; c++)
				AddPin(pins, Occupied[c]);

			// resting on a chunk seam. the neighbor has to stay solid too
			if (Occupied.Count == 1)
			{
				Vector2Int neighbor = NearestNeighbor(world, body.transform.position, Occupied[0]);
				AddPin(pins, neighbor);
			}
		}
	}

	private static void AddChunk(WorldGeneration world, Vector3 worldPos)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));
		for (int i = 0; i < Occupied.Count; i++)
		{
			if (Occupied[i] == chunk)
				return;
		}
		Occupied.Add(chunk);
	}

	private static void AddPin(List<Vector2Int> pins, Vector2Int chunk)
	{
		for (int i = 0; i < pins.Count; i++)
		{
			if (pins[i] == chunk)
				return;
		}
		pins.Add(chunk);
	}

	private static Vector2Int NearestNeighbor(WorldGeneration world, Vector3 origin, Vector2Int home)
	{
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		Vector2Int best = home;
		float bestD = float.MaxValue;
		int[] dx = { 1, -1, 0, 0 };
		int[] dy = { 0, 0, 1, -1 };
		for (int i = 0; i < 4; i++)
		{
			int nx = home.x + dx[i];
			int ny = home.y + dy[i];
			if (nx < 0 || ny < 0 || nx >= width || ny >= height)
				continue;
			Vector2 center = ChunkCenter(world, nx, ny);
			float d = (center - (Vector2)origin).sqrMagnitude;
			if (d < bestD)
			{
				bestD = d;
				best = new Vector2Int(nx, ny);
			}
		}
		return best;
	}

	private static Vector2 ChunkCenter(WorldGeneration world, int cx, int cy)
	{
		float w = world.chunkWidth;
		float h = world.chunkHeight;
		return new Vector2(
			(cx - w * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE,
			(cy - h * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE);
	}
}
