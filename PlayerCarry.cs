using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// worn and pocketed items live under a Body. host and remote players are the same.
/// stream teardown used to spare only PlayerCamera.main.body.
/// </summary>
internal static class PlayerCarry
{
	internal static bool Holds(Transform node)
	{
		for (Transform cur = node; cur != null; cur = cur.parent)
		{
			if (cur.GetComponent<Body>() != null)
				return true;
		}
		return false;
	}
}
