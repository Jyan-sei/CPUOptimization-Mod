using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

// throttled item decay - mirrors item.handledecay with explicit deltatime
internal static class ItemDecayHelper
{
	internal const float IntervalSeconds = 0.05f;

	internal static void ApplyDecay(Item item, float deltaTime)
	{
		if (!item || deltaTime <= 0f)
			return;

		float mult = item.decayMultiplier * (item.isWet ? 6f : 1f) * WorldGeneration.globalDecayRate;
		if ((item.Stats.decayInfo & 1) != 0 && item.container.itemCount == 0)
			mult *= 0f;

		if ((item.Stats.decayInfo & 2) != 0 && (!item.transform.parent || item.ParentContainer()))
			mult *= 0f;

		if ((item.Stats.decayInfo & 4) != 0
		    && (!item.transform.parent || PlayerCamera.main.body.rb.velocity.magnitude < 0.5f))
			mult *= 0f;

		if ((item.Stats.decayInfo & 0x10) != 0)
		{
			float scale = item.battery.preset == BatteryItem.BatteryPreset.Large ? 3f
				: item.battery.preset == BatteryItem.BatteryPreset.Medium ? 1f
				: 0.5f;
			item.battery.DrainCharge(item.Stats.rotSpeed * 0.01f * scale * mult * deltaTime);
		}
		else
		{
			item.condition -= item.Stats.rotSpeed * mult * deltaTime * 0.01f;
		}

		item.condition = Mathf.Clamp01(item.condition);
	}
}
