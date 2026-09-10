using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CPUOptimization.Features.Mp;

// krokmp may load after awake, mp gameplay starts way later - retry until perf patches arm
internal sealed class KrokMpPerfDeferredHost : MonoBehaviour
{
	private const float LogIntervalSeconds = 15f;

	private Harmony _harmony;
	private int _attempts;
	private bool _armed;
	private bool _hooksInstalled;
	private float _logAge;

	internal static void Ensure(Harmony harmony, GameObject host)
	{
		if (!host)
			return;

		var existing = host.GetComponent<KrokMpPerfDeferredHost>();
		if (existing)
		{
			existing._harmony = harmony;
			return;
		}

		var component = host.AddComponent<KrokMpPerfDeferredHost>();
		component._harmony = harmony;
	}

	private void Start()
	{
		InstallHooks();
		TryArm(forceLog: true);
	}

	private void Update()
	{
		if (_armed || KrokMpPerfBootstrap.IsArmed)
		{
			_armed = true;
			return;
		}

		_logAge += Time.unscaledDeltaTime;
		bool forceLog = _logAge >= LogIntervalSeconds;
		if (forceLog)
			_logAge = 0f;

		TryArm(forceLog);
	}

	private void InstallHooks()
	{
		if (_hooksInstalled)
			return;

		_hooksInstalled = true;
		AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnDestroy()
	{
		AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
		SceneManager.sceneLoaded -= OnSceneLoaded;
	}

	private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
	{
		if (_armed || KrokMpPerfBootstrap.IsArmed)
			return;

		string name = args.LoadedAssembly.GetName().Name;
		if (name == null)
			return;

		if (name.IndexOf("Krokosha", StringComparison.OrdinalIgnoreCase) >= 0
		    || name.IndexOf("KrokMP", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			KrokMpOptional.Invalidate();
			TryArm(forceLog: true);
		}
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (_armed || KrokMpPerfBootstrap.IsArmed)
			return;

		TryArm(forceLog: false);
	}

	private void TryArm(bool forceLog)
	{
		_attempts++;
		if (_harmony == null)
			return;

		if (KrokMpPerfBootstrap.TryRegister(_harmony))
		{
			_armed = true;
			return;
		}

		if (forceLog)
		{
			CpuLog.Info(
				$"[CPUOpt] KrokMP perf waiting attempt={_attempts} bootstrap={KrokMpPerfBootstrap.Attempts} " +
				$"reason={KrokMpPerfBootstrap.GetWaitReason()}");
		}
	}
}
