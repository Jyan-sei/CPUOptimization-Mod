using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// stream keep-window mode, plus how many objects we spawn/destroy per frame.
/// Dynamic2x2 is the ortho-measured 2x2..3x3. Fixed3x3 / Fixed4x4 are centered odd spans.
/// </summary>
internal static class StreamWindowPolicy
{
	internal enum Mode
	{
		Dynamic2x2 = 0,
		Fixed3x3 = 1,
		Fixed4x4 = 2,
	}

	internal static readonly string[] ModeLabels =
	{
		"2x2/2x3 (ultrawide)",
		"3x3",
		"4x4",
	};

	internal static bool Locked { get; private set; }
	internal static int SpanX { get; private set; } = 3;
	internal static int SpanY { get; private set; } = 3;
	internal static float MeasuredHalfW { get; private set; }
	internal static float MeasuredHalfH { get; private set; }
	internal static float MeasuredAspect { get; private set; }

	internal static Mode Current
	{
		get
		{
			int v = Plugin.StreamWindowMode != null ? Plugin.StreamWindowMode.Value : (int)Mode.Fixed3x3;
			if (v < 0)
				v = 0;
			if (v > (int)Mode.Fixed4x4)
				v = (int)Mode.Fixed4x4;
			return (Mode)v;
		}
	}

	/// <summary>max Instantiate/Destroy ops per tick while objects are streaming</summary>
	internal static int ObjectOpsPerFrame
	{
		get
		{
			switch (Current)
			{
				case Mode.Fixed4x4:
					return 40;
				case Mode.Fixed3x3:
					return 64;
				default:
					return 128;
			}
		}
	}

	internal static void Reset()
	{
		Locked = false;
		ApplyFixedSpansForMode(Current);
		MeasuredHalfW = MeasuredHalfH = MeasuredAspect = 0f;
	}

	/// <summary>video dropdown / cfg. takes effect mid-run</summary>
	internal static void SetMode(Mode mode, string reason)
	{
		int v = (int)mode;
		if (Plugin.StreamWindowMode != null && Plugin.StreamWindowMode.Value != v)
			Plugin.StreamWindowMode.Value = v;
		Plugin.Instance?.Config.Save();

		Locked = false;
		if (mode == Mode.Dynamic2x2)
		{
			WorldGeneration world = WorldGeneration.world;
			if (world != null && TryMeasure(world, out int sx, out int sy, out float hw, out float hh, out float aspect))
			{
				SpanX = sx;
				SpanY = sy;
				MeasuredHalfW = hw;
				MeasuredHalfH = hh;
				MeasuredAspect = aspect;
				Locked = true;
			}
			else
			{
				SpanX = 2;
				SpanY = 2;
			}
		}
		else
		{
			ApplyFixedSpansForMode(mode);
			Locked = true;
		}

		Plugin.Log.LogInfo(
			$"stream window mode ({reason}) — {ModeLabels[(int)Current]} → {FormatSpans()} " +
			$"objectOps/frame={ObjectOpsPerFrame}");

		StreamController.NotifyPolicyChanged(WorldGeneration.world);
	}

	internal static bool TryLockFromCamera(WorldGeneration world, string reason)
	{
		Mode mode = Current;
		if (mode != Mode.Dynamic2x2)
		{
			ApplyFixedSpansForMode(mode);
			Locked = true;
			Plugin.Log.LogInfo(
				$"stream window policy ({reason}) — fixed {FormatSpans()} ({ModeLabels[(int)mode]}) " +
				$"objectOps/frame={ObjectOpsPerFrame}");
			return true;
		}

		if (Locked)
			return true;
		if (!TryMeasure(world, out int sx, out int sy, out float halfW, out float halfH, out float aspect))
		{
			SpanX = 2;
			SpanY = 2;
			Plugin.Log.LogInfo(
				$"stream window policy ({reason}) — dynamic fallback 2x2 (no ortho yet); " +
				$"objectOps/frame={ObjectOpsPerFrame}");
			return false;
		}

		SpanX = sx;
		SpanY = sy;
		MeasuredHalfW = halfW;
		MeasuredHalfH = halfH;
		MeasuredAspect = aspect;
		Locked = true;
		Plugin.Log.LogInfo(
			$"stream window policy locked ({reason}) — {SpanX}x{SpanY} chunks " +
			$"(view half={halfW:F1}x{halfH:F1} aspect={aspect:F2} chunk={WorldGeneration.CHUNKSIZE}) " +
			$"objectOps/frame={ObjectOpsPerFrame}");
		return true;
	}

