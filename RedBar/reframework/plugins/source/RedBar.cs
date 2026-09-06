using System;
using System.Threading;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 80a2903200cedb41bdff1539bacd6232e31eca02
// Source commit: bf9441cbd6d766904d046ceea6750b80212a516b
public enum ModLogLevel
{
    Info,
    Warning,
    Error,
}

public abstract class ModBase
{
    protected ModBase(string modName, string modVersion)
    {
        ModName = modName;
        ModVersion = modVersion;
    }

    public string ModName { get; }

    public string ModVersion { get; }

    protected void Log(string message, ModLogLevel level = ModLogLevel.Info)
    {
        var text = $"[{ModName} v{ModVersion}] {message}";
        switch (level)
        {
            case ModLogLevel.Info:
                API.LogInfo(text);
                break;

            case ModLogLevel.Warning:
                API.LogWarning(text);
                break;

            case ModLogLevel.Error:
                API.LogError(text);
                break;
        }
    }
}
// END copied source: Util/ModBase.cs

public sealed class RedBar : ModBase
{
    private const float HealthRedScale = 0.77f;
    private const float HealthGreenScale = 0.23f;
    private const float HealthBlueScale = 0.38f;
    private const float StaminaGreenScale = 3.20f;
    private const int MaxEnemyGaugeCount = 64;

    private static readonly RedBar Instance = new();

    private static REFrameworkNET.ValueType _healthColorStorage;
    private static REFrameworkNET.ValueType _staminaColorStorage;
    private static REFrameworkNET.ValueType _zeroOffsetStorage;
    private static via.Float4 _healthColor;
    private static via.Float4 _staminaColor;
    private static via.Float3 _zeroOffset;
    private static int _loadedLogged;
    private static int _disabled;
    private static int _runtimeErrorReported;

    private RedBar()
        : base("Red Bar", "1.1")
    {
    }

