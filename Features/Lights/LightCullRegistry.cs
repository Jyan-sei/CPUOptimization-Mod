using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

// incremental light2d registry - no findobjectsbytype rescans
internal static class LightCullRegistry
{
	private static readonly List<LightCullEntry> Entries = new List<LightCullEntry>(512);
	private static readonly Dictionary<Light2D, LightCullEntry> Known = new Dictionary<Light2D, LightCullEntry>(512);

	internal static int Count => Entries.Count;

	internal static IReadOnlyList<LightCullEntry> All => Entries;

	internal static LightCullFlags ComputeFlags(Light2D light)
	{
		if (!light)
			return LightCullFlags.None;

		LightCullFlags flags = LightCullFlags.None;
		if (light.lightType == Light2D.LightType.Global)
			flags |= LightCullFlags.Global;
		if (light.GetComponentInParent<FxLightToEmission>())
			flags |= LightCullFlags.FxConverted;
		if (light.GetComponentInParent<LightItem>())
			flags |= LightCullFlags.LightItem;
		return flags;
	}

	internal static void Register(Light2D light)
	{
		if (!light || Known.ContainsKey(light))
			return;

		var entry = new LightCullEntry(light);
		Known[light] = entry;
		Entries.Add(entry);
	}

	internal static void Unregister(Light2D light)
	{
		if (!light || !Known.TryGetValue(light, out LightCullEntry entry))
			return;

		Known.Remove(light);
		Entries.Remove(entry);
	}

	internal static void CompactDead()
	{
		for (int i = Entries.Count - 1; i >= 0; i--)
		{
			if (Entries[i].Light)
				continue;
			Entries.RemoveAt(i);
		}

		List<Light2D> deadKeys = null;
		foreach (KeyValuePair<Light2D, LightCullEntry> pair in Known)
		{
			if (pair.Key)
				continue;
			deadKeys ??= new List<Light2D>(8);
			deadKeys.Add(pair.Key);
		}

		if (deadKeys == null)
			return;

		for (int i = 0; i < deadKeys.Count; i++)
			Known.Remove(deadKeys[i]);
	}
}
