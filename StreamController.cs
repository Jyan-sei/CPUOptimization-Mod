using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPUOptimization2;

internal static class StreamController
{
	private const float UnloadGraceSeconds = 5f;
	private const int MaxStepsPerTickPlay = 2;
	private const int MaxStepsPerTickBootstrap = 8;

	private static int _lastX0 = int.MinValue;
	private static int _lastY0 = int.MinValue;
	private static int _lastX1 = int.MinValue;
	private static int _lastY1 = int.MinValue;
	private static int _lastAnchorCount;

	private static readonly Dictionary<long, float> PendingUnload = new Dictionary<long, float>();
	private static readonly Dictionary<long, int> Phase = new Dictionary<long, int>();
	private static readonly HashSet<long> DesiredScratch = new HashSet<long>();
	private static readonly HashSet<long> LastDesired = new HashSet<long>();
	private static readonly List<long> KeyScratch = new List<long>();
	private static readonly List<Vector3> AnchorScratch = new List<Vector3>(8);
	private static readonly List<Vector2Int> ElderPinScratch = new List<Vector2Int>(8);
	private static readonly HashSet<long> ElderPins = new HashSet<long>();
	private static readonly HashSet<long> ElderPinsPrev = new HashSet<long>();

	private static int _bootstrapTotal;
	private static int _bootstrapDone;
	private static float _bootstrapSavedTimeScale = 1f;
	// still stepping load/unload for the current window
	private static bool _busy;

	internal static bool NeedsTick =>
		StreamState.Active
		&& (StreamState.Bootstrapping || _busy || PendingUnload.Count > 0);

	internal static void ActivateBootstrap(WorldGeneration world)
	{
		if (world == null || !CaptureStore.Captured)
			return;

		StreamState.EnsureDummy();
		StreamState.Active = true;
		StreamState.Bootstrapping = true;
		_busy = true;
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		PendingUnload.Clear();
		Phase.Clear();

		_bootstrapSavedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
		Time.timeScale = 0f;

		if (world.loadingObject != null)
			world.loadingObject.SetActive(true);
		world.SetLoadingTextNoLocale("CPUOptimization2: packing stream cache…");

		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			if (WorldArrays.IsLoaded(world, x, y))
				Phase[Key(x, y)] = 3;
		}

		StreamWindowPolicy.TryLockFromCamera(world, "bootstrap");
		FillDesiredForWorld(world, out int x0, out int y0, out int x1, out int y1, out int anchors);
		Vector3 cam = ResolveCamera(world);
		Vector2Int camChunk = world.BlockToChunkPos(world.WorldToBlockPos(cam));

		_bootstrapTotal = 0;
		_bootstrapDone = 0;
		foreach (var kv in Phase)
		{
			if (!DesiredScratch.Contains(kv.Key))
				_bootstrapTotal++;
		}

		CaptureStore.TileCaptureDone = true;
		CaptureStore.FramesUntilTileCapture = -1;

		string unionNote = anchors > 1 ? $" unionAnchors={anchors}" : "";
		Plugin.Log.LogInfo(
			$"stream bootstrap — keep window {x0},{y0}..{x1},{y1} ({StreamWindowPolicy.FormatSpans()}) " +
			$"chunks={DesiredScratch.Count}{unionNote} camChunk={camChunk.x},{camChunk.y}; " +
			$"culling {_bootstrapTotal} chunks on load screen (no play-time grace)");

