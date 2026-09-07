using System;
using System.Threading;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 25417359db8c70a84c6f557d62440b857d2d6419
// Source commit: 781ee109dd96a8780de91bf7b8c16d82c6ae9aaf
// I do this to avoid panicing users. Copying code everythere instead of publishing a DLL is indeed stupid, but users’ antivirus software is stupider.
// Module: Mod identity, logging, one-time error reporting, and managed-object helpers.
public enum ModLogLevel
{
    Info,
    Warning,
    Error,
}

public abstract partial class ModBase
{
    private int _errorReported;

    protected ModBase(string modName, string modVersion)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(modName);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(modVersion);
        if (modName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new System.ArgumentException("Mod name must be a valid file name.", nameof(modName));
        }

        ModName = modName;
        ModVersion = modVersion;
        InitializeOptionalFeatures();
    }

    partial void InitializeOptionalFeatures();

    public string ModName { get; }

    public string ModVersion { get; }

    protected static T GetManagedObject<T>(ulong address)
        where T : class
    {
        if (!REFrameworkNET.ManagedObject.IsManagedObject(address))
        {
            return null;
        }

        return REFrameworkNET.ManagedObject.ToManagedObject(address)?.As<T>();
    }

    protected static T GetHookArgument<T>(
        System.ReadOnlySpan<ulong> args,
        int index)
        where T : class =>
        index >= 0 && index < args.Length
            ? GetManagedObject<T>(args[index])
            : null;

    protected void LogErrorOnce(string operation, System.Exception exception)
    {
        if (System.Threading.Interlocked.Exchange(ref _errorReported, 1) == 0)
        {
            Log($"{operation}: {exception}", ModLogLevel.Error);
        }
    }

    protected void ResetErrorReporting() =>
        System.Threading.Volatile.Write(ref _errorReported, 0);

    protected void Log(string message, ModLogLevel level = ModLogLevel.Info)
    {
        var text = $"[{ModName} v{ModVersion}] {message}";
        switch (level)
        {
            case ModLogLevel.Info:
                REFrameworkNET.API.LogInfo(text);
                break;

            case ModLogLevel.Warning:
                REFrameworkNET.API.LogWarning(text);
                break;

            case ModLogLevel.Error:
                REFrameworkNET.API.LogError(text);
                break;
        }
    }
}
// END copied source: Util/ModBase.cs

// BEGIN copied source: Util/ModBase.Config.cs
// Source blob SHA-1: 16e66416e77f2925f58edf35b6a91f1f4f8a165b
// Source commit: 781ee109dd96a8780de91bf7b8c16d82c6ae9aaf
// Module: ModBase configuration, persistence, and ImGui helpers.
// Requires: Util/ModBase.cs from the same committed Git revision.
public delegate bool ModConfigRenderer<T>(string label, ref T value);

public interface IModConfigEntry
{
    string Key { get; }

    object SerializedValue { get; }

    void Draw(string id);

    void Reset();

    bool TryLoad(System.Text.Json.JsonElement value);
}

public sealed class ModConfig<T> : IModConfigEntry
{
    private readonly string _name;
    private readonly T _defaultValue;
    private readonly ModConfigRenderer<T> _renderer;
    private readonly System.Action _onChanged;

    internal ModConfig(
        string key,
        string name,
        T defaultValue,
        ModConfigRenderer<T> renderer,
        System.Action onChanged)
    {
        Key = key;
        _name = name;
        _defaultValue = defaultValue;
        _renderer = renderer;
        _onChanged = onChanged;
        Value = defaultValue;
    }

    public string Key { get; }

    public T Value { get; private set; }

    object IModConfigEntry.SerializedValue => Value;

    public void Draw(string id)
    {
        var value = Value;
        if (_renderer($"{_name}##{id}", ref value))
        {
            Value = value;
            _onChanged();
        }
    }

    public void Reset() => Value = _defaultValue;

