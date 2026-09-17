using System;
using System.Reflection;
using UnityEngine;

namespace CPUOptimization.Features.Mp;

// resolved krokmp types/members for perf patches (no compile-time dependency)
internal static class KrokMpReflect
{
	private static bool _resolved;
	private static string _lastFailure = "not attempted";

	internal static bool Ok { get; private set; }
	internal static bool CoreOk { get; private set; }
	internal static bool SpiderOk { get; private set; }

	internal static string LastFailure => _lastFailure;

	internal static void Reset()
	{
		_resolved = false;
		Ok = false;
		CoreOk = false;
		SpiderOk = false;
		_lastFailure = "reset";
	}

	internal static Type OnWillRenderType;
	internal static MethodInfo OnWillRenderUpdate;
	internal static FieldInfo OnWillRenderTargetField;

	internal static Type ItemDespawnerType;
	internal static MethodInfo ItemDespawnerUpdate;
	internal static FieldInfo ItemDespawnerTimerField;
	internal static FieldInfo ItemDespawnerTimeField;

	internal static MethodInfo IsNetworkActiveAndIsClientMethod;
	internal static MethodInfo SafeDestroyObjectMethod;

	internal static Type SpiderTrackerType;
	internal static MethodInfo SpiderTrackerLateUpdate;

	internal static Type VoicechatType;
	internal static MethodInfo VoicechatUpdate;
	internal static PropertyInfo VoicechatRulesEnabledProp;
	internal static PropertyInfo VoicechatIsRecordingProp;

	internal static Type KrokoshaScavMultiplayerType;
	internal static PropertyInfo IsClientProp;

	internal static Type ItemUpdateMpPatchType;
	internal static MethodInfo MPinSimRangeMethod;

	internal static Type NetBodyType;
	internal static MethodInfo NetBodyOnDeath;
	internal static MethodInfo NetBodyGetBodiesFillMethod;

	internal static Type NetObjectRegistryType;
	internal static MethodInfo NetObjectRegistryNewGoMethod;
	internal static MethodInfo NetObjectRegistryQueueSyncMethod;

	internal static Type ScavTrackerType;
	internal static FieldInfo ScavTrackerIsWithinViewField;
	internal static FieldInfo ScavTrackerSyncInfoField;

	internal static PropertyInfo NetBodyIsPlayerProp;
	internal static PropertyInfo NetBodyBodyProp;
	internal static PropertyInfo NetBodyPlayerProp;

	internal static Type UtilType;
	internal static MethodInfo UtilIsBodyLocalMethod;

