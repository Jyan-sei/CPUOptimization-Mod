using UnityEngine;

namespace CPUOptimization.Features.Mp;

internal sealed class ForceForMPSchedulerHost : MonoBehaviour
{
	private void Update()
	{
		ForceForMPScheduler.Tick();
	}
}
