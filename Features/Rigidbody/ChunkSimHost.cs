using System.Collections.Generic;
using CPUOptimization.Features.Lights;
using CPUOptimization.Features.Mp;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization.Features.ChunkSim;

internal sealed class ChunkSimHost : MonoBehaviour
{
	private struct ColliderCellDiff
	{
		internal int X;
		internal int Y;
		internal bool Enabled;
	}

	private static readonly List<Vector3> PlayerPositions = new List<Vector3>(8);

	private ChunkSimWindow _lastWindow;
	private ChunkSimUnionWindow _lastUnion;
	private readonly List<ColliderCellDiff> _colliderQueue = new List<ColliderCellDiff>(64);
	private readonly HashSet<long> _appliedColliderChunks = new HashSet<long>();
	private static readonly Collider2D[] _physicsOverlapBuffer = new Collider2D[32];
	private bool _wasGenerating = true;
	private bool _armedLogged;
	private bool _registryBootstrapped;
	private float _telemetryAge;
	private bool _mpModeLogged;
	private bool _rebaselined;


	private void Update()
	{
		if (Plugin.ChunkSimEnabled == null || !Plugin.ChunkSimEnabled.Value)
			return;

		WorldGeneration world = WorldGeneration.world;
		if (!world)
			return;

		KrokMpOptional.Resolve();

		if (MpChunkSimContext.PollSessionTransition(out bool mpStarted, out bool mpEnded))
		{
			_lastWindow = null;
			_lastUnion = null;
			ClearColliderSpreadState();
			ChunkSimBodySync.RestoreAllOptDisabled();
			ChunkSimState.Clear();
			BuildingSimRegistry.Clear();
			SoundCannonSimRegistry.Clear();
			ChunkSimTrackTable.Clear();
			ElderRegistry.Clear();
			BodyPinCache.Clear();
			_registryBootstrapped = false;
			_mpModeLogged = false;
			_rebaselined = false;

			if (mpStarted)
			{
				CpuLog.Info("[CPUOpt] chunk-sim MP union mode started");
				KrokMpPerfBootstrap.RequestRetry("mp session started");
			}
			if (mpEnded)
				CpuLog.Info("[CPUOpt] chunk-sim SP camera mode resumed");
		}

		bool generating = world.generatingWorld;
		if (_wasGenerating && !generating && world.worldExists)
		{
			_lastWindow = null;
			_lastUnion = null;
			_mpModeLogged = false;
			ClearColliderSpreadState();
			ChunkSimBodySync.RestoreAllOptDisabled();
			ChunkSimState.Clear();
			BuildingSimRegistry.Clear();
			SoundCannonSimRegistry.Clear();
			ChunkSimTrackTable.Clear();
			ElderRegistry.Clear();
			BodyPinCache.Clear();
			_rebaselined = false;
			RebootstrapBuildingRegistry();
			RescanForElders();
			RebaselineAllChunksOnce(world);
			CpuLog.Info("[CPUOpt] chunk-sim waiting for camera/world (world gen finished)");
		}
		_wasGenerating = generating;
		if (generating || !world.worldExists)
			return;

		EnsureBuildingRegistryBootstrapped();

		if (MpChunkSimContext.UseClientLocalSimOnly && !_mpModeLogged)
		{
			_mpModeLogged = true;
			CpuLog.Info("[CPUOpt] chunk-sim MP client-local presentation mode active");
		}

		bool windowChanged;
		if (MpChunkSimContext.UseClientLocalSimOnly)
			windowChanged = UpdateClientLocalWindow(world);
		else if (MpChunkSimContext.IsMpChunkSimActive)
			windowChanged = UpdateMpUnion(world);
		else
			windowChanged = UpdateSpWindow(world);

		if (windowChanged)
			CPUOptimization.Features.Mp.Patches.SpiderTrackerPerfPatch.SyncEnabled();

		if (!_armedLogged && ChunkSimState.IsActive)
		{
			_armedLogged = true;
			CpuLog.Info(
				$"[CPUOpt] chunk-sim armed mode={ChunkSimState.FormatSimMode()} " +
				$"colliders={(Plugin.ChunkSimColliders?.Value == true ? 1 : 0)} " +
				$"freezeCrates={(Plugin.ChunkSimFreezeCrates?.Value == true ? 1 : 0)} " +
				$"grid={world.chunkWidth}x{world.chunkHeight} krokmp={(KrokMpOptional.IsPresent ? 1 : 0)}");
			if (KrokMpOptional.IsPresent)
				KrokMpPerfBootstrap.RequestRetry("chunk-sim armed");
		}

		float telemetryEvery = Plugin.ChunkSimTelemetrySeconds?.Value ?? 10f;
		if (telemetryEvery > 0f)
		{
			_telemetryAge += Time.unscaledDeltaTime;
			if (_telemetryAge >= telemetryEvery)
			{
				_telemetryAge = 0f;
				LogTelemetry(steadyWindow: !windowChanged);
			}
		}

		ProcessColliderQueue(world);
		ProcessBodySpread();
		ChunkSimBodySync.ProcessDyingBuildings();
	}