	internal static void EnsureSpans(WorldGeneration world)
	{
		Mode mode = Current;
		if (mode != Mode.Dynamic2x2)
		{
			ApplyFixedSpansForMode(mode);
			return;
		}

		if (Locked)
			return;
		if (TryMeasure(world, out int sx, out int sy, out _, out _, out _))
		{
			SpanX = sx;
			SpanY = sy;
		}
		else
		{
			SpanX = 2;
			SpanY = 2;
		}
	}

	private static void ApplyFixedSpansForMode(Mode mode)
	{
		if (mode == Mode.Fixed4x4)
		{
			SpanX = 4;
			SpanY = 4;
		}
		else
		{
			SpanX = 3;
			SpanY = 3;
		}
	}

	private static bool TryMeasure(
		WorldGeneration world,
		out int spanX,
		out int spanY,
		out float halfW,
		out float halfH,
		out float aspect)
	{
		spanX = 2;
		spanY = 2;
		halfW = halfH = aspect = 0f;
		if (world == null)
			return false;

		Camera cam = ResolveCamera(world);
		if (cam == null || !cam.orthographic)
			return false;

		halfH = cam.orthographicSize;
		if (halfH < 0.5f)
			return false;
		aspect = cam.aspect > 0.1f ? cam.aspect : ((float)Screen.width / Mathf.Max(1, Screen.height));
		halfW = halfH * aspect;

		float margin = 1.12f;
		int chunk = WorldGeneration.CHUNKSIZE;
		if (chunk < 1)
			chunk = 64;

		int needX = Mathf.CeilToInt((2f * halfW * margin) / chunk);
		int needY = Mathf.CeilToInt((2f * halfH * margin) / chunk);
		if (needX < 2)
			needX = 2;
		if (needY < 2)
			needY = 2;

		// dynamic: usually 2x2, ultrawide can bump to 3x2 / 3x3
		spanX = Mathf.Clamp(needX, 2, 3);
		spanY = Mathf.Clamp(needY, 2, 3);
		return true;
	}

	internal static void GetAnchoredWindow(
		WorldGeneration world,
		Vector3 camWorld,
		out int x0,
		out int y0,
		out int x1,
		out int y1)
	{
		EnsureSpans(world);

		int spanX = SpanX;
		int spanY = SpanY;
		int maxX = (int)world.chunkWidth - 1;
		int maxY = (int)world.chunkHeight - 1;
		if (spanX > maxX + 1)
			spanX = maxX + 1;
		if (spanY > maxY + 1)
			spanY = maxY + 1;
		if (spanX < 1)
			spanX = 1;
		if (spanY < 1)
			spanY = 1;

		Vector2Int block = world.WorldToBlockPos(camWorld);
		Vector2Int chunk = world.BlockToChunkPos(block);
		int lx = Mod(block.x, WorldGeneration.CHUNKSIZE);
		int ly = Mod(block.y, WorldGeneration.CHUNKSIZE);
		int half = world.HALFCHUNKSIZE;

		int ax = AnchorAxis(chunk.x, lx, half, spanX, maxX);
		int ay = AnchorAxis(chunk.y, ly, half, spanY, maxY);

		x0 = ax;
		y0 = ay;
		x1 = ax + spanX - 1;
		y1 = ay + spanY - 1;
		if (x1 > maxX)
		{
			x0 = Mathf.Max(0, maxX - (spanX - 1));
			x1 = maxX;
		}
		if (y1 > maxY)
		{
			y0 = Mathf.Max(0, maxY - (spanY - 1));
			y1 = maxY;
		}
	}

	private static int AnchorAxis(int chunk, int local, int half, int span, int maxChunk)
	{
		int maxAnchor = maxChunk - (span - 1);
		if (maxAnchor < 0)
			maxAnchor = 0;

		int ax;
		if ((span & 1) == 1)
			ax = chunk - (span / 2);
		else
			ax = chunk - (local < half ? span / 2 : (span / 2 - 1));

		return Mathf.Clamp(ax, 0, maxAnchor);
	}

	private static int Mod(int v, int m)
	{
		int r = v % m;
		return r < 0 ? r + m : r;
	}

	private static Camera ResolveCamera(WorldGeneration world)
	{
		Camera cam = FieldAccess.Get<Camera>(world, "mainCam");
		if (cam != null)
			return cam;
		if (PlayerCamera.main != null)
		{
			cam = PlayerCamera.main.GetComponent<Camera>();
			if (cam != null)
				return cam;
		}
		return Camera.main;
	}

	internal static string FormatSpans() => $"{SpanX}x{SpanY}";

	internal static string FormatMode() => ModeLabels[(int)Current];
}
