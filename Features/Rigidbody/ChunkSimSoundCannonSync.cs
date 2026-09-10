using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// disable soundcannon + mp tracker off-window (same pattern as buildingentity)
internal static class ChunkSimSoundCannonSync
{
	private static readonly AccessTools.FieldRef<SoundCannon, bool> ChargingRef =
		AccessTools.FieldRefAccess<SoundCannon, bool>("charging");
	private static readonly AccessTools.FieldRef<SoundCannon, bool> SpentRef =
		AccessTools.FieldRefAccess<SoundCannon, bool>("spent");

	private static Type _trackerType;
	private static FieldInfo _trackerChargingField;
	private static FieldInfo _trackerSpentField;
	private static bool _trackerResolved;

	internal static void SyncAll()
	{
		if (!ChunkSimState.IsActive)
			return;

		IReadOnlyList<SoundCannon> cannons = SoundCannonSimRegistry.All;
		for (int i = 0; i < cannons.Count; i++)
		{
			SoundCannon cannon = cannons[i];
			if (cannon)
				Apply(cannon);
		}
	}

	internal static void Apply(SoundCannon cannon)
	{
		if (!cannon || !ChunkSimState.IsActive)
			return;

		ChunkSimTrackState track = ChunkSimTrackTable.Get(cannon);
		bool inSim = ChunkSimState.ShouldSimulatePresentationWorldPos(cannon.transform.position);
		bool busy = IsBusy(cannon, track);
		bool wantEnabled = inSim || busy;
		SyncCannonEnabled(cannon, track, wantEnabled);
		SyncTrackerEnabled(cannon, track, wantEnabled);
	}

	internal static void Restore(SoundCannon cannon, ChunkSimTrackState track)
	{
		if (!cannon || track == null)
			return;

		if (track.CannonDisabledByOpt)
		{
			cannon.enabled = true;
			track.CannonDisabledByOpt = false;
		}

		RestoreTracker(cannon, track);
	}

	internal static void OnDestroyed(SoundCannon cannon)
	{
		if (!cannon)
			return;

		Restore(cannon, ChunkSimTrackTable.Get(cannon));
		SoundCannonSimRegistry.Unregister(cannon);
		ChunkSimTrackTable.Remove(cannon);
	}

	internal static void RestoreAllOptDisabled()
	{
		IReadOnlyList<SoundCannon> cannons = SoundCannonSimRegistry.All;
		for (int i = 0; i < cannons.Count; i++)
		{
			SoundCannon cannon = cannons[i];
			if (cannon)
				Restore(cannon, ChunkSimTrackTable.Get(cannon));
		}
	}

	private static bool IsBusy(SoundCannon cannon, ChunkSimTrackState track)
	{
		if (!cannon)
			return false;

		if (ChargingRef(cannon) || SpentRef(cannon))
			return true;

		Behaviour tracker = ResolveTracker(cannon, track);
		if (!tracker)
			return false;

		EnsureTrackerFields();
		if (_trackerChargingField != null
		    && _trackerChargingField.GetValue(tracker) is bool charging
		    && charging)
			return true;

		if (_trackerSpentField != null
		    && _trackerSpentField.GetValue(tracker) is bool spent
		    && spent)
			return true;

		return false;
	}

	private static void SyncCannonEnabled(SoundCannon cannon, ChunkSimTrackState track, bool wantEnabled)
	{
		if (!cannon)
			return;

		if (wantEnabled)
		{
			if (track.CannonDisabledByOpt || !cannon.enabled)
			{
				cannon.enabled = true;
				track.CannonDisabledByOpt = false;
			}
			return;
		}

		if (cannon.enabled && !track.CannonDisabledByOpt)
		{
			cannon.enabled = false;
			track.CannonDisabledByOpt = true;
		}
	}

	private static void SyncTrackerEnabled(SoundCannon cannon, ChunkSimTrackState track, bool wantEnabled)
	{
		Behaviour tracker = ResolveTracker(cannon, track);
		if (!tracker)
			return;

		if (wantEnabled)
		{
			RestoreTracker(cannon, track);
			return;
		}

		if (tracker.enabled && !track.CannonTrackerDisabledByOpt)
		{
			tracker.enabled = false;
			track.CannonTrackerDisabledByOpt = true;
		}
	}

	private static void RestoreTracker(SoundCannon cannon, ChunkSimTrackState track)
	{
		if (track == null || !track.CannonTrackerDisabledByOpt)
			return;

		Behaviour tracker = ResolveTracker(cannon, track);
		if (tracker)
			tracker.enabled = true;
		track.CannonTrackerDisabledByOpt = false;
	}

	private static Behaviour ResolveTracker(SoundCannon cannon, ChunkSimTrackState track)
	{
		if (track.CannonTracker)
			return track.CannonTracker;

		EnsureTrackerType();
		if (_trackerType == null || !cannon)
			return null;

		Behaviour tracker = cannon.GetComponent(_trackerType) as Behaviour;
		track.CannonTracker = tracker;
		return tracker;
	}

	private static void EnsureTrackerType()
	{
		if (_trackerResolved)
			return;
		_trackerResolved = true;

		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			_trackerType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaSoundCannonNetworkTrackerComponent");
			if (_trackerType != null)
				break;
		}
	}

	private static void EnsureTrackerFields()
	{
		EnsureTrackerType();
		if (_trackerType == null || _trackerChargingField != null)
			return;

		const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		_trackerChargingField = _trackerType.GetField("charging", inst);
		_trackerSpentField = _trackerType.GetField("spent", inst);
	}
}
