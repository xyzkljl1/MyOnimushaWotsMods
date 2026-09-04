using System;
using System.Threading;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: ce4609bf43b16500480561fe072e0baef79c1971
// Source commit: 489fb2e3ca2afef1660ac3a3f11ff6fe2665e7f3
public enum ModLogLevel
{
    Info,
    Warning,
    Error,
}

public delegate bool ModConfigRenderer<T>(string label, ref T value);

public interface IModConfigEntry
{
    void Draw(string id);
}

public sealed class ModConfig<T> : IModConfigEntry
{
    private readonly string _name;
    private readonly ModConfigRenderer<T> _renderer;

    internal ModConfig(string name, T defaultValue, ModConfigRenderer<T> renderer)
    {
        _name = name;
        _renderer = renderer;
        Value = defaultValue;
    }

    public T Value { get; private set; }

    public void Draw(string id)
    {
        var value = Value;
        if (_renderer($"{_name}##{id}", ref value))
        {
            Value = value;
        }
    }
}

public abstract class ModBase
{
    private readonly System.Collections.Generic.List<IModConfigEntry> _configEntries = new();

    protected ModBase(string modName, string modVersion)
    {
        ModName = modName;
        ModVersion = modVersion;
    }

    public string ModName { get; }

    public string ModVersion { get; }

    protected ModConfig<T> AddConfig<T>(
        string name,
        T defaultValue,
        ModConfigRenderer<T> renderer)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(name);
        System.ArgumentNullException.ThrowIfNull(renderer);

        var entry = new ModConfig<T>(name, defaultValue, renderer);
        _configEntries.Add(entry);
        return entry;
    }

    protected ModConfig<bool> AddBoolConfig(string name, bool defaultValue) =>
        AddConfig(
            name,
            defaultValue,
            static (string label, ref bool value) =>
                Hexa.NET.ImGui.ImGui.Checkbox(label, ref value));

    protected ModConfig<int> AddIntConfig(
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        string format = "%d") =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref int value) =>
                Hexa.NET.ImGui.ImGui.SliderInt(
                    label,
                    ref value,
                    minimum,
                    maximum,
                    format));

    protected ModConfig<float> AddFloatConfig(
        string name,
        float defaultValue,
        float minimum,
        float maximum,
        string format = "%.2f") =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref float value) =>
                Hexa.NET.ImGui.ImGui.SliderFloat(
                    label,
                    ref value,
                    minimum,
                    maximum,
                    format));

    protected void DrawConfigUI()
    {
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
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.TreePop();
        }
    }

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

public sealed class AutoRun : ModBase
{
    private static readonly AutoRun Instance = new();
    private static app.GameFlowManager _gameFlowManager;
    private static app.PlayerManager _playerManager;
    private static float _runStartedAt = -1.0f;
    private static bool _autoSprintActive;
    private static int _errorReported;

    private readonly ModConfig<float> _sprintDelay;

    [ThreadStatic]
    private static ulong _dashManagerAddress;

    [ThreadStatic]
    private static bool _forceDashThisUpdate;

    private AutoRun() : base("AutoRun", "1.1")
    {
        _sprintDelay = AddFloatConfig(
            "Sprint delay (seconds)",
            0.5f,
            0.0f,
            5.0f,
            "%.2f s");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.Log("Loaded.");

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [PluginExitPoint]
    public static void OnUnload()
    {
        _gameFlowManager = null;
        _playerManager = null;
        _runStartedAt = -1.0f;
        _autoSprintActive = false;
        _errorReported = 0;
        _dashManagerAddress = 0;
        _forceDashThisUpdate = false;
    }

    [MethodHook(
        typeof(app.cPlayerActionSelector.cDashKeepManager),
        "update",
        MethodHookType.Pre)]
    public static PreHookResult BeforeDashKeepUpdate(Span<ulong> args)
    {
        _dashManagerAddress = args.Length > 1 ? args[1] : 0;
        _forceDashThisUpdate = false;

        try
        {
            _gameFlowManager ??= API.GetManagedSingletonT<app.GameFlowManager>();
            if (_gameFlowManager is null || !_gameFlowManager.IsIngameStable)
            {
                ResetSprint();
                return PreHookResult.Continue;
            }

            var characterAddress = args.Length > 2 ? args[2] : 0;
            var controllerAddress = args.Length > 3 ? args[3] : 0;
            if (!ManagedObject.IsManagedObject(characterAddress) ||
                !ManagedObject.IsManagedObject(controllerAddress))
            {
                ResetSprint();
                return PreHookResult.Continue;
            }

            _playerManager ??= API.GetManagedSingletonT<app.PlayerManager>();
            var playerControllerAddress =
                (_playerManager?.getControllingPlayerInfo()?.ControllerEntity as IProxyable)
                ?.GetAddress() ?? 0;
            if (controllerAddress != playerControllerAddress)
            {
                return PreHookResult.Continue;
            }

            var character = ManagedObject.ToManagedObject(characterAddress)
                ?.As<app.cPlayerCharacterEntity>();
            var controller = ManagedObject.ToManagedObject(controllerAddress)
                ?.As<app.cPlayerDefaultInputControllerEntity>();
            if (character is null || controller?.CommandResult?.IsEnableInputWorldDirL != true)
            {
                ResetSprint();
                return PreHookResult.Continue;
            }

            var moveType = character.getCurrentActionMoveType();
            if (!_autoSprintActive)
            {
                if (moveType != app.PlayerDef.MOVE_TYPE.RUN)
                {
                    return PreHookResult.Continue;
                }

                var now = via.Application.UpTimeSecond;
                if (_runStartedAt < 0.0f)
                {
                    _runStartedAt = now;
                    return PreHookResult.Continue;
                }

                if (now - _runStartedAt < Instance._sprintDelay.Value)
                {
                    return PreHookResult.Continue;
                }

                _autoSprintActive = true;
            }

            _forceDashThisUpdate =
                moveType == app.PlayerDef.MOVE_TYPE.RUN ||
                moveType == app.PlayerDef.MOVE_TYPE.DASH;
        }
        catch (Exception exception)
        {
            ResetSprint();
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"DASH state preparation failed: {exception}", ModLogLevel.Error);
            }
        }

        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.cPlayerActionSelector.cDashKeepManager),
        "update",
        MethodHookType.Post)]
    public static void AfterDashKeepUpdate(ref ulong returnValue)
    {
        if (!_forceDashThisUpdate)
        {
            return;
        }

        try
        {
            var address = _dashManagerAddress;
            if (!ManagedObject.IsManagedObject(address))
            {
                return;
            }

            var manager = ManagedObject.ToManagedObject(address)
                ?.As<app.cPlayerActionSelector.cDashKeepManager>();
            if (manager is null)
            {
                return;
            }

            manager.IsDashKeep = true;
        }
        catch (Exception exception)
        {
            ResetSprint();
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"DASH state override failed: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static void ResetSprint()
    {
        _runStartedAt = -1.0f;
        _autoSprintActive = false;
    }
}