	private bool UpdateClientLocalWindow(WorldGeneration world)
	{
		if (!TryGetCameraWorld(out Vector3 camWorld))
			return false;

		if (!ChunkSimWindow.TryCreate(world, camWorld, out ChunkSimWindow window))
			return false;

		ChunkSimState.SetClientLocalWindow(window);

		bool windowChanged = _lastWindow == null || !_lastWindow.MatchesAnchor(window);
		if (windowChanged)
		{
			ApplyColliderWindow(world, _lastWindow, window);
			ChunkSimBodySync.SyncAll();
			LogWindowChange(world, camWorld, window);
			LightCullHost.RequestImmediateCull();
			_lastWindow = window;
			_lastUnion = null;
		}

		return windowChanged;
	}

	private bool UpdateMpUnion(WorldGeneration world)
	{
		MpChunkSimContext.CollectPlayerPositions(PlayerPositions);
		if (PlayerPositions.Count == 0)
			return false;

		var union = new ChunkSimUnionWindow();
		if (!union.TryBuild(world, PlayerPositions))
			return false;

		ChunkSimState.SetUnionWindow(union);

		if (!_mpModeLogged)
		{
			_mpModeLogged = true;
			CpuLog.Info(
				$"[CPUOpt] chunk-sim MP union active players={union.PlayerCount} " +
				$"chunks={union.ChunkCount}/{world.chunkWidth * world.chunkHeight}");
		}

		bool unionChanged = _lastUnion == null || !_lastUnion.Matches(union);
		if (unionChanged)
		{
			EnqueueUnionColliderDiff(world, union);
			LogUnionChange(world, union, _colliderQueue.Count,
				ChunkSimBodySpread.BuildingsRemaining + ChunkSimBodySpread.ItemsRemaining);
			LightCullHost.RequestImmediateCull();
			_lastUnion = union;
			_lastWindow = null;
		}

		return unionChanged;
	}

	private bool UpdateSpWindow(WorldGeneration world)
	{
		if (!TryGetCameraWorld(out Vector3 camWorld))
			return false;

		if (!ChunkSimWindow.TryCreate(world, camWorld, out ChunkSimWindow window))
			return false;

		ChunkSimState.SetWindow(window);

		bool windowChanged = _lastWindow == null || !_lastWindow.MatchesAnchor(window);
		if (windowChanged)
		{
			ApplyColliderWindow(world, _lastWindow, window);
			ChunkSimBodySync.SyncAll();
			LogWindowChange(world, camWorld, window);
			LightCullHost.RequestImmediateCull();
			_lastWindow = window;
			_lastUnion = null;
		}

		return windowChanged;
	}

	private void EnsureBuildingRegistryBootstrapped()
	{
		if (_registryBootstrapped)
			return;

		RebootstrapBuildingRegistry();
	}

	private void RebootstrapBuildingRegistry()
	{
		BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>();
		for (int i = 0; i < buildings.Length; i++)
		{
			BuildingEntity building = buildings[i];
			if (!building)
				continue;

			BuildingSimRegistry.Register(building);
			if (ChunkSimState.IsActive)
				ChunkSimBodySync.ApplyBuilding(building);
		}

		SoundCannon[] cannons = Object.FindObjectsOfType<SoundCannon>();
		for (int i = 0; i < cannons.Length; i++)
		{
			SoundCannon cannon = cannons[i];
			if (!cannon)
				continue;

			SoundCannonSimRegistry.Register(cannon);
			if (ChunkSimState.IsActive)
				ChunkSimSoundCannonSync.Apply(cannon);
		}

		_registryBootstrapped = true;
		if (buildings.Length > 0 || cannons.Length > 0)
		{
			CpuLog.Info(
				$"[CPUOpt] chunk-sim building registry bootstrap count={BuildingSimRegistry.Count} " +
				$"found={buildings.Length} cannons={SoundCannonSimRegistry.Count}");
		}

		if (KrokMpPerfBootstrap.IsArmed)
			ForceForMPScheduler.BootstrapSeed(allowRepeat: true);
	}

