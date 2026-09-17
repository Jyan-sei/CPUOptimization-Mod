using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.Mp;

internal static class MpChunkSimContext
{
	private static bool _wasNetworkRunning;

	internal static bool IsMpUnionEnabled =>
		Plugin.ChunkSimMpUnionEnabled != null
		&& Plugin.ChunkSimMpUnionEnabled.Value
		&& KrokMpOptional.IsPresent;

	internal static bool IsNetworkRunning => KrokMpOptional.IsNetworkRunning;

	internal static bool IsMpChunkSimActive =>
		IsMpUnionEnabled && IsNetworkRunning;

	internal static bool UseClientLocalSimOnly =>
		IsNetworkRunning
		&& Plugin.ChunkSimMpClientLocalSim != null
		&& Plugin.ChunkSimMpClientLocalSim.Value
		&& KrokMpOptional.IsPureClient;

	// true once when mp session starts; false once when it ends
	internal static bool PollSessionTransition(out bool started, out bool ended)
	{
		started = false;
		ended = false;
		bool running = IsMpChunkSimActive;
		if (running && !_wasNetworkRunning)
			started = true;
		else if (!running && _wasNetworkRunning)
			ended = true;
		_wasNetworkRunning = running;
		return started || ended;
	}

	internal static void CollectPlayerPositions(List<Vector3> dest)
	{
		if (dest == null)
			return;
		dest.Clear();

		if (PlayerCamera.main)
			dest.Add(PlayerCamera.main.transform.position);
		else if (Camera.main)
			dest.Add(Camera.main.transform.position);

		if (!IsNetworkRunning)
			return;

		var mpPositions = new List<Vector3>(8);
		KrokMpOptional.CollectLivingPlayerPositions(mpPositions);
		KrokMpOptional.AppendDeadPlayerBodyPositions(mpPositions);
		BodyPinCache.AppendPinnedBodies(mpPositions);
		for (int i = 0; i < mpPositions.Count; i++)
			TryAddUnique(dest, mpPositions[i]);
	}

	private static void TryAddUnique(List<Vector3> dest, Vector3 pos)
	{
		const float eps = 0.5f;
		for (int i = 0; i < dest.Count; i++)
		{
			if ((dest[i] - pos).sqrMagnitude < eps * eps)
				return;
		}
		dest.Add(pos);
	}
}

internal static class BodyPinCache
{
	private static readonly List<Body> Pinned = new List<Body>(16);
	private static int _deaths;
	private static int _droppedNull;
	private static int _fot;

	internal static void Clear()
	{
		Pinned.Clear();
	}

	internal static void Pin(Body body)
	{
		if (!body)
			return;
		for (int i = 0; i < Pinned.Count; i++)
		{
			if (Pinned[i] == body)
				return;
		}
		Pinned.Add(body);
		_deaths++;
	}

	internal static void AppendPinnedBodies(List<Vector3> dest)
	{
		if (dest == null)
			return;
		for (int i = Pinned.Count - 1; i >= 0; i--)
		{
			Body b = Pinned[i];
			if (!b)
			{
				Pinned.RemoveAt(i);
				_droppedNull++;
				continue;
			}
			dest.Add(b.transform.position);
		}
	}

	internal static void ConsumeProbe(out int deaths, out int pinned, out int droppedNull, out int fot)
	{
		deaths = _deaths;
		pinned = Pinned.Count;
		droppedNull = _droppedNull;
		fot = _fot;
		_deaths = 0;
		_droppedNull = 0;
		_fot = 0;
	}
}
