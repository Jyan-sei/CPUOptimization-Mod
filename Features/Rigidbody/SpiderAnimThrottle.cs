using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class SpiderAnimThrottle
{
	internal static bool IsEnabled =>
		Plugin.SpiderAnimThrottleEnabled != null && Plugin.SpiderAnimThrottleEnabled.Value;

	internal static bool ShouldRunFullAnim(SpiderHandler spider)
	{
		if (!IsEnabled || !spider)
			return true;

		int interval = Plugin.SpiderAnimThrottleFrames?.Value ?? 2;
		if (interval <= 1)
			return true;

		if (spider.GetComponent<ElderThornbackBehaviour>())
			return true;
		if (spider.stunTime > 0f)
			return true;
		if (spider.moveTime <= 0.05f)
			return true;

		BuildingEntity bld = spider.GetComponent<BuildingEntity>();
		if (bld && spider.retreatHealth > 0f && bld.health <= spider.retreatHealth)
			return true;

		return Time.frameCount % interval == 0;
	}
}