	private static bool TryGetCameraWorld(out Vector3 camWorld)
	{
		if (PlayerCamera.main)
		{
			camWorld = PlayerCamera.main.transform.position;
			return true;
		}
		if (Camera.main)
		{
			camWorld = Camera.main.transform.position;
			return true;
		}
		camWorld = Vector3.zero;
		return false;
	}

	private void ClearColliderSpreadState()
	{
		_colliderQueue.Clear();
		_appliedColliderChunks.Clear();
		ChunkSimBodySpread.Clear();
	}

	private void EnqueueUnionColliderDiff(WorldGeneration world, ChunkSimUnionWindow targetUnion)
	{
		ChunkSimBodySpread.Schedule();

		if (Plugin.ChunkSimColliders == null || !Plugin.ChunkSimColliders.Value)
			return;

		_colliderQueue.Clear();

		if (_lastUnion != null)
		{
			foreach (Vector2Int chunk in _lastUnion.EnumerateChunks())
			{
				if (targetUnion.ContainsChunk(chunk.x, chunk.y))
					continue;
				if (!_appliedColliderChunks.Contains(PackChunk(chunk.x, chunk.y)))
					continue;
				_colliderQueue.Add(new ColliderCellDiff { X = chunk.x, Y = chunk.y, Enabled = false });
			}

			foreach (Vector2Int chunk in targetUnion.EnumerateChunks())
			{
				if (_lastUnion.ContainsChunk(chunk.x, chunk.y))
					continue;
				_colliderQueue.Add(new ColliderCellDiff { X = chunk.x, Y = chunk.y, Enabled = true });
			}
		}
		else
		{
			foreach (Vector2Int chunk in targetUnion.EnumerateChunks())
			{
				if (_appliedColliderChunks.Contains(PackChunk(chunk.x, chunk.y)))
					continue;
				_colliderQueue.Add(new ColliderCellDiff { X = chunk.x, Y = chunk.y, Enabled = true });
			}

			foreach (long key in _appliedColliderChunks)
			{
				int cx = (int)(key >> 32);
				int cy = (int)(key & 0xFFFFFFFF);
				if (targetUnion.ContainsChunk(cx, cy))
					continue;
				_colliderQueue.Add(new ColliderCellDiff { X = cx, Y = cy, Enabled = false });
			}
		}

	}

	private void ProcessColliderQueue(WorldGeneration world)
	{
		if (_colliderQueue.Count == 0)
			return;

		if (Plugin.ChunkSimColliders == null || !Plugin.ChunkSimColliders.Value)
		{
			_colliderQueue.Clear();
			return;
		}

		int batch = Mathf.Max(1, Plugin.ChunkUnionApplyBatchPerFrame?.Value ?? 2);
		int applied = 0;
		while (_colliderQueue.Count > 0 && applied < batch)
		{
			ColliderCellDiff cell = _colliderQueue[0];
			_colliderQueue.RemoveAt(0);
			ApplyChunkColliderState(world, cell.X, cell.Y, cell.Enabled);
			applied++;
		}

	}

	private void ProcessBodySpread()
	{
		if (!ChunkSimBodySpread.IsActive)
			return;

		int batch = Mathf.Max(1, Plugin.ChunkUnionBodySyncBatchPerFrame?.Value ?? 128);
		ChunkSimBodySpread.ProcessBatch(batch);
	}

	private void ApplyChunkColliderState(WorldGeneration world, int cx, int cy, bool enabled)
	{
		SetChunkColliders(world, cx, cy, enabled);
		long key = PackChunk(cx, cy);
		if (enabled)
			_appliedColliderChunks.Add(key);
		else
			_appliedColliderChunks.Remove(key);
	}

	private static long PackChunk(int cx, int cy) => ((long)cx << 32) | (uint)cy;

