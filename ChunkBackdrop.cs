using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// one backdrop for the whole layer, copied off a live ChunkBack before the first unload.
/// none is a real answer. toxic caverns never paint one, glacial chunks do.
/// depth tables guessed layer 5 as rock and covered open air.
/// </summary>
internal static class ChunkBackdrop
{
	internal static bool Known;
	internal static bool HasBackdrop;
	internal static Sprite Sprite;
	internal static Material Material;
	internal static Color Color = UnityEngine.Color.gray;
	internal static int SortingOrder = -9999;

	internal static void Clear()
	{
		Known = false;
		HasBackdrop = false;
		Sprite = null;
		Material = null;
		Color = UnityEngine.Color.gray;
		SortingOrder = -9999;
	}

	internal static void Sample(WorldGeneration world)
	{
		Clear();
		Tilemap[,] chunks = world != null ? WorldArrays.Chunks(world) : null;
		if (chunks == null)
			return;

		bool sawChunk = false;
		int width = chunks.GetLength(0);
		int height = chunks.GetLength(1);
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			Tilemap map = chunks[x, y];
			if (map == null)
				continue;
			sawChunk = true;
			Transform root = map.transform;
			for (int i = 0; i < root.childCount; i++)
			{
				Transform child = root.GetChild(i);
				if (child == null || child.name != "ChunkBack")
					continue;
				SpriteRenderer sr = child.GetComponent<SpriteRenderer>();
				if (sr == null || sr.sprite == null)
					continue;
				Sprite = sr.sprite;
				Material = sr.sharedMaterial;
				Color = sr.color;
				SortingOrder = sr.sortingOrder;
				HasBackdrop = true;
				Known = true;
				Plugin.Log.LogInfo(
					$"chunk backdrop — sprite={sr.sprite.name} sort={SortingOrder}");
				return;
			}
		}

		Known = sawChunk;
		HasBackdrop = false;
		if (sawChunk)
			Plugin.Log.LogInfo("chunk backdrop — none (open air)");
	}

	internal static void Paint(WorldGeneration world, Tilemap map)
	{
		if (world == null || map == null || !HasBackdrop || Sprite == null)
			return;

		GameObject go = new GameObject("ChunkBack", typeof(SpriteRenderer));
		go.transform.SetParent(map.transform);
		go.transform.localPosition = Vector2.zero;
		SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
		sr.sprite = Sprite;
		sr.sharedMaterial = Material != null ? Material : world.defaultMat;
		sr.drawMode = SpriteDrawMode.Tiled;
		sr.size = Vector2.one * WorldGeneration.CHUNKSIZE;
		sr.sortingOrder = SortingOrder;
		sr.color = Color;
		if (world.backgrounds != null)
			world.backgrounds.Add(go);
	}
}
