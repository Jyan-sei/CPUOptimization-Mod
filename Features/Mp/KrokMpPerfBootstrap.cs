using CPUOptimization.Features.Mp.Patches;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.Mp;

internal static class KrokMpPerfBootstrap
{
	private const float WaitLogIntervalSeconds = 15f;
	private const float RetryIntervalSeconds = 1f;

	private static bool _armed;
	private static bool _coreArmed;
	private static bool _spiderArmed;
	private static Harmony _harmony;
	private static int _attempts;
	private static string _lastWaitReason = "init";
	private static string _lastLoggedReason;
	private static float _waitLogAge;
	private static float _retryAge;

	internal static bool IsArmed => _armed;

	internal static void EnsureDeferred(Harmony harmony, GameObject host)
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return;

		_harmony = harmony;
		if (_armed)
			return;

		KrokMpPerfDeferredHost.Ensure(harmony, host);
	}

	internal static void RequestRetry(string reason)
	{
		if (_armed)
			return;

		if (_harmony == null)
		{
			_lastWaitReason = "harmony not ready";
			return;
		}

		_lastWaitReason = reason;
		_retryAge = RetryIntervalSeconds;
		TryRegister(_harmony);
	}

	internal static void TickRetry(float unscaledDeltaTime)
	{
		if (_armed || Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return;

		if (_harmony == null)
			return;

		_waitLogAge += unscaledDeltaTime;
		_retryAge += unscaledDeltaTime;
		bool forceLog = _waitLogAge >= WaitLogIntervalSeconds;
		if (forceLog)
			_waitLogAge = 0f;

		if (_retryAge < RetryIntervalSeconds)
			return;
		_retryAge = 0f;

		string reasonBefore = KrokMpReflect.LastFailure;
		TryRegister(_harmony);
		if (_armed)
			return;

		if (forceLog || reasonBefore != KrokMpReflect.LastFailure || _lastLoggedReason != _lastWaitReason)
			LogWaiting(forceLog);
	}

	internal static bool TryRegister(Harmony harmony)
	{
		if (_armed)
			return true;

		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
		{
			_lastWaitReason = "disabled via config";
			return false;
		}

		_harmony = harmony;
		_attempts++;

		KrokMpOptional.Resolve();
		if (!KrokMpOptional.IsPresent)
		{
			_lastWaitReason = "KrokMP assembly/types not ready";
			return false;
		}

		KrokMpReflect.Reset();
		try
		{
			if (!KrokMpReflect.TryResolve())
			{
				_lastWaitReason = KrokMpReflect.LastFailure;
				return false;
			}
		}
		catch (System.Exception ex)
		{
			_lastWaitReason = "reflect exception: " + ex.Message;
			CpuLog.Warn($"[CPUOpt] KrokMP perf reflect failed: {ex.Message}");
			return false;
		}

		try
		{
			if (KrokMpReflect.CoreOk && !_coreArmed)
			{
				OnWillRenderObjectPerfPatch.Apply(harmony);
				ItemDespawnerPerfPatch.Apply(harmony);
				VoicechatPerfPatch.Apply(harmony);
				_coreArmed = true;
			}

			if (KrokMpReflect.SpiderOk && !_spiderArmed)
			{
				SpiderTrackerPerfPatch.Apply(harmony);
				_spiderArmed = true;
			}
		}
		catch (System.Exception ex)
		{
			_lastWaitReason = "harmony patch failed: " + ex.Message;
			CpuLog.Warn($"[CPUOpt] KrokMP perf patch exception: {ex.Message}");
			return false;
		}

		if (!KrokMpReflect.CoreOk)
		{
			_lastWaitReason = KrokMpReflect.LastFailure;
			return false;
		}

		ForceForMPScheduler.BootstrapSeed();

		_armed = true;
		CpuLog.Info(
			$"[CPUOpt] KrokMP perf armed attempts={_attempts} onWillRender={Plugin.KrokMpPerfOnWillRenderInterval?.Value ?? 6}f " +
			$"trapBudget={Plugin.KrokMpPerfOnWillRenderTrapBudget?.Value ?? 6} " +
			$"traderBudget={Plugin.KrokMpPerfOnWillRenderTraderBudget?.Value ?? 2} " +
			$"despawnerSkip={Plugin.KrokMpPerfDespawnerFrameSkip?.Value ?? 4} " +
			$"chunkGate={(Plugin.KrokMpPerfGateChunkSim?.Value == true ? 1 : 0)} " +
			$"voiceEarlyOut={(Plugin.KrokMpPerfVoicechatEarlyOut?.Value == true ? 1 : 0)} " +
			$"forceForMP traps={ForceForMPScheduler.TrapCount} traders={ForceForMPScheduler.TraderCount} " +
			$"spider={(_spiderArmed ? 1 : 0)}");
		return true;
	}

	internal static string GetWaitReason() => _lastWaitReason;

	internal static int Attempts => _attempts;

	private static void LogWaiting(bool forceLog)
	{
		_lastLoggedReason = _lastWaitReason;
		CpuLog.Info(
			$"[CPUOpt] KrokMP perf waiting attempt={_attempts} bootstrap={Attempts} " +
			$"reason={GetWaitReason()} reflect={KrokMpReflect.LastFailure}");
	}
}