	private void ApplyColliderWindow(WorldGeneration world, ChunkSimWindow oldWindow, ChunkSimWindow newWindow)
	{
		if (Plugin.ChunkSimColliders == null || !Plugin.ChunkSimColliders.Value)
			return;

		if (oldWindow == null)
		{
			for (int x = 0; x < world.chunkWidth; x++)
			{
				for (int y = 0; y < world.chunkHeight; y++)
					SetChunkColliders(world, x, y, newWindow.ContainsChunk(x, y));
			}
			return;
		}

		for (int x = 0; x < world.chunkWidth; x++)
		{
			for (int y = 0; y < world.chunkHeight; y++)
			{
				bool wasOn = oldWindow.ContainsChunk(x, y);
				bool nowOn = newWindow.ContainsChunk(x, y);
				if (wasOn != nowOn)
					SetChunkColliders(world, x, y, nowOn);
			}
		}
	}

	private static void SetChunkColliders(WorldGeneration world, int cx, int cy, bool enabled)
	{
		var renderer = world.renderChunks[cx, cy];
		if (!renderer)
			return;
		var go = renderer.gameObject;
		var composite = go.GetComponent<CompositeCollider2D>();
		if (composite)
			composite.enabled = enabled;
		var tile = go.GetComponent<TilemapCollider2D>();
		if (tile)
			tile.enabled = enabled;

		// freeze/unfreeze dynamic objects when their terrain support collider toggles
		// (DamagingCrates only if ChunkSimFreezeCrates; Items/minibarrels always)
		// prevents falling through when colliders disabled
		if (enabled)
			UnfreezeChunkSupportObjects(world, cx, cy);
		else
			FreezeChunkSupportObjects(world, cx, cy);
	}

	private static void FreezeChunkSupportObjects(WorldGeneration world, int cx, int cy)
	{
		if (world == null || !world.worldExists)
			return;

		float cs = WorldGeneration.CHUNKSIZE;
		float hw = world.chunkWidth * 0.5f;
		float hh = world.chunkHeight * 0.5f;
		float cxWorld = (cx - hw + 0.5f) * cs;
		float cyWorld = (cy - hh + 0.5f) * cs;

		int count = Physics2D.OverlapBoxNonAlloc(
			new Vector2(cxWorld, cyWorld),
			new Vector2(cs, cs),
			0f,
			_physicsOverlapBuffer);

		for (int i = 0; i < count && i < _physicsOverlapBuffer.Length; i++)
		{
			Collider2D col = _physicsOverlapBuffer[i];
			if (col == null)
				continue;

			if (Plugin.ChunkSimFreezeCrates == null || Plugin.ChunkSimFreezeCrates.Value)
			{
				DamagingCrate crate = col.GetComponent<DamagingCrate>();
				if (crate != null)
				{
					Rigidbody2D rb = crate.GetComponent<Rigidbody2D>();
					if (rb != null)
					{
						rb.isKinematic = true;
						rb.velocity = Vector2.zero;
					}
				}
			}

			// also freeze spawned items and minibarrels when their terrain support disappears
			Item item = col.GetComponent<Item>();
			if (item != null && !item.transform.parent)
			{
				ChunkSimBodySync.ApplyItemSleep(item);
				if (item.rb)
				{
					item.rb.velocity = Vector2.zero;
				}
			}
		}
	}

	private static void UnfreezeChunkSupportObjects(WorldGeneration world, int cx, int cy)
	{
		if (world == null || !world.worldExists)
			return;

		float cs = WorldGeneration.CHUNKSIZE;
		float hw = world.chunkWidth * 0.5f;
		float hh = world.chunkHeight * 0.5f;
		float cxWorld = (cx - hw + 0.5f) * cs;
		float cyWorld = (cy - hh + 0.5f) * cs;

		int count = Physics2D.OverlapBoxNonAlloc(
			new Vector2(cxWorld, cyWorld),
			new Vector2(cs, cs),
			0f,
			_physicsOverlapBuffer);

		for (int i = 0; i < count && i < _physicsOverlapBuffer.Length; i++)
		{
			Collider2D col = _physicsOverlapBuffer[i];
			if (col == null)
				continue;

			if (Plugin.ChunkSimFreezeCrates == null || Plugin.ChunkSimFreezeCrates.Value)
			{
				DamagingCrate crate = col.GetComponent<DamagingCrate>();
				if (crate != null)
				{
					Rigidbody2D rb = crate.GetComponent<Rigidbody2D>();
					if (rb != null)
					{
						bool should = !ChunkSimState.IsActive || ChunkSimState.ShouldSimulateWorldPos(crate.transform.position);
						rb.isKinematic = !should;
						rb.simulated = should;
					}
				}
			}

			// unfreeze items/minibarrels when terrain support returns
			Item item = col.GetComponent<Item>();
			if (item != null && !item.transform.parent)
			{
				bool should = !ChunkSimState.IsActive || ChunkSimState.ShouldSimulateWorldPos(item.transform.position);
				if (should)
					ChunkSimBodySync.ApplyItemWake(item);
				else
					ChunkSimBodySync.ApplyItemSleep(item);
			}
		}
	}

