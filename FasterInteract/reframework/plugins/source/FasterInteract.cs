using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 25417359db8c70a84c6f557d62440b857d2d6419
// Source commit: 7eadaa1411ca922a2fbbf34f067928275e4c53ec
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
// Source blob SHA-1: 23b9c5bcce310f6c969aa06b2652f0cc75136b72
// Source commit: 7eadaa1411ca922a2fbbf34f067928275e4c53ec
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

    protected ModConfig<int> AddRadioGroupConfig(
        string name,
        int defaultValue,
        string[] options,
        bool sameLine = true,
        string key = null)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        if (options.Length == 0)
        {
            throw new System.ArgumentException(
                "A radio group must contain at least one option.",
                nameof(options));
        }

        if (defaultValue < 0 || defaultValue >= options.Length)
        {
            throw new System.ArgumentOutOfRangeException(nameof(defaultValue));
        }

        var labels = (string[])options.Clone();
        for (var index = 0; index < labels.Length; index++)
        {
            System.ArgumentException.ThrowIfNullOrWhiteSpace(labels[index]);
        }

        return AddConfig(
            name,
            defaultValue,
            (string label, ref int value) =>
                DrawRadioGroup(label, ref value, labels, sameLine),
            key);
    }

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

    protected ModConfig<float> AddPixelInputConfig(
        string name,
        float defaultValue,
        float minimum,
        float maximum,
        string key = null)
    {
        if (!float.IsFinite(defaultValue) || !float.IsFinite(minimum) ||
            !float.IsFinite(maximum) || minimum > maximum ||
            defaultValue < minimum || defaultValue > maximum)
        {
            throw new System.ArgumentOutOfRangeException(nameof(defaultValue));
        }

        return AddConfig(
            name,
            System.MathF.Round(defaultValue),
            (string label, ref float value) =>
                DrawPixelInput(label, ref value, minimum, maximum),
            key);
    }

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

    private static bool DrawRadioGroup(
        string label,
        ref int value,
        string[] options,
        bool sameLine)
    {
        var separator = label.IndexOf("##", System.StringComparison.Ordinal);
        var name = separator >= 0 ? label[..separator] : label;
        var id = separator >= 0 ? label[(separator + 2)..] : label;
        Hexa.NET.ImGui.ImGui.TextUnformatted($"{name}:");
        Hexa.NET.ImGui.ImGui.SameLine();

        var changed = false;
        var normalized = System.Math.Clamp(value, 0, options.Length - 1);
        if (normalized != value)
        {
            value = normalized;
            changed = true;
        }

        for (var index = 0; index < options.Length; index++)
        {
            if (sameLine && index > 0)
            {
                Hexa.NET.ImGui.ImGui.SameLine();
            }

            if (Hexa.NET.ImGui.ImGui.RadioButton(
                    $"{options[index]}##{id}.Radio.{index}",
                    value == index))
            {
                value = index;
                changed = true;
            }
        }

        return changed;
    }

    private static bool DrawPixelInput(
        string label,
        ref float value,
        float minimum,
        float maximum)
    {
        var original = value;
        if (!float.IsFinite(value))
        {
            value = minimum;
        }

        var changed = Hexa.NET.ImGui.ImGui.InputFloat(
            label,
            ref value,
            1.0f,
            100.0f,
            "%.0f");
        value = System.Math.Clamp(System.MathF.Round(value), minimum, maximum);
        return changed || value != original;
    }

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

public sealed class FasterInteract : ModBase
{
    private static readonly FasterInteract Instance = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, float>
        OriginalPlayerLayerSpeeds = new();
    [ThreadStatic]
    private static ulong _enteringPlayerAction;
    private static ulong _modifiedAction;
    private static bool _originalOverrideEnabled;
    private static float _originalOverrideSpeed;

    private readonly ModConfig<float> _oniWallSpeed;

