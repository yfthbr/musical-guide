using Dalamud.Game.Config;

namespace MusicalGuide;

internal static unsafe class Debug
{
    public static void PrintDebug(Configuration configuration)
    {
#if DEBUG
        if (S.GameConfig.UiConfig.TryGetUInt("FPSCameraInterpolationType", out var value))
        {
            S.Log.Info($"FPSCameraInterpolationType: {value}");
        }
#endif
    }
}
