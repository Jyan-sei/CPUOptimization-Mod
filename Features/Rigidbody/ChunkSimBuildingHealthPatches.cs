using HarmonyLib;

namespace CPUOptimization.Features.ChunkSim;

[HarmonyPatch(typeof(Damageable), nameof(Damageable.Damage))]
internal static class DamageableBuildingHealthPatch
{
	static void Postfix(Damageable __instance)
	{
		if (!ChunkSimState.IsActive)
			return;
		if (Plugin.ChunkSimBuildingHealthEnabled == null || !Plugin.ChunkSimBuildingHealthEnabled.Value)
			return;

		BuildingEntity building = __instance.GetComponent<BuildingEntity>();
		if (building)
			ChunkSimBodySync.NotifyBuildingHealthChanged(building);
	}
}

[HarmonyPatch(typeof(Body), nameof(Body.Attack))]
internal static class BodyAttackBuildingHealthPatch
{
	static void Postfix()
	{
		if (ChunkSimState.IsActive
		    && (Plugin.ChunkSimBuildingHealthEnabled == null || Plugin.ChunkSimBuildingHealthEnabled.Value))
			ChunkSimBodySync.ProcessDyingBuildings();
	}
}
