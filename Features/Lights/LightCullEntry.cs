using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

[System.Flags]
internal enum LightCullFlags : byte
{
	None = 0,
	FxConverted = 1,
	Global = 2,
	LightItem = 4,
}

internal sealed class LightCullEntry
{
	internal Light2D Light;
	internal LightCullFlags Flags;

	internal LightCullEntry(Light2D light)
	{
		Light = light;
		Flags = LightCullRegistry.ComputeFlags(light);
	}
}
