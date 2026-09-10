using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal sealed class ParticleCullHost : MonoBehaviour
{
	private const int BatchSize = 96;

	private float _cullAge;
	private int _cursor;

	internal static void Register(ParticleSystem ps) => ParticleCullRegistry.Register(ps);

	private void Update()
	{
		if (Plugin.ChunkSimEnabled == null || !Plugin.ChunkSimEnabled.Value || !ChunkSimState.IsActive)
			return;

		WorldGeneration world = WorldGeneration.world;
		if (!world || world.generatingWorld)
			return;

		_cullAge += Time.unscaledDeltaTime;
		if (_cullAge < 0.35f)
			return;
		_cullAge = 0f;

		if (!TryOrigin(out _))
			return;

		IReadOnlyList<ParticleSystem> all = ParticleCullRegistry.All;
		int count = all.Count;
		if (count == 0)
			return;

		if (_cursor >= count)
			_cursor = 0;

		Transform localBody = PlayerCamera.main && PlayerCamera.main.body
			? PlayerCamera.main.body.transform
			: null;

		int batch = count <= BatchSize ? count : BatchSize;
		for (int i = 0; i < batch; i++)
		{
			int index = _cursor + i;
			if (index >= count)
				index -= count;
			Process(all[index], localBody);
		}

		_cursor += batch;
	}

	private static void Process(ParticleSystem ps, Transform localBody)
	{
		if (!ps || (localBody && ps.transform.IsChildOf(localBody)))
			return;

		if (ChunkSimState.ShouldSimulatePresentationWorldPos(ps.transform.position))
		{
			if (!ps.isPlaying)
			{
				var main = ps.main;
				if (main.loop)
					ps.Play(true);
			}
		}
		else if (ps.isPlaying)
		{
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
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
}
