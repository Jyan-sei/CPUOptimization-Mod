using System.Collections.Generic;
using CPUOptimization.Features.ChunkSim;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

internal sealed class LightCullHost : MonoBehaviour
{
	private const int CullBatchSize = 128;

	private float _cullAge;
	private float _logAge;
	private bool _wasGenerating = true;
	private bool _loggedFirstScan;
	private int _compactCullTicks;
	private int _cullCursor;
	private int _passOn;
	private int _passCulled;
	private int _passProt;
	private int _passFx;
	private int _passLive;

	private static bool _forceImmediateCull;

	// Incremental nearest-light culling (decoupled from chunks for smooth appearance)
	private int _registryScanCursor;
	private readonly List<Light2D> _lightCandidates = new List<Light2D>(128);
	private readonly Dictionary<Light2D, float> _candidateDistances = new Dictionary<Light2D, float>(128);
	private readonly Queue<Light2D> _lightsToEnable = new Queue<Light2D>();
	private readonly Queue<Light2D> _lightsToDisable = new Queue<Light2D>();
	private int _candidateEvalCursor;

	private static int _fxConverted;
	private static string _lastFxKind;

	internal static int LastTotal;
	internal static int LastOn;
	internal static int LastCulled;
	internal static int LastProtected;
	internal static int LastFx;

	internal static void NoteFxConverted(string kind, string name)
	{
		_fxConverted++;
		_lastFxKind = kind;
		if (Plugin.LightsVerboseLogging != null && Plugin.LightsVerboseLogging.Value)
			CpuLog.Info($"[CPUOpt] lights fx convert {kind} name={name} n={_fxConverted}");
		else if (_fxConverted == 1)
			CpuLog.Info($"[CPUOpt] lights fx convert first {kind} name={name}");
	}

	internal static void RegisterLight(Light2D light) => LightCullRegistry.Register(light);

	internal static void RequestRescan()
	{
		LightCullRegistry.CompactDead();
	}

	// called by ChunkSim on window/union changes so lights in newly active chunks turn on immediately
	// (avoids waiting for the periodic batch cursor to reach them)
	internal static void RequestImmediateCull()
	{
		_forceImmediateCull = true;
	}

	private void Update()
	{
		if (Plugin.LightsEnabled == null || !Plugin.LightsEnabled.Value)
			return;

		WorldGeneration world = WorldGeneration.world;
		bool generating = world && world.generatingWorld;
		if (_wasGenerating && !generating && world)
		{
			LightCullRegistry.CompactDead();
			_lightCandidates.Clear();
			_candidateDistances.Clear();
			_lightsToEnable.Clear();
			_lightsToDisable.Clear();
			CpuLog.Info("[CPUOpt] lights registry compacted (world gen finished)");
		}
		_wasGenerating = generating;
		if (generating)
			return;

		float dt = Time.unscaledDeltaTime;
		_cullAge += dt;
		_logAge += dt;

		if (_forceImmediateCull)
		{
			_cullAge = 999f;
			_forceImmediateCull = false;
		}

		// New incremental decoupled light culling (1 registry scan + 1 eval + max 1 on + 1 off per frame)
		if (Plugin.LightsCullEnabled == null || Plugin.LightsCullEnabled.Value)
		{
			ScanOneRegistryLightForCandidates();
			EvaluateOneCandidate();
			ApplyAtMostOneLightStateChange();
		}

		float logEvery = Plugin.LightsTelemetryWindowSeconds?.Value ?? 10f;
		if (logEvery < 2f)
			logEvery = 2f;
		if (_logAge >= logEvery)
		{
			_logAge = 0f;
			LogWindow();
		}
	}

	private void CullBatch()
	{
		if (++_compactCullTicks % 8 == 0)
			LightCullRegistry.CompactDead();

		if (!TryOrigin(out Vector3 origin))
			return;

		IReadOnlyList<LightCullEntry> entries = LightCullRegistry.All;
		int count = entries.Count;
		if (count == 0)
			return;

		if (_cullCursor >= count)
			_cullCursor = 0;

		if (_cullCursor == 0)
		{
			_passOn = 0;
			_passCulled = 0;
			_passProt = 0;
			_passFx = 0;
			_passLive = 0;
		}

		CameraViewMetrics.TryGetCullRadii(out float onR, out float offR);
		float onSq = onR * onR;
		float offSq = offR * offR;

		WorldGeneration world = WorldGeneration.world;
		Transform localBody = PlayerCamera.main && PlayerCamera.main.body
			? PlayerCamera.main.body.transform
			: null;

		int batch = count <= CullBatchSize ? count : CullBatchSize;
		for (int n = 0; n < batch; n++)
		{
			int index = _cullCursor + n;
			if (index >= count)
				index -= count;
			ProcessEntry(entries[index], world, localBody, origin, onSq, offSq);
		}

		_cullCursor += batch;
		if (_cullCursor >= count)
		{
			_cullCursor = 0;
			CommitPassStats(count, onR);
		}
	}