    public bool TryLoad(System.Text.Json.JsonElement value)
    {
        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<T>(value);
            if (loaded is null)
            {
                return false;
            }

            Value = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public abstract partial class ModBase
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private readonly System.Collections.Generic.List<IModConfigEntry> _configEntries = new();
    private System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>
        _savedConfig = new(System.StringComparer.Ordinal);
    private string _configPath;
    private bool _configDirty;

    partial void InitializeOptionalFeatures()
    {
        _configPath = GetConfigPath(ModName);
        LoadConfig();
    }

    public string ConfigPath => _configPath;

    protected ModConfig<T> AddConfig<T>(
        string name,
        T defaultValue,
        ModConfigRenderer<T> renderer,
        string key = null)
    {
        key ??= name;
        System.ArgumentException.ThrowIfNullOrWhiteSpace(key);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(name);
        System.ArgumentNullException.ThrowIfNull(renderer);
        if (_configEntries.Exists(entry => entry.Key == key))
        {
            throw new System.ArgumentException($"Duplicate configuration key: {key}", nameof(key));
        }

        var entry = new ModConfig<T>(key, name, defaultValue, renderer, MarkConfigDirty);
        if (_savedConfig.TryGetValue(key, out var savedValue) && !entry.TryLoad(savedValue))
        {
            Log($"Ignoring incompatible configuration value '{key}'.", ModLogLevel.Warning);
        }

        _configEntries.Add(entry);
        return entry;
    }

    protected ModConfig<bool> AddBoolConfig(
        string name,
        bool defaultValue,
        string key = null) =>
        AddConfig(
            name,
            defaultValue,
            static (string label, ref bool value) =>
                Hexa.NET.ImGui.ImGui.Checkbox(label, ref value),
            key);

    protected ModConfig<int> AddIntConfig(
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        string format = "%d",
        string key = null) =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref int value) =>
                Hexa.NET.ImGui.ImGui.SliderInt(
                    label,
                    ref value,
                    minimum,
                    maximum,
                    format),
            key);

    protected ModConfig<float> AddFloatConfig(
        string name,
        float defaultValue,
        float minimum,
        float maximum,
        string format = "%.2f",
        string key = null) =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref float value) =>
                Hexa.NET.ImGui.ImGui.SliderFloat(
                    label,
                    ref value,
                    minimum,
                    maximum,
                    format),
            key);

    protected void InitializeMod()
    {
        SaveConfig();
        Log($"Loaded. Configuration: {_configPath}");
    }

    protected void UnloadMod()
    {
        if (_configDirty)
        {
            SaveConfig();
        }

        ResetErrorReporting();
    }

    protected static void DrawText(string text, bool disabled = false)
    {
        if (disabled)
        {
            Hexa.NET.ImGui.ImGui.TextDisabled(text);
        }
        else
        {
            Hexa.NET.ImGui.ImGui.TextWrapped(text);
        }
    }

    protected bool DrawButton(string label, string id) =>
        Hexa.NET.ImGui.ImGui.Button($"{label}##{ModName}.{id}");

    protected void DrawCollapsible(
        string label,
        string id,
        System.Action drawContent)
    {
        System.ArgumentNullException.ThrowIfNull(drawContent);
        if (!Hexa.NET.ImGui.ImGui.TreeNode($"{label}##{ModName}.{id}"))
        {
            return;
        }

        try
        {
            drawContent();
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.TreePop();
        }
    }

    protected void DrawConfigUI(System.Action drawAdditionalContent = null)
    {
        if (_configDirty && !Hexa.NET.ImGui.ImGui.IsAnyItemActive())
        {
            SaveConfig();
        }

        if (_configEntries.Count == 0 ||
            !Hexa.NET.ImGui.ImGui.TreeNode($"{ModName} v{ModVersion}"))
        {
            return;
        }

        try
        {
            for (var index = 0; index < _configEntries.Count; index++)
            {
                _configEntries[index].Draw($"{ModName}.Config.{index}");
            }

            if (DrawButton("reset settings", "Config.Reset"))
            {
                foreach (var entry in _configEntries)
                {
                    entry.Reset();
                }

                MarkConfigDirty();
                SaveConfig();
            }

            drawAdditionalContent?.Invoke();
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.TreePop();
        }
    }

    private static string GetConfigPath(string modName)
    {
        var pluginPath = REFrameworkNET.API.GetPluginDirectory(typeof(ModBase).Assembly);
        var directory = new System.IO.DirectoryInfo(
            pluginPath ?? System.Environment.CurrentDirectory);
        while (directory is not null &&
               !string.Equals(directory.Name, "reframework",
                   System.StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        var reframeworkPath = directory?.FullName ??
                              System.IO.Path.Combine(
                                  System.Environment.CurrentDirectory,
                                  "reframework");
        return System.IO.Path.Combine(reframeworkPath, "data", $"{modName}.json");
    }

    private void LoadConfig()
    {
        if (!System.IO.File.Exists(_configPath))
        {
            return;
        }

        try
        {
            _savedConfig = System.Text.Json.JsonSerializer.Deserialize<
                               System.Collections.Generic.Dictionary<
                                   string,
                                   System.Text.Json.JsonElement>>(
                System.IO.File.ReadAllText(_configPath),
                JsonOptions) ?? new(System.StringComparer.Ordinal);
        }
        catch (System.Exception exception)
        {
            Log($"Could not read configuration; defaults will be used: {exception.Message}",
                ModLogLevel.Warning);
        }
    }

    private void MarkConfigDirty() => _configDirty = true;

    private void SaveConfig()
    {
        var temporaryPath = $"{_configPath}.tmp";
        try
        {
            var values = new System.Collections.Generic.Dictionary<string, object>(
                System.StringComparer.Ordinal);
            foreach (var entry in _configEntries)
            {
                values[entry.Key] = entry.SerializedValue;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_configPath)!);
            System.IO.File.WriteAllText(
                temporaryPath,
                System.Text.Json.JsonSerializer.Serialize(values, JsonOptions));
            System.IO.File.Move(temporaryPath, _configPath, true);
            _configDirty = false;
        }
        catch (System.Exception exception)
        {
            try
            {
                System.IO.File.Delete(temporaryPath);
            }
            catch
            {
            }

            Log($"Could not save configuration: {exception}", ModLogLevel.Error);
        }
    }
}
// END copied source: Util/ModBase.Config.cs

