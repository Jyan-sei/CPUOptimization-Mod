using UnityEngine;

namespace CPUOptimization.Features.Mp;

internal sealed class KrokMpPerfTickHost : MonoBehaviour
{
	private void Awake()
	{
		if (!GetComponent<ForceForMPSchedulerHost>())
			gameObject.AddComponent<ForceForMPSchedulerHost>();
	}

	private void Update()
	{
		KrokMpPerfBootstrap.TickRetry(Time.unscaledDeltaTime);
	}
}