	private void ProcessEntry(
		LightCullEntry entry,
		WorldGeneration world,
		Transform localBody,
		Vector3 origin,
		float onSq,
		float offSq)
	{
		Light2D light = entry.Light;
		if (!light)
			return;

		_passLive++;

		if ((entry.Flags & LightCullFlags.FxConverted) != 0)
		{
			if (light.enabled)
				light.enabled = false;
			_passFx++;
			return;
		}

		if (IsProtected(entry, light, world, localBody))
		{
			_passProt++;
			if (light.enabled)
				_passOn++;
			return;
		}

		// Prefer ChunkSim active chunks as truth (consistent with particles, buildings, items, etc.)
		// This avoids camera-distance pop-in/out and "several seconds to appear" from full-registry batching.
		// In MP: presentation window means "local for this player" (even outside global union).
		if (ChunkSimState.IsActive && Plugin.ChunkSimEnabled != null && Plugin.ChunkSimEnabled.Value)
		{
			bool inSim = ChunkSimState.ShouldSimulatePresentationWorldPos(light.transform.position);
			if (light.enabled != inSim)
				light.enabled = inSim;
			if (inSim)
				_passOn++;
			else
				_passCulled++;
			return;
		}

		float distSq = (light.transform.position - origin).sqrMagnitude;
		bool wantOn = light.enabled ? distSq <= offSq : distSq <= onSq;
		if (light.enabled != wantOn)
			light.enabled = wantOn;

		if (wantOn)
			_passOn++;
		else
			_passCulled++;
	}

	private static bool IsProtected(
		LightCullEntry entry,
		Light2D light,
		WorldGeneration world,
		Transform localBody)
	{
		if ((entry.Flags & LightCullFlags.Global) != 0)
			return true;
		if (world && world.ambientLight == light)
			return true;
		if ((entry.Flags & LightCullFlags.LightItem) != 0)
			return true;
		if (localBody && light.transform.IsChildOf(localBody))
			return true;
		return false;
	}

	private void CommitPassStats(int live, float onR)
	{
		LastTotal = live;
		LastOn = _passOn;
		LastCulled = _passCulled;
		LastProtected = _passProt;
		LastFx = _passFx;

		if (!_loggedFirstScan && live > 0)
		{
			_loggedFirstScan = true;
			CameraViewMetrics.TryGetCullRadii(out float logOnR, out _);
			bool fromCam = Plugin.LightsCullUseCameraView == null || Plugin.LightsCullUseCameraView.Value;
			CpuLog.Info(
				$"[CPUOpt] lights first-cull total={live} on={_passOn} culled={_passCulled} " +
				$"protected={_passProt} fx={_passFx} radius={logOnR:F1} src={(fromCam ? "camera" : "config")} " +
				$"batch={CullBatchSize} (incremental registry)");
		}
	}

	private static bool TryOrigin(out Vector3 origin)
	{
		if (PlayerCamera.main)
		{
			origin = PlayerCamera.main.transform.position;
			return true;
		}
		if (Camera.main)
		{
			origin = Camera.main.transform.position;
			return true;
		}
		origin = Vector3.zero;
		return false;
	}

	private void LogWindow()
	{
		if (Plugin.LightsDebugLog != null && !Plugin.LightsDebugLog.Value &&
		    (Plugin.LightsVerboseLogging == null || !Plugin.LightsVerboseLogging.Value))
			return;

		CameraViewMetrics.TryGetCullRadii(out float onR, out _);
		CpuLog.Info(
			$"[CPUOpt] lights total={LastTotal} on={LastOn} culled={LastCulled} " +
			$"protected={LastProtected} fx={LastFx} converted={_fxConverted} " +
			$"lastFx={_lastFxKind ?? "-"} radius={onR:F1} " +
			$"cull={(Plugin.LightsCullEnabled != null && Plugin.LightsCullEnabled.Value ? 1 : 0)} " +
			$"traps={(Plugin.LightsReplaceTrapLights != null && Plugin.LightsReplaceTrapLights.Value ? 1 : 0)}");
	}