public struct RedBarColor
{
    public RedBarColor(float red, float green, float blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    public float Red { get; set; }

    public float Green { get; set; }

    public float Blue { get; set; }
}

public sealed class RedBar : ModBase
{
    private const float MaxColorComponent = 4.0f;
    private const int MaxEnemyGaugeCount = 64;

    private static readonly RedBar Instance = new();

    private readonly ModConfig<RedBarColor> _healthColorSetting;
    private readonly ModConfig<RedBarColor> _staminaColorSetting;
    private readonly ModConfig<bool> _disableHealthTexture;
    private readonly ModConfig<bool> _disableStaminaTexture;

    private static REFrameworkNET.ValueType _healthColorStorage;
    private static REFrameworkNET.ValueType _staminaColorStorage;
    private static REFrameworkNET.ValueType _solidColorScaleStorage;
    private static REFrameworkNET.ValueType _zeroOffsetStorage;
    private static REFrameworkNET.ValueType _zeroAddColorStorage;
    private static REFrameworkNET.ValueType _healthAddColorStorage;
    private static REFrameworkNET.ValueType _staminaAddColorStorage;
    private static via.Float4 _healthColor;
    private static via.Float4 _staminaColor;
    private static via.Float4 _solidColorScale;
    private static via.Float3 _zeroOffset;
    private static via.Color _zeroAddColor;
    private static via.Color _healthAddColor;
    private static via.Color _staminaAddColor;
    private static int _loadedLogged;
    private static int _disabled;
    private static int _runtimeErrorReported;

    private RedBar()
        : base("Red Bar", "1.2")
    {
        _healthColorSetting = AddConfig(
            "health color",
            new RedBarColor(0.77f, 0.23f, 0.38f),
            DrawColor,
            "healthColor");
        _staminaColorSetting = AddConfig(
            "stamina color",
            new RedBarColor(0.0f, 3.20f, 0.0f),
            DrawColor,
            "staminaColor");
        _disableHealthTexture = AddBoolConfig(
            "disable health texture",
            false,
            "disableHealthTexture");
        _disableStaminaTexture = AddBoolConfig(
            "disable stamina texture",
            false,
            "disableStaminaTexture");
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

        RefreshColorBuffers();
        Instance.InitializeMod();
        Instance.Log("Loaded. Player and enemy gauge colors are configurable.");
    }

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI()
    {
        Instance.DrawConfigUI();
        if (Volatile.Read(ref _disabled) == 0)
        {
            RefreshColorBuffers();
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        Instance.UnloadMod();
        _healthColor = null;
        _staminaColor = null;
        _solidColorScale = null;
        _zeroOffset = null;
        _zeroAddColor = null;
        _healthAddColor = null;
        _staminaAddColor = null;
        _healthColorStorage = null;
        _staminaColorStorage = null;
        _solidColorScaleStorage = null;
        _zeroOffsetStorage = null;
        _zeroAddColorStorage = null;
        _healthAddColorStorage = null;
        _staminaAddColorStorage = null;
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

            var disableHealthTexture = Instance._disableHealthTexture.Value;
            var disableStaminaTexture = Instance._disableStaminaTexture.Value;
            var applied = ApplyPlayerGaugeColors(
                guiManager,
                disableHealthTexture,
                disableStaminaTexture);
            applied |= ApplyEnemyGaugeColors(
                guiManager,
                disableHealthTexture,
                disableStaminaTexture);

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
        _solidColorScaleStorage = API.GetTDB().FindType("via.Float4")?.CreateValueType();
        _zeroOffsetStorage = API.GetTDB().FindType("via.Float3")?.CreateValueType();
        _zeroAddColorStorage = API.GetTDB().FindType("via.Color")?.CreateValueType();
        _healthAddColorStorage = API.GetTDB().FindType("via.Color")?.CreateValueType();
        _staminaAddColorStorage = API.GetTDB().FindType("via.Color")?.CreateValueType();
        _healthColor = _healthColorStorage?.As<via.Float4>();
        _staminaColor = _staminaColorStorage?.As<via.Float4>();
        _solidColorScale = _solidColorScaleStorage?.As<via.Float4>();
        _zeroOffset = _zeroOffsetStorage?.As<via.Float3>();
        _zeroAddColor = _zeroAddColorStorage?.As<via.Color>();
        _healthAddColor = _healthAddColorStorage?.As<via.Color>();
        _staminaAddColor = _staminaAddColorStorage?.As<via.Color>();
        if (_healthColor is null || _staminaColor is null ||
            _solidColorScale is null || _zeroOffset is null ||
            _zeroAddColor is null || _healthAddColor is null || _staminaAddColor is null)
        {
            return false;
        }

        SetColor(_solidColorScale, 0.0f, 0.0f, 0.0f, 1.0f);
        _zeroOffset.x = 0.0f;
        _zeroOffset.y = 0.0f;
        _zeroOffset.z = 0.0f;
        _zeroAddColor.rgba = 0;
        return true;
    }

    private static void RefreshColorBuffers()
    {
        var health = Instance._healthColorSetting.Value;
        SetColor(_healthColor, health.Red, health.Green, health.Blue, 1.0f);
        SetAddColor(_healthAddColor, health);

        var stamina = Instance._staminaColorSetting.Value;
        SetColor(_staminaColor, stamina.Red, stamina.Green, stamina.Blue, 1.0f);
        SetAddColor(_staminaAddColor, stamina);
    }

    private static bool ApplyPlayerGaugeColors(
        app.GUIManager guiManager,
        bool disableHealthTexture,
        bool disableStaminaTexture)
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

        ApplyColor(health, _healthColor, _healthAddColor, disableHealthTexture);
        ApplyColor(stamina, _staminaColor, _staminaAddColor, disableStaminaTexture);
        return true;
    }

