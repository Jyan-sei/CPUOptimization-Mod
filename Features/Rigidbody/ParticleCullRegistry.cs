using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ParticleCullRegistry
{
	private static readonly List<ParticleSystem> Systems = new List<ParticleSystem>(512);
	private static readonly HashSet<ParticleSystem> Known = new HashSet<ParticleSystem>();

	internal static IReadOnlyList<ParticleSystem> All => Systems;

	internal static void Register(ParticleSystem ps)
	{
		if (!ps || Known.Contains(ps))
			return;
		Known.Add(ps);
		Systems.Add(ps);
	}

	internal static void RegisterTree(Component owner)
	{
		if (!owner)
			return;

		ParticleSystem[] systems = owner.GetComponentsInChildren<ParticleSystem>(true);
		for (int i = 0; i < systems.Length; i++)
			Register(systems[i]);
	}

	internal static void CompactDead()
	{
		for (int i = Systems.Count - 1; i >= 0; i--)
		{
			if (!Systems[i])
			{
				Known.Remove(Systems[i]);
				Systems.RemoveAt(i);
			}
		}
	}
}
