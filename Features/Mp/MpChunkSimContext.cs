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
