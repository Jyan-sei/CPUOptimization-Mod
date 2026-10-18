using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

internal static class StreamController
{
	private const float UnloadGraceSeconds = 5f;
	private const int MaxStepsPerTickBootstrap = 8;

	// play points per frame = configMult (4-10) * mpMult.
	// mpMult is 1 through 4 players, 1.5 for 5-9, 2 for 10+.
	private const int WeightRenderer = 1;
	private const int WeightWriteback = 4;
	private const int WeightStoreLoose = 4;
	// post-gen cull heavies cost the default chunk budget, not the live setting.
	private const int CullHeavyCost = 4;
	private const float QueuePanicAfterSeconds = 2f;

	private static int _lastX0 = int.MinValue;
	private static int _lastY0 = int.MinValue;
	private static int _lastX1 = int.MinValue;
	private static int _lastY1 = int.MinValue;
	private static int _lastAnchorCount;
	private static bool _spanValid;
	private static bool _spanHeld;
	private static int _spanBx0, _spanBy0, _spanBx1, _spanBy1;
	private static bool _spanView;
	private static int _spanVx0, _spanVy0, _spanVx1, _spanVy1;
	private static int _holdBx0, _holdBy0, _holdBx1, _holdBy1;
	private static bool _holdView;
	private static int _holdVx0, _holdVy0, _holdVx1, _holdVy1;
	private static float _bodyOutLog;
	private static float _queueBusySince = -1f;
	private static bool _queuePanicOn;
	private static int _playActionFrame = -1;
	private static readonly HashSet<long> LooseStored = new HashSet<long>();
	private static readonly HashSet<long> CullLoose = new HashSet<long>();

	private static readonly Dictionary<long, float> PendingUnload = new Dictionary<long, float>();
	private static readonly Dictionary<long, int> Phase = new Dictionary<long, int>();
	private static readonly HashSet<long> DesiredScratch = new HashSet<long>();
	private static readonly HashSet<long> LastDesired = new HashSet<long>();
	private static readonly List<long> KeyScratch = new List<long>();
	private static readonly List<Vector3> AnchorScratch = new List<Vector3>(8);
	private static readonly List<Vector2Int> ElderPinScratch = new List<Vector2Int>(8);
	private static readonly HashSet<long> ElderPins = new HashSet<long>();
	private static readonly HashSet<long> ElderPinsPrev = new HashSet<long>();
	private static int _heavyFrame = -1;
	private static bool _heavySpent;

	private static int _bootstrapTotal;
	private static int _bootstrapDone;
	private static float _bootstrapSavedTimeScale = 1f;
	// overlay stays up until keep chunks are phase 3 and the rest are phase 0.
	// timeScale is 0, so the beat uses realtime. same token across beats means that chunk stopped.
	private const float BootstrapBeatSeconds = 2f;
	private static float _bootstrapStartedAt;
	private static float _bootstrapBeatAt;
	private static string _bootstrapBeatToken;
	private static int _bootstrapBeatStall;
	// last bootstrap step. the overlay and the log both read this so a hang has a chunk.
	private static int _stepCx = -1;
	private static int _stepCy;
	private static int _stepPhase;
	private static string _stepName = "";
	private static string _genStepLast;
	// still stepping load/unload for the current window
	private static bool _busy;
	// camera is not allowed to move the keep set until the overlay drops.
	// a falling or walking player otherwise retargets spawn and cull forever.
	private static readonly HashSet<long> FrozenDesired = new HashSet<long>();
	private static bool _windowFrozen;
	private static int _freezeX0, _freezeY0, _freezeX1, _freezeY1;
	// chunks whose renderer we already removed. GetPhase must not put them back at 3
	// just because the tile array still says loaded for a frame.
	private static readonly HashSet<long> BootstrapRemoved = new HashSet<long>();
	// keep-window chunks that lost their collider while teardown was on
	private static readonly HashSet<long> RepairColliders = new HashSet<long>();
	// a rebuild that did not produce a tilemap. retrying it every frame eats the heavy slot.
	private static readonly HashSet<long> BuildGaveUp = new HashSet<long>();