	// === New decoupled incremental light culling ===
	// - Uses nearest 9 chunks (3x3) to filter candidates (decouples from full chunk sim window)
	// - Checks 1 registry light per frame to maintain up to 100 nearest by distance
	// - Evaluates 1 candidate per frame for on/off decision using distance + hysteresis
	// - Applies at most 1 enable + 1 disable per frame from queues
	// - Smooth, no hard chunk border swaps, doesn't walk entire layer

	private void ScanOneRegistryLightForCandidates()
	{
		var entries = LightCullRegistry.All;
		if (entries.Count == 0) return;

		_registryScanCursor = (_registryScanCursor + 1) % entries.Count;
		LightCullEntry entry = entries[_registryScanCursor];
		Light2D light = entry.Light;
		if (!light) return;

		Vector3 pos = light.transform.position;
		if (IsWithinNearest9Chunks(pos))
		{
			UpdateOrAddCandidate(light, pos);
		}
		else
		{
			RemoveFromCandidates(light);
		}
	}

	private bool IsWithinNearest9Chunks(Vector3 pos)
	{
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists) return true; // fallback to consider

		try
		{
			Vector3 cam = GetCameraPositionForLights();
			Vector2Int camBlock = world.WorldToBlockPos(cam);
			Vector2Int camChunk = world.BlockToChunkPos(camBlock);

			Vector2Int lightBlock = world.WorldToBlockPos(pos);
			Vector2Int lightChunk = world.BlockToChunkPos(lightBlock);

			int dx = System.Math.Abs(lightChunk.x - camChunk.x);
			int dy = System.Math.Abs(lightChunk.y - camChunk.y);
			return dx <= 1 && dy <= 1; // 3x3 = 9 chunks
		}
		catch
		{
			return true;
		}
	}

	private Vector3 GetCameraPositionForLights()
	{
		if (PlayerCamera.main) return PlayerCamera.main.transform.position;
		if (Camera.main) return Camera.main.transform.position;
		return Vector3.zero;
	}

	private void UpdateOrAddCandidate(Light2D light, Vector3 pos)
	{
		Vector3 origin = GetCameraPositionForLights();
		float dist = (pos - origin).magnitude;

		_candidateDistances[light] = dist;

		if (!_lightCandidates.Contains(light))
			_lightCandidates.Add(light);

		// Keep only nearest 100
		while (_lightCandidates.Count > 100)
		{
			Light2D farthest = null;
			float maxDist = float.MinValue;
			foreach (var l in _lightCandidates)
			{
				if (_candidateDistances.TryGetValue(l, out float d) && d > maxDist)
				{
					maxDist = d;
					farthest = l;
				}
			}
			if (farthest != null)
			{
				_lightCandidates.Remove(farthest);
				_candidateDistances.Remove(farthest);
			}
			else break;
		}
	}

	private void RemoveFromCandidates(Light2D light)
	{
		_lightCandidates.Remove(light);
		_candidateDistances.Remove(light);
	}

	private void EvaluateOneCandidate()
	{
		if (_lightCandidates.Count == 0) return;

		_candidateEvalCursor = (_candidateEvalCursor + 1) % _lightCandidates.Count;
		Light2D light = _lightCandidates[_candidateEvalCursor];
		if (!light)
		{
			RemoveFromCandidates(light);
			return;
		}

		// Respect protected / fx (always on or special)
		// Quick check, approximate
		if ((light.lightType == Light2D.LightType.Global) || light.GetComponentInParent<FxLightToEmission>() != null || light.GetComponentInParent<LightItem>() != null)
		{
			if (!light.enabled) light.enabled = true;
			return;
		}

		if (!_candidateDistances.TryGetValue(light, out float dist))
		{
			dist = (light.transform.position - GetCameraPositionForLights()).magnitude;
			_candidateDistances[light] = dist;
		}

		CameraViewMetrics.TryGetCullRadii(out float onR, out float offR);

		bool currentlyOn = light.enabled;
		bool shouldOn = dist <= (currentlyOn ? offR : onR);

		if (currentlyOn != shouldOn)
		{
			if (shouldOn)
				_lightsToEnable.Enqueue(light);
			else
				_lightsToDisable.Enqueue(light);
		}
	}

	private void ApplyAtMostOneLightStateChange()
	{
		// At most 1 deactivate + 1 activate per frame
		if (_lightsToDisable.Count > 0)
		{
			Light2D l = _lightsToDisable.Dequeue();
			if (l && l.enabled)
				l.enabled = false;
		}

		if (_lightsToEnable.Count > 0)
		{
			Light2D l = _lightsToEnable.Dequeue();
			if (l && !l.enabled)
				l.enabled = true;
		}

		// Update stats for logs (approximate)
		LastTotal = _lightCandidates.Count;
	}
}