    private static bool ApplyEnemyGaugeColors(
        app.GUIManager guiManager,
        bool disableHealthTexture,
        bool disableStaminaTexture)
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

                ApplyColor(health, _healthColor, _healthAddColor, disableHealthTexture);
                ApplyColor(stamina, _staminaColor, _staminaAddColor, disableStaminaTexture);
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

                ApplyColor(health, _healthColor, _healthAddColor, disableHealthTexture);
                ApplyColor(stamina, _staminaColor, _staminaAddColor, disableStaminaTexture);
                applied = true;
            }
        }

        return applied;
    }

    private static void ApplyColor(
        via.gui.Control control,
        via.Float4 textureColor,
        via.Color solidColor,
        bool disableTexture)
    {
        control.Saturation = 1.0f;
        control.UseColorScaleSrgb = false;
        control.ColorScale = disableTexture ? _solidColorScale : textureColor;
        control.ColorOffset = _zeroOffset;
        control.AdjustAddColor = disableTexture ? solidColor : _zeroAddColor;
    }

    private static void SetColor(via.Float4 color, float red, float green, float blue, float alpha)
    {
        color.x = red;
        color.y = green;
        color.z = blue;
        color.w = alpha;
    }

    private static void SetAddColor(via.Color target, RedBarColor color)
    {
        var red = (uint)Math.Round(Math.Clamp(color.Red, 0.0f, 1.0f) * 255.0f);
        var green = (uint)Math.Round(Math.Clamp(color.Green, 0.0f, 1.0f) * 255.0f);
        var blue = (uint)Math.Round(Math.Clamp(color.Blue, 0.0f, 1.0f) * 255.0f);
        target.rgba = red | (green << 8) | (blue << 16);
    }

    private static bool DrawColor(string label, ref RedBarColor value)
    {
        var preview = new System.Numerics.Vector3(value.Red, value.Green, value.Blue);
        var flags = Hexa.NET.ImGui.ImGuiColorEditFlags.Float |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.Hdr |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.PickerHueBar |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.NoSidePreview |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.NoLabel;
        var idSeparator = label.IndexOf("##", StringComparison.Ordinal);
        var visibleLabel = idSeparator < 0 ? label : label[..idSeparator];
        Hexa.NET.ImGui.ImGui.TextUnformatted(visibleLabel);
        if (!Hexa.NET.ImGui.ImGui.ColorPicker3(label, ref preview, flags))
        {
            return false;
        }

        value = new RedBarColor(
            Math.Clamp(preview.X, 0.0f, MaxColorComponent),
            Math.Clamp(preview.Y, 0.0f, MaxColorComponent),
            Math.Clamp(preview.Z, 0.0f, MaxColorComponent));
        return true;
    }

    private static bool IsAlive(object proxy)
    {
        var address = (proxy as IProxyable)?.GetAddress() ?? 0;
        return address != 0 && ManagedObject.IsManagedObject(address);
    }
}
