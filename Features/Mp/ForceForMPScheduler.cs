using System;
using System.Collections.Generic;
using System.Reflection;
using CPUOptimization.Features.ChunkSim;
using CPUOptimization.Features.Mp.Patches;
using UnityEngine;

namespace CPUOptimization.Features.Mp;

internal enum ForceForMPCategory
{
	Trap,
	Trader,
	Unknown
}

internal static class ForceForMPScheduler
{
	private static readonly Dictionary<Type, MethodInfo> MethodCache = new Dictionary<Type, MethodInfo>(8);
	private static readonly Dictionary<Type, ForceForMPCategory> CategoryCache = new Dictionary<Type, ForceForMPCategory>(8);

	private static readonly List<MonoBehaviour> TrapList = new List<MonoBehaviour>(192);
	private static readonly List<MonoBehaviour> TraderList = new List<MonoBehaviour>(32);
	private static readonly List<MonoBehaviour> UnknownList = new List<MonoBehaviour>(16);
	private static readonly HashSet<MonoBehaviour> Known = new HashSet<MonoBehaviour>();

	private static int _trapCursor;
	private static int _traderCursor;
	private static int _unknownCursor;
	private static bool _seeded;

	internal static int TrapCount => TrapList.Count;
	internal static int TraderCount => TraderList.Count;

	internal static void BootstrapSeed(bool allowRepeat = false)
	{
		if (KrokMpReflect.OnWillRenderType == null)
			return;
		if (_seeded && !allowRepeat)
			return;

		_seeded = true;
		int before = Known.Count;
		var instances = UnityEngine.Object.FindObjectsOfType(KrokMpReflect.OnWillRenderType);
		for (int i = 0; i < instances.Length; i++)
		{
			if (instances[i] is MonoBehaviour mb)
				Register(mb);
		}

		int added = Known.Count - before;
		if (added > 0 || (!allowRepeat && instances.Length > 0))
		{
			CpuLog.Info(
				$"[CPUOpt] ForceForMP registry seeded total={Known.Count} added={added} " +
				$"traps={TrapCount} traders={TraderCount}");
		}
	}

	internal static void Register(MonoBehaviour instance)
	{
		if (!instance || !Known.Add(instance))
			return;

		ForceForMPCategory cat = ResolveCategory(instance);
		ListFor(cat).Add(instance);
	}

	internal static void Unregister(MonoBehaviour instance)
	{
		if (!instance || !Known.Remove(instance))
			return;

		ForceForMPCategory cat = ResolveCategory(instance);
		ListFor(cat).Remove(instance);
	}

	internal static void Tick()
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return;
		if (KrokMpReflect.IsKrokMpClient())
			return;

		int interval = Plugin.KrokMpPerfOnWillRenderInterval?.Value ?? 6;
		if (interval > 1 && Time.frameCount % interval != 0)
			return;

