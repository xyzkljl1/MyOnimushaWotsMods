using System;
using System.Threading;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

public static class AutoSucker
{
    // The game stops the force-absorption timer early once no souls remain.
    // A short renewable duration prevents the forced state from surviving a scene change.
    private const float ForceAbsorptionDuration = 1.0f;
    private const float EmptySoulGracePeriod = 0.15f;
    private const long ScanIntervalMilliseconds = 300;

    private static int _errorReported;
    private static long _nextScanTick;

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[AutoSucker] Loaded.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        _errorReported = 0;
        _nextScanTick = 0;
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        var now = Environment.TickCount64;
        if (now < _nextScanTick)
        {
            return;
        }

        _nextScanTick = now + ScanIntervalMilliseconds;

        try
        {
            var soulManager = API.GetManagedSingletonT<app.SoulManager>();
            var supporter = app.SoulManager.getPlayerSoulAbsorptionSupporter();
            var forceTimer = supporter?._ForceAbsorbeTimer;

            if (soulManager is null || supporter is null || forceTimer is null)
            {
                return;
            }

            // Do not restart an active native request or interfere with manual absorption.
            if (forceTimer.Enabled || supporter.IsAbsorption)
            {
                return;
            }

            if (!HasAutoAbsorbableSoul(soulManager))
            {
                return;
            }

            // The native force-absorption state machine decides whether its
            // action can run in the current player state. The auto-type query
            // remains false until that state is entered, so it is not a valid
            // precondition for starting the request.
            supporter.requestForceAbsorbe(ForceAbsorptionDuration, EmptySoulGracePeriod);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                API.LogError($"[AutoSucker] Update failed: {exception}");
            }
        }
    }

    private static bool HasAutoAbsorbableSoul(app.SoulManager soulManager)
    {
        var activeSouls = soulManager._ActiveSoulList;
        if (activeSouls is null || activeSouls.Count == 0)
        {
            return false;
        }

        foreach (var soul in activeSouls)
        {
            if (soul is null)
            {
                continue;
            }

            var soulType = soul.SoulType;
            if (soulType == app.SoulDef.ID_Fixed.Red ||
                soulType == app.SoulDef.ID_Fixed.Yellow ||
                soulType == app.SoulDef.ID_Fixed.Blue ||
                soulType == app.SoulDef.ID_Fixed.Purple)
            {
                return true;
            }
        }

        return false;
    }
}
