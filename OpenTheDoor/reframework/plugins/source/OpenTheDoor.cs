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

public sealed class OpenTheDoor : ModBase
{
    private enum DoorCategory { NormalOneWay, BreakableLock, ThreadMechanism, MaskItem, OtherSpecial }

    private sealed class DoorAttempt
    {
        public ulong Address;
        public ulong ContextAddress;
        // A native refusal takes priority over the no-original-text notice.
        public string ReplacementMessage;
        public bool HasOriginalMessage;
        public bool SilentOpening;
        public bool Started;
        public bool Finished;
        public long StartedAt;
        public bool BypassMaskLock;
        public bool MaskCameraEndRequested;
    }

    private sealed class BarrierAttempt
    {
        public ulong Address;
        public ulong ContextAddress;
        public string ReplacementMessage;
    }

    private const int MaximumAttempts = 16;
    // Native Gm028_001 side checks use touch sensor 1 for the visible lock side.
    private const int BreakableLockSideSensor = 1;
    private const long AttemptLifetimeMs = 15000;
    private static readonly OpenTheDoor Instance = new();
    private static readonly object AttemptLock = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, DoorAttempt>
        Attempts = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, BarrierAttempt>
        BarrierAttempts = new();
    // Lock objects can disappear after breaking. Remember the category for this
    // door/context lifetime so closing it does not move it to another checkbox.
    private const int MaximumRememberedBreakableDoors = 256;
    private static readonly System.Collections.Generic.Dictionary<ulong, ulong> BreakableDoorContexts = new();
    private static readonly System.Collections.Generic.Queue<(ulong Address, ulong Context)> BreakableDoorOrder = new();
    [ThreadStatic] private static int _unlockDepth;
    [ThreadStatic] private static ulong _openingDoor;
    [ThreadStatic] private static int _readingOriginalChecks;
    [ThreadStatic] private static System.Collections.Generic.Stack<ulong?> _doorResults;
    [ThreadStatic] private static System.Collections.Generic.Stack<ulong> _startingDoors;
    private readonly ModConfig<bool> _ordinaryDoors;
    private readonly ModConfig<bool> _breakableLockDoors;
    private readonly ModConfig<bool> _breakableBarriers;
    private readonly ModConfig<bool> _threadMechanismDoors;
    private readonly ModConfig<bool> _maskDoors;
    private readonly ModConfig<bool> _otherSpecialDoors;