	internal static bool NeedsTick =>
		StreamState.Active
		&& (StreamState.Bootstrapping || _busy || PendingUnload.Count > 0 || RepairColliders.Count > 0);

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
		_bootstrapStartedAt = Time.realtimeSinceStartup;
		_bootstrapBeatAt = 0f;
		_bootstrapBeatToken = null;
		_bootstrapBeatStall = 0;
		_stepCx = -1;
		_stepName = "";
		_genStepLast = null;
		BootstrapRemoved.Clear();
		HideLoadScreen(world);

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
		FreezeBootstrapWindow(x0, y0, x1, y1);
		// gen ghosted against whatever camera existed then. WorldPlacePlayer is later,
		// so the spawn chunks are terrain-only. phase 3 would skip SpawnStep forever.
		int spawnFill = 0;
		foreach (long key in DesiredScratch)
		{
			if (Phase.TryGetValue(key, out int phase) && phase >= 3)
			{
				Phase[key] = 2;
				spawnFill++;
			}
		}
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
			$"stream bootstrap - keep window {x0},{y0}..{x1},{y1} ({StreamWindowPolicy.FormatSpans()}) " +
			$"chunks={DesiredScratch.Count}{unionNote} camChunk={camChunk.x},{camChunk.y}; " +
			$"culling {_bootstrapTotal} chunks on load screen (no play-time grace); " +
			$"spawn-fill {spawnFill} keep chunks");

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
		ChunkSeams.Seed(world);
		Plugin.Log.LogInfo(
			$"stream active - {StreamWindowPolicy.FormatMode()} {StreamWindowPolicy.FormatSpans()}; " +
			$"play budget {PlayFrameBudget()} (configMult {ConfigMult()} * mpMult, panic does not scale play); " +
			$"cull budget {CullFrameBudget()} (own setting, heavy {CullHeavyCost}); " +
			$"out-of-window grace {UnloadGraceSeconds:0.#}s");
	}

	internal static void Deactivate()
	{
		if (!MpSession.Running && StreamState.Bootstrapping && Time.timeScale == 0f)
			Time.timeScale = _bootstrapSavedTimeScale > 0f ? _bootstrapSavedTimeScale : 1f;
		StreamState.Reset();
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		PendingUnload.Clear();
		LooseStored.Clear();
		CullLoose.Clear();
		_playActionFrame = -1;
		Phase.Clear();
		DesiredScratch.Clear();
		LastDesired.Clear();
		FrozenDesired.Clear();
		_windowFrozen = false;
		_queueBusySince = -1f;
		_queuePanicOn = false;
		BootstrapRemoved.Clear();
		RepairColliders.Clear();
		BuildGaveUp.Clear();
		ElderPins.Clear();
		ElderPinsPrev.Clear();
		_heavySpent = false;
		StreamObjectJobs.Clear();
		_bootstrapTotal = _bootstrapDone = 0;
		_stepCx = -1;
		_stepName = "";
		_genStepLast = null;
		_lastAnchorCount = 0;
		_busy = false;
		ScreenCull.Clear();
		LayerAlertHold.Clear();
		ChunkSeams.Clear();
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

	/// <summary>
	/// chunk renderer teardown flipped. on queues every hidden chunk that still
	/// has a tilemap. off rebuilds any keep-window chunk whose tilemap is already gone.
	/// </summary>
	internal static void NotifyRendererTeardown(bool on)
	{
		WorldGeneration world = WorldGeneration.world;
		if (!ReadyForToggle(world))
			return;
		if (on)
			QueueOutsideTeardown(world, renderers: true);
		else
			RewakeMissing(world);
		_busy = true;
	}

	/// <summary>
	/// collider teardown flipped. on queues outside chunks that still simulate.
	/// off puts colliders back on the chunks you are standing in.
	/// </summary>
	internal static void NotifyColliderTeardown(bool on)
	{
		WorldGeneration world = WorldGeneration.world;
		if (!ReadyForToggle(world))
			return;
		if (on)
			QueueOutsideTeardown(world, renderers: false);
		else
			QueueColliderRepair(world);
		_busy = true;
	}

	private static bool ReadyForToggle(WorldGeneration world)
	{
		return StreamState.Active && world != null && !world.generatingWorld && world.worldExists;
	}

	private static void QueueOutsideTeardown(WorldGeneration world, bool renderers)
	{
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		int queued = 0;
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			if (!WorldArrays.IsLoaded(world, x, y))
				continue;
			long key = Key(x, y);
			if (DesiredScratch.Contains(key) || ElderPins.Contains(key))
				continue;
			if (!renderers && !ChunkTerrain.CollidersEnabled(world, x, y))
				continue;
			int phase = 0;
			Phase.TryGetValue(key, out phase);
			// phase 0 already finished the cheap hide. bump it so HideChunk runs again
			// and actually destroys the tilemap, or disables the collider.
			if (phase <= 0)
				Phase[key] = 1;
			if (!PendingUnload.ContainsKey(key))
				queued++;
			PendingUnload[key] = 0f;
		}
		Plugin.Log.LogInfo(
			(renderers ? "stream renderer teardown on, queued " : "stream collider teardown on, queued ")
			+ queued);
	}

	private static void RewakeMissing(WorldGeneration world)
	{
		int n = 0;
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			if (WorldArrays.IsLoaded(world, cx, cy))
				continue;
			BuildGaveUp.Remove(key);
			Phase[key] = 0;
			n++;
		}
		Plugin.Log.LogInfo("stream renderer teardown off, rebuild " + n);
	}

	private static void QueueColliderRepair(WorldGeneration world)
	{
		RepairColliders.Clear();
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			if (!WorldArrays.IsLoaded(world, cx, cy))
				continue;
			if (ChunkTerrain.CollidersEnabled(world, cx, cy))
				continue;
			RepairColliders.Add(key);
		}
		Plugin.Log.LogInfo("stream collider teardown off, repair " + RepairColliders.Count);
	}

	internal static void Sync(WorldGeneration world, bool force = false)
	{
		if (!StreamState.Active || world == null || world.generatingWorld || !world.worldExists)
			return;

		Vector3 cam = ResolveCamera(world);
		Vector2Int camChunk = world.BlockToChunkPos(world.WorldToBlockPos(cam));

		// the 2x2 slides when the body crosses a chunk half, not only a chunk edge.
		// until that rect changes, the set and the renderers are already right.
		// the host union moves when someone else walks. a living client follows her body.
		// a dead client also follows the camera once it leaves the corpse chunks.
		bool hostUnion = MpSession.Running && MpSession.IsServer;
		if (!force && !StreamState.Bootstrapping && !_busy && PendingUnload.Count == 0
			&& _lastX0 != int.MinValue && !hostUnion)
		{
			if (ClientSpanStable(world))
				return;
		}

		long tFill = StepTrace.Stamp();
		FillDesiredForWorld(world, out int x0, out int y0, out int x1, out int y1, out int anchors);
		StepTrace.Mark("book", "fill-desired", x0, y0, 0, -1,
			x0 + "," + y0 + ".." + x1 + "," + y1 + " anchors=" + anchors, tFill, false);
		bool windowChanged = !DesiredMatchesLast()
			|| x0 != _lastX0 || y0 != _lastY0 || x1 != _lastX1 || y1 != _lastY1
			|| anchors != _lastAnchorCount;
		NoteBodyOutside(world, x0, y0, x1, y1);

		// settled. visibility pokes us alot, dont walk 256 chunks
		if (!force && !windowChanged && !StreamState.Bootstrapping && !_busy && PendingUnload.Count == 0)
		{
			long tOn = StepTrace.Stamp();
			EnableDesiredRenderers(world);
			StepTrace.Mark("book", "enable-renderers", x0, y0, 0, -1, null, tOn, false);
			long tHole = StepTrace.Stamp();
			WallholeResident.SyncWindow(world, x0, y0, x1, y1);
			StepTrace.Mark("book", "wallhole", x0, y0, 0, -1, null, tHole, false);
			return;
		}

		int scheduled = 0;
		int cancelled = 0;
		long tSched = StepTrace.Stamp();

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
				long backKey = KeyScratch[i];
				PendingUnload.Remove(backKey);
				LooseStored.Remove(backKey);
				cancelled++;
				// noclip leaves long enough for the destroy job to finish.
				// a short step back cancels grace while DestroyStep already snapshotted
				// and maybe deleted. an idle job must not stick, or the next leave skips writeback.
				Decode(backKey, out int bx, out int by);
				StreamObjectJobs.UnloadCancel back = StreamObjectJobs.CancelUnload(bx, by);
				if (back.Wrote && !back.DestroyedAny)
					ChunkObjects.MarkChunkLive(bx, by);
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

		StepTrace.Mark("book", "schedule-unload", x0, y0, 0, -1,
			"grace+=" + scheduled + " cancel=" + cancelled + " pending=" + PendingUnload.Count, tSched, false);
		long tOnBusy = StepTrace.Stamp();
		EnableDesiredRenderers(world);
		StepTrace.Mark("book", "enable-renderers", x0, y0, 0, -1, null, tOnBusy, false);
		// wallholes stay resident. only flip their lights/audio to the live window box
		long tHoleBusy = StepTrace.Stamp();
		WallholeResident.SyncWindow(world, x0, y0, x1, y1);
		StepTrace.Mark("book", "wallhole", x0, y0, 0, -1, null, tHoleBusy, false);

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
			sb.Append(BodyNote(world));
			Plugin.Log.LogInfo(sb.ToString());
		}
		else if (windowChanged && !StreamState.Bootstrapping)
		{
			string unionNote = anchors > 1 ? $" anchors={anchors}" : "";
			Plugin.Log.LogInfo(
				$"stream window {x0},{y0}..{x1},{y1} chunks={DesiredScratch.Count}{unionNote} " +
				$"camChunk={camChunk.x},{camChunk.y} pending={PendingUnload.Count}{BodyNote(world)}");
		}

		if (StreamState.Bootstrapping)
			TryFinishBootstrap(world);
	}

	internal static int TickCount;

	internal static string DebugLine()
	{
		return "ticks=" + TickCount
			+ " boot=" + (StreamState.Bootstrapping ? 1 : 0)
			+ " culled=" + _bootstrapDone + "/" + _bootstrapTotal
			+ " step=" + _stepCx + "," + _stepCy
			+ " p" + _stepPhase + " " + _stepName
			+ " scale=" + Time.timeScale;
	}

	internal static void Tick(WorldGeneration world)
	{
		TickCount++;
		if (!NeedsTick || world == null || world.generatingWorld || !world.worldExists)
			return;

		long tFill = StepTrace.Stamp();
		FillDesiredForWorld(world, out int x0, out int y0, out int x1, out int y1, out int anchors);
		StepTrace.Mark("book", "fill-desired", x0, y0, 0, -1,
			x0 + "," + y0 + ".." + x1 + "," + y1 + " anchors=" + anchors, tFill, false);
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
		bool running = StreamState.Bootstrapping || PendingUnload.Count > 0 || RepairColliders.Count > 0;
		if (!running)
		{
			foreach (long key in DesiredScratch)
			{
				Decode(key, out int cx, out int cy);
				if (GetPhase(world, key, cx, cy) < 3)
				{
					running = true;
					break;
				}
			}
		}

		_busy = running;
		NoteQueuePanic(running);
	}

	/// <summary>one heavy collider or renderer op per frame. reset before elders, which can spend it first.</summary>
	internal static void BeginFrame()
	{
		if (_heavyFrame == Time.frameCount)
			return;
		_heavyFrame = Time.frameCount;
		_heavySpent = false;
	}

	private static bool WantColliders =>
		Plugin.BuildColliders != null && Plugin.BuildColliders.Value;

	private static bool WantRenderers =>
		Plugin.BuildRenderers != null && Plugin.BuildRenderers.Value;

	/// <summary>
	/// a play build or teardown spends the whole play budget, whatever that setting is.
	/// the bootstrap cull does not: it has its own pool and spends points.
	/// </summary>
	private static bool TrySpendHeavy(ref int budget)
	{
		if (_heavySpent)
			return false;
		if (!StreamState.Bootstrapping && budget < PlayFrameBudget())
			return false;
		budget = 0;
		_heavySpent = true;
		return true;
	}

	/// <summary>post-gen cull pool. the config value itself. no chunk-budget scale and no mp multiplier.</summary>
	private static int CullFrameBudget()
	{
		int n = Plugin.CullBudget != null ? Plugin.CullBudget.Value : 8;
		if (n < 1)
			n = 1;
		if (n > 40)
			n = 40;
		return n;
	}

	/// <summary>same stop rule as play: an action runs only when the points left cover its cost.</summary>
	private static bool TrySpendPoints(ref int budget, int cost)
	{
		if (cost < 1)
			cost = 1;
		if (budget < cost)
			return false;
		budget -= cost;
		return true;
	}

	/// <summary>configMult clamped to 4-10. default 4.</summary>
	private static int ConfigMult()
	{
		int configMult = Plugin.StreamBudget != null ? Plugin.StreamBudget.Value : 4;
		if (configMult < 4)
			configMult = 4;
		if (configMult > 10)
			configMult = 10;
		return configMult;
	}

	/// <summary>
	/// points this frame. configMult * mpMult.
	/// solo through 4 living players: 1. lobbies of 5-9: 1.5 (half rounds up). 10+: 2.
	/// a setting of 10 with 10 players is 20.
	/// </summary>
	private static int PlayFrameBudget()
	{
		int configMult = ConfigMult();
		int players = 0;
		if (MpSession.Running)
			players = _lastAnchorCount > 0 ? _lastAnchorCount : 1;
		if (players >= 10)
			return configMult * 2;
		if (players >= 5)
			return (configMult * 3 + 1) / 2;
		return configMult;
	}

	/// <summary>
	/// create and teardown budgets. QueuePanic, after the queue has been busy for two
	/// seconds without a gap, doubles both allotments for as long as it stays busy.
	/// </summary>
	private static int QueueAllotment(int baseAmount)
	{
		if (baseAmount < 0)
			baseAmount = 0;
		return _queuePanicOn ? baseAmount * 2 : baseAmount;
	}

	private static bool PanicAllowed()
	{
		return Plugin.QueuePanic != null && Plugin.QueuePanic.Value;
	}

	private static void NoteQueuePanic(bool running)
	{
		if (!running || !PanicAllowed())
		{
			if (_queuePanicOn)
				Plugin.Log.LogInfo("stream queue panic off");
			_queueBusySince = -1f;
			_queuePanicOn = false;
			return;
		}

		float now = Time.realtimeSinceStartup;
		if (_queueBusySince < 0f)
			_queueBusySince = now;
		if (_queuePanicOn || now - _queueBusySince < QueuePanicAfterSeconds)
			return;
		_queuePanicOn = true;
		Plugin.Log.LogInfo(
			"stream queue panic x2 after " + (now - _queueBusySince).ToString("0.0") + "s");
	}

	private static void EnableDesiredRenderers(WorldGeneration world)
	{
		if (world.renderChunks == null)
			return;
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int x, out int y);
			ChunkTerrain.SetRendererEnabled(world, x, y, true);
		}
	}

	private static void TryFinishBootstrap(WorldGeneration world)
	{
		if (!StreamState.Bootstrapping)
			return;

		int placeLeft = 0;
		int cullLeft = 0;
		long placeKey = long.MaxValue;
		long cullKey = long.MinValue;
		int placePhase = 0;
		int cullPhase = 0;
		long stepKey = _stepCx >= 0 ? Key(_stepCx, _stepCy) : long.MinValue;
		bool stepPlace = false;
		bool stepCull = false;
		int stepPhase = _stepPhase;
		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			int phase = GetPhase(world, key, cx, cy);
			if (phase >= 3)
				continue;
			placeLeft++;
			if (key == stepKey)
			{
				stepPlace = true;
				stepPhase = phase;
			}
			if (key < placeKey)
			{
				placeKey = key;
				placePhase = phase;
			}
		}

		foreach (var kv in Phase)
		{
			if (DesiredScratch.Contains(kv.Key))
				continue;
			// elder chunks sit at phase 2 for the whole roam. they are not leftover cull work
			if (ElderPins.Contains(kv.Key))
				continue;
			if (kv.Value <= 0)
				continue;
			cullLeft++;
			if (kv.Key == stepKey)
			{
				stepCull = true;
				stepPhase = kv.Value;
			}
			if (kv.Key > cullKey)
			{
				cullKey = kv.Key;
				cullPhase = kv.Value;
			}
		}

		if (placeLeft > 0)
		{
			int cx;
			int cy;
			int phase;
			if (stepPlace)
			{
				cx = _stepCx;
				cy = _stepCy;
				phase = stepPhase;
			}
			else
			{
				Decode(placeKey, out cx, out cy);
				phase = placePhase;
			}
			HideLoadScreen(world);
			BeatBootstrap(true, cx, cy, phase, placeLeft, cullLeft);
			return;
		}

		if (cullLeft > 0)
		{
			int cx;
			int cy;
			int phase;
			if (stepCull)
			{
				cx = _stepCx;
				cy = _stepCy;
				phase = stepPhase;
			}
			else
			{
				Decode(cullKey, out cx, out cy);
				phase = cullPhase;
			}
			HideLoadScreen(world);
			BeatBootstrap(false, cx, cy, phase, placeLeft, cullLeft);
			return;
		}

		StreamState.Bootstrapping = false;
		_windowFrozen = false;
		FrozenDesired.Clear();
		PendingUnload.Clear();
		_busy = false;
		// walls exist before physics resumes. the load-screen cull is not a play budget.
		ChunkSeams.Seed(world);
		int seamBudget = 0;
		ChunkSeams.Refresh(world, ref seamBudget, false);
		if (!MpSession.Running && !LayerLoadLog.ScreenDropped)
		{
			Time.timeScale = _bootstrapSavedTimeScale > 0f ? _bootstrapSavedTimeScale : 1f;
			if (world.loadingObject != null)
				world.loadingObject.SetActive(false);
			LayerAlertHold.Release();
		}

		Plugin.Log.LogInfo(
			$"stream bootstrap done t={(Time.realtimeSinceStartup - _bootstrapStartedAt):0.0}s - live {StreamWindowPolicy.FormatSpans()} only; arrays hold the rest " +
			$"(traders={CaptureStore.Traders.Count} enemies={CaptureStore.Enemies.Count} " +
			$"entities={CaptureStore.Entities.Count} worldItems={CaptureStore.WorldItems.Count} " +
			$"lights={CaptureStore.Lights.Count})");
		LayerLoadLog.Finish("bootstrap");
		SanityLog.LogBootstrapDone();
	}

	/// <summary>the mod does not own a loading screen. keep the game's object hidden if a status write turned it back on.</summary>
	private static void HideLoadScreen(WorldGeneration world)
	{
		if (world != null && world.loadingObject != null && world.loadingObject.activeSelf)
			world.loadingObject.SetActive(false);
		if (!LayerLoadLog.ScreenDropped)
			LayerLoadLog.NoteScreenDropped();
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
		if (!StreamState.Bootstrapping)
			return StepPlay(world);

		int stepped = 0;
		int width = (int)world.chunkWidth;
		int height = (int)world.chunkHeight;
		int maxSteps = QueueAllotment(MaxStepsPerTickBootstrap);
		int objectBudget = QueueAllotment(StreamWindowPolicy.ObjectOpsPerFrame);
		int cullBudget = CullFrameBudget();

		for (int pass = 0; pass < 2; pass++)
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
					if (_heavySpent)
						goto afterUnloadPass;
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
				for (int x = 0; x < width && cullBudget > 0 && stepped < maxSteps; x++)
				for (int y = 0; y < height && cullBudget > 0 && stepped < maxSteps; y++)
				{
					long key = Key(x, y);
					if (DesiredScratch.Contains(key))
						continue;
					int phase = GetPhase(world, key, x, y);
					if (phase <= 0)
						continue;
					int before = phase;
					bool advanced = StepDown(world, key, x, y, phase, bootstrap: true, ref cullBudget, setupCost: 0);
					if (phase >= 3)
					{
						int after = GetPhase(world, key, x, y);
						if (advanced && before > 0 && after == 0)
							_bootstrapDone++;
						if (advanced)
							stepped++;
						// this chunk's objects are mid-destroy. resume it next frame
						// instead of opening another chunk on the leftover points.
						if (!advanced || after >= 3)
							goto afterUnloadPass;
						continue;
					}
					if (advanced)
					{
						if (before > 0 && GetPhase(world, key, x, y) == 0)
							_bootstrapDone++;
						stepped++;
					}
					if (cullBudget <= 0)
						goto afterUnloadPass;
				}
			}
		}

		afterUnloadPass:
		if (StreamState.Bootstrapping && _bootstrapTotal > 0)
			HideLoadScreen(world);

		StepTrace.NoteQueue(cullBudget, -1, stepped, _queuePanicOn);
		return stepped;
	}

	/// <summary>
	/// play: one shared point budget per rendered frame. Update and Late do not both spend it.
	/// create runs before teardown. an action runs only when the points left cover its weight.
	/// </summary>
	private static int StepPlay(WorldGeneration world)
	{
		if (_playActionFrame == Time.frameCount)
			return 0;
		_playActionFrame = Time.frameCount;

		int budget = PlayFrameBudget();
		int ops = 0;
		float now = Time.realtimeSinceStartup;

		while (budget >= WeightRenderer && StampWindowGround(world, ref ops, ref budget))
		{
		}

		foreach (long key in DesiredScratch)
		{
			if (budget <= 0)
				break;
			Decode(key, out int cx, out int cy);
			if (!StreamObjectJobs.DestroyStarted(cx, cy))
				continue;
			int before = budget;
			bool done = StreamObjectJobs.DestroyStep(world, cx, cy, writeback: true, ref budget, WeightWriteback, weighted: true);
			if (done)
				Phase[key] = 2;
			if (budget < before || done)
			{
				ops++;
				break;
			}
		}

		int guard = 0;
		while (budget > 0 && guard++ < 32)
		{
			bool progress = false;
			foreach (long key in DesiredScratch)
			{
				if (budget <= 0)
					break;
				Decode(key, out int cx, out int cy);
				if (GetPhase(world, key, cx, cy) != 2)
					continue;
				int before = budget;
				bool done = StreamObjectJobs.SpawnStep(world, cx, cy, ref budget, weighted: true);
				if (done)
					Phase[key] = 3;
				if (budget < before)
				{
					progress = true;
					ops++;
				}
			}
			if (!progress)
				break;
		}

		KeyScratch.Clear();
		foreach (var kv in PendingUnload)
		{
			if (kv.Value > now)
				continue;
			if (DesiredScratch.Contains(kv.Key))
				continue;
			KeyScratch.Add(kv.Key);
		}

		guard = 0;
		while (budget > 0 && guard++ < 32)
		{
			bool progress = false;
			for (int i = 0; i < KeyScratch.Count; i++)
			{
				if (budget <= 0)
					break;
				long key = KeyScratch[i];
				if (!PendingUnload.ContainsKey(key) || DesiredScratch.Contains(key))
					continue;
				Decode(key, out int cx, out int cy);
				int phase = GetPhase(world, key, cx, cy);
				if (phase >= 3)
					continue;
				if (phase <= 0)
				{
					PendingUnload.Remove(key);
					continue;
				}
				if (ElderPins.Contains(key))
					continue;
				if (phase == 2)
				{
					// still spawning. walking off a chunk that just entered must finish the
					// snapshot before the renderer hides, or the gen copy comes back.
					if (StreamObjectJobs.HasSpawn(cx, cy))
					{
						int before = budget;
						bool spawned = StreamObjectJobs.SpawnStep(world, cx, cy, ref budget, weighted: true);
						if (spawned)
							Phase[key] = 3;
						if (budget < before)
						{
							progress = true;
							ops++;
						}
						continue;
					}
					if (!LooseStored.Contains(key))
					{
						if (budget < WeightStoreLoose)
							continue;
						long tLoose = StepTrace.Stamp();
						ChunkObjects.StoreLooseItemsIfPresent(world, cx, cy);
						LooseStored.Add(key);
						budget -= WeightStoreLoose;
						StepTrace.Mark("tear", "store-loose", cx, cy, WeightStoreLoose, budget, null, tLoose, true);
						progress = true;
						ops++;
						continue;
					}
					if (!HideChunk(world, key, cx, cy, ref budget, ref ops))
						continue;
					progress = true;
					continue;
				}
				if (!HideChunk(world, key, cx, cy, ref budget, ref ops))
					continue;
				progress = true;
			}
			if (!progress)
				break;
		}

		for (int i = 0; i < KeyScratch.Count && budget > 0; i++)
		{
			long key = KeyScratch[i];
			if (!PendingUnload.ContainsKey(key) || DesiredScratch.Contains(key))
				continue;
			Decode(key, out int cx, out int cy);
			int phase = GetPhase(world, key, cx, cy);
			if (phase < 3)
			{
				if (phase <= 0)
					PendingUnload.Remove(key);
				continue;
			}

			int before = budget;
			bool done = StreamObjectJobs.DestroyStep(world, cx, cy, writeback: true, ref budget, WeightWriteback, weighted: true);
			if (done)
				Phase[key] = 2;
			if (GetPhase(world, key, cx, cy) == 0)
				PendingUnload.Remove(key);
			if (budget < before || done)
				ops++;
		}

		ChunkSeams.Refresh(world, ref budget, true);
		StepTrace.NoteQueue(budget, budget, ops, _queuePanicOn);
		return ops;
	}

	/// <summary>true when a terrain phase moved, or the object job finished this tick</summary>
	private static bool StepUp(WorldGeneration world, long key, int cx, int cy, int phase, ref int objectBudget)
	{
		// the toggle only chooses destroy versus hide. a missing tilemap still has to be built,
		// or the chunk is marked done and you walk into empty air.
		if (phase < 3 && !WorldArrays.IsLoaded(world, cx, cy))
		{
			if (BuildGaveUp.Contains(key))
			{
				Phase[key] = 3;
				return true;
			}
			int spent = objectBudget;
			if (!TrySpendHeavy(ref spent))
				return false;
			objectBudget = spent;
			NoteGenStep("placing", cx, cy, phase, "renderer-on");
			long tMake = StepTrace.Stamp();
			try
			{
				ChunkTerrain.CreateRenderer(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault("placing", cx, cy, phase, "renderer-on", "1", ex);
			}
			StepTrace.Mark("create", "renderer-on", cx, cy, 0, -1, "rebuild", tMake, true);
			if (!WorldArrays.IsLoaded(world, cx, cy))
			{
				BuildGaveUp.Add(key);
				Phase[key] = 3;
				return true;
			}
			// create leaves the collider off. phase 1 turns it on, toggle or not.
			Phase[key] = 1;
			return true;
		}

		if (phase <= 1)
		{
			if (phase < 1)
			{
				NoteGenStep("placing", cx, cy, phase, "renderer-on");
				long tOn = StepTrace.Stamp();
				try
				{
					ChunkTerrain.SetRendererEnabled(world, cx, cy, true);
				}
				catch (System.Exception ex)
				{
					LogGenFault("placing", cx, cy, phase, "renderer-on", "2", ex);
				}
				StepTrace.Mark("create", "renderer-on", cx, cy, 0, -1, "bootstrap", tOn, true);
				Phase[key] = ChunkTerrain.CollidersEnabled(world, cx, cy) ? 2 : 1;
				return true;
			}

			if (!ChunkTerrain.CollidersEnabled(world, cx, cy))
			{
				int spent = objectBudget;
				if (!TrySpendHeavy(ref spent))
					return false;
				objectBudget = spent;
				NoteGenStep("placing", cx, cy, phase, "colliders-on");
				long tCol = StepTrace.Stamp();
				try
				{
					ChunkTerrain.EnableColliders(world, cx, cy);
				}
				catch (System.Exception ex)
				{
					LogGenFault("placing", cx, cy, phase, "colliders-on", "2", ex);
				}
				StepTrace.Mark("create", "colliders-on", cx, cy, 0, -1, "bootstrap", tCol, true);
				Phase[key] = 2;
				return true;
			}

			Phase[key] = 2;
			return true;
		}
		if (phase == 2)
		{
			if (objectBudget <= 0)
				return false;
			NoteGenStep("placing", cx, cy, phase, "spawn");
			bool done;
			try
			{
				done = StreamObjectJobs.SpawnStep(world, cx, cy, ref objectBudget);
			}
			catch (System.Exception ex)
			{
				// phase would stay 2 and a throw that does not move the spawn index retries forever.
				// advance to phase 3 so bootstrap can finish. later objects in this chunk are skipped.
				LogGenFault("placing", cx, cy, phase, "spawn", "3", ex);
				Phase[key] = 3;
				return true;
			}
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

		long tPins = StepTrace.Stamp();
		ElderResident.CollectPins(world, ElderPinScratch);
		// dead players share the hold. collider stays until respawn, disconnect, or the body is gone
		PlayerCorpseGround.AppendPins(world, ElderPinScratch);
		StepTrace.Mark("book", "elder-collect", -1, -1, 0, -1, "n=" + ElderPinScratch.Count, tPins, false);
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
			{
				if (!wasLoaded)
				{
					if (!_heavySpent)
					{
						int slot = PlayFrameBudget();
						if (TrySpendHeavy(ref slot))
						{
							long tMake = StepTrace.Stamp();
							ChunkTerrain.CreateRenderer(world, cx, cy);
							StepTrace.Mark("create", "elder-sim", cx, cy, PlayFrameBudget(), 0, "renderer", tMake, true);
						}
					}
				}
			}

			// off-window elders keep the collider. the tilemap draw stays with the camera.
			bool draw = DesiredScratch.Contains(key);
			ChunkTerrain.SetRendererEnabled(world, cx, cy, draw);

			if (WantColliders && WorldArrays.IsLoaded(world, cx, cy) && !ChunkTerrain.CollidersEnabled(world, cx, cy) && !_heavySpent)
			{
				int slot = PlayFrameBudget();
				if (TrySpendHeavy(ref slot))
				{
					long tCol = StepTrace.Stamp();
					ChunkTerrain.EnableColliders(world, cx, cy);
					StepTrace.Mark("create", "elder-sim", cx, cy, PlayFrameBudget(), 0, "colliders", tCol, true);
				}
			}

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
		ref int objectBudget,
		int setupCost)
	{
		if (phase >= 3)
		{
			// objects keep the per-chunk op cap. the 3x pool is for collider and renderer teardowns.
			int destroyBudget = bootstrap
				? QueueAllotment(StreamWindowPolicy.ObjectOpsPerFrame)
				: objectBudget;
			if (destroyBudget <= 0)
				return false;
			// CollectDestroyTargets throws before WritebackDone, so the same chunk stays phase 3.
			NoteGenStep(bootstrap ? "culling" : "unload", cx, cy, phase, "destroy");
			bool done;
			try
			{
				done = StreamObjectJobs.DestroyStep(
					world, cx, cy, writeback: !bootstrap, ref destroyBudget, setupCost,
					singleStep: !bootstrap, weighted: false);
			}
			catch (System.Exception ex)
			{
				LogGenFault(bootstrap ? "culling" : "unload", cx, cy, phase, "destroy", "2", ex);
				Phase[key] = 2;
				return true;
			}
			if (done)
				Phase[key] = 2;
			return done;
		}
		if (phase == 2)
		{
			// chew + sim. collider was enabled on the way in. don't rebuild it while they stay
			if (ElderPins.Contains(key))
				return false;
			bool chargeLoose = bootstrap && !CullLoose.Contains(key);
			if (chargeLoose && !TrySpendPoints(ref objectBudget, WeightStoreLoose))
				return false;
			NoteGenStep(bootstrap ? "culling" : "unload", cx, cy, phase, "colliders-off");
			long tLoose = StepTrace.Stamp();
			try
			{
				if (!bootstrap || chargeLoose)
					ChunkObjects.StoreLooseItemsIfPresent(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault(bootstrap ? "culling" : "unload", cx, cy, phase, "colliders-off", "1", ex);
			}
			if (bootstrap)
				CullLoose.Add(key);
			StepTrace.Mark("tear", "store-loose", cx, cy, chargeLoose ? WeightStoreLoose : 0, objectBudget, bootstrap ? "bootstrap" : null, tLoose, true);
			if (bootstrap)
			{
				try
				{
					StreamObjectJobs.RollbackPartialSpawn(world, cx, cy);
				}
				catch (System.Exception ex)
				{
					LogGenFault("culling", cx, cy, phase, "store-loose", "1", ex);
				}
			}
			if (WantColliders && ChunkTerrain.CollidersEnabled(world, cx, cy))
			{
				// fixed cost, so the cull pool does not follow the gameplay budget
				if (bootstrap)
				{
					if (!TrySpendPoints(ref objectBudget, CullHeavyCost))
						return false;
				}
				else if (!TrySpendHeavy(ref objectBudget))
					return false;
				NoteGenStep(bootstrap ? "culling" : "unload", cx, cy, phase, "colliders-off");
				long tCol = StepTrace.Stamp();
				try
				{
					ChunkTerrain.DisableColliders(world, cx, cy);
				}
				catch (System.Exception ex)
				{
					LogGenFault(bootstrap ? "culling" : "unload", cx, cy, phase, "colliders-off", "1", ex);
				}
				StepTrace.Mark("tear", "colliders-off", cx, cy, 0, objectBudget, bootstrap ? "bootstrap" : "stepdown", tCol, true);
			}
			Phase[key] = 1;
			return true;
		}
		if (phase == 1)
		{
			if (WantRenderers && WorldArrays.IsLoaded(world, cx, cy))
			{
				if (bootstrap)
				{
					if (!TrySpendPoints(ref objectBudget, CullHeavyCost))
						return false;
				}
				else if (!TrySpendHeavy(ref objectBudget))
					return false;
				NoteGenStep(bootstrap ? "culling" : "unload", cx, cy, phase, "renderer-off");
				long tRend = StepTrace.Stamp();
				try
				{
					ChunkTerrain.DestroyRenderer(world, cx, cy);
				}
				catch (System.Exception ex)
				{
					LogGenFault(bootstrap ? "culling" : "unload", cx, cy, phase, "renderer-off", "0", ex);
				}
				StepTrace.Mark("tear", "renderer-off", cx, cy, 0, objectBudget, bootstrap ? "bootstrap" : "stepdown", tRend, true);
				Phase[key] = 0;
				return true;
			}

			if (bootstrap && !TrySpendPoints(ref objectBudget, WeightRenderer))
				return false;
			NoteGenStep(bootstrap ? "culling" : "unload", cx, cy, phase, "renderer-off");
			long tHide = StepTrace.Stamp();
			try
			{
				ChunkTerrain.SetRendererEnabled(world, cx, cy, false);
			}
			catch (System.Exception ex)
			{
				LogGenFault(bootstrap ? "culling" : "unload", cx, cy, phase, "renderer-off", "0", ex);
			}
			StepTrace.Mark("tear", "renderer-off", cx, cy, 0, objectBudget, bootstrap ? "bootstrap" : "stepdown", tHide, true);
			Phase[key] = 0;
			return true;
		}
		return false;
	}

	/// <summary>
	/// one line per distinct bootstrap step. same chunk and job index on the next frame is not logged again.
	/// play unloads do not log here. GenStepLogging defaults on.
	/// </summary>
	private static void NoteGenStep(string kind, int cx, int cy, int phase, string step)
	{
		_stepCx = cx;
		_stepCy = cy;
		_stepPhase = phase;
		_stepName = step;
		if (StreamState.Bootstrapping)
			LayerLoadLog.Step(kind + " " + step);
		if (!StreamState.Bootstrapping)
			return;
		if (Plugin.GenStepLogging != null && !Plugin.GenStepLogging.Value)
			return;
		string progress = StreamObjectJobs.ProgressText(cx, cy);
		string line = "CPUOpt2 gen " + kind
			+ " chunk=" + cx + "," + cy
			+ " phase=" + phase
			+ " step=" + step
			+ " " + progress
			+ " cull=" + _bootstrapDone + "/" + _bootstrapTotal;
		if (line == _genStepLast)
			return;
		_genStepLast = line;
		Plugin.Log.LogInfo(line);
	}

	private static void LogGenFault(string kind, int cx, int cy, int phase, string step, string advanced, System.Exception ex)
	{
		Plugin.Log.LogError(
			"CPUOpt2 gen fault " + kind
			+ " chunk=" + cx + "," + cy
			+ " phase=" + phase
			+ " step=" + step
			+ " advanced=" + advanced
			+ " " + StreamObjectJobs.ProgressText(cx, cy)
			+ ": " + ex);
	}

	/// <summary>
	/// one line every 2s while the overlay is up. stalled climbs when the same chunk and job sit still.
	/// grep CPUOpt2 bootstrap beat.
	/// </summary>
	private static void BeatBootstrap(bool placing, int cx, int cy, int phase, int placeLeft, int cullLeft)
	{
		float now = Time.realtimeSinceStartup;
		string token = (placing ? "placing" : "culling")
			+ " " + cx + "," + cy
			+ " p" + phase
			+ " " + _stepName
			+ " " + StreamObjectJobs.ProgressText(cx, cy);
		bool same = token == _bootstrapBeatToken;
		if (!same)
		{
			_bootstrapBeatToken = token;
			_bootstrapBeatStall = 0;
		}

		if (_bootstrapBeatAt > 0f && now - _bootstrapBeatAt < BootstrapBeatSeconds)
			return;

		if (same)
			_bootstrapBeatStall++;
		_bootstrapBeatAt = now;
		Plugin.Log.LogInfo(
			"CPUOpt2 bootstrap beat " + token
			+ " t=" + (now - _bootstrapStartedAt).ToString("0.0")
			+ "s stalled=" + _bootstrapBeatStall
			+ " placeLeft=" + placeLeft
			+ " cullLeft=" + cullLeft
			+ " culled=" + _bootstrapDone + "/" + _bootstrapTotal);
	}

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "SetTimeScale"),
			prefix: new HarmonyMethod(typeof(StreamController), nameof(BlockTimeScaleWhileBootstrapping)));
	}

	internal static void HoldBootstrapTime()
	{
	}

	private static bool BlockTimeScaleWhileBootstrapping()
	{
		return true;
	}

	private static void FreezeBootstrapWindow(int x0, int y0, int x1, int y1)
	{
		FrozenDesired.Clear();
		foreach (long key in DesiredScratch)
			FrozenDesired.Add(key);
		_freezeX0 = x0;
		_freezeY0 = y0;
		_freezeX1 = x1;
		_freezeY1 = y1;
		_windowFrozen = FrozenDesired.Count > 0;
	}

	private static int GetPhase(WorldGeneration world, long key, int cx, int cy)
	{
		// bootstrap cull leaves the key in BootstrapRemoved forever.
		// that used to outrank the rebuild, so every later frame looked like
		// phase 0, burned the create budget, and never spawned interiors.
		if (Phase.TryGetValue(key, out int p))
			return p;
		if (BootstrapRemoved.Contains(key))
			return 0;
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
	/// sp and client: one window around the local body. a dead body adds the camera
	/// once the view has left the corpse chunks.
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
		_spanValid = false;
		if (StreamState.Bootstrapping && _windowFrozen && FrozenDesired.Count > 0)
		{
			DesiredScratch.Clear();
			foreach (long key in FrozenDesired)
				DesiredScratch.Add(key);
			x0 = _freezeX0;
			y0 = _freezeY0;
			x1 = _freezeX1;
			y1 = _freezeY1;
			anchors = 1;
			return;
		}

		DesiredScratch.Clear();
		anchors = 0;
		x0 = y0 = int.MaxValue;
		x1 = y1 = int.MinValue;

		if (MpSession.Running && MpSession.IsServer)
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
			// one window on the body. a living camera lags behind and unioning
			// it flickered the chunk ahead. a corpse stays put, so once the
			// camera leaves those chunks (spectate, freecam) that view is a
			// second window. the gap between them stays unloaded.
			MeasureClientSpan(world, out int sx0, out int sy0, out int sx1, out int sy1, out bool view, out int vx0, out int vy0, out int vx1, out int vy1);
			x0 = y0 = int.MaxValue;
			x1 = y1 = int.MinValue;
			AddWindow(sx0, sy0, sx1, sy1, ref x0, ref y0, ref x1, ref y1);
			if (view)
				AddWindow(vx0, vy0, vx1, vy1, ref x0, ref y0, ref x1, ref y1);
			_spanValid = true;
			_spanBx0 = sx0;
			_spanBy0 = sy0;
			_spanBx1 = sx1;
			_spanBy1 = sy1;
			_spanView = view;
			_spanVx0 = vx0;
			_spanVy0 = vy0;
			_spanVx1 = vx1;
			_spanVy1 = vy1;
			if (anchors == 0)
				anchors = 1;
			if (view)
				anchors++;
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
		if (_spanValid)
		{
			_spanHeld = true;
			_holdBx0 = _spanBx0;
			_holdBy0 = _spanBy0;
			_holdBx1 = _spanBx1;
			_holdBy1 = _spanBy1;
			_holdView = _spanView;
			_holdVx0 = _spanVx0;
			_holdVy0 = _spanVy0;
			_holdVx1 = _spanVx1;
			_holdVy1 = _spanVy1;
		}
	}

	internal static int LiveDesiredHash()
	{
		int hash = DesiredScratch.Count;
		foreach (long key in DesiredScratch)
			hash = unchecked(hash * 31 + (int)key ^ (int)(key >> 32));
		return hash;
	}

	// body window, plus the camera window when a dead player is looking elsewhere.
	private static void MeasureClientSpan(
		WorldGeneration world,
		out int bx0,
		out int by0,
		out int bx1,
		out int by1,
		out bool view,
		out int vx0,
		out int vy0,
		out int vx1,
		out int vy1)
	{
		Vector3 anchor = TryBodyPosition(out Vector3 bodyPos) ? bodyPos : ResolveCamera(world);
		GetWindowAround(world, anchor, out bx0, out by0, out bx1, out by1);
		view = false;
		vx0 = vy0 = vx1 = vy1 = 0;
		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || cam.body == null || cam.body.alive)
			return;
		Vector3 viewPos = ResolveCamera(world);
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(viewPos));
		if (chunk.x >= bx0 && chunk.x <= bx1 && chunk.y >= by0 && chunk.y <= by1)
			return;
		GetWindowAround(world, viewPos, out vx0, out vy0, out vx1, out vy1);
		view = true;
	}

	private static bool ClientSpanStable(WorldGeneration world)
	{
		if (!_spanHeld)
			return false;
		MeasureClientSpan(world, out int bx0, out int by0, out int bx1, out int by1, out bool view, out int vx0, out int vy0, out int vx1, out int vy1);
		if (bx0 != _holdBx0 || by0 != _holdBy0 || bx1 != _holdBx1 || by1 != _holdBy1 || view != _holdView)
			return false;
		if (!view)
			return true;
		return vx0 == _holdVx0 && vy0 == _holdVy0 && vx1 == _holdVx1 && vy1 == _holdVy1;
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
		if (TryBodyPosition(out Vector3 bodyPos))
			return bodyPos;
		return Vector3.zero;
	}

	private static bool TryBodyPosition(out Vector3 pos)
	{
		PlayerCamera cam = PlayerCamera.main;
		if (cam != null && cam.body != null)
		{
			pos = cam.body.transform.position;
			return true;
		}
		pos = default;
		return false;
	}

	private static bool TryBodyChunk(WorldGeneration world, out int cx, out int cy)
	{
		cx = cy = 0;
		if (world == null || !TryBodyPosition(out Vector3 pos))
			return false;
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		cx = chunk.x;
		cy = chunk.y;
		if (cx < 0 || cy < 0 || cx >= (int)world.chunkWidth || cy >= (int)world.chunkHeight)
			return false;
		return true;
	}

	private static string BodyNote(WorldGeneration world)
	{
		if (!TryBodyChunk(world, out int bx, out int by))
			return "";
		long key = Key(bx, by);
		int phase = Phase.TryGetValue(key, out int p) ? p : 0;
		int inside = DesiredScratch.Contains(key) ? 1 : 0;
		return " bodyChunk=" + bx + "," + by + " bodyIn=" + inside + " bodyPhase=" + phase;
	}

	private static void NoteBodyOutside(WorldGeneration world, int x0, int y0, int x1, int y1)
	{
		if (StreamState.Bootstrapping || !TryBodyChunk(world, out int bx, out int by))
			return;
		if (DesiredScratch.Contains(Key(bx, by)))
			return;
		float now = Time.realtimeSinceStartup;
		if (now < _bodyOutLog)
			return;
		_bodyOutLog = now + 1f;
		Plugin.Log.LogWarning(
			"stream body outside " + x0 + "," + y0 + ".." + x1 + "," + y1
			+ " bodyChunk=" + bx + "," + by);
	}

	/// <summary>
	/// collider teardown, then renderer teardown. a heavy op spends the whole budget and leaves the chunk for the next frame.
	/// toggles off keeps the cheap renderer hide.
	/// </summary>
	private static bool HideChunk(WorldGeneration world, long key, int cx, int cy, ref int budget, ref int ops)
	{
		if (WantColliders && ChunkTerrain.CollidersEnabled(world, cx, cy))
		{
			if (!TrySpendHeavy(ref budget))
				return false;
			long tCol = StepTrace.Stamp();
			ChunkTerrain.DisableColliders(world, cx, cy);
			ops++;
			StepTrace.Mark("tear", "colliders-off", cx, cy, PlayFrameBudget(), budget, null, tCol, true);
			return true;
		}

		if (WantRenderers && WorldArrays.IsLoaded(world, cx, cy))
		{
			if (!TrySpendHeavy(ref budget))
				return false;
			long tRend = StepTrace.Stamp();
			try
			{
				ChunkTerrain.DestroyRenderer(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault("unload", cx, cy, GetPhase(world, key, cx, cy), "renderer-off", "0", ex);
			}
			Phase[key] = 0;
			PendingUnload.Remove(key);
			LooseStored.Remove(key);
			ops++;
			StepTrace.Mark("tear", "renderer-off", cx, cy, PlayFrameBudget(), budget, null, tRend, true);
			return true;
		}

		if (budget < WeightRenderer)
			return false;
		long tHide = StepTrace.Stamp();
		ChunkTerrain.SetRendererEnabled(world, cx, cy, false);
		Phase[key] = 0;
		PendingUnload.Remove(key);
		LooseStored.Remove(key);
		budget -= WeightRenderer;
		ops++;
		StepTrace.Mark("tear", "renderer-off", cx, cy, WeightRenderer, budget, null, tHide, true);
		return true;
	}

	/// <summary>
	/// one ground step, body cell first. true when a renderer was enabled.
	/// </summary>
	private static bool StampWindowGround(WorldGeneration world, ref int ops, ref int createLeft)
	{
		if (TryBodyChunk(world, out int bx, out int by) && StampChunkGround(world, bx, by, ref ops, ref createLeft))
			return true;

		foreach (long key in DesiredScratch)
		{
			Decode(key, out int cx, out int cy);
			if (StampChunkGround(world, cx, cy, ref ops, ref createLeft))
				return true;
		}
		return false;
	}

	private static bool StampChunkGround(WorldGeneration world, int cx, int cy, ref int ops, ref int createLeft)
	{
		long key = Key(cx, cy);
		if (!DesiredScratch.Contains(key))
			return false;

		// destroyed while renderer teardown was on. rebuild even if that toggle is now off,
		// and even if a previous step already marked the chunk finished.
		if (!WorldArrays.IsLoaded(world, cx, cy))
		{
			if (BuildGaveUp.Contains(key))
				return false;
			if (!TrySpendHeavy(ref createLeft))
				return false;
			long tMake = StepTrace.Stamp();
			try
			{
				ChunkTerrain.CreateRenderer(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault("placing", cx, cy, 0, "renderer-on", "1", ex);
			}
			ops++;
			StepTrace.Mark("create", "renderer-on", cx, cy, PlayFrameBudget(), createLeft, "rebuild", tMake, true);
			if (!WorldArrays.IsLoaded(world, cx, cy))
			{
				BuildGaveUp.Add(key);
				Phase[key] = 3;
				return true;
			}
			Phase[key] = 1;
			return true;
		}

		int phase = GetPhase(world, key, cx, cy);
		if (RepairColliders.Contains(key) && !ChunkTerrain.CollidersEnabled(world, cx, cy))
		{
			if (!TrySpendHeavy(ref createLeft))
				return false;
			long tFix = StepTrace.Stamp();
			try
			{
				ChunkTerrain.EnableColliders(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault("placing", cx, cy, phase, "colliders-on", "repair", ex);
			}
			RepairColliders.Remove(key);
			if (phase < 2)
				Phase[key] = 2;
			ops++;
			StepTrace.Mark("create", "colliders-on", cx, cy, PlayFrameBudget(), createLeft, "repair", tFix, true);
			return true;
		}

		if (phase >= 2)
			return false;

		if (phase < 1)
		{
			if (createLeft < WeightRenderer)
				return false;
			long tOn = StepTrace.Stamp();
			try
			{
				ChunkTerrain.SetRendererEnabled(world, cx, cy, true);
			}
			catch (System.Exception ex)
			{
				LogGenFault("placing", cx, cy, phase, "renderer-on", "2", ex);
			}
			createLeft -= WeightRenderer;
			Phase[key] = ChunkTerrain.CollidersEnabled(world, cx, cy) ? 2 : 1;
			ops++;
			StepTrace.Mark("create", "renderer-on", cx, cy, WeightRenderer, createLeft, "ground", tOn, true);
			return true;
		}

		if (!ChunkTerrain.CollidersEnabled(world, cx, cy))
		{
			if (!TrySpendHeavy(ref createLeft))
				return false;
			long tCol = StepTrace.Stamp();
			try
			{
				ChunkTerrain.EnableColliders(world, cx, cy);
			}
			catch (System.Exception ex)
			{
				LogGenFault("placing", cx, cy, phase, "colliders-on", "2", ex);
			}
			Phase[key] = 2;
			ops++;
			StepTrace.Mark("create", "colliders-on", cx, cy, PlayFrameBudget(), createLeft, "ground", tCol, true);
			return true;
		}

		Phase[key] = 2;
		return true;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

	private static void Decode(long key, out int x, out int y)
	{
		x = (int)(key >> 32);
		y = (int)(uint)key;
	}
}
