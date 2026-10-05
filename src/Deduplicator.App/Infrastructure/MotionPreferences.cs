using System.Runtime.InteropServices;

namespace Deduplicator.App.Infrastructure;

/// <summary>Уважает системную настройку Windows «Эффекты анимации».
/// На других платформах декоративные анимации остаются включёнными.</summary>
internal static class MotionPreferences
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static readonly bool SystemAnimationsEnabled = ReadAnimationsEnabled();
    private static bool _reduceMotion;

    public static bool AnimationsEnabled => SystemAnimationsEnabled && !_reduceMotion;
    public static event Action? Changed;

    public static void ApplyUserPreference(bool reduceMotion)
    {
        if (_reduceMotion == reduceMotion) return;
        _reduceMotion = reduceMotion;
        Changed?.Invoke();
    }

    private static bool ReadAnimationsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out bool enabled, 0) && enabled;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        [MarshalAs(UnmanagedType.Bool)] out bool pvParam,
        uint fWinIni);
}