    private FasterInteract() : base("FasterInteract", "1.0")
    {
        _oniWallSpeed = AddFloatConfig(
            "Oni wall interaction speed",
            3.0f,
            1.0f,
            10.0f,
            "%.1fx",
            key: "OniWallSpeed");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.InitializeMod();

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [PluginExitPoint]
    public static void OnUnload()
    {
        RestorePlayerLayerSpeeds();
        RestoreModifiedAction();
        _enteringPlayerAction = 0;
        Instance.UnloadMod();
    }

    [MethodHook(
        typeof(app.PlayerActionBase.cPlayerActionBase),
        "doEnter",
        MethodHookType.Pre)]
    public static PreHookResult BeforePlayerActionEnter(Span<ulong> args)
    {
        _enteringPlayerAction = args.Length > 1 ? args[1] : 0;
        try
        {
            ApplyPlayerActionSpeed(_enteringPlayerAction);
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to prepare Oni wall interaction speed", exception);
        }

        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.PlayerActionBase.cPlayerActionBase),
        "doEnter",
        MethodHookType.Post)]
    public static void AfterPlayerActionEnter(ref ulong returnValue)
    {
        var actionAddress = _enteringPlayerAction;
        _enteringPlayerAction = 0;

        try
        {
            ApplyPlayerActionSpeed(actionAddress);
            if (actionAddress == _modifiedAction)
            {
                ApplyPlayerLayerSpeeds();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to accelerate Oni wall interaction", exception);
        }
    }

    [MethodHook(
        typeof(app.PlayerActionBase.cPlayerActionBase),
        "doExit",
        MethodHookType.Pre)]
    public static PreHookResult BeforePlayerActionExit(Span<ulong> args)
    {
        try
        {
            if (args.Length > 1 && args[1] == _modifiedAction)
            {
                RestorePlayerLayerSpeeds();
                RestoreModifiedAction();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to restore interaction speed", exception);
        }

        return PreHookResult.Continue;
    }

    private static void ApplyPlayerActionSpeed(ulong actionAddress)
    {
        if (actionAddress == 0 || !ManagedObject.IsManagedObject(actionAddress))
        {
            return;
        }

        var actionObject = ManagedObject.ToManagedObject(actionAddress);
        var action = actionObject
            ?.As<app.PlayerActionBase.cPlayerActionBase>();
        var playerAddress =
            (API.GetManagedSingletonT<app.PlayerManager>()
                ?.getControllingPlayerInfo()
                ?.CharacterEntity as IProxyable)
            ?.GetAddress() ?? 0;
        if (action is null ||
            playerAddress == 0 ||
            (action.CharacterEntity as IProxyable)?.GetAddress() != playerAddress)
        {
            return;
        }

        var typeName = (actionObject as IObject)
            ?.GetTypeDefinition()?.FullName;
        if (!IsDemonTendonAction(typeName))
        {
            return;
        }

        if (_modifiedAction != actionAddress)
        {
            RestoreModifiedAction();
            _modifiedAction = actionAddress;
            _originalOverrideEnabled = action._UseOverrideMotionSpeed;
            _originalOverrideSpeed = action._OverrideMotionSpeed;
        }

        action._UseOverrideMotionSpeed = true;
        action._OverrideMotionSpeed =
            MathF.Max(1.0f, Instance._oniWallSpeed.Value);
    }

    private static bool IsDemonTendonAction(string typeName) =>
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionStart" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionAction" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionEnd" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruption";

    private static void ApplyPlayerLayerSpeeds()
    {
        var motion = API.GetManagedSingletonT<app.PlayerManager>()
            ?.getControllingPlayerInfo()?.Character?.Motion?._Params?.MotionComponent;
        if (motion is null)
        {
            return;
        }

        ApplyLayerSpeeds(motion, motion.getLayerCount(), isPrivate: false);
        ApplyLayerSpeeds(motion, motion.getPrivateLayerCount(), isPrivate: true);
    }

    private static void ApplyLayerSpeeds(
        via.motion.Motion motion,
        uint count,
        bool isPrivate)
    {
        var multiplier = MathF.Max(1.0f, Instance._oniWallSpeed.Value);
        for (uint index = 0; index < Math.Min(count, 64u); index++)
        {
            var layer = isPrivate
                ? motion.getPrivateLayer(index)
                : motion.getLayer(index);
            var address = (layer as IProxyable)?.GetAddress() ?? 0;
            if (address == 0 || OriginalPlayerLayerSpeeds.ContainsKey(address))
            {
                continue;
            }

            var originalSpeed = layer.Speed;
            OriginalPlayerLayerSpeeds.Add(address, originalSpeed);
            if (originalSpeed > 0.0f)
            {
                layer.Speed = originalSpeed * multiplier;
            }
        }
    }

    private static void RestorePlayerLayerSpeeds()
    {
        foreach (var (address, speed) in OriginalPlayerLayerSpeeds)
        {
            if (!ManagedObject.IsManagedObject(address))
            {
                continue;
            }

            var layer = ManagedObject.ToManagedObject(address)
                ?.As<via.motion.TreeLayer>();
            if (layer is not null)
            {
                layer.Speed = speed;
            }
        }

        OriginalPlayerLayerSpeeds.Clear();
    }

    private static void RestoreModifiedAction()
    {
        var actionAddress = _modifiedAction;
        _modifiedAction = 0;
        if (!ManagedObject.IsManagedObject(actionAddress))
        {
            return;
        }

        var action = ManagedObject.ToManagedObject(actionAddress)
            ?.As<app.PlayerCommonAction.cInteractGimmickBase>();
        if (action is null)
        {
            return;
        }

        action._UseOverrideMotionSpeed = _originalOverrideEnabled;
        action._OverrideMotionSpeed = _originalOverrideSpeed;
    }
}
