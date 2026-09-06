using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 2885bdae92689aa27743326540ec40410e46356e
// Source commit: 7c0372f0ed752663f8f2aa4408c2115804233b0c
// I do this to avoid panicing users. Copying code everythere instead of publishing a DLL is indeed stupid, but users’ antivirus software is stupider.
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

            if (DrawButton("reset settings", "Config.Reset"))
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

public sealed class AutoLoot : ModBase
{
    private const float RetryDelaySeconds = 1.0f;
    private const string TreasureBoxTypeName = "app.GimmickTreasureBox";
    private const string DiscoveredChestTypeName = "app.Gm002_006";
    private const string DialogueChestTypeName = "app.Gm002_009";
    private const string ReleasedChestTypeName = "app.Gm002_010";

    private static readonly AutoLoot Instance = new();
    private static readonly ConcurrentQueue<ulong> PendingItems = new();
    private static readonly HashSet<ulong> ProcessingItems = new();
    private static readonly Dictionary<ulong, float> LastAttempts = new();
    private static app.GameFlowManager _gameFlowManager;
    private static app.PlayerManager _playerManager;

    [ThreadStatic] private static ulong _checkingBaseItem;
    [ThreadStatic] private static ulong _checkingTreasureBox;
    [ThreadStatic] private static ulong _checkingHeldItem;
    [ThreadStatic] private static ulong _checkingAttachedItem;
    [ThreadStatic] private static ulong _checkingDiscoveredChest;
    [ThreadStatic] private static ulong _checkingReleasedChest;

    private readonly ModConfig<bool> _gatheringItems;
    private readonly ModConfig<bool> _roadsideChests;
    private readonly ModConfig<float> _collectionDistance;
    private readonly HistoricalStageItemSaveRepair _stageItemSaveRepair;

    private AutoLoot() : base("AutoLoot", "1.1")
    {
        _stageItemSaveRepair = new HistoricalStageItemSaveRepair(this);
        _gatheringItems = AddBoolConfig(
            "Gather spot and items",
            true,
            "Gathering points and loose items");
        _roadsideChests = AddBoolConfig(
            "junior chests",
            true,
            "Roadside and spawned chests");
        _collectionDistance = AddFloatConfig(
            "Collection distance (m)",
            10.0f,
            0.5f,
            50.0f,
            "%.1f",
            "CollectionDistance");
    }

