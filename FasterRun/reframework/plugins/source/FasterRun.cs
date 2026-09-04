using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 8cf293ad59fe706e911d926fa6df02125d82557f
// Source commit: 7ce573eb29737e02fdc5c0bc5e0a17db0eb854a7
public enum ModLogLevel
{
    Info,
    Warning,
    Error,
}

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

public abstract class ModBase
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
    private readonly string _configPath;
    private bool _configDirty;
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
        _configPath = GetConfigPath(modName);
        LoadConfig();
    }

    public string ModName { get; }

    public string ModVersion { get; }

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

    protected void DrawConfigUI()
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

            if (Hexa.NET.ImGui.ImGui.Button($"Reset to defaults##{ModName}.Config.Reset"))
            {
                foreach (var entry in _configEntries)
                {
                    entry.Reset();
                }

                MarkConfigDirty();
                SaveConfig();
            }
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

public sealed class FasterRun : ModBase
{
    private static readonly FasterRun Instance = new();
    private static ulong _enteringPlayerAction;
    private static ulong _modifiedAction;
    private static bool _originalOverrideEnabled;
    private static float _originalOverrideSpeed;

    private readonly ModConfig<float> _runSpeed;
    private readonly ModConfig<float> _dashSpeed;

    private FasterRun() : base("FasterRun", "1.0")
    {
        _runSpeed = AddFloatConfig(
            "Run speed",
            6.75f,
            0.1f,
            50.0f,
            "%.2f",
            key: "RunSpeed");
        _dashSpeed = AddFloatConfig(
            "Sprint speed",
            21.0f,
            0.1f,
            50.0f,
            "%.2f",
            key: "DashSpeed");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.InitializeMod();

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [PluginExitPoint]
    public static void OnUnload()
    {
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
            ApplySpeed(actionAddress);
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to configure player speed", exception);
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
                RestoreModifiedAction();
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to restore player speed", exception);
        }

        return PreHookResult.Continue;
    }

    private static void ApplySpeed(ulong actionAddress)
    {
        if (actionAddress == 0 || !ManagedObject.IsManagedObject(actionAddress))
        {
            return;
        }

        var actionObject = ManagedObject.ToManagedObject(actionAddress);
        var action = actionObject?.As<app.PlayerActionBase.cPlayerActionBase>();
        if (action is null)
        {
            return;
        }

        var playerEntityAddress =
            (API.GetManagedSingletonT<app.PlayerManager>()
                ?.getControllingPlayerInfo()
                ?.CharacterEntity as IProxyable)
            ?.GetAddress() ?? 0;
        if ((action.CharacterEntity as IProxyable)?.GetAddress() != playerEntityAddress ||
            playerEntityAddress == 0)
        {
            return;
        }

        var speed = Instance.GetConfiguredSpeed(actionObject, action);
        if (!speed.HasValue)
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
        action._OverrideMotionSpeed = speed.Value;
    }

    private float? GetConfiguredSpeed(
        ManagedObject actionObject,
        app.PlayerActionBase.cPlayerActionBase action)
    {
        var typeName = (actionObject as IObject)?.GetTypeDefinition()?.FullName;
        if (action._RuntimeMoveType == app.PlayerDef.MOVE_TYPE.DASH ||
            action._MoveType == app.PlayerDef.MOVE_TYPE.DASH ||
            typeName?.EndsWith(".cDashStart", StringComparison.Ordinal) == true)
        {
            return _dashSpeed.Value;
        }

        if (action._RuntimeMoveType == app.PlayerDef.MOVE_TYPE.RUN ||
            action._MoveType == app.PlayerDef.MOVE_TYPE.RUN ||
            typeName?.EndsWith(".cRunStart", StringComparison.Ordinal) == true)
        {
            return _runSpeed.Value;
        }

        return null;
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
            ?.As<app.PlayerActionBase.cPlayerActionBase>();
        if (action is null)
        {
            return;
        }

        action._UseOverrideMotionSpeed = _originalOverrideEnabled;
        action._OverrideMotionSpeed = _originalOverrideSpeed;
    }
}