	// one-time walk over every chunk after terrain/world load to rebaseline crate/item positions
	// ensures they settle on terrain before any later sleep decisions or first player visit
	// addresses: fall on spawn, snap only when walking near spawn point
	private void RebaselineAllChunksOnce(WorldGeneration world)
	{
		if (_rebaselined)
			return;
		if (world == null || !world.worldExists)
			return;

		for (int x = 0; x < world.chunkWidth; x++)
		{
			for (int y = 0; y < world.chunkHeight; y++)
			{
				BaselineChunkSupportObjects(world, x, y);
			}
		}
		_rebaselined = true;
		CpuLog.Info("[CPUOpt] chunk-sim one-time rebaseline walk complete (all chunks)");
	}

	private static void BaselineChunkSupportObjects(WorldGeneration world, int cx, int cy)
	{
		if (world == null || !world.worldExists)
			return;

		float cs = WorldGeneration.CHUNKSIZE;
		float hw = world.chunkWidth * 0.5f;
		float hh = world.chunkHeight * 0.5f;
		float cxWorld = (cx - hw + 0.5f) * cs;
		float cyWorld = (cy - hh + 0.5f) * cs;

		int count = Physics2D.OverlapBoxNonAlloc(
			new Vector2(cxWorld, cyWorld),
			new Vector2(cs, cs),
			0f,
			_physicsOverlapBuffer);

		for (int i = 0; i < count && i < _physicsOverlapBuffer.Length; i++)
		{
			Collider2D col = _physicsOverlapBuffer[i];
			if (col == null)
				continue;

			DamagingCrate crate = col.GetComponent<DamagingCrate>();
			if (crate != null)
			{
				Rigidbody2D rb = crate.GetComponent<Rigidbody2D>();
				if (rb != null)
				{
					rb.isKinematic = false;
					rb.simulated = true;
					// leave velocity as-is so any natural settle can occur; zero only if prior momentum bad
				}
			}

			Item item = col.GetComponent<Item>();
			if (item != null && !item.transform.parent)
			{
				if (Plugin.ChunkSimItemsEnabled == null || Plugin.ChunkSimItemsEnabled.Value)
					ChunkSimBodySync.ForceBaselineItem(item);
			}
		}
	}

	private static void LogUnionChange(
		WorldGeneration world,
		ChunkSimUnionWindow union,
		int colliderQueue,
		int bodySpreadRemaining)
	{
		if (Plugin.ChunkSimLogOnChange != null && !Plugin.ChunkSimLogOnChange.Value)
			return;

		CountBodies(out int itemsSim, out int itemsSleep, out int bldDyn, out int bldStatic, out int elder);
		int total = (int)(world.chunkWidth * world.chunkHeight);
		CpuLog.Info(
			$"[CPUOpt] chunk-sim union changed players={union.PlayerCount} " +
			$"chunks={union.FormatChunks()} collidersOn={union.ChunkCount}/{total} " +
			$"colliderQueue={colliderQueue} bodySpreadRemaining={bodySpreadRemaining} " +
			$"items sim={itemsSim} sleep={itemsSleep} buildings dyn={bldDyn} static={bldStatic} elder={elder}");
	}

