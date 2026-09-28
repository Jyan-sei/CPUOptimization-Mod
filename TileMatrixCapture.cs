using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

internal static class TileMatrixCapture
{
	internal static void CaptureIfReady(WorldGeneration world)
	{
		if (!CaptureStore.Captured || CaptureStore.TileCaptureDone)
			return;
		if (CaptureStore.FramesUntilTileCapture < 0)
			return;

		if (CaptureStore.FramesUntilTileCapture > 0)
		{
			CaptureStore.FramesUntilTileCapture--;
			return;
		}

		CaptureStore.TileFlips.Clear();
		ChunkScript[] scripts = Object.FindObjectsOfType<ChunkScript>();
		FieldInfo hasRandomizedField = AccessTools.Field(typeof(ChunkScript), "hasRandomized");

		for (int i = 0; i < scripts.Length; i++)
		{
			ChunkScript script = scripts[i];
			if (script == null)
				continue;

			if (hasRandomizedField == null || !(bool)hasRandomizedField.GetValue(script))
				continue;

			Tilemap map = script.GetComponent<Tilemap>();
			if (map == null)
				continue;

			ChunkTileFlipRecord record = new ChunkTileFlipRecord { Chunk = script.pos };
			int half = world.HALFCHUNKSIZE;
			int size = WorldGeneration.CHUNKSIZE;

			for (int lx = 0; lx < size; lx++)
			for (int ly = 0; ly < size; ly++)
			{
				Vector3Int cell = new Vector3Int(lx - half, ly - half, 0);
				Matrix4x4 m = map.GetTransformMatrix(cell);
				if (IsIdentityish(m))
					continue;

				ExtractFlip(m, out sbyte flipX, out sbyte flipY, out byte rotStep);
				record.Cells.Add(new TileCellFlip
				{
					LocalX = (byte)lx,
					LocalY = (byte)ly,
					FlipX = flipX,
					FlipY = flipY,
					RotStep = rotStep,
				});
			}

			if (record.Cells.Count > 0)
				CaptureStore.TileFlips.Add(record);
		}

		CaptureStore.TileCaptureDone = true;
		CaptureStore.FramesUntilTileCapture = -1;
		SanityLog.LogTileCapture();
	}

	private static bool IsIdentityish(Matrix4x4 m)
	{
		Vector3 scale = m.lossyScale;
		Vector3 euler = m.rotation.eulerAngles;
		bool scaleOne = Mathf.Abs(scale.x - 1f) < 0.01f && Mathf.Abs(scale.y - 1f) < 0.01f;
		bool noRot = Mathf.Abs(Mathf.DeltaAngle(euler.z, 0f)) < 1f;
		return scaleOne && noRot;
	}

	private static void ExtractFlip(Matrix4x4 m, out sbyte flipX, out sbyte flipY, out byte rotStep)
	{
		Vector3 scale = m.lossyScale;
		flipX = scale.x < 0f ? (sbyte)(-1) : (sbyte)1;
		flipY = scale.y < 0f ? (sbyte)(-1) : (sbyte)1;
		float z = m.rotation.eulerAngles.z;
		int step = Mathf.RoundToInt(Mathf.DeltaAngle(0f, z) / 90f);
		if (step < 0)
			step += 5;
		step %= 5;
		rotStep = (byte)step;
	}
}
