using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// A long frame owes one physics step per 20 ms, and Unity will run that many
/// FixedUpdates on the next frame up to maximumDeltaTime. On a slow machine
/// those extra steps make the next frame long too. Five steps is enough for a
/// normal slow frame. Anything older is dropped instead of replayed.
/// </summary>
internal static class PhysicsStepCap
{
	const int MaxSteps = 5;
	static float _saved = -1f;

	internal static void Tick()
	{
		float step = Time.fixedDeltaTime;
		if (step <= 0f)
			step = 0.02f;
		float cap = step * MaxSteps;
		if (_saved < 0f)
			_saved = Time.maximumDeltaTime;
		if (Time.maximumDeltaTime > cap)
			Time.maximumDeltaTime = cap;
	}

	internal static void Restore()
	{
		if (_saved < 0f)
			return;
		Time.maximumDeltaTime = _saved;
		_saved = -1f;
	}
}
