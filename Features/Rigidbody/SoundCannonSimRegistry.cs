using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class SoundCannonSimRegistry
{
	private static readonly List<SoundCannon> Cannons = new List<SoundCannon>(128);
	private static readonly HashSet<SoundCannon> Known = new HashSet<SoundCannon>();

	internal static int Count => Cannons.Count;

	internal static IReadOnlyList<SoundCannon> All => Cannons;

	internal static void Register(SoundCannon cannon)
	{
		if (!cannon || !Known.Add(cannon))
			return;
		Cannons.Add(cannon);
	}

	internal static void Unregister(SoundCannon cannon)
	{
		if (!cannon)
			return;
		if (!Known.Remove(cannon))
			return;
		Cannons.Remove(cannon);
	}

	internal static void Clear()
	{
		Cannons.Clear();
		Known.Clear();
	}
}