	private static void LogWindowChange(WorldGeneration world, Vector3 camWorld, ChunkSimWindow window)
	{
		if (Plugin.ChunkSimLogOnChange != null && !Plugin.ChunkSimLogOnChange.Value)
			return;

		CountBodies(out int itemsSim, out int itemsSleep, out int bldDyn, out int bldStatic, out int elder);

		Vector2 blockWorld = world.BlockToWorldPos(window.CameraBlock);
		int total = (int)(world.chunkWidth * world.chunkHeight);
		CpuLog.Info(
			$"[CPUOpt] chunk-sim window changed cam=({camWorld.x:F1},{camWorld.y:F1}) " +
			$"block=({window.CameraBlock.x},{window.CameraBlock.y}) blockWorld=({blockWorld.x:F1},{blockWorld.y:F1}) " +
			$"chunk=({window.CameraChunk.x},{window.CameraChunk.y}) local=({window.LocalX},{window.LocalY}) " +
			$"anchor=({window.Anchor.x},{window.Anchor.y}) chunks={window.FormatChunks()} " +
			$"collidersOn=4/{total} " +
			$"items sim={itemsSim} sleep={itemsSleep} buildings dyn={bldDyn} static={bldStatic} elder={elder}");
	}

	private static void LogTelemetry(bool steadyWindow)
	{
		if (Plugin.ChunkSimLogOnChange != null && !Plugin.ChunkSimLogOnChange.Value && steadyWindow)
			return;

		CountBodies(out int itemsSim, out int itemsSleep, out int bldDyn, out int bldStatic, out int elder);
		string mode = ChunkSimState.FormatSimMode();
		int chunkCount = ChunkSimState.ActiveChunkCount;
		ChunkSimBodySync.ConsumeDespawnerProbe(out int despSleep, out int despWake);
		int fot = ElderRegistry.ConsumeFotRescan();
		CPUOptimization.Features.Mp.Patches.SpiderTrackerPerfPatch.ConsumeProbe(out int trkOff, out int trkOn);
		BodyPinCache.ConsumeProbe(out int deaths, out int pinned, out int dropped, out int fotBody);
		CpuLog.Info(
			$"[CPUOpt] chunk-sim steady={ (steadyWindow ? 1 : 0) } mode={mode} " +
			$"chunks={ChunkSimState.FormatActiveChunks()} collidersOn={chunkCount} " +
			$"items sim={itemsSim} sleep={itemsSleep} buildings dyn={bldDyn} static={bldStatic} elder={elder}");
		CpuLog.Info(
			$"[CPUOpt] despawner sleepDisable={despSleep} wakeEnable={despWake}");
		CpuLog.Info(
			$"[CPUOpt] spider-trk disabled={trkOff} enabled={trkOn} elders={ElderRegistry.Count} fotRescan={fot}");
		CpuLog.Info(
			$"[CPUOpt] body-pin deaths={deaths} pinned={pinned} droppedNull={dropped} fot={fotBody}");
	}

	private static void CountBodies(
		out int itemsSim,
		out int itemsSleep,
		out int bldDyn,
		out int bldStatic,
		out int elder)
	{
		itemsSim = 0;
		itemsSleep = 0;

		for (int i = 0; i < Item.allItems.Count; i++)
		{
			Item item = Item.allItems[i];
			if (!item || item.transform.parent)
				continue;
			if (item.rb && item.rb.simulated)
				itemsSim++;
			else
				itemsSleep++;
		}

		bldDyn = BuildingSimRegistry.DynamicCount;
		bldStatic = BuildingSimRegistry.StaticCount;
		elder = BuildingSimRegistry.ElderCount;
	}

	private void RescanForElders()
	{
		ElderRegistry.NoteFotRescan();
		try
		{
			var elders = UnityEngine.Object.FindObjectsOfType<ElderThornbackBehaviour>(false);
			for (int i = 0; i < elders.Length; i++)
			{
				var elder = elders[i];
				if (elder == null)
					continue;

				ElderRegistry.Register(elder);
				if (!ChunkSimState.IsActive)
					continue;
				var building = elder.GetComponent<BuildingEntity>();
				if (building)
				{
					BuildingSimRegistry.Register(building);
					ChunkSimBodySync.ApplyBuilding(building);
				}
			}
		}
		catch { }
	}

	private void OnDestroy()
	{
		ChunkSimBodySync.RestoreAllOptDisabled();

		if (Plugin.ChunkSimColliders == null || !Plugin.ChunkSimColliders.Value)
			return;
		WorldGeneration world = WorldGeneration.world;
		if (!world || !world.worldExists)
			return;
		for (int x = 0; x < world.chunkWidth; x++)
		{
			for (int y = 0; y < world.chunkHeight; y++)
				SetChunkColliders(world, x, y, true);
		}
	}
}