		PruneDeadEntries();
		ProcessCategory(ForceForMPCategory.Trap, TrapList, ref _trapCursor,
			Plugin.KrokMpPerfOnWillRenderTrapBudget?.Value ?? 6);
		ProcessCategory(ForceForMPCategory.Trader, TraderList, ref _traderCursor,
			Plugin.KrokMpPerfOnWillRenderTraderBudget?.Value ?? 2);
		ProcessCategory(ForceForMPCategory.Unknown, UnknownList, ref _unknownCursor,
			Plugin.KrokMpPerfOnWillRenderInvokeBudget?.Value ?? 2);
	}

	internal static void SyncAllEnabledStates()
	{
		if (!ChunkSimState.IsActive)
			return;

		foreach (MonoBehaviour instance in Known)
		{
			if (instance)
				ApplyEnabledState(instance);
		}
	}

	internal static void ApplyEnabledState(MonoBehaviour instance)
	{
		if (!instance)
			return;

		ForceForMPCategory cat = ResolveCategory(instance);
		bool wantEnabled = cat switch
		{
			ForceForMPCategory.Trap => ChunkSimState.ShouldSimulateAuthorityWorldPos(instance.transform.position),
			ForceForMPCategory.Trader => ShouldTraderForceForMPEnable(instance),
			_ => ChunkSimState.ShouldSimulateAuthorityWorldPos(instance.transform.position)
		};

		if (instance.enabled != wantEnabled)
			instance.enabled = wantEnabled;
	}

	private static bool ShouldTraderForceForMPEnable(MonoBehaviour instance)
	{
		if (ChunkSimState.ShouldSimulateAuthorityWorldPos(instance.transform.position))
			return true;

		object trackerObj = instance.GetComponent(KrokMpReflect.ScavTrackerType);
		if (trackerObj == null)
			return false;

		object withinView = KrokMpReflect.ScavTrackerIsWithinViewField?.GetValue(trackerObj);
		return withinView is bool inView && inView;
	}

	private static void ProcessCategory(
		ForceForMPCategory category,
		List<MonoBehaviour> list,
		ref int cursor,
		int budget)
	{
		if (list.Count == 0 || budget <= 0)
			return;

		budget = Mathf.Max(1, budget);
		int processed = 0;
		int attempts = 0;
		int maxAttempts = list.Count + budget;

		while (processed < budget && attempts < maxAttempts && list.Count > 0)
		{
			attempts++;
			if (cursor >= list.Count)
				cursor = 0;

			MonoBehaviour instance = list[cursor++];
			if (!instance || !instance.enabled)
				continue;

			if (category == ForceForMPCategory.Trap
			    && !ChunkSimState.ShouldSimulateAuthorityWorldPos(instance.transform.position))
				continue;

			if (!KrokMpChunkGate.ShouldRunAt(instance.transform.position, usePresentationOnClient: false))
				continue;

			RunForceOnWillRender(instance);
			processed++;
		}
	}

	private static void RunForceOnWillRender(MonoBehaviour instance)
	{
		try
		{
			object target = KrokMpReflect.OnWillRenderTargetField?.GetValue(instance);
			if (target == null)
				return;

			Component targetComp = target as Component;
			if (!targetComp)
				return;

			object trackerObj = instance.GetComponent(KrokMpReflect.ScavTrackerType);
			if (trackerObj == null)
				return;

			Renderer renderer = targetComp.GetComponent<Renderer>();
			if (!renderer || renderer.isVisible)
				return;

			object withinView = KrokMpReflect.ScavTrackerIsWithinViewField?.GetValue(trackerObj);
			if (withinView is bool inView && !inView)
				return;

			Type targetType = targetComp.GetType();
			if (!MethodCache.TryGetValue(targetType, out MethodInfo method) || method == null)
			{
				method = targetType.GetMethod(
					"OnWillRenderObject",
					BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
						| BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
				MethodCache[targetType] = method;
			}

			method?.Invoke(target, null);
		}
		catch (Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] ForceForMP invoke: {ex.Message}");
		}
	}

	private static ForceForMPCategory ResolveCategory(MonoBehaviour instance)
	{
		try
		{
			object target = KrokMpReflect.OnWillRenderTargetField?.GetValue(instance);
			if (target == null)
				return ForceForMPCategory.Unknown;

			Type targetType = (target as Component)?.GetType();
			if (targetType == null)
				return ForceForMPCategory.Unknown;

			if (CategoryCache.TryGetValue(targetType, out ForceForMPCategory cached))
				return cached;

			string name = targetType.Name;
			ForceForMPCategory cat;
			if (name == "GunmineScript" || name == "StalactiteDropper")
				cat = ForceForMPCategory.Trap;
			else if (name == "TraderScript")
				cat = ForceForMPCategory.Trader;
			else
				cat = ForceForMPCategory.Unknown;

			CategoryCache[targetType] = cat;
			return cat;
		}
		catch
		{
			return ForceForMPCategory.Unknown;
		}
	}

	private static List<MonoBehaviour> ListFor(ForceForMPCategory cat)
	{
		switch (cat)
		{
			case ForceForMPCategory.Trader:
				return TraderList;
			case ForceForMPCategory.Unknown:
				return UnknownList;
			default:
				return TrapList;
		}
	}

	private static void PruneDeadEntries()
	{
		PruneList(TrapList);
		PruneList(TraderList);
		PruneList(UnknownList);

		Known.Clear();
		AddAllToKnown(TrapList);
		AddAllToKnown(TraderList);
		AddAllToKnown(UnknownList);
	}

	private static void PruneList(List<MonoBehaviour> list)
	{
		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (!list[i])
				list.RemoveAt(i);
		}
	}

	private static void AddAllToKnown(List<MonoBehaviour> list)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i])
				Known.Add(list[i]);
		}
	}
}