    [PluginEntryPoint]
    public static void Main() => Instance.InitializeMod();

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        try
        {
            _gameFlowManager ??= API.GetManagedSingletonT<app.GameFlowManager>();
            if (_gameFlowManager?.IsIngameStable != true)
            {
                PendingItems.Clear();
                return;
            }

            Instance._stageItemSaveRepair.Update();

            _playerManager ??= API.GetManagedSingletonT<app.PlayerManager>();
            var playerTransform = _playerManager
                ?.getControllingPlayerInfo()
                ?.Object
                ?.Transform;
            if (playerTransform is null)
            {
                PendingItems.Clear();
                return;
            }

            ProcessingItems.Clear();
            while (PendingItems.TryDequeue(out var address))
            {
                ProcessingItems.Add(address);
            }

            var now = via.Application.UpTimeSecond;
            var playerPosition = playerTransform.Position;
            var collectionDistance = Instance._collectionDistance.Value;
            var collectionDistanceSquared = collectionDistance * collectionDistance;
            foreach (var address in ProcessingItems)
            {
                Instance.TryCollect(
                    address,
                    now,
                    playerPosition,
                    collectionDistanceSquared);
            }

            Instance.ResetErrorReporting();
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Automatic collection failed", exception);
        }
    }

    [MethodHook(typeof(app.Gm002), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeBaseItemCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingBaseItem);

    [MethodHook(typeof(app.Gm002), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterBaseItemCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingBaseItem);

    [MethodHook(
        typeof(app.GimmickTreasureBox),
        "onGmInteract_CheckOpenPopIcon",
        MethodHookType.Pre)]
    public static PreHookResult BeforeTreasureBoxCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingTreasureBox);

    [MethodHook(
        typeof(app.GimmickTreasureBox),
        "onGmInteract_CheckOpenPopIcon",
        MethodHookType.Post)]
    public static void AfterTreasureBoxCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingTreasureBox);

    [MethodHook(typeof(app.Gm002_003), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeHeldItemCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingHeldItem);

    [MethodHook(typeof(app.Gm002_003), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterHeldItemCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingHeldItem);

    [MethodHook(typeof(app.Gm002_004), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeAttachedItemCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingAttachedItem);

    [MethodHook(typeof(app.Gm002_004), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterAttachedItemCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingAttachedItem);

    [MethodHook(typeof(app.Gm002_006), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeDiscoveredChestCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingDiscoveredChest);

    [MethodHook(typeof(app.Gm002_006), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterDiscoveredChestCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingDiscoveredChest);

    [MethodHook(typeof(app.Gm002_010), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult BeforeReleasedChestCheck(Span<ulong> args) =>
        CaptureCheckedItem(args, ref _checkingReleasedChest);

    [MethodHook(typeof(app.Gm002_010), "onGmInteract_CheckOpenPopIcon", MethodHookType.Post)]
    public static void AfterReleasedChestCheck(ref ulong returnValue) =>
        QueueAvailableItem(ref returnValue, ref _checkingReleasedChest);

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI()
    {
        Instance.DrawConfigUI();
        Instance._stageItemSaveRepair.DrawUI();
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        _gameFlowManager = null;
        _playerManager = null;
        _checkingBaseItem = 0;
        _checkingTreasureBox = 0;
        _checkingHeldItem = 0;
        _checkingAttachedItem = 0;
        _checkingDiscoveredChest = 0;
        _checkingReleasedChest = 0;
        Instance._stageItemSaveRepair.Reset();
        PendingItems.Clear();
        ProcessingItems.Clear();
        LastAttempts.Clear();
        Instance.UnloadMod();
    }

    private static PreHookResult CaptureCheckedItem(Span<ulong> args, ref ulong address)
    {
        address = args.Length > 1 ? args[1] : 0;
        return PreHookResult.Continue;
    }

    private static void QueueAvailableItem(ref ulong returnValue, ref ulong address)
    {
        var checkedAddress = address;
        address = 0;
        if ((returnValue & 0xff) != 0 && checkedAddress != 0)
        {
            PendingItems.Enqueue(checkedAddress);
        }
    }

    private void TryCollect(
        ulong address,
        float now,
        via.vec3 playerPosition,
        float collectionDistanceSquared)
    {
        var managedItem = ManagedObject.IsManagedObject(address)
            ? ManagedObject.ToManagedObject(address)
            : null;
        var item = managedItem?.As<app.Gm002>();
        if (item is null ||
            !item._IsDisplayPopIcon ||
            !item._IsEnableInteractSensor ||
            !item._IsEnabledItem ||
            item.onGmInteract_CheckDisablePopIcon())
        {
            return;
        }

        var itemTransform = item.GameObject?.Transform;
        if (itemTransform is null ||
            !IsWithinDistance(
                playerPosition,
                itemTransform.Position,
                collectionDistanceSquared))
        {
            return;
        }

        var itemType = managedItem.GetTypeDefinition();
        if (itemType is null)
        {
            return;
        }

        var isTreasureBox = IsTypeOrDerivedFrom(itemType, TreasureBoxTypeName);
        var isDialogueChest = IsTypeOrDerivedFrom(itemType, DialogueChestTypeName);
        var isSeniorChest = isDialogueChest ||
            IsTypeOrDerivedFrom(itemType, DiscoveredChestTypeName) ||
            IsTypeOrDerivedFrom(itemType, ReleasedChestTypeName);
        var treasureBox = isTreasureBox
            ? managedItem.As<app.GimmickTreasureBox>()
            : null;
        if (isTreasureBox && treasureBox is null)
        {
            return;
        }

        if (isDialogueChest)
        {
            var dialogueChest = managedItem.As<app.Gm002_009>();
            if (dialogueChest is null || HasValidDialogue(dialogueChest))
            {
                return;
            }
        }

        if (!IsCategoryEnabled(isTreasureBox, isSeniorChest) ||
            !item.onGmInteract_CheckOpenPopIcon() ||
            treasureBox is not null &&
            (treasureBox.State != app.GimmickTreasureBox.STATE.IDLE ||
             !isDialogueChest && !treasureBox.checkEnableGetIAlltem()) ||
            LastAttempts.TryGetValue(address, out var attemptedAt) &&
            now - attemptedAt < RetryDelaySeconds)
        {
            return;
        }

        LastAttempts[address] = now;
        if (isDialogueChest)
        {
            treasureBox.takeItem();
        }
        else if (treasureBox is not null && !isSeniorChest)
        {
            treasureBox.onInteract();
        }
        else
        {
            item.onGmInteract_Success(null);
        }
    }

    private static bool IsWithinDistance(
        via.vec3 playerPosition,
        via.vec3 itemPosition,
        float maximumDistanceSquared)
    {
        var x = itemPosition.x - playerPosition.x;
        var y = itemPosition.y - playerPosition.y;
        var z = itemPosition.z - playerPosition.z;
        return x * x + y * y + z * z <= maximumDistanceSquared;
    }

    private static bool HasValidDialogue(app.Gm002_009 chest)
    {
        var dialogueId = chest._MyUniqueParam?.DialogueID;
        var resolvedDialogueId = default(app.DialogueDef.ID);
        return dialogueId is not null &&
            dialogueId.tryGetEnum(ref resolvedDialogueId);
    }

    private bool IsCategoryEnabled(bool isTreasureBox, bool isSeniorChest)
    {
        if (!isTreasureBox)
        {
            return _gatheringItems.Value;
        }

        // Senior-chest support remains implemented but is hidden and disabled
        // until its unlock and event flows have been verified in game.
        return !isSeniorChest && _roadsideChests.Value;
    }

    private static bool IsTypeOrDerivedFrom(
        TypeDefinition type,
        string fullName) =>
        string.Equals(type.FullName, fullName, StringComparison.Ordinal) ||
        type.IsDerivedFrom(fullName);

    // Repairs saves blocked by a historical AutoLoot bug that collected Stage items
    // before the matching story objective was active. It never runs automatically.
    private sealed class HistoricalStageItemSaveRepair
    {
        private readonly AutoLoot _owner;
        private int _pendingItemEvent;
        private string _status = "No repair event has been replayed.";

        public HistoricalStageItemSaveRepair(AutoLoot owner) => _owner = owner;

        public void Update()
        {
            var itemId = System.Threading.Interlocked.Exchange(ref _pendingItemEvent, 0);
            if (itemId != 0)
            {
                Replay(itemId);
            }
        }

        public void DrawUI()
        {
            if (!Hexa.NET.ImGui.ImGui.TreeNode(
                    $"Historical save repair (debug)##{_owner.ModName}.StageItemRepair"))
            {
                return;
            }

            try
            {
                AutoLoot.DrawText(
                    "This tool only repairs saves blocked by a bug in historical AutoLoot " +
                    "versions that collected a Stage item too early. NEVER click a button " +
                    "unless a bug involving that exact item has blocked story progression. " +
                    "Replaying the wrong event may damage story state. Back up the save first. " +
                    "These buttons do not add items.");

                if (System.Threading.Volatile.Read(ref _pendingItemEvent) != 0)
                {
                    AutoLoot.DrawText("Repair queued...", true);
                }
                else
                {
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE100_ITEM001);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE100_ITEM002);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE100_ITEM003);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE100_ITEM004);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE204_ITEM001);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE204_ITEM002);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE209_ITEM000);
                    DrawButton(app.ItemEnum.ID_Fixed.STAGE213_ITEM000);
                }

                AutoLoot.DrawText(System.Threading.Volatile.Read(ref _status));
            }
            catch (Exception exception)
            {
                _owner.LogErrorOnce("Could not draw the historical save-repair UI", exception);
            }
            finally
            {
                Hexa.NET.ImGui.ImGui.TreePop();
            }
        }

        public void Reset()
        {
            _pendingItemEvent = 0;
            _status = "No repair event has been replayed.";
        }

        private void DrawButton(app.ItemEnum.ID_Fixed itemId)
        {
            var label = itemId.ToString();
            if (_owner.DrawButton(
                    $"Replay {label} event once",
                    $"StageItemRepair.{label}"))
            {
                System.Threading.Interlocked.Exchange(
                    ref _pendingItemEvent,
                    unchecked((int)itemId));
                System.Threading.Volatile.Write(
                    ref _status,
                    $"Queued {label} for the next stable game update.");
            }
        }

        private void Replay(int itemId)
        {
            try
            {
                var storyManager = API.GetManagedSingletonT<app.StoryManager>();
                if (storyManager is null)
                {
                    System.Threading.Volatile.Write(
                        ref _status,
                        "StoryManager is unavailable. Enter gameplay and try again.");
                    _owner.Log(
                        "Historical save repair skipped because StoryManager is unavailable.",
                        ModLogLevel.Warning);
                    return;
                }

                storyManager.evGetItem(itemId, 1u);
                System.Threading.Volatile.Write(
                    ref _status,
                    "Event replayed. Wait for the story to advance, then save in a new slot.");
                _owner.Log(
                    $"Replayed historical Stage-item event once: itemId={itemId}. " +
                    "No item was added.");
            }
            catch (Exception exception)
            {
                System.Threading.Volatile.Write(
                    ref _status,
                    "Repair failed. Check re2_framework_log.txt and try again.");
                _owner.LogErrorOnce("Historical Stage-item save repair failed", exception);
            }
        }
    }
}
