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
    private enum InteractionFeature
    {
        None,
        OniWall,
        Door,
    }

    private static readonly FasterInteract Instance = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, float>
        OriginalPlayerLayerSpeeds = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, float>
        OriginalDoorLayerSpeeds = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, float>
        LastElevatorPositions = new();
    [ThreadStatic]
    private static ulong _enteringPlayerAction;
    [ThreadStatic]
    private static ulong _updatingElevator;
    [ThreadStatic]
    private static float _originalElevatorMoveSpeed;
    private static ulong _modifiedAction;
    private static ulong _acceleratedDoor;
    private static InteractionFeature _activeFeature;
    private static bool _originalOverrideEnabled;
    private static float _originalOverrideSpeed;

    private readonly ModConfig<float> _interactionSpeed;
    private readonly ModConfig<bool> _oniWallEnabled;
    private readonly ModConfig<bool> _doorEnabled;
    private readonly ModConfig<bool> _elevatorDescentEnabled;

    private FasterInteract() : base("FasterInteract", "1.0")
    {
        _interactionSpeed = AddFloatConfig(
            "Interaction speed",
            3.0f,
            1.0f,
            10.0f,
            "%.1fx",
            key: "InteractionSpeed");
        _oniWallEnabled = AddBoolConfig(
            "Enable Oni wall acceleration",
            true,
            key: "OniWallEnabled");
        _doorEnabled = AddBoolConfig(
            "Enable door acceleration",
            true,
            key: "DoorEnabled");
        _elevatorDescentEnabled = AddBoolConfig(
            "Enable elevator descent acceleration",
            true,
            key: "ElevatorDescentEnabled");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.InitializeMod();

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [PluginExitPoint]
    public static void OnUnload()
    {
        RestoreActiveInteraction();
        RestoreDoorLayerSpeeds();
        LastElevatorPositions.Clear();
        _enteringPlayerAction = 0;
        _updatingElevator = 0;
        Instance.UnloadMod();
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        if (_modifiedAction == 0)
        {
            return;
        }

        try
        {
            var character = API.GetManagedSingletonT<app.PlayerManager>()
                ?.getControllingPlayerInfo()?.Character;
            var baseAction = (character?.BaseCurrentAction as IProxyable)
                ?.GetAddress() ?? 0;
            var subAction = (character?.SubCurrentAction as IProxyable)
                ?.GetAddress() ?? 0;
            if (_modifiedAction != baseAction && _modifiedAction != subAction)
            {
                RestoreActiveInteraction();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to monitor interaction exit", exception);
        }
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
            ApplyPlayerActionSpeed(
                _enteringPlayerAction,
                applyMotionLayers: false);
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to prepare interaction speed", exception);
        }

        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.GimmickDoor), "doUpdateBegin", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorUpdate(Span<ulong> args)
    {
        try
        {
            if (args.Length > 1 && args[1] == _acceleratedDoor)
            {
                UpdateDoorSpeed(args[1]);
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to update door speed", exception);
        }

        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.GimmickElevator),
        "updateMoveState",
        MethodHookType.Pre)]
    public static PreHookResult BeforeElevatorMoveUpdate(Span<ulong> args)
    {
        _updatingElevator = 0;
        try
        {
            if (!Instance._elevatorDescentEnabled.Value || args.Length <= 1)
            {
                return PreHookResult.Continue;
            }

            var elevator = GetManagedObject<app.GimmickElevator>(args[1]);
            var transform = elevator?.GameObject?.Transform;
            if (elevator is null ||
                transform is null ||
                elevator.MoveState != app.GimmickElevator.MOVE_STATE.MOVE)
            {
                return PreHookResult.Continue;
            }

            var address = args[1];
            var positionY = transform.Position.y;
            var isDescending = LastElevatorPositions.TryGetValue(
                address,
                out var previousY) &&
                positionY < previousY - 0.0001f;
            LastElevatorPositions[address] = positionY;
            if (isDescending)
            {
                _updatingElevator = address;
                _originalElevatorMoveSpeed = elevator._MoveSpeed;
                elevator._MoveSpeed =
                    _originalElevatorMoveSpeed * GetInteractionSpeed();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to accelerate elevator descent", exception);
        }

        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.GimmickElevator),
        "updateMoveState",
        MethodHookType.Post)]
    public static void AfterElevatorMoveUpdate(ref ulong returnValue)
    {
        var elevatorAddress = _updatingElevator;
        _updatingElevator = 0;
        if (elevatorAddress == 0)
        {
            return;
        }

        try
        {
            var elevator = GetManagedObject<app.GimmickElevator>(elevatorAddress);
            if (elevator is not null)
            {
                elevator._MoveSpeed = _originalElevatorMoveSpeed;
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to restore elevator speed", exception);
        }
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
            ApplyPlayerActionSpeed(actionAddress, applyMotionLayers: true);
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to accelerate interaction", exception);
        }
    }

    [MethodHook(
        typeof(app.PlayerCommonAction.cSimpleInteractGimmickBase),
        "detailUpdate",
        MethodHookType.Pre)]
    public static PreHookResult BeforeSimpleInteractionUpdate(Span<ulong> args)
    {
        try
        {
            if (args.Length > 1)
            {
                ApplyPlayerActionSpeed(args[1], applyMotionLayers: true);
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to update interaction speed", exception);
        }

        return PreHookResult.Continue;
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
                RestoreActiveInteraction();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to restore interaction speed", exception);
        }

        return PreHookResult.Continue;
    }

    private static void ApplyPlayerActionSpeed(
        ulong actionAddress,
        bool applyMotionLayers)
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

        var gimmickAddress = 0ul;
        var feature = actionAddress == _modifiedAction
            ? _activeFeature
            : GetInteractionFeature(actionObject, out gimmickAddress);
        if (!IsFeatureEnabled(feature))
        {
            if (actionAddress == _modifiedAction)
            {
                RestoreActiveInteraction();
            }

            return;
        }

        if (_modifiedAction != actionAddress)
        {
            RestoreActiveInteraction();
            _modifiedAction = actionAddress;
            _activeFeature = feature;
            _originalOverrideEnabled = action._UseOverrideMotionSpeed;
            _originalOverrideSpeed = action._OverrideMotionSpeed;
            if (feature == InteractionFeature.Door)
            {
                BeginDoorAcceleration(gimmickAddress);
            }
        }

        var speed = GetInteractionSpeed();
        action._UseOverrideMotionSpeed = true;
        action._OverrideMotionSpeed = speed;
        if (applyMotionLayers)
        {
            ApplyPlayerLayerSpeeds(speed);
            if (feature == InteractionFeature.Door)
            {
                ApplyDoorLayerSpeeds(_acceleratedDoor, speed);
            }
        }
    }

    private static InteractionFeature GetInteractionFeature(
        ManagedObject actionObject,
        out ulong gimmickAddress)
    {
        gimmickAddress = 0;
        var typeName = (actionObject as IObject)
            ?.GetTypeDefinition()?.FullName;
        if (IsDemonTendonAction(typeName))
        {
            return Instance._oniWallEnabled.Value
                ? InteractionFeature.OniWall
                : InteractionFeature.None;
        }

        if (!Instance._doorEnabled.Value)
        {
            return InteractionFeature.None;
        }

        var action = actionObject
            ?.TryAs<app.PlayerCommonAction.cInteractGimmickBase>();
        if (action is null)
        {
            return InteractionFeature.None;
        }

        gimmickAddress = GetActionGimmickAddress(actionObject, action);
        return GetManagedObject<app.GimmickDoor>(gimmickAddress) is not null
            ? InteractionFeature.Door
            : InteractionFeature.None;
    }

    private static float GetInteractionSpeed() =>
        MathF.Max(1.0f, Instance._interactionSpeed.Value);

    private static bool IsFeatureEnabled(InteractionFeature feature) =>
        feature switch
        {
            InteractionFeature.OniWall => Instance._oniWallEnabled.Value,
            InteractionFeature.Door => Instance._doorEnabled.Value,
            _ => false,
        };

    private static bool IsDemonTendonAction(string typeName) =>
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionStart" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionAction" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruptionEnd" ||
        typeName == "app.PlayerBasicAction.cDemonTendonInterruption";

    private static ulong GetActionGimmickAddress(
        ManagedObject actionObject,
        app.PlayerCommonAction.cInteractGimmickBase action)
    {
        var address = (action.ActionGimmick as IProxyable)?.GetAddress() ?? 0;
        if (address != 0)
        {
            return address;
        }

        var rawGimmick = (actionObject as IObject)
            ?.GetField("_ActionGimmick") as ManagedObject;
        address = (rawGimmick as IProxyable)?.GetAddress() ?? 0;
        if (address != 0)
        {
            return address;
        }

        return 0;
    }

    private static void ApplyPlayerLayerSpeeds(float multiplier)
    {
        var motion = API.GetManagedSingletonT<app.PlayerManager>()
            ?.getControllingPlayerInfo()?.Character?.Motion?._Params?.MotionComponent;
        if (motion is null)
        {
            return;
        }

        ApplyLayerSpeeds(
            motion,
            motion.getLayerCount(),
            isPrivate: false,
            multiplier,
            OriginalPlayerLayerSpeeds);
        ApplyLayerSpeeds(
            motion,
            motion.getPrivateLayerCount(),
            isPrivate: true,
            multiplier,
            OriginalPlayerLayerSpeeds);
    }

    private static void ApplyLayerSpeeds(
        via.motion.Motion motion,
        uint count,
        bool isPrivate,
        float multiplier,
        System.Collections.Generic.Dictionary<ulong, float> originalSpeeds)
    {
        for (uint index = 0; index < Math.Min(count, 64u); index++)
        {
            var layer = isPrivate
                ? motion.getPrivateLayer(index)
                : motion.getLayer(index);
            var address = (layer as IProxyable)?.GetAddress() ?? 0;
            if (address == 0)
            {
                continue;
            }

            if (!originalSpeeds.TryGetValue(
                    address,
                    out var originalSpeed))
            {
                originalSpeed = layer.Speed;
                originalSpeeds.Add(address, originalSpeed);
            }

            if (originalSpeed > 0.0f)
            {
                layer.Speed = originalSpeed * multiplier;
            }
        }
    }

    private static void RestorePlayerLayerSpeeds()
    {
        RestoreLayerSpeeds(OriginalPlayerLayerSpeeds);
    }

    private static void BeginDoorAcceleration(ulong doorAddress)
    {
        if (doorAddress == 0 || doorAddress == _acceleratedDoor)
        {
            return;
        }

        RestoreDoorLayerSpeeds();
        _acceleratedDoor = doorAddress;
    }

    private static void UpdateDoorSpeed(ulong doorAddress)
    {
        var door = GetManagedObject<app.GimmickDoor>(doorAddress);
        if (door is null ||
            !Instance._doorEnabled.Value ||
            (_activeFeature != InteractionFeature.Door &&
             door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.OPENING))
        {
            RestoreDoorLayerSpeeds();
            return;
        }

        ApplyDoorLayerSpeeds(doorAddress, GetInteractionSpeed());
    }

    private static void ApplyDoorLayerSpeeds(
        ulong doorAddress,
        float multiplier)
    {
        var doorObject = ManagedObject.IsManagedObject(doorAddress)
            ? ManagedObject.ToManagedObject(doorAddress)
            : null;
        var mcMotion = (doorObject as IObject)
            ?.GetField("_McMotion") as ManagedObject;
        var motionObject = (mcMotion as IObject)
            ?.GetField("_Motion") as ManagedObject;
        var motion = motionObject?.As<via.motion.Motion>();
        if (motion is null)
        {
            return;
        }

        ApplyLayerSpeeds(
            motion,
            motion.getLayerCount(),
            isPrivate: false,
            multiplier,
            OriginalDoorLayerSpeeds);
        ApplyLayerSpeeds(
            motion,
            motion.getPrivateLayerCount(),
            isPrivate: true,
            multiplier,
            OriginalDoorLayerSpeeds);
    }

    private static void RestoreDoorLayerSpeeds()
    {
        RestoreLayerSpeeds(OriginalDoorLayerSpeeds);
        _acceleratedDoor = 0;
    }

    private static void RestoreLayerSpeeds(
        System.Collections.Generic.Dictionary<ulong, float> originalSpeeds)
    {
        foreach (var (address, speed) in originalSpeeds)
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

        originalSpeeds.Clear();
    }

    private static void RestoreActiveInteraction()
    {
        RestorePlayerLayerSpeeds();
        RestoreModifiedAction();
        _activeFeature = InteractionFeature.None;
    }

    private static void RestoreModifiedAction()
    {
        var actionAddress = _modifiedAction;
        _modifiedAction = 0;
        if (!ManagedObject.IsManagedObject(actionAddress))
        {
            return;
        }

        var action = GetManagedObject<app.PlayerActionBase.cPlayerActionBase>(
            actionAddress);
        if (action is null)
        {
            return;
        }

        action._UseOverrideMotionSpeed = _originalOverrideEnabled;
        action._OverrideMotionSpeed = _originalOverrideSpeed;
    }
}
