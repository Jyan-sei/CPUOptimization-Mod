using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// stock ElderThornbackBehaviour.OnDestroy touches PlayerCamera.main.body and build
/// with no null checks. stream cull / pre-Start destroy NREs.
/// </summary>
internal static class ElderThornbackPatches
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(ElderThornbackBehaviour), "OnDestroy"),
			prefix: new HarmonyMethod(typeof(ElderThornbackPatches), nameof(OnDestroyPrefix)));
	}

	private static bool OnDestroyPrefix(ElderThornbackBehaviour __instance)
	{
		if (__instance == null)
			return false;

		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || cam.body == null)
			return false;

		Body body = cam.body;
		BuildingEntity build = FieldAccess.Get<BuildingEntity>(__instance, "build")
			?? __instance.GetComponent<BuildingEntity>();
		if (build == null)
			return false;

		body.horrifiedLevel = 0f;
		if (build.health > 0f)
			return false;

		float maxDist = ElderThornbackBehaviour.maxDistance;
		if (Vector2.Distance(__instance.transform.position, body.transform.position) >= maxDist)
			return false;

		body.happiness += 40f;
		body.caffeinated += 600f;
		if (MusicManager.main != null)
		{
			MusicManager.main.StopDrone(15);
			MusicManager.main.StopSong();
		}
		Sound.Play("music/thornbackDefeat", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
		if (cam != null)
			cam.threatMusicTime = 0f;
		return false;
	}
}
