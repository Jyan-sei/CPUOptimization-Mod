using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class ChunkTerrain
{
	/// <summary>make the chunk go, paint tiles, enable the renderer. colliders stay off untill EnableColliders</summary>
	internal static void CreateRenderer(WorldGeneration world, int cx, int cy)
	{
		if (WorldArrays.IsLoaded(world, cx, cy))
			return;

		GameObject gameObject = new GameObject("Chunk", typeof(Tilemap), typeof(TilemapRenderer), typeof(TilemapCollider2D));
		gameObject.isStatic = true;
		gameObject.layer = 6;
		gameObject.tag = "BlockGround";

		Tilemap map = gameObject.GetComponent<Tilemap>();
		TilemapRenderer renderer = gameObject.GetComponent<TilemapRenderer>();
		TilemapCollider2D collider = gameObject.GetComponent<TilemapCollider2D>();
		collider.extrusionFactor = 0.0005f;
		collider.useDelaunayMesh = false;
		collider.maximumTileChangeCount = 99999u;
		collider.usedByComposite = false;
		collider.enabled = false;

		Rigidbody2D rb = gameObject.AddComponent<Rigidbody2D>();
		rb.bodyType = RigidbodyType2D.Static;
		rb.simulated = false;

		gameObject.AddComponent<CompositeCollider2D>().enabled = false;

		renderer.detectChunkCullingBounds = TilemapRenderer.DetectChunkCullingBounds.Manual;
		renderer.chunkCullingBounds = Vector3.zero;
		renderer.material = world.defaultMat;
		renderer.sortingOrder = 1000;
		renderer.enabled = true;

		float w = world.chunkWidth;
		float h = world.chunkHeight;
		gameObject.transform.position = new Vector2(
			(cx - w * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE,
			(cy - h * 0.5f) * WorldGeneration.CHUNKSIZE + world.HALFCHUNKSIZE);
		gameObject.transform.SetParent(world.worldGrid.transform);

		if (world.ChunkUpdated != null)
			world.ChunkUpdated[cx, cy] = new UnityEvent();

		ChunkScript script = gameObject.AddComponent<ChunkScript>();
		script.pos = new Vector2Int(cx, cy);

		WorldArrays.SetChunk(world, cx, cy, map);
		if (world.renderChunks != null)
			world.renderChunks[cx, cy] = renderer;
		WorldArrays.SetChunkScript(world, cx, cy, script);
		ScreenCull.RegisterRenderer(renderer);
		ScreenCull.RegisterGameObject(gameObject);

		CreateBackgroundFor(world, map);
		world.UpdateChunk(new Vector2Int(cx, cy));
		ApplyCachedTileFlips(world, script, map, cx, cy);
		// UpdateChunk just invoked the fresh event. traps that survived in a neighbor chunk
		// (or were already spawned) need their seating listener on this one.
		SeatingHooks.RebindChunk(world, cx, cy);

		if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
		{
			int solid = CountSolidTiles(world, cx, cy);
			Plugin.Log.LogInfo($"stream load renderer chunk ({cx},{cy}) solid={solid}");
		}
	}

	internal static void EnableColliders(WorldGeneration world, int cx, int cy)
	{
		if (!WorldArrays.IsLoaded(world, cx, cy))
			return;

		Tilemap map = WorldArrays.Chunks(world)[cx, cy];
		if (map == null)
			return;

		TilemapCollider2D collider = map.GetComponent<TilemapCollider2D>();
		Rigidbody2D rb = map.GetComponent<Rigidbody2D>();
		CompositeCollider2D composite = map.GetComponent<CompositeCollider2D>();

		// already on means leave it. writing enabled or SyncTransforms rebuilds the polygon
		bool changed = false;
		if (collider != null)
		{
			if (collider.usedByComposite)
			{
				collider.usedByComposite = false;
				changed = true;
			}
			if (!collider.enabled)
			{
				collider.enabled = true;
				changed = true;
			}
		}
		if (rb != null && !rb.simulated)
		{
			rb.simulated = true;
			changed = true;
		}
		if (composite != null && composite.enabled)
		{
			composite.enabled = false;
			changed = true;
		}
		if (!changed)
			return;

		Physics2D.SyncTransforms();
		if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
			Plugin.Log.LogInfo($"stream load collider chunk ({cx},{cy})");
	}

	internal static void DisableColliders(WorldGeneration world, int cx, int cy)
	{
		if (!WorldArrays.IsLoaded(world, cx, cy))
			return;
		Tilemap map = WorldArrays.Chunks(world)[cx, cy];
		if (map == null)
			return;
		DisablePhysics(map.gameObject);
	}

	internal static void DestroyRenderer(WorldGeneration world, int cx, int cy)
	{
		if (!WorldArrays.IsLoaded(world, cx, cy))
			return;

		Tilemap map = WorldArrays.Chunks(world)[cx, cy];
		ChunkScript script = WorldArrays.ChunkScripts(world)?[cx, cy];
		if (map != null && script != null)
			PackTileFlips(world, script, map, cx, cy);

		if (map != null)
		{
			RemoveBackgroundsUnder(world, map.transform);
			DisablePhysics(map.gameObject);
			if (world.renderChunks != null && world.renderChunks[cx, cy] != null)
				world.renderChunks[cx, cy].enabled = false;
			Object.Destroy(map.gameObject);
		}

		WorldArrays.SetChunk(world, cx, cy, null);
		WorldArrays.SetChunkScript(world, cx, cy, null);
		if (world.renderChunks != null)
			world.renderChunks[cx, cy] = null;
		if (world.ChunkUpdated != null)
			world.ChunkUpdated[cx, cy] = new UnityEvent();

		if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
			Plugin.Log.LogInfo($"stream destroy renderer chunk ({cx},{cy})");
	}

	private static int CountSolidTiles(WorldGeneration world, int cx, int cy)
	{
		ushort[,] blocks = WorldArrays.WorldBlocks(world);
		if (blocks == null)
			return -1;
		int solid = 0;
		int size = WorldGeneration.CHUNKSIZE;
		int ox = cx * size;
		int oy = cy * size;
		for (int i = 0; i < size; i++)
		for (int j = 0; j < size; j++)
		{
			if (blocks[ox + i, oy + j] != 0)
				solid++;
		}
		return solid;
	}

	private static void DisablePhysics(GameObject go)
	{
		TilemapCollider2D tile = go.GetComponent<TilemapCollider2D>();
		if (tile != null && tile.enabled)
			tile.enabled = false;
		CompositeCollider2D composite = go.GetComponent<CompositeCollider2D>();
		if (composite != null && composite.enabled)
			composite.enabled = false;
		Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
		if (rb != null && rb.simulated)
			rb.simulated = false;
	}

	/// <summary>
	/// renderer on, colliders on. elder sim reads the renderer, chew hits the collider,
	/// DamageBlock wants the tilemap for the break sprite.
	/// </summary>
	internal static void EnsureSim(WorldGeneration world, int cx, int cy)
	{
		if (world == null)
			return;
		if (!WorldArrays.IsLoaded(world, cx, cy))
			CreateRenderer(world, cx, cy);
		EnableColliders(world, cx, cy);
		if (world.renderChunks != null && world.renderChunks[cx, cy] != null)
			world.renderChunks[cx, cy].enabled = true;
	}

	private static void CreateBackgroundFor(WorldGeneration world, Tilemap map)
	{
		// sampled off whatever gen actually painted. none means open air, leave it bare.
		// depth table is only for a chunk built before that sample exists.
		if (ChunkBackdrop.Known)
		{
			if (ChunkBackdrop.HasBackdrop)
				ChunkBackdrop.Paint(world, map);
			return;
		}

		// stock WorldCreateBackground stops at grass (depth 4).
		// SuperSecret.DeepBackgroundGen paints the rest: glacial rock, fungal and geothermal wasteland
		string path = null;
		if (world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial)
			path = "steelBackground";
		else if (world.biomeDepth <= 1)
			path = world.biomeDepth == 0 ? "rockBackground" : "soilBackground";
		else if (world.biomeDepth == 2)
			path = "sandBackground";
		else if (world.biomeDepth == 3)
			path = "wastelandBackground";
		else if (world.biomeDepth == 4)
			path = "grassBackground";
		else if (world.biomeDepth == 5)
			path = "rockBackground";
		else if (world.biomeDepth == 6)
			path = "wastelandBackground";
		else if (world.biomeDepth >= 7)
			path = "wastelandBackground";
		if (path == null)
			return;

		world.CreateBackground(path, map);
	}

	private static void RemoveBackgroundsUnder(WorldGeneration world, Transform chunkRoot)
	{
		if (world.backgrounds == null || chunkRoot == null)
			return;
		for (int i = world.backgrounds.Count - 1; i >= 0; i--)
		{
			GameObject bg = world.backgrounds[i];
			if (bg == null)
			{
				world.backgrounds.RemoveAt(i);
				continue;
			}
			if (bg.transform.IsChildOf(chunkRoot) || bg.transform.parent == chunkRoot)
				world.backgrounds.RemoveAt(i);
		}
	}

	private static void PackTileFlips(WorldGeneration world, ChunkScript script, Tilemap map, int cx, int cy)
	{
		FieldInfo hasRandomized = AccessTools.Field(typeof(ChunkScript), "hasRandomized");
		if (hasRandomized == null || !(bool)hasRandomized.GetValue(script))
			return;

		for (int i = CaptureStore.TileFlips.Count - 1; i >= 0; i--)
		{
			if (CaptureStore.TileFlips[i].Chunk.x == cx && CaptureStore.TileFlips[i].Chunk.y == cy)
				CaptureStore.TileFlips.RemoveAt(i);
		}

		ChunkTileFlipRecord record = new ChunkTileFlipRecord { Chunk = new Vector2Int(cx, cy) };
		int half = world.HALFCHUNKSIZE;
		int size = WorldGeneration.CHUNKSIZE;
		for (int lx = 0; lx < size; lx++)
		for (int ly = 0; ly < size; ly++)
		{
			Matrix4x4 m = map.GetTransformMatrix(new Vector3Int(lx - half, ly - half, 0));
			Vector3 scale = m.lossyScale;
			float z = m.rotation.eulerAngles.z;
			bool identity = Mathf.Abs(scale.x - 1f) < 0.01f && Mathf.Abs(scale.y - 1f) < 0.01f
				&& Mathf.Abs(Mathf.DeltaAngle(z, 0f)) < 1f;
			if (identity)
				continue;
			int step = Mathf.RoundToInt(Mathf.DeltaAngle(0f, z) / 90f);
			if (step < 0) step += 5;
			record.Cells.Add(new TileCellFlip
			{
				LocalX = (byte)lx,
				LocalY = (byte)ly,
				FlipX = scale.x < 0f ? (sbyte)(-1) : (sbyte)1,
				FlipY = scale.y < 0f ? (sbyte)(-1) : (sbyte)1,
				RotStep = (byte)(step % 5),
			});
		}
		if (record.Cells.Count > 0)
			CaptureStore.TileFlips.Add(record);
	}

	private static void ApplyCachedTileFlips(WorldGeneration world, ChunkScript script, Tilemap map, int cx, int cy)
	{
		ChunkTileFlipRecord flips = null;
		for (int i = 0; i < CaptureStore.TileFlips.Count; i++)
		{
			if (CaptureStore.TileFlips[i].Chunk.x == cx && CaptureStore.TileFlips[i].Chunk.y == cy)
			{
				flips = CaptureStore.TileFlips[i];
				break;
			}
		}

		FieldInfo hasRandomized = AccessTools.Field(typeof(ChunkScript), "hasRandomized");
		if (flips == null || flips.Cells.Count == 0)
		{
			if (hasRandomized != null)
				hasRandomized.SetValue(script, false);
			return;
		}

		int half = world.HALFCHUNKSIZE;
		for (int i = 0; i < flips.Cells.Count; i++)
		{
			TileCellFlip cell = flips.Cells[i];
			Vector3 scale = new Vector3(cell.FlipX, cell.FlipY, 1f);
			Quaternion rot = Quaternion.Euler(0f, 0f, cell.RotStep * 90f);
			map.SetTransformMatrix(
				new Vector3Int(cell.LocalX - half, cell.LocalY - half, 0),
				Matrix4x4.TRS(Vector3.zero, rot, scale));
		}

		if (hasRandomized != null)
			hasRandomized.SetValue(script, true);
	}
}