		Sync(world, force: true);
		TryFinishBootstrap(world);
	}

	internal static void Activate(WorldGeneration world)
	{
		if (world == null || !CaptureStore.Captured)
			return;
		if (StreamState.Bootstrapping)
			return;

		StreamState.EnsureDummy();
		StreamState.Active = true;
		_busy = true;
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		PendingUnload.Clear();
		Phase.Clear();
		StreamWindowPolicy.TryLockFromCamera(world, "activate");
		Sync(world, force: true);
		Plugin.Log.LogInfo(
			$"stream active — {StreamWindowPolicy.FormatMode()} {StreamWindowPolicy.FormatSpans()}; " +
			$"objectOps/frame={StreamWindowPolicy.ObjectOpsPerFrame}; out-of-window grace {UnloadGraceSeconds:0.#}s; " +
			$"≤{MaxStepsPerTickPlay} terrain steps/tick");
	}

	internal static void Deactivate()
	{
		if (StreamState.Bootstrapping && Time.timeScale == 0f)
			Time.timeScale = _bootstrapSavedTimeScale > 0f ? _bootstrapSavedTimeScale : 1f;
		StreamState.Reset();
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		PendingUnload.Clear();
		Phase.Clear();
		DesiredScratch.Clear();
		LastDesired.Clear();
		ElderPins.Clear();
		ElderPinsPrev.Clear();
		StreamObjectJobs.Clear();
		_bootstrapTotal = _bootstrapDone = 0;
		_lastAnchorCount = 0;
		_busy = false;
		ScreenCull.Clear();
	}

	/// <summary>video dropdown changed window mode mid-run, force a window resync</summary>
	internal static void NotifyPolicyChanged(WorldGeneration world)
	{
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		if (world == null || !StreamState.Active || world.generatingWorld)
			return;
		_busy = true;
		Sync(world, force: true);
	}

	internal static void Sync(WorldGeneration world, bool force = false)
	{
		if (!StreamState.Active || world == null || world.generatingWorld || !world.worldExists)
			return;

		Vector3 cam = ResolveCamera(world);
		Vector2Int camChunk = world.BlockToChunkPos(world.WorldToBlockPos(cam));
		FillDesiredForWorld(world, out int x0, out int y0, out int x1, out int y1, out int anchors);
		bool windowChanged = !DesiredMatchesLast()
			|| x0 != _lastX0 || y0 != _lastY0 || x1 != _lastX1 || y1 != _lastY1
			|| anchors != _lastAnchorCount;

		// settled. visibility pokes us alot, dont walk 256 chunks
		if (!force && !windowChanged && !StreamState.Bootstrapping && !_busy && PendingUnload.Count == 0)
		{
			EnableDesiredRenderers(world);
			WallholeResident.SyncWindow(world, x0, y0, x1, y1);
			return;
		}

		int scheduled = 0;
		int cancelled = 0;

		if (!StreamState.Bootstrapping)
		{
			KeyScratch.Clear();
			foreach (var kv in PendingUnload)
			{
				if (DesiredScratch.Contains(kv.Key))
					KeyScratch.Add(kv.Key);
			}
			for (int i = 0; i < KeyScratch.Count; i++)
			{
				PendingUnload.Remove(KeyScratch[i]);
				cancelled++;
			}

			if (windowChanged || force)
			{
				float deadline = Time.realtimeSinceStartup + UnloadGraceSeconds;
				// only unload stuff we actually phased in
				KeyScratch.Clear();
				foreach (var kv in Phase)
				{
					if (DesiredScratch.Contains(kv.Key))
						continue;
					if (kv.Value <= 0)
						continue;
					if (!PendingUnload.ContainsKey(kv.Key))
						scheduled++;
					PendingUnload[kv.Key] = deadline;
				}
				_busy = true;
			}
		}

		EnableDesiredRenderers(world);
		// wallholes stay resident. only flip their lights/audio to the live window box
		WallholeResident.SyncWindow(world, x0, y0, x1, y1);

		CommitDesired(x0, y0, x1, y1, anchors);

		int stepped = StepTowardTargets(world);
		RefreshBusy(world);

		if (Plugin.VerboseLogging.Value && (force || windowChanged || stepped > 0 || scheduled > 0 || cancelled > 0))
		{
			StringBuilder sb = new StringBuilder();
			if (StreamState.Bootstrapping)
				sb.Append("stream bootstrap ");
			else
				sb.Append("stream window ");
			sb.Append(x0).Append(',').Append(y0).Append(" .. ").Append(x1).Append(',').Append(y1);
			sb.Append(" chunks=").Append(DesiredScratch.Count);
			if (anchors > 1)
				sb.Append(" anchors=").Append(anchors);
			sb.Append(" stepped=").Append(stepped);
			if (!StreamState.Bootstrapping)
			{
				sb.Append(" grace+=").Append(scheduled);
				sb.Append(" graceCancel=").Append(cancelled);
				sb.Append(" pending=").Append(PendingUnload.Count);
			}
			else
			{
				sb.Append(" culled=").Append(_bootstrapDone).Append('/').Append(_bootstrapTotal);
			}
			sb.Append(" camChunk=").Append(camChunk.x).Append(',').Append(camChunk.y);
			Plugin.Log.LogInfo(sb.ToString());
		}
		else if (windowChanged && !StreamState.Bootstrapping)
		{
			string unionNote = anchors > 1 ? $" anchors={anchors}" : "";
			Plugin.Log.LogInfo(
				$"stream window {x0},{y0}..{x1},{y1} chunks={DesiredScratch.Count}{unionNote} " +
				$"camChunk={camChunk.x},{camChunk.y} pending={PendingUnload.Count}");
		}

		if (StreamState.Bootstrapping)
			TryFinishBootstrap(world);
	}

	internal static void Tick(WorldGeneration world)
	{
		if (!NeedsTick || world == null || world.generatingWorld || !world.worldExists)
			return;

		FillDesiredForWorld(world, out int x0, out int y0, out int x1, out int y1, out int anchors);
		bool windowChanged = !DesiredMatchesLast()
			|| x0 != _lastX0 || y0 != _lastY0 || x1 != _lastX1 || y1 != _lastY1
			|| anchors != _lastAnchorCount;
		if (windowChanged)
		{
			Sync(world);
			return;
		}

		StepTowardTargets(world);
		RefreshBusy(world);

		if (StreamState.Bootstrapping)
			TryFinishBootstrap(world);
	}

	private static void RefreshBusy(WorldGeneration world)
	{
		if (StreamState.Bootstrapping)
		{
			_busy = true;
			return;
		}

		if (PendingUnload.Count > 0)
		{
			_busy = true;
			return;
		}

		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			if (GetPhase(world, key, cx, cy) < 3)
			{
				_busy = true;
				return;
			}
		}

		_busy = false;
	}

	private static void EnableDesiredRenderers(WorldGeneration world)
	{
		if (world.renderChunks == null)
			return;
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int x, out int y);
			if (world.renderChunks[x, y] != null)
				world.renderChunks[x, y].enabled = true;
		}
	}

	private static void TryFinishBootstrap(WorldGeneration world)
	{
		if (!StreamState.Bootstrapping)
			return;

		foreach (var kv in Phase)
		{
			if (DesiredScratch.Contains(kv.Key))
				continue;
			// elder chunks sit at phase 2 for the whole roam. they are not leftover cull work
			if (ElderPins.Contains(kv.Key))
				continue;
			if (kv.Value > 0)
			{
				world.SetLoadingTextNoLocale($"CPUOptimization2: culling {_bootstrapDone}/{_bootstrapTotal}…");
				return;
			}
		}

		StreamState.Bootstrapping = false;
		PendingUnload.Clear();
		_busy = false;
		Time.timeScale = _bootstrapSavedTimeScale > 0f ? _bootstrapSavedTimeScale : 1f;
		if (world.loadingObject != null)
			world.loadingObject.SetActive(false);

		Plugin.Log.LogInfo(
			$"stream bootstrap done — live {StreamWindowPolicy.FormatSpans()} only; arrays hold the rest " +
			$"(traders={CaptureStore.Traders.Count} enemies={CaptureStore.Enemies.Count} " +
			$"entities={CaptureStore.Entities.Count} worldItems={CaptureStore.WorldItems.Count} " +
			$"lights={CaptureStore.Lights.Count})");
		SanityLog.LogBootstrapDone();
	}

	/// <summary>a keep-window chunk still needs its tilemap, colliders, or object spawns</summary>
	private static bool CreatesWaiting(WorldGeneration world)
	{
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			if (GetPhase(world, key, cx, cy) < 3)
				return true;
		}
		return false;
	}

	private static int StepTowardTargets(WorldGeneration world)
	{
		float now = Time.realtimeSinceStartup;
		int stepped = 0;
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		int maxSteps = StreamState.Bootstrapping ? MaxStepsPerTickBootstrap : MaxStepsPerTickPlay;
		int objectBudget = StreamWindowPolicy.ObjectOpsPerFrame;

		for (int pass = 0; pass < 2 && stepped < maxSteps; pass++)
		{
			bool loadingPass = pass == 0;

			if (loadingPass)
			{
				foreach (long key in DesiredScratch)
				{
					if (stepped >= maxSteps)
						break;
					Decode(key, out int cx, out int cy);
					int phase = GetPhase(world, key, cx, cy);
					if (phase >= 3)
						continue;
					bool advanced = StepUp(world, key, cx, cy, phase, ref objectBudget);
					if (phase >= 2)
					{
						// paced object spawn. one chunk per tick untill its arrays drain
						if (advanced)
							stepped++;
						break;
					}
					if (advanced)
						stepped++;
				}
				continue;
			}

			// spawns own the frame. dont tear a chunk down while another still needs objects
			if (CreatesWaiting(world))
				break;

			if (StreamState.Bootstrapping)
			{
				for (int x = 0; x < width && stepped < maxSteps; x++)
				for (int y = 0; y < height && stepped < maxSteps; y++)
				{
					long key = Key(x, y);
					if (DesiredScratch.Contains(key))
						continue;
					int phase = GetPhase(world, key, x, y);
					if (phase <= 0)
						continue;
					int before = phase;
					bool advanced = StepDown(world, key, x, y, phase, bootstrap: true, ref objectBudget);
					if (phase >= 3)
					{
						if (advanced && before > 0 && GetPhase(world, key, x, y) == 0)
							_bootstrapDone++;
						if (advanced)
							stepped++;
						goto afterUnloadPass;
					}
					if (advanced)
					{
						if (before > 0 && GetPhase(world, key, x, y) == 0)
							_bootstrapDone++;
						stepped++;
					}
				}
			}
			else
			{
				KeyScratch.Clear();
				foreach (var kv in PendingUnload)
				{
					if (kv.Value > now)
						continue;
					if (DesiredScratch.Contains(kv.Key))
						continue;
					KeyScratch.Add(kv.Key);
				}
				for (int i = 0; i < KeyScratch.Count && stepped < maxSteps; i++)
				{
					long key = KeyScratch[i];
					Decode(key, out int cx, out int cy);
					int phase = GetPhase(world, key, cx, cy);
					if (phase <= 0)
					{
						PendingUnload.Remove(key);
						continue;
					}
					bool advanced = StepDown(world, key, cx, cy, phase, bootstrap: false, ref objectBudget);
					if (phase >= 3)
					{
						if (advanced)
						{
							if (GetPhase(world, key, cx, cy) == 0)
								PendingUnload.Remove(key);
							stepped++;
						}
						break;
					}
					if (advanced)
					{
						if (GetPhase(world, key, cx, cy) == 0)
							PendingUnload.Remove(key);
						stepped++;
					}
				}
			}
		}

		afterUnloadPass:
		if (StreamState.Bootstrapping && _bootstrapTotal > 0)
		{
			world.SetLoadingTextNoLocale(
				$"CPUOptimization2: culling {_bootstrapDone}/{_bootstrapTotal}…");
		}

		return stepped;
	}

	/// <summary>true when a terrain phase moved, or the object job finished this tick</summary>
	private static bool StepUp(WorldGeneration world, long key, int cx, int cy, int phase, ref int objectBudget)
	{
		if (phase <= 0)
		{
			ChunkTerrain.CreateRenderer(world, cx, cy);
			Phase[key] = 1;
			return true;
		}
		if (phase == 1)
		{
			ChunkTerrain.EnableColliders(world, cx, cy);
			Phase[key] = 2;
			return true;
		}
		if (phase == 2)
		{
			if (objectBudget <= 0)
				return false;
			bool done = StreamObjectJobs.SpawnStep(world, cx, cy, ref objectBudget);
			if (done)
				Phase[key] = 3;
			return done;
		}
		return false;
	}

	/// <summary>
	/// elders roam outside the window. a chunk they enter gets its collider once.
	/// a chunk they leave drops the collider now, then the rest of the unload waits out the grace.
	/// holding the same chunk does not touch physics.
	/// </summary>
	internal static void MaintainElders(WorldGeneration world)
	{
		if (!StreamState.Active || world == null)
			return;

		ElderResident.CollectPins(world, ElderPinScratch);
		// dead players share the hold. collider stays until respawn, disconnect, or the body is gone
		PlayerCorpseGround.AppendPins(world, ElderPinScratch);
		ElderPinsPrev.Clear();
		foreach (long key in ElderPins)
			ElderPinsPrev.Add(key);
		ElderPins.Clear();
		ElderResident.ClearProtectedRenderers();

		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		for (int i = 0; i < ElderPinScratch.Count; i++)
		{
			int cx = ElderPinScratch[i].x;
			int cy = ElderPinScratch[i].y;
			if (cx < 0 || cy < 0 || cx >= width || cy >= height)
				continue;

			long key = Key(cx, cy);
			ElderPins.Add(key);

			bool wasLoaded = WorldArrays.IsLoaded(world, cx, cy);
			int existing = 0;
			if (!Phase.TryGetValue(key, out existing))
				existing = wasLoaded ? 3 : 0;

			// same chunk as last frame and the tilemap is still there: collider stays as it is
			bool held = ElderPinsPrev.Contains(key) && wasLoaded;
			if (!held)
				ChunkTerrain.EnsureSim(world, cx, cy);
			else if (world.renderChunks != null && world.renderChunks[cx, cy] != null && !world.renderChunks[cx, cy].enabled)
				world.renderChunks[cx, cy].enabled = true;

			if (existing < 2)
				Phase[key] = 2;
			else if (!Phase.ContainsKey(key))
				Phase[key] = existing;

			PendingUnload.Remove(key);
			if (world.renderChunks != null && world.renderChunks[cx, cy] != null)
				ElderResident.ProtectRenderer(world.renderChunks[cx, cy]);
		}

		float releaseAt = Time.realtimeSinceStartup + UnloadGraceSeconds;
		foreach (long key in ElderPinsPrev)
		{
			if (ElderPins.Contains(key))
				continue;
			// player window still wants this ground. only the elder's private chunks drop the collider
			if (DesiredScratch.Contains(key))
				continue;
			Decode(key, out int cx, out int cy);
			ChunkTerrain.DisableColliders(world, cx, cy);
			if (!Phase.TryGetValue(key, out int phase) || phase <= 0)
				continue;
			if (!PendingUnload.ContainsKey(key))
				PendingUnload[key] = releaseAt;
			_busy = true;
		}
	}

	private static bool StepDown(
		WorldGeneration world,
		long key,
		int cx,
		int cy,
		int phase,
		bool bootstrap,
		ref int objectBudget)
	{
		if (phase >= 3)
		{
			if (objectBudget <= 0)
				return false;
			bool done = StreamObjectJobs.DestroyStep(world, cx, cy, writeback: !bootstrap, ref objectBudget);
			if (done)
				Phase[key] = 2;
			return done;
		}
		if (phase == 2)
		{
			// chew + sim. collider was enabled on the way in. don't rebuild it while they stay
			if (ElderPins.Contains(key))
				return false;
			ChunkTerrain.DisableColliders(world, cx, cy);
			Phase[key] = 1;
			return true;
		}
		if (phase == 1)
		{
			ChunkTerrain.DestroyRenderer(world, cx, cy);
			Phase.Remove(key);
			return true;
		}
		return false;
	}

	private static int GetPhase(WorldGeneration world, long key, int cx, int cy)
	{
		if (Phase.TryGetValue(key, out int p))
			return p;
		if (WorldArrays.IsLoaded(world, cx, cy))
		{
			Phase[key] = 3;
			return 3;
		}
		return 0;
	}

	/// <summary>cam-anchored window from StreamWindowPolicy (dynamic / 3x3 / 4x4)</summary>
	internal static void GetWindowAround(WorldGeneration world, Vector3 camWorld, out int x0, out int y0, out int x1, out int y1)
	{
		StreamWindowPolicy.GetAnchoredWindow(world, camWorld, out x0, out y0, out x1, out y1);
	}

	/// <summary>same thing but pretend the cam is sitting on the chunk center</summary>
	internal static void GetWindowAround(WorldGeneration world, Vector2Int chunk, out int x0, out int y0, out int x1, out int y1)
	{
		int half = world.HALFCHUNKSIZE;
		Vector2Int block = new Vector2Int(
			chunk.x * WorldGeneration.CHUNKSIZE + half,
			chunk.y * WorldGeneration.CHUNKSIZE + half);
		Vector2 worldPos = world.BlockToWorldPos(block);
		GetWindowAround(world, worldPos, out x0, out y0, out x1, out y1);
	}

	/// <summary>
	/// sp: one window around the local cam.
	/// mp host: union of windows around each living NetPlayer anchor.
	/// </summary>
	private static void FillDesiredForWorld(
		WorldGeneration world,
		out int x0,
		out int y0,
		out int x1,
		out int y1,
		out int anchors)
	{
		DesiredScratch.Clear();
		anchors = 0;
		x0 = y0 = int.MaxValue;
		x1 = y1 = int.MinValue;

		if (MpSession.Running)
		{
			MpSession.CollectLivingPlayerAnchors(AnchorScratch);
			anchors = AnchorScratch.Count;
			for (int i = 0; i < AnchorScratch.Count; i++)
			{
				GetWindowAround(world, AnchorScratch[i], out int ax0, out int ay0, out int ax1, out int ay1);
				AddWindow(ax0, ay0, ax1, ay1, ref x0, ref y0, ref x1, ref y1);
			}
		}

		if (DesiredScratch.Count == 0)
		{
			Vector3 cam = ResolveCamera(world);
			GetWindowAround(world, cam, out int sx0, out int sy0, out int sx1, out int sy1);
			x0 = y0 = int.MaxValue;
			x1 = y1 = int.MinValue;
			AddWindow(sx0, sy0, sx1, sy1, ref x0, ref y0, ref x1, ref y1);
			if (anchors == 0)
				anchors = 1;
		}
	}

	private static void AddWindow(int ax0, int ay0, int ax1, int ay1, ref int x0, ref int y0, ref int x1, ref int y1)
	{
		if (ax0 < x0) x0 = ax0;
		if (ay0 < y0) y0 = ay0;
		if (ax1 > x1) x1 = ax1;
		if (ay1 > y1) y1 = ay1;
		for (int x = ax0; x <= ax1; x++)
		for (int y = ay0; y <= ay1; y++)
			DesiredScratch.Add(Key(x, y));
	}

	private static bool DesiredMatchesLast()
	{
		if (DesiredScratch.Count != LastDesired.Count)
			return false;
		foreach (long key in DesiredScratch)
		{
			if (!LastDesired.Contains(key))
				return false;
		}
		return true;
	}

	private static void CommitDesired(int x0, int y0, int x1, int y1, int anchors)
	{
		LastDesired.Clear();
		foreach (long key in DesiredScratch)
			LastDesired.Add(key);
		_lastX0 = x0;
		_lastY0 = y0;
		_lastX1 = x1;
		_lastY1 = y1;
		_lastAnchorCount = anchors;
	}

	internal static bool IsChunkDesired(int cx, int cy)
	{
		if (!StreamState.Active)
			return true;
		long key = Key(cx, cy);
		if (DesiredScratch.Count == 0 && LastDesired.Count == 0)
			return true;
		if (DesiredScratch.Count > 0)
			return DesiredScratch.Contains(key);
		return LastDesired.Contains(key);
	}

	/// <summary>for StreamWindowQuery. stream inactive means in-window, dont cull</summary>
	internal static bool IsAuthoritySimWorldPos(Vector3 worldPos)
	{
		if (!StreamState.Active)
			return true;

		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists)
			return true;

		// keep set isnt committed yet, safer to keep it
		if (LastDesired.Count == 0 && DesiredScratch.Count == 0)
			return true;

		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(worldPos));
		long key = Key(chunk.x, chunk.y);
		if (DesiredScratch.Count > 0)
			return DesiredScratch.Contains(key);
		return LastDesired.Contains(key);
	}

	private static Vector3 ResolveCamera(WorldGeneration world)
	{
		Camera cam = FieldAccess.Get<Camera>(world, "mainCam");
		if (cam != null)
			return cam.transform.position;
		if (PlayerCamera.main != null)
			return PlayerCamera.main.transform.position;
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			return PlayerCamera.main.body.transform.position;
		return Vector3.zero;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

	private static void Decode(long key, out int x, out int y)
	{
		x = (int)(key >> 32);
		y = (int)(uint)key;
	}
}
