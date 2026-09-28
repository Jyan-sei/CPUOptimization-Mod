using System;
using System.Reflection;
using HarmonyLib;

namespace CPUOptimization2;

// qol's SeededRunPatcher prefix calls Random.InitState before DistributeEntities.
// a prefix that returns false skips every prefix still waiting, so ours has to run after theirs
internal static class QolSeed
{
	internal const int AfterOtherPrefixes = Priority.Last - 1;

	private static bool _looked;
	private static FieldInfo _isSeeded;
	private static bool _logged;

	internal static bool Active
	{
		get
		{
			Resolve();
			if (_isSeeded == null || MpSession.Running)
				return false;
			try
			{
				return _isSeeded.GetValue(null) is bool seeded && seeded;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	internal static void NoteIfActive()
	{
		if (_logged || !Active)
			return;
		_logged = true;
		Plugin.Log?.LogInfo("QoL seed is set — ghost placement follows their Random stream.");
	}

	private static void Resolve()
	{
		if (_looked)
			return;
		_looked = true;
		Type seedManager = null;
		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			seedManager = asm.GetType("QoL_Unknown.SeedManager", throwOnError: false);
			if (seedManager != null)
				break;
		}

		_isSeeded = seedManager?.GetField("IsSeeded", BindingFlags.Public | BindingFlags.Static);
	}
}
