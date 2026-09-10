namespace CPUOptimization;

internal static class CpuLog
{
	internal static void Info(string message) => Plugin.Log?.LogInfo(message);

	internal static void Warn(string message) => Plugin.Log?.LogWarning(message);

	internal static void Error(string message) => Plugin.Log?.LogError(message);
}