	internal static bool TryResolve()
	{
		if (_resolved)
			return Ok;
		_resolved = true;

		Assembly asm = KrokMpOptional.FindAssembly();
		if (asm == null)
		{
			_lastFailure = "KrokoshaCasualtiesMP assembly not loaded";
			return false;
		}

		const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		OnWillRenderType = asm.GetType("KrokoshaCasualtiesMP.Krokosha_OnWillRenderObject_ForceForMPComponent");
		ItemDespawnerType = asm.GetType("KrokoshaCasualtiesMP.ItemDespawnerIfUntouched");
		SpiderTrackerType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaSpiderTrackerComponent");
		VoicechatType = asm.GetType("KrokoshaCasualtiesMP.Voicechat");
		KrokoshaScavMultiplayerType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaScavMultiplayer");
		ItemUpdateMpPatchType = asm.GetType("KrokoshaCasualtiesMP.Item_Update_MultiplayerPatch");
		NetBodyType = asm.GetType("KrokoshaCasualtiesMP.NetBody");
		NetObjectRegistryType = asm.GetType("KrokoshaCasualtiesMP.NetObjectRegistry");
		ScavTrackerType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaScavMultiGameObjectNetworkTracker");
		UtilType = asm.GetType("KrokoshaCasualtiesUtils.Util")
		           ?? FindTypeInLoadedAssemblies("KrokoshaCasualtiesUtils.Util");

		if (OnWillRenderType != null)
		{
			OnWillRenderUpdate = OnWillRenderType.GetMethod("Update", inst);
			OnWillRenderTargetField = OnWillRenderType.GetField("target", inst);
		}

		if (ItemDespawnerType != null)
		{
			ItemDespawnerUpdate = ItemDespawnerType.GetMethod("Update", inst);
			ItemDespawnerTimerField = ItemDespawnerType.GetField("timer", inst);
			ItemDespawnerTimeField = ItemDespawnerType.GetField("despawntime", inst);
		}

		if (SpiderTrackerType != null)
			SpiderTrackerLateUpdate = SpiderTrackerType.GetMethod("LateUpdate", inst);

		if (VoicechatType != null)
		{
			VoicechatUpdate = VoicechatType.GetMethod("Update", inst);
			VoicechatRulesEnabledProp = VoicechatType.GetProperty("VCRULE_enabled", stat);
			VoicechatIsRecordingProp = VoicechatType.GetProperty("IS_RECORDING", stat);
		}

		if (KrokoshaScavMultiplayerType != null)
		{
			IsClientProp = KrokoshaScavMultiplayerType.GetProperty("is_client", stat);
			IsNetworkActiveAndIsClientMethod =
				KrokoshaScavMultiplayerType.GetMethod("IsNetworkActiveAndIsClient", stat);
		}

		if (ItemUpdateMpPatchType != null)
			MPinSimRangeMethod = ItemUpdateMpPatchType.GetMethod("MPinSimRange", stat);

		if (NetBodyType != null)
		{
			NetBodyOnDeath = NetBodyType.GetMethod("OnDeath", inst);
			Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(NetBodyType);
			foreach (MethodInfo mi in NetBodyType.GetMethods(stat))
			{
				if (mi.Name != "GetBodiesInRadius")
					continue;

				ParameterInfo[] ps = mi.GetParameters();
				if (ps.Length == 3
				    && ps[0].ParameterType == typeof(Vector2)
				    && ps[1].ParameterType == typeof(float)
				    && ps[2].ParameterType == listType)
				{
					NetBodyGetBodiesFillMethod = mi;
					break;
				}
			}

			NetBodyIsPlayerProp = NetBodyType.GetProperty("is_player", inst);
			NetBodyBodyProp = NetBodyType.GetProperty("body", inst);
			NetBodyPlayerProp = NetBodyType.GetProperty("player", inst);
		}

		if (NetObjectRegistryType != null)
		{
			NetObjectRegistryNewGoMethod = NetObjectRegistryType.GetMethod("NewGO", stat);
			SafeDestroyObjectMethod = NetObjectRegistryType.GetMethod("SafeDestroyObject", stat);
			foreach (MethodInfo mi in NetObjectRegistryType.GetMethods(stat))
			{
				if (mi.Name == "Server_QueueSyncForOne")
					NetObjectRegistryQueueSyncMethod = mi;
			}
		}

		if (ScavTrackerType != null)
		{
			ScavTrackerIsWithinViewField = ScavTrackerType.GetField("is_within_anyones_view", inst);
			ScavTrackerSyncInfoField = ScavTrackerType.GetField("syncinfo", inst);
		}

		if (UtilType != null)
		{
			UtilIsBodyLocalMethod = UtilType.GetMethod(
				"IsBodyLocal",
				stat,
				null,
				new[] { typeof(Body) },
				null);
		}

		bool onWillRenderOk = OnWillRenderUpdate != null && OnWillRenderTargetField != null;
		bool despawnerOk = ItemDespawnerUpdate != null && ItemDespawnerTimerField != null;
		bool voiceOk = VoicechatUpdate != null;
		bool spiderOk = SpiderTrackerLateUpdate != null;

		CoreOk = onWillRenderOk && despawnerOk && voiceOk;
		SpiderOk = spiderOk;
		Ok = CoreOk && SpiderOk;
		if (!Ok)
		{
			_lastFailure =
				$"core={(CoreOk ? 1 : 0)} onWillRender={(onWillRenderOk ? 1 : 0)} despawner={(despawnerOk ? 1 : 0)} " +
				$"voice={(voiceOk ? 1 : 0)} spider={(spiderOk ? 1 : 0)}";
		}

		return Ok;
	}

	private static Type FindTypeInLoadedAssemblies(string fullName)
	{
		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type type = asm.GetType(fullName, throwOnError: false);
			if (type != null)
				return type;
		}

		return null;
	}

	internal static bool IsKrokMpClient()
	{
		if (IsClientProp == null)
			return false;
		try
		{
			return IsClientProp.GetValue(null, null) is bool b && b;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsNetworkActiveAndIsClient()
	{
		if (IsNetworkActiveAndIsClientMethod == null)
			return false;
		try
		{
			return IsNetworkActiveAndIsClientMethod.Invoke(null, null) is bool b && b;
		}
		catch
		{
			return false;
		}
	}

	internal static bool VoiceChatRulesEnabled()
	{
		if (VoicechatRulesEnabledProp == null)
			return true;
		try
		{
			return VoicechatRulesEnabledProp.GetValue(null, null) is bool b && b;
		}
		catch
		{
			return true;
		}
	}

	internal static bool VoiceChatIsRecording()
	{
		if (VoicechatIsRecordingProp == null)
			return false;
		try
		{
			return VoicechatIsRecordingProp.GetValue(null, null) is bool b && b;
		}
		catch
		{
			return false;
		}
	}

	internal static bool MPinSimRange(Vector2 pos)
	{
		if (MPinSimRangeMethod == null)
			return true;
		try
		{
			return MPinSimRangeMethod.Invoke(null, new object[] { pos }) is bool b && b;
		}
		catch
		{
			return true;
		}
	}
}