    private OpenTheDoor() : base("OpenTheDoor", "1.1")
    {
        _ordinaryDoors = AddBoolConfig("Normal one-way Doors", true, key: "EnableOrdinaryDoors");
        _breakableLockDoors = AddBoolConfig("Breakable lock Doors", true, key: "EnableBreakableLockDoors");
        _breakableBarriers = AddBoolConfig("One-way Oni Walls", true, key: "EnableBreakableBarriers");
        _threadMechanismDoors = AddBoolConfig("Thread mechanism Doors", true, key: "EnableThreadMechanismDoors");
        _maskDoors = AddBoolConfig("Mask item Doors", true, key: "EnableMaskDoors");
        _otherSpecialDoors = AddBoolConfig("Other special Doors", true, key: "EnableOtherSpecialDoors");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.InitializeMod();

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [PluginExitPoint]
    public static void OnUnload()
    {
        lock (AttemptLock)
        {
            Attempts.Clear();
            BarrierAttempts.Clear();
            BreakableDoorContexts.Clear();
            BreakableDoorOrder.Clear();
        }
        _openingDoor = 0;
        _unlockDepth = 0;
        _readingOriginalChecks = 0;
        _doorResults?.Clear();
        _startingDoors?.Clear();
        Instance.UnloadMod();
    }

    // requestUnlock also uses requestUnavailableAnnounce for its SUCCESS text.
    // Keep those normal announcements out of the blocked-door path.
    [MethodHook(typeof(app.GimmickDoor), "requestUnlock", MethodHookType.Pre)]
    public static PreHookResult BeforeUnlock(Span<ulong> args)
    {
        _unlockDepth++;
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.GimmickDoor), "requestUnlock", MethodHookType.Post)]
    public static void AfterUnlock(ref ulong returnValue)
    {
        _unlockDepth = Math.Max(0, _unlockDepth - 1);
    }

    [MethodHook(typeof(app.GimmickDoor), "requestUnavailableAnnounce", MethodHookType.Pre)]
    public static PreHookResult BeforeBlockedMessage(Span<ulong> args)
    {
        if (args.Length < 2) return PreHookResult.Continue;
        var address = args[1];
        // Unlock callbacks may synchronously announce again for this same door.
        if (_openingDoor == address) return PreHookResult.Skip;
        if (_unlockDepth != 0) return PreHookResult.Continue;

        try
        {
            var door = GetManagedObject<app.GimmickDoor>(address);
            var contextAddress = AddressOf(door?.GimmickContext);
            if (contextAddress == 0 || door._IsEventMode || !IsDoorEnabled(door)) return PreHookResult.Continue;
            DoorAttempt pending;
            lock (AttemptLock)
            {
                Attempts.TryGetValue(address, out pending);
                if (pending is not null && !IsPendingOpening(pending, contextAddress, door.CurrentState))
                    pending = null;
            }

            if (pending?.Started == true) return PreHookResult.Skip;
            if (pending is null && (door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.CLOSED ||
                (!HasUnlockedSave(door) && door.isOpenable()) ||
                API.GetManagedSingletonT<app.GUIManager>() is null))
                return PreHookResult.Continue;

            // A callback made reachable only by our UI override is not an
            // original refusal. It uses the fallback, except on the breakable
            // lock side, where removing the need to attack stays silent.
            var silentOpening = pending?.SilentOpening ?? IsSilentOpening(door);
            var originallyDisabled = WasInteractionDisabled(door);
            var hasOriginalMessage = false;
            var replacement = silentOpening || originallyDisabled ? null :
                ReplacementForMessage(args, out hasOriginalMessage);
            // onGmInteract_Success writes its failure save state after announcing.
            // Apply our opening only after the current behavior update has ended.
            if (!QueueOpening(door, replacement, hasOriginalMessage)) return PreHookResult.Continue;
            // Unknown messages/languages retain the actual game text, never an
            // invented success sentence or an English fallback translation.
            return silentOpening || !hasOriginalMessage || replacement is not null
                ? PreHookResult.Skip : PreHookResult.Continue;
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to handle the blocked-door message", exception);
            return PreHookResult.Continue;
        }
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        UpdateBarriers();
        DoorAttempt[] snapshot;
        lock (AttemptLock)
        {
            if (Attempts.Count == 0) return;
            snapshot = new DoorAttempt[Attempts.Count];
            Attempts.Values.CopyTo(snapshot, 0);
        }

        foreach (var attempt in snapshot)
        {
            try
            {
                var door = ResolveDoor(attempt);
                var now = Environment.TickCount64;
                if (door is null || door._IsEventMode || !IsDoorEnabled(door) ||
                    (attempt.Finished && now - attempt.StartedAt >= AttemptLifetimeMs))
                {
                    Forget(attempt);
                    continue;
                }
                if (attempt.Started &&
                    (door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSING ||
                     door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED))
                {
                    // The player can back away instead of crossing. Opening and
                    // closing may both occur between our samples, so Finished is
                    // not proof that this request still owns the current cycle.
                    Instance.Log($"Door 0x{attempt.Address:X}: {door.CurrentState}; opening record cleared.");
                    Forget(attempt);
                    continue;
                }
                if (!attempt.Started)
                {
                    if (!PrepareMaskOpening(door, attempt)) continue;
                    if (door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.OPENING &&
                        door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.OPENED)
                        OpenDoor(door, attempt);
                    attempt.Started = true;
                    attempt.StartedAt = now;
                    AnnounceReplacement(attempt);
                    Instance.Log($"Door 0x{attempt.Address:X}: opening requested ({door.CurrentState}).");
                }
                // Let the native animation finish regardless of elapsed time.
                if (!attempt.Finished && door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.OPENED)
                {
                    attempt.Finished = true;
                    Instance.Log($"Door 0x{attempt.Address:X}: reached OPENED.");
                }
            }
            catch (Exception exception)
            {
                Forget(attempt);
                // A mod failure belongs in the log, not in the game's dialogue.
                // The failed attempt is removed, so this is not a per-frame log.
                Instance.Log($"Failed to open door 0x{attempt.Address:X}: {exception}", ModLogLevel.Error);
            }
        }
    }

    // Gm042 is a one-way destructible barrier, not a GimmickDoor. Its back
    // sensor (1) uses NO_REACTION and only announces a refusal. Preserve that
    // player reaction and break the barrier after native interaction has returned.
    [MethodHook(typeof(app.Gm042), "onGmInteract_Success", MethodHookType.Pre)]
    public static PreHookResult BeforeBarrierInteraction(Span<ulong> args)
    {
        if (!Instance._breakableBarriers.Value || args.Length < 2) return PreHookResult.Continue;
        try
        {
            var barrier = ResolveBarrier(args[1]);
            if (barrier is null || barrier._IsEventMode ||
                (int)barrier._Interact_SuccessSensorID != 1 ||
                (int)barrier._Interact_TouchSensorID != 1)
                return PreHookResult.Continue;
            if (IsBarrierBroken(barrier)) return PreHookResult.Skip;
            var contextAddress = AddressOf(barrier.GimmickContext);
            lock (AttemptLock)
            {
                if (BarrierAttempts.TryGetValue(args[1], out var pending) &&
                    pending.ContextAddress == contextAddress)
                    return PreHookResult.Skip;
                if (barrier._State != app.Gm042.STATE.IDLE ||
                    (BarrierAttempts.Count >= MaximumAttempts && !BarrierAttempts.ContainsKey(args[1])))
                    return PreHookResult.Continue;
                var message = BarrierMessage();
                BarrierAttempts[args[1]] = new BarrierAttempt
                {
                    Address = args[1], ContextAddress = contextAddress,
                    ReplacementMessage = message,
                };
                // Untranslated languages retain their actual native announcement.
                // No global message hook or guessed English translation is used.
                return message is null ? PreHookResult.Continue : PreHookResult.Skip;
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to handle the one-way barrier interaction", exception);
            return PreHookResult.Continue;
        }
    }

    private static app.Gm042 ResolveBarrier(ulong address)
    {
        if (address == 0 || !ManagedObject.IsManagedObject(address)) return null;
        var managed = ManagedObject.ToManagedObject(address);
        if (managed is null || managed.IsGoingToBeDestroyed()) return null;
        var barrier = managed.TryAs<app.Gm042>();
        return AddressOf(barrier?.GimmickContext) != 0 ? barrier : null;
    }

    private static bool IsBarrierBroken(app.Gm042 barrier) =>
        barrier._State is app.Gm042.STATE.BREAK or app.Gm042.STATE.END ||
        barrier.GimmickContext.SaveFlagHolder?.State == app.Gm042.GM042_SAVE_STATE_BROKEN;

    private static void UpdateBarriers()
    {
        BarrierAttempt[] snapshot;
        lock (AttemptLock)
        {
            if (BarrierAttempts.Count == 0) return;
            snapshot = new BarrierAttempt[BarrierAttempts.Count];
            BarrierAttempts.Values.CopyTo(snapshot, 0);
        }
        foreach (var attempt in snapshot)
        {
            try
            {
                var barrier = ResolveBarrier(attempt.Address);
                if (!Instance._breakableBarriers.Value || barrier is null || barrier._IsEventMode ||
                    AddressOf(barrier.GimmickContext) != attempt.ContextAddress || IsBarrierBroken(barrier))
                    continue;
                // A mapped refusal was suppressed and left IDLE intact. Otherwise
                // native code enters ANNOUNCE, then ANNOUNCE_WAIT. Any other state
                // belongs to a new native action; never interrupt it to force a break.
                var expectedState = attempt.ReplacementMessage is not null
                    ? barrier._State == app.Gm042.STATE.IDLE
                    : barrier._State is app.Gm042.STATE.ANNOUNCE or app.Gm042.STATE.ANNOUNCE_WAIT;
                if (!expectedState) continue;
                barrier.wakeup();
                // This runs onBreak, including dynamics, collision, AI/path flags,
                // save state 15 and mesh fade. Do not call onBreak alone or set END.
                barrier.changeState(app.Gm042.STATE.BREAK);
                if (barrier._State is not (app.Gm042.STATE.BREAK or app.Gm042.STATE.END))
                    throw new InvalidOperationException("The barrier did not enter its native broken state.");
                Instance.Log($"Barrier 0x{attempt.Address:X}: native destruction requested ({barrier._State}).");
                if (attempt.ReplacementMessage is not null)
                {
                    if (API.GetManagedSingletonT<app.GUIManager>() is null)
                        throw new InvalidOperationException("Barrier destroyed, but the announcement GUI is unavailable.");
                    Announce(attempt.ReplacementMessage);
                }
            }
            catch (Exception exception)
            {
                // Never show a success notice after destruction fails, or retry a
                // partly executed native callback automatically on the next frame.
                Instance.Log($"Failed to process barrier 0x{attempt.Address:X}: {exception}", ModLogLevel.Error);
            }
            finally
            {
                lock (AttemptLock)
                {
                    if (BarrierAttempts.TryGetValue(attempt.Address, out var current) && ReferenceEquals(current, attempt))
                        BarrierAttempts.Remove(attempt.Address);
                }
            }
        }
    }

    private static string BarrierMessage() => via.gui.GUISystem.MessageLanguage switch
    {
        // SignboardText_Com0004, ad627bd9-ffd7-4c31-9ebd-bf8d6c954141.
        via.Language.SimplelifiedChinese => "可以从这一侧破坏",
        via.Language.TransitionalChinese => "可以從此側破壞",
        via.Language.English => "Can be destroyed from this side.",
        _ => null,
    };

    // These are native per-save door states, not a timed/address-only whitelist:
    // 1 = opened, 2 = closed after unlocking; 0/3 do not mean unlocked.
    // This also works after the scene recreates the door or a save is loaded.
    private static bool HasUnlockedSave(app.GimmickDoor door) =>
        IsDoorEnabled(door) && door?.GimmickContext?.SaveFlagHolder is not null &&
        door.isUnlockSaveState();

    private static bool IsDoorEnabled(app.GimmickDoor door)
    {
        if (door?.GimmickContext is null) return false;
        return GetDoorCategory(door) switch
        {
            DoorCategory.NormalOneWay => Instance._ordinaryDoors.Value,
            DoorCategory.BreakableLock => Instance._breakableLockDoors.Value,
            DoorCategory.ThreadMechanism => Instance._threadMechanismDoors.Value,
            DoorCategory.MaskItem => Instance._maskDoors.Value,
            _ => Instance._otherSpecialDoors.Value,
        };
    }

    private static DoorCategory GetDoorCategory(app.GimmickDoor door)
    {
        // Unique door behavior takes priority over attached locks and messages.
        if (GetMaskDoor(door) is not null) return DoorCategory.MaskItem;
        if (GetThreadDoor(door) is not null) return DoorCategory.ThreadMechanism;
        var address = AddressOf(door);
        var context = AddressOf(door.GimmickContext);
        var breakable = GetDoorLock(door)?.TryAs<app.Gm028_001>() is not null;
        lock (AttemptLock)
        {
            if (BreakableDoorContexts.TryGetValue(address, out var previousContext))
            {
                if (previousContext == context) return DoorCategory.BreakableLock;
                BreakableDoorContexts.Remove(address);
            }
            if (breakable)
            {
                BreakableDoorContexts[address] = context;
                BreakableDoorOrder.Enqueue((address, context));
                while (BreakableDoorOrder.Count > MaximumRememberedBreakableDoors)
                {
                    var oldest = BreakableDoorOrder.Dequeue();
                    if (BreakableDoorContexts.TryGetValue(oldest.Address, out var remembered) && remembered == oldest.Context)
                        BreakableDoorContexts.Remove(oldest.Address);
                }
                return DoorCategory.BreakableLock;
            }
        }
        // Explicit ordinary implementations; do not classify a new derived type
        // as ordinary merely because it also displays the generic refusal text.
        var name = ManagedObject.ToManagedObject(address)?.GetTypeDefinition()?.FullName;
        return name is "app.GimmickDoor" or "app.Gm052" or "app.Gm052_000" or
            "app.Gm052_001" or "app.Gm052_003"
            ? DoorCategory.NormalOneWay : DoorCategory.OtherSpecial;
    }

    // A mask inspection owns camera fades, UI and per-frame player visibility.
    // endCamera requests its native cleanup; changing _CameraState directly would
    // skip that cleanup. Open only after it has returned to DEFAULT.
    private static bool PrepareMaskOpening(app.GimmickDoor door, DoorAttempt attempt)
    {
        var mask = GetMaskDoor(door);
        if (mask is null) return true;
        if (mask._CameraState == app.Gm053_001.CAMERA_STATE.GET_ITEM_CAMERA ||
            (mask._CameraState != app.Gm053_001.CAMERA_STATE.DEFAULT && !attempt.BypassMaskLock))
        {
            Forget(attempt);
            return false;
        }
        if (mask._CameraState == app.Gm053_001.CAMERA_STATE.NO_ITEM_CAMERA)
        {
            if (!attempt.MaskCameraEndRequested)
            {
                mask.endCamera();
                attempt.MaskCameraEndRequested = true;
                Instance.Log($"Door 0x{attempt.Address:X}: ending the mask inspection before opening.");
            }
        }
        else if (mask._CameraState == app.Gm053_001.CAMERA_STATE.DEFAULT && !mask._IsPlDrawOff)
            return true;

        return false;
    }

    private enum DoorCheck { Locked, Openable, OpenIcon, DisabledIcon, Reaction }

    private static PreHookResult PrepareDoorResult(Span<ulong> args, DoorCheck check)
    {
        ulong? result = null;
        try
        {
            var door = args.Length > 1 ? GetManagedObject<app.GimmickDoor>(args[1]) : null;
            if (_readingOriginalChecks == 0 && IsDoorEnabled(door) &&
                door?.GimmickContext is not null && !door._IsEventMode && !HasActiveMaskCamera(door) &&
                (HasUnlockedSave(door) || (check != DoorCheck.Locked && HasIntactBreakableLock(door))))
            {
                // An intact breakable lock disables the button before any refusal
                // message is emitted. Permit interaction, but keep isLocked native
                // until the player actually opens it and its unlock callbacks run.
                result = check switch
                {
                    DoorCheck.Locked or DoorCheck.DisabledIcon or DoorCheck.Reaction => 0UL,
                    DoorCheck.OpenIcon => door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED
                        && door._AnnounceManager?._IsAnnouncementOn != true ? 1UL : 0UL,
                    _ => 1UL,
                };
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to check the door's saved unlock state", exception);
        }
        // Hooks can nest (isOpenable calls isLocked); each post consumes its own pre.
        (_doorResults ??= new()).Push(result);
        return result.HasValue ? PreHookResult.Skip : PreHookResult.Continue;
    }

    private static void ApplyDoorResult(ref ulong result)
    {
        if (_doorResults is { Count: > 0 } && _doorResults.Pop() is ulong replacement)
            result = replacement;
    }

    [MethodHook(typeof(app.GimmickDoor), "isLocked", MethodHookType.Pre)]
    public static PreHookResult BeforeIsLocked(Span<ulong> args) => PrepareDoorResult(args, DoorCheck.Locked);
    [MethodHook(typeof(app.GimmickDoor), "isLocked", MethodHookType.Post)]
    public static void AfterIsLocked(ref ulong result) => ApplyDoorResult(ref result);

    [MethodHook(typeof(app.GimmickDoor), "isOpenable", MethodHookType.Pre)]
    public static PreHookResult BeforeIsOpenable(Span<ulong> args) => PrepareDoorResult(args, DoorCheck.Openable);
    [MethodHook(typeof(app.GimmickDoor), "isOpenable", MethodHookType.Post)]
    public static void AfterIsOpenable(ref ulong result) => ApplyDoorResult(ref result);

    // The native pop-icon functions inline lock and side checks, so an isLocked
    // hook alone cannot restore the interact button on the return side.
    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeOpenIcon(Span<ulong> args) => PrepareDoorResult(args, DoorCheck.OpenIcon);
    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterOpenIcon(ref ulong result) => ApplyDoorResult(ref result);

    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_CheckDisablePopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeDisabledIcon(Span<ulong> args) => PrepareDoorResult(args, DoorCheck.DisabledIcon);
    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_CheckDisablePopIcon", MethodHookType.Post)]
    public static void AfterDisabledIcon(ref ulong result) => ApplyDoorResult(ref result);

    [MethodHook(typeof(app.GimmickDoor), "getInteractReaction", MethodHookType.Pre)]
    public static PreHookResult BeforeReaction(Span<ulong> args) => PrepareDoorResult(args, DoorCheck.Reaction);
    [MethodHook(typeof(app.GimmickDoor), "getInteractReaction", MethodHookType.Post)]
    public static void AfterReaction(ref ulong result) => ApplyDoorResult(ref result);

    [MethodHook(typeof(app.GimmickDoor), "doStartBegin", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorStart(Span<ulong> args)
    {
        (_startingDoors ??= new()).Push(args.Length > 1 ? args[1] : 0);
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.GimmickDoor), "doStartBegin", MethodHookType.Post)]
    public static void AfterDoorStart(ref ulong result)
    {
        var address = _startingDoors is { Count: > 0 } ? _startingDoors.Pop() : 0;
        RestoreSavedDoor(address);
    }

    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_Touch", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorTouch(Span<ulong> args)
    {
        if (args.Length > 1) RestoreSavedDoor(args[1]);
        return PreHookResult.Continue;
    }

    private static void RestoreSavedDoor(ulong address)
    {
        try
        {
            var door = GetManagedObject<app.GimmickDoor>(address);
            if (HasUnlockedSave(door) && !door._IsEventMode)
            {
                UnlockUniqueMechanism(door);
                if (door.GimmickContext.State == app.GimmickDef.APP_STATE.DISABLE)
                    door.GimmickContext.State = app.GimmickDef.APP_STATE.ENABLE;
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to restore the saved door's interaction", exception);
        }
    }

    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_Success", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorInteraction(Span<ulong> args)
    {
        try
        {
            var door = args.Length > 1 ? GetManagedObject<app.GimmickDoor>(args[1]) : null;
            if (IsDoorEnabled(door) && door?.GimmickContext is not null &&
                !door._IsEventMode && !HasActiveMaskCamera(door) &&
                door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED)
            {
                var savedUnlocked = HasUnlockedSave(door);
                // This mechanism can be ENABLE with its private lock still set.
                // Native onSuccess then fails silently, without calling the refusal
                // message hook. Recover only the door the player is interacting with.
                if (savedUnlocked || HasIntactBreakableLock(door) || GetThreadDoor(door)?._IsUnLock == false ||
                    GetMaskDoor(door)?.checkUniqueLock() == true)
                    QueueOpening(door);
            }
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to reopen the saved door", exception);
        }
        return PreHookResult.Continue;
    }

    // The derived success method starts GET_ITEM_CAMERA even after its base has
    // queued a saved-unlocked reopen. Skip that extra camera on normal repeats.
    // On the first missing-item interaction let the base capture any real refusal
    // text, then finish the native NO_ITEM_CAMERA cleanup before the deferred open.
    [MethodHook(typeof(app.Gm053_001), "onGmInteract_Success", MethodHookType.Pre)]
    public static PreHookResult BeforeMaskDoorInteraction(Span<ulong> args)
    {
        if (args.Length < 2) return PreHookResult.Continue;
        try
        {
            var door = GetManagedObject<app.GimmickDoor>(args[1]);
            if (door?.GimmickContext is not null && !door._IsEventMode &&
                !HasActiveMaskCamera(door) && HasUnlockedSave(door) &&
                door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED)
                return QueueOpening(door) ? PreHookResult.Skip : PreHookResult.Continue;
        }
        catch (Exception exception)
        {
            Instance.Log($"Failed to handle mask door 0x{args[1]:X}: {exception}", ModLogLevel.Error);
        }
        return PreHookResult.Continue;
    }

    // Keep late failed animation events from writing LOCKED_NO_REACTION. Saved
    // unlocked doors use this path on every interaction, even after Attempts expires.
    [MethodHook(typeof(app.GimmickDoor), "interactEvent", MethodHookType.Pre)]
    public static PreHookResult BeforeInteractionEvent(Span<ulong> args)
    {
        if (args.Length < 2) return PreHookResult.Continue;
        try
        {
            var savedDoor = GetManagedObject<app.GimmickDoor>(args[1]);
            if (!IsDoorEnabled(savedDoor) || savedDoor?._IsEventMode == true || HasActiveMaskCamera(savedDoor))
                return PreHookResult.Continue;
            var savedUnlocked = HasUnlockedSave(savedDoor);
            if (savedDoor?.GimmickContext is not null && !savedDoor._IsEventMode &&
                (savedUnlocked || (savedDoor.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED &&
                    HasIntactBreakableLock(savedDoor))))
            {
                if (savedDoor.CurrentState == app.GimmickDoor.GM_DOOR_STATE.CLOSED)
                    return QueueOpening(savedDoor) ? PreHookResult.Skip : PreHookResult.Continue;
                return PreHookResult.Skip;
            }
            DoorAttempt attempt;
            lock (AttemptLock) Attempts.TryGetValue(args[1], out attempt);
            if (attempt is null || !attempt.Started ||
                Environment.TickCount64 - attempt.StartedAt >= AttemptLifetimeMs)
                return PreHookResult.Continue;
            var door = ResolveDoor(attempt);
            return door is not null &&
                (door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.OPENING ||
                 door.CurrentState == app.GimmickDoor.GM_DOOR_STATE.OPENED)
                ? PreHookResult.Skip : PreHookResult.Continue;
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to protect the door interaction result", exception);
            return PreHookResult.Continue;
        }
    }

    private static bool QueueOpening(app.GimmickDoor door, string replacementMessage = null,
        bool hasOriginalMessage = false)
    {
        if (!IsDoorEnabled(door)) return false;
        var address = AddressOf(door);
        var contextAddress = AddressOf(door.GimmickContext);
        lock (AttemptLock)
        {
            if (Attempts.TryGetValue(address, out var current) &&
                IsPendingOpening(current, contextAddress, door.CurrentState))
            {
                // Native text may arrive after onSuccess. Replace the fallback;
                // an unmapped native message also removes it to avoid two notices.
                if (!current.Started && !current.SilentOpening &&
                    hasOriginalMessage && !current.HasOriginalMessage)
                {
                    current.ReplacementMessage = replacementMessage;
                    current.HasOriginalMessage = true;
                }
                return true;
            }
            if (Attempts.Count >= MaximumAttempts && !Attempts.ContainsKey(address)) return false;
            if (door._IsEventMode || HasActiveMaskCamera(door)) return false;
            var silentOpening = IsSilentOpening(door);
            Attempts[address] = new DoorAttempt
            {
                Address = address, ContextAddress = contextAddress,
                BypassMaskLock = !HasUnlockedSave(door) && GetMaskDoor(door)?.checkUniqueLock() == true,
                SilentOpening = silentOpening,
                HasOriginalMessage = hasOriginalMessage,
                ReplacementMessage = silentOpening ? null :
                    hasOriginalMessage ? replacementMessage : PassageMessage(),
            };
        }
        return true;
    }

    private static bool IsPendingOpening(
        DoorAttempt attempt, ulong contextAddress, app.GimmickDoor.GM_DOOR_STATE state) =>
        attempt.ContextAddress == contextAddress && !attempt.Finished &&
        (!attempt.Started || state == app.GimmickDoor.GM_DOOR_STATE.OPENING);

    private static void OpenDoor(app.GimmickDoor door, DoorAttempt attempt)
    {
        var previousOpeningDoor = _openingDoor;
        _openingDoor = attempt.Address;
        try
        {
            var bolt = GetDoorLock(door)?.TryAs<app.Gm028>();
            if (bolt is not null)
            {
                // Gm028_001 (breakable lock) inherits this state machine but its
                // isUnlockable rejects intact locks. UNLOCKING writes the unlocked
                // save flag and invokes the callbacks; jumping straight to UNLOCKED
                // would miss both. Clear a pending vanilla request to avoid replay.
                if (bolt.isLocked())
                {
                    bolt.requestState(app.Gm028.GM028_STATE.NONE);
                    bolt.wakeup();
                    bolt.changeState(app.Gm028.GM028_STATE.UNLOCKING, false);
                }
            }
            else if (door._LockGmCtrl is not null)
            {
                door.unlockGmCtrl();
            }
            UnlockUniqueMechanism(door);
            door.GimmickContext.State = app.GimmickDef.APP_STATE.ENABLE;
            door.requestOpen(true);
            if (door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.OPENING &&
                door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.OPENED)
                door.forceOpen();
        }
        finally { _openingDoor = previousOpeningDoor; }
    }

    private static ManagedObject GetDoorLock(app.GimmickDoor door)
    {
        var address = AddressOf(door?._LockGmCtrl?.LockedGimmick);
        if (address == 0 || !ManagedObject.IsManagedObject(address)) return null;
        var managed = ManagedObject.ToManagedObject(address);
        return managed is not null && !managed.IsGoingToBeDestroyed() ? managed : null;
    }

    private static bool HasIntactBreakableLock(app.GimmickDoor door) =>
        GetDoorLock(door)?.TryAs<app.Gm028_001>()?.isLocked() == true;

    private static app.Gm053_001 GetMaskDoor(app.GimmickDoor door)
    {
        var address = AddressOf(door);
        return address != 0 && ManagedObject.IsManagedObject(address)
            ? ManagedObject.ToManagedObject(address)?.TryAs<app.Gm053_001>() : null;
    }

    private static bool HasActiveMaskCamera(app.GimmickDoor door) =>
        GetMaskDoor(door) is { } mask &&
        (mask._CameraState != app.Gm053_001.CAMERA_STATE.DEFAULT || mask._IsPlDrawOff);

    private static app.Gm053_002 GetThreadDoor(app.GimmickDoor door)
    {
        var address = AddressOf(door);
        return address != 0 && ManagedObject.IsManagedObject(address)
            ? ManagedObject.ToManagedObject(address)?.TryAs<app.Gm053_002>() : null;
    }

    private static void UnlockUniqueMechanism(app.GimmickDoor door)
    {
        var threadDoor = GetThreadDoor(door);
        // Saved-open alone does not restore Gm053_002's private lock. Its native
        // updateChangeState checks isLocked before applying doUnlocked, so our
        // saved-unlock override would otherwise prevent that initialization.
        // Use its own operation for the private flag, story state and update flag.
        if (threadDoor is not null && !threadDoor._IsUnLock)
            threadDoor.doUnlocked();
    }

    private static app.GimmickDoor ResolveDoor(DoorAttempt attempt)
    {
        if (!ManagedObject.IsManagedObject(attempt.Address)) return null;
        var managed = ManagedObject.ToManagedObject(attempt.Address);
        if (managed is null || managed.IsGoingToBeDestroyed()) return null;
        var door = managed.TryAs<app.GimmickDoor>();
        return AddressOf(door?.GimmickContext) == attempt.ContextAddress ? door : null;
    }

    private static ulong AddressOf(object value) => (value as IProxyable)?.GetAddress() ?? 0;

    private static void Forget(DoorAttempt attempt)
    {
        lock (AttemptLock)
        {
            if (Attempts.TryGetValue(attempt.Address, out var current) &&
                ReferenceEquals(current, attempt)) Attempts.Remove(attempt.Address);
        }
    }

    private static void Announce(string text)
    {
        var gui = API.GetManagedSingletonT<app.GUIManager>();
        if (gui is null) return;
        // An empty MsgID plus a string parameter is the engine's literal-message
        // representation. GUIManager copies it into its own announcement queue.
        using var managed = ace.cGUIMessageInfo.REFType.CreateInstance(0);
        var message = managed?.As<ace.cGUIMessageInfo>()
            ?? throw new InvalidOperationException("Could not create a door announcement.");
        message.setMessageInfo(text);
        gui.requestAnnounce(message, 3.0f);
    }

    private static bool WasInteractionDisabled(app.GimmickDoor door)
    {
        _readingOriginalChecks++;
        try { return door.onGmInteract_CheckDisablePopIcon(); }
        finally { _readingOriginalChecks--; }
    }

    private static bool IsSilentOpening(app.GimmickDoor door) => HasUnlockedSave(door) ||
        (HasIntactBreakableLock(door) && (int)door._Interact_TouchSensorID == BreakableLockSideSensor);

    private static string PassageMessage() => via.gui.GUISystem.MessageLanguage switch
    {
        via.Language.SimplelifiedChinese => "畅通无阻",
        via.Language.TransitionalChinese => "暢通無阻",
        _ => "FBI Open the Door",
    };

    private static string ReplacementForMessage(ReadOnlySpan<ulong> args, out bool hasOriginalMessage)
    {
        hasOriginalMessage = false;
        if (args.Length < 3 || args[2] == 0) return null;
        // Native requestUnavailableAnnounce(Guid) receives a pointer to the
        // 16-byte value (caller-owned, often on its stack). Copy it in this hook;
        // do not interpret it as a managed object or retain its pointer.
        var id = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>((IntPtr)args[2]);
        hasOriginalMessage = id != Guid.Empty;
        var language = via.gui.GUISystem.MessageLanguage;
        if (id == OtherSideMessageId)
            return language switch
            {
                // 无法从这一侧打开 / 無法從此側打開 / It won't open from this side.
                via.Language.SimplelifiedChinese => "可以从这一侧打开",
                via.Language.TransitionalChinese => "可以從此側打開",
                via.Language.English => "It will open from this side.",
                _ => null,
            };
        if (id == LockedMessageId)
            return language switch
            {
                // 已上锁，无法打开 / 上鎖了，打不開 / It's locked and won't open.
                via.Language.SimplelifiedChinese => "没上锁，可以打开",
                via.Language.TransitionalChinese => "沒上鎖，打得開",
                via.Language.English => "It's not locked and will open.",
                _ => null,
            };
        return null;
    }

    private static readonly Guid OtherSideMessageId = new("7ab6a207-82a6-478b-b2f7-feb417c68881");
    private static readonly Guid LockedMessageId = new("2982d221-8059-4d30-9792-e6bbe267497f");

    private static void AnnounceReplacement(DoorAttempt attempt)
    {
        var message = attempt.ReplacementMessage;
        attempt.ReplacementMessage = null;
        if (message is null) return;
        try { Announce(message); }
        catch (Exception exception)
        {
            // A UI failure must not discard native opening-completion tracking.
            Instance.Log($"Failed to replace the message for door 0x{attempt.Address:X}: {exception}", ModLogLevel.Error);
        }
    }
}