    [PluginEntryPoint]
    public static void Main()
    {
        if (!CreateColorBuffers())
        {
            Instance.Log("Could not create color buffers.", ModLogLevel.Error);
            Interlocked.Exchange(ref _disabled, 1);
            return;
        }

        Instance.Log("Loaded. Player and enemy health will be red; stamina will be green.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        _healthColor = null;
        _staminaColor = null;
        _zeroOffset = null;
        _healthColorStorage = null;
        _staminaColorStorage = null;
        _zeroOffsetStorage = null;
        _loadedLogged = 0;
        _disabled = 0;
        _runtimeErrorReported = 0;
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        if (Volatile.Read(ref _disabled) != 0)
        {
            return;
        }

        try
        {
            var guiManager = API.GetManagedSingletonT<app.GUIManager>();
            if (guiManager is null || guiManager.isVisibleGUIApp(app.GUIID.ID.UI010200))
            {
                return;
            }

            var applied = ApplyPlayerGaugeColors(guiManager);
            applied |= ApplyEnemyHealthColors(guiManager);

            if (applied && Interlocked.Exchange(ref _loadedLogged, 1) == 0)
            {
                Instance.Log("Applied colors to the native player and enemy HUD panels.");
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _runtimeErrorReported, 1) == 0)
            {
                Instance.Log($"HUD recoloring failed for a frame and will retry: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static bool CreateColorBuffers()
    {
        _healthColorStorage = API.GetTDB().FindType("via.Float4")?.CreateValueType();
        _staminaColorStorage = API.GetTDB().FindType("via.Float4")?.CreateValueType();
        _zeroOffsetStorage = API.GetTDB().FindType("via.Float3")?.CreateValueType();
        _healthColor = _healthColorStorage?.As<via.Float4>();
        _staminaColor = _staminaColorStorage?.As<via.Float4>();
        _zeroOffset = _zeroOffsetStorage?.As<via.Float3>();
        if (_healthColor is null || _staminaColor is null || _zeroOffset is null)
        {
            return false;
        }

        SetColor(
            _healthColor,
            HealthRedScale,
            HealthGreenScale,
            HealthBlueScale,
            1.0f);
        SetColor(
            _staminaColor,
            0.0f,
            StaminaGreenScale,
            0.0f,
            1.0f);
        _zeroOffset.x = 0.0f;
        _zeroOffset.y = 0.0f;
        _zeroOffset.z = 0.0f;
        return true;
    }

    private static bool ApplyPlayerGaugeColors(app.GUIManager guiManager)
    {
        if (!guiManager.isVisibleGUIApp(app.GUIID.ID.UI020206))
        {
            return false;
        }

        var rawHud = (guiManager as IObject)?.Call(
            "getGUI", (int)app.GUIID.ID.UI020206) as ManagedObject;
        var lifeGauge = rawHud?.As<app.GUI020206>()?.LifeGauge;
        var health = lifeGauge?._HpIncrease?._PanelGauge;
        var stamina = lifeGauge?._RikidoIncrease?._PanelGauge;
        if (!IsAlive(health) || !IsAlive(stamina))
        {
            return false;
        }

        ApplyColor(health, _healthColor, _zeroOffset);
        ApplyColor(stamina, _staminaColor, _zeroOffset);
        return true;
    }

    private static bool ApplyEnemyHealthColors(app.GUIManager guiManager)
    {
        var applied = false;
        if (guiManager.isVisibleGUIApp(app.GUIID.ID.UI020200))
        {
            var rawHud = (guiManager as IObject)?.Call(
                "getGUI", (int)app.GUIID.ID.UI020200) as ManagedObject;
            var controls = rawHud?.As<app.GUI020200>()?._GaugeControlArray;
            var count = Math.Clamp(app.GUI020200.MAX_GAUGE_NUM, 0, MaxEnemyGaugeCount);
            for (var index = 0; controls is not null && index < count; ++index)
            {
                var control = controls[index];
                if (!IsAlive(control))
                {
                    continue;
                }

                var health = control._HpGaugeIncrease?._PanelGauge;
                var stamina = control._RikidoGaugeIncrease?._PanelGauge;
                if (!IsAlive(health) || !IsAlive(stamina))
                {
                    continue;
                }

                ApplyColor(health, _healthColor, _zeroOffset);
                ApplyColor(stamina, _staminaColor, _zeroOffset);
                applied = true;
            }
        }

        if (guiManager.isVisibleGUIApp(app.GUIID.ID.UI020207))
        {
            var rawHud = (guiManager as IObject)?.Call(
                "getGUI", (int)app.GUIID.ID.UI020207) as ManagedObject;
            var hud = rawHud?.As<app.GUI020207>();
            var controls = hud?._GaugeControls;
            var count = Math.Clamp(hud?.MAX_GAUGE_NUM ?? 0, 0, MaxEnemyGaugeCount);
            for (var index = 0; controls is not null && index < count; ++index)
            {
                var control = controls[index];
                if (!IsAlive(control))
                {
                    continue;
                }

                var health = control._HpGaugeIncrease?._PanelGauge;
                var stamina = control._RikidoGaugeIncrease?._PanelGauge;
                if (!IsAlive(health) || !IsAlive(stamina))
                {
                    continue;
                }

                ApplyColor(health, _healthColor, _zeroOffset);
                ApplyColor(stamina, _staminaColor, _zeroOffset);
                applied = true;
            }
        }

        return applied;
    }

    private static void ApplyColor(
        via.gui.Control control,
        via.Float4 color,
        via.Float3 offset)
    {
        control.Saturation = 1.0f;
        control.UseColorScaleSrgb = false;
        control.ColorScale = color;
        control.ColorOffset = offset;
    }

    private static void SetColor(via.Float4 color, float red, float green, float blue, float alpha)
    {
        color.x = red;
        color.y = green;
        color.z = blue;
        color.w = alpha;
    }

    private static bool IsAlive(object proxy)
    {
        var address = (proxy as IProxyable)?.GetAddress() ?? 0;
        return address != 0 && ManagedObject.IsManagedObject(address);
    }
}
