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

    private static readonly RedBar Instance = new();

    private static REFrameworkNET.ValueType _healthColorStorage;
    private static REFrameworkNET.ValueType _staminaColorStorage;
    private static REFrameworkNET.ValueType _zeroOffsetStorage;
    private static via.Float4 _healthColor;
    private static via.Float4 _staminaColor;
    private static via.Float3 _zeroOffset;
    private static int _loadedLogged;
    private static int _errorReported;

    private RedBar()
        : base("Red Bar", "1.0")
    {
    }

    [PluginEntryPoint]
    public static void Main()
    {
        if (!CreateColorBuffers())
        {
            Instance.Log("Could not create color buffers.", ModLogLevel.Error);
            Interlocked.Exchange(ref _errorReported, 1);
            return;
        }

        Instance.Log("Loaded. Health will be red and stamina will be green.");
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
        _errorReported = 0;
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        if (Volatile.Read(ref _errorReported) != 0)
        {
            return;
        }

        try
        {
            if (!TryGetGaugePanels(out var health, out var stamina))
            {
                return;
            }

            ApplyColor(health, _healthColor, _zeroOffset);
            ApplyColor(stamina, _staminaColor, _zeroOffset);

            if (Interlocked.Exchange(ref _loadedLogged, 1) == 0)
            {
                Instance.Log("Applied red health and green stamina colors to the native HUD panels.");
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"HUD recoloring stopped after an error: {exception}", ModLogLevel.Error);
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

    private static bool TryGetGaugePanels(
        out via.gui.Panel health,
        out via.gui.Panel stamina)
    {
        health = null;
        stamina = null;

        var guiManager = API.GetManagedSingletonT<app.GUIManager>();
        if (guiManager is null)
        {
            return false;
        }

        var loading = guiManager.isVisibleGUIApp(app.GUIID.ID.UI010200);
        var hudVisible = guiManager.isVisibleGUIApp(app.GUIID.ID.UI020206);
        if (loading || !hudVisible)
        {
            return false;
        }

        var rawHud = (guiManager as IObject)?.Call(
            "getGUI", (int)app.GUIID.ID.UI020206) as ManagedObject;
        var lifeGauge = rawHud?.As<app.GUI020206>()?.LifeGauge;
        health = lifeGauge?._HpIncrease?._PanelGauge;
        stamina = lifeGauge?._RikidoIncrease?._PanelGauge;
        return health is not null && stamina is not null;
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

}
