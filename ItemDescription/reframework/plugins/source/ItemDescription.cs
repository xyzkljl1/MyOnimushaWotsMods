using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using BuffType = app.PlayerBadStatus.cPlayerItemBuff.BUFF_TYPE;
using ParamTarget = app.user_data.ItemAdditionalParam.cGeneralParam.PARAM_TARGET;
using ParamValueType = app.user_data.ItemAdditionalParam.cGeneralParam.VALUE_TYPE;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: fda16e1bf690df89ab770438e70d909fe58fc55e
// Source commit: b924fa0619291388ba64396ad1ddd32a91d96a34
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

public sealed class ItemDescription : ModBase
{
    private const int SoulReturningMirrorItemId = 19198;

    private static readonly System.Text.Json.JsonSerializerOptions
        LocalizationJsonOptions = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        };
    private static readonly ItemDescription Instance = new();
    private static readonly System.Collections.Generic.Dictionary<int, string>
        DetailCache = new();

    private readonly string _localizationDirectory;
    private readonly System.Collections.Generic.Dictionary<string, string>
        _gameMessageNames = new(System.StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, string>
        _resolvedText = new(System.StringComparer.Ordinal);
    private System.Collections.Generic.Dictionary<string, string> _activeText;

    [System.ThreadStatic]
    private static PendingUpdate _inventoryUpdate;

    private ItemDescription() : base("ItemDescription", "1.0")
    {
        var dataDirectory = System.IO.Path.GetDirectoryName(ConfigPath);
        var reframeworkDirectory = dataDirectory is null
            ? null
            : System.IO.Directory.GetParent(dataDirectory)?.FullName;
        _localizationDirectory = System.IO.Path.Combine(
            reframeworkDirectory ??
            System.IO.Path.Combine(
                System.Environment.CurrentDirectory,
                "reframework"),
            "plugins",
            "source",
            "ItemDescription",
            "Localization");
    }

    private void Initialize()
    {
        LoadLocalization();
        Log($"Loaded. Localization: {_localizationDirectory}");
    }

    private void LoadLocalization()
    {
        _gameMessageNames.Clear();
        LoadDictionary(
            System.IO.Path.Combine(
                _localizationDirectory,
                "GameMessageNames.default.json"),
            _gameMessageNames,
            true);
        LoadDictionary(
            System.IO.Path.Combine(
                _localizationDirectory,
                "GameMessageNames.user.json"),
            _gameMessageNames,
            false);
        _activeText = new(System.StringComparer.Ordinal);
        var language = GetCurrentLanguage();
        if (!string.IsNullOrWhiteSpace(language) &&
            language.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
        {
            var languagesDirectory = System.IO.Path.Combine(
                _localizationDirectory,
                "Languages");
            LoadDictionary(
                System.IO.Path.Combine(
                    languagesDirectory,
                    $"{language}.default.json"),
                _activeText,
                false);
            LoadDictionary(
                System.IO.Path.Combine(
                    languagesDirectory,
                    $"{language}.user.json"),
                _activeText,
                false);
        }

        _resolvedText.Clear();
        DetailCache.Clear();
    }

    private void LoadDictionary(
        string path,
        System.Collections.Generic.Dictionary<string, string> destination,
        bool required)
    {
        if (!System.IO.File.Exists(path))
        {
            if (required)
            {
                Log($"Localization file not found: {path}", ModLogLevel.Warning);
            }

            return;
        }

        try
        {
            var loaded =
                System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.Generic.Dictionary<string, string>>(
                    System.IO.File.ReadAllText(path),
                    LocalizationJsonOptions);
            if (loaded is null)
            {
                return;
            }

            foreach (var entry in loaded)
            {
                destination[entry.Key] = entry.Value ?? string.Empty;
            }
        }
        catch (Exception exception)
        {
            Log(
                $"Could not load localization file '{path}': " + exception,
                ModLogLevel.Error);
        }
    }

    private static string GetCurrentLanguage()
    {
        try
        {
            return via.gui.GUISystem.MessageLanguage.ToString();
        }
        catch
        {
            return null;
        }
    }

    [PluginEntryPoint]
    public static void Main() => Instance.Initialize();

    [PluginExitPoint]
    public static void OnUnload()
    {
        DetailCache.Clear();
        Instance._gameMessageNames.Clear();
        Instance._resolvedText.Clear();
        Instance._activeText = null;
        _inventoryUpdate = default;
        Instance.UnloadMod();
    }

    [MethodHook(
        typeof(app.GUI030800.cItemDetailWindow),
        "setupItem",
        MethodHookType.Pre)]
    public static PreHookResult BeforeInventoryItem(Span<ulong> args)
    {
        _inventoryUpdate = CaptureDirectItem(args);
        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.GUI030800.cItemDetailWindow),
        "setupItem",
        MethodHookType.Post)]
    public static void AfterInventoryItem(ref ulong returnValue)
    {
        var update = _inventoryUpdate;
        _inventoryUpdate = default;

        try
        {
            if (!update.IsValid)
            {
                return;
            }

            var window = GetManagedObject<app.GUI030800.cItemDetailWindow>(
                update.OwnerAddress);
            ApplyDescription(window?._TextItemDescription, update.ItemId);
            Instance.ResetErrorReporting();
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Failed to update the inventory description", exception);
        }
    }

    private static PendingUpdate CaptureDirectItem(System.ReadOnlySpan<ulong> args)
    {
        if (args.Length <= 2 || args[1] == 0)
        {
            return default;
        }

        return new PendingUpdate(
            args[1],
            unchecked((int)args[2]),
            true);
    }

    private static void ApplyDescription(via.gui.Text text, int itemId)
    {
        if (text is null)
        {
            return;
        }

        var details = GetDetails(itemId);
        if (string.IsNullOrEmpty(details))
        {
            return;
        }

        var original = text.Message ?? string.Empty;
        if (original == details ||
            original.EndsWith($"\n{details}", StringComparison.Ordinal))
        {
            return;
        }

        text.Message = string.IsNullOrWhiteSpace(original)
            ? details
            : $"{original}\n{details}";
    }

    private string GetText(string key)
    {
        if (_resolvedText.TryGetValue(key, out var resolved))
        {
            return resolved;
        }

        var messageName = GetGameMessageName(key);
        if (!string.IsNullOrWhiteSpace(messageName))
        {
            try
            {
                var guid = via.gui.message.getGuidByName(messageName);
                var gameText = via.gui.message.get(guid)?.Trim();
                if (!string.IsNullOrWhiteSpace(gameText))
                {
                    resolved = gameText;
                }
            }
            catch (Exception exception)
            {
                Log(
                    $"Could not resolve game message '{messageName}' for '{key}': " +
                    exception.Message,
                    ModLogLevel.Warning);
            }
        }

        if (resolved is null)
        {
            resolved = _activeText is not null &&
                       _activeText.TryGetValue(key, out var text)
                ? text ?? string.Empty
                : string.Empty;
        }

        _resolvedText[key] = resolved;
        resolved = ResolveReferences(resolved);
        _resolvedText[key] = resolved;
        return resolved;
    }

    private string ResolveReferences(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\{([^{}]+)\}",
            match =>
            {
                var key = match.Groups[1].Value;
                return _gameMessageNames.ContainsKey(key) ||
                       _activeText?.ContainsKey(key) == true
                    ? GetText(key)
                    : match.Value;
            });

    private string GetGameMessageName(string key)
    {
        return _gameMessageNames.TryGetValue(key, out var messageName)
            ? messageName
            : null;
    }

    private static string Text(string key) => Instance.GetText(key);

    private static void Replace(
        ref string result,
        string placeholder,
        string value) =>
        result = result.Replace(placeholder, value);

    private static string Render(
        string key,
        string target = null,
        string value = null,
        string duration = null,
        string cadence = null,
        string seconds = null) =>
        Text(key)
            .Replace("{target}", target ?? string.Empty)
            .Replace("{value}", value ?? string.Empty)
            .Replace("{duration}", duration ?? string.Empty)
            .Replace("{cadence}", cadence ?? string.Empty)
            .Replace("{seconds}", seconds ?? string.Empty);

    private static string GetDetails(int itemId)
    {
        if (itemId == SoulReturningMirrorItemId)
        {
            return string.Empty;
        }

        if (DetailCache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var source = app.GA.VariousData?.ItemAdditionalParam;
        if (source is null)
        {
            return null;
        }

        var lines = new System.Collections.Generic.List<string>();
        AppendGeneralItem(lines, source.getItemParam(itemId));
        AppendMedicineBag(lines, source.getMedicineBagParam(itemId));

        var details = lines.Count == 0
            ? string.Empty
            : string.Join("\n", lines);
        DetailCache[itemId] = details;
        return details;
    }

    private static void AppendGeneralItem(
        System.Collections.Generic.List<string> lines,
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneItem item)
    {
        if (item?._Params is not { } consumes)
        {
            return;
        }

        foreach (var consume in consumes)
        {
            var prefix =
                consume?._ParamType ==
                app.user_data.ItemAdditionalParam.cGeneralParam.PARAM_TYPE.OBTAIN_EFFECT
                    ? Text("PickupPrefix")
                    : string.Empty;
            if (consume?._Effects is not { } effects)
            {
                continue;
            }

            foreach (var effect in effects)
            {
                AppendEffect(lines, effect, prefix);
            }
        }
    }

    private static void AppendMedicineBag(
        System.Collections.Generic.List<string> lines,
        app.user_data.ItemAdditionalParam.cMedicineBagParam bag)
    {
        if (bag?._Effects is not { } effects)
        {
            return;
        }

        foreach (var effect in effects)
        {
            AppendEffect(lines, effect, string.Empty);
        }
    }

    private static void AppendEffect(
        System.Collections.Generic.List<string> lines,
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneEffect effect,
        string prefix)
    {
        if (effect is null)
        {
            return;
        }

        var durationSuffix = DurationSuffix(effect._EffectSec);
        if (effect._ParamTargets is { } parameters)
        {
            foreach (var parameter in parameters)
            {
                AddLine(
                    lines,
                    prefix,
                    DescribeParameter(parameter, effect, durationSuffix));
            }
        }

        var buffLineCount = 0;
        if (effect._BuffTargets is { } buffs)
        {
            foreach (var buff in buffs)
            {
                if (AddLine(
                        lines,
                        prefix,
                        DescribeBuff(buff, durationSuffix)))
                {
                    ++buffLineCount;
                }
            }
        }

        if ((effect._EffectType ==
                 app.user_data.ItemAdditionalParam.cGeneralParam.EFFECT_TYPE.INSTANT ||
             effect._EffectType ==
                 app.user_data.ItemAdditionalParam.cGeneralParam.EFFECT_TYPE.BUFF &&
             buffLineCount == 0) &&
            effect._EffectSec > 0.0f)
        {
            AddLine(
                lines,
                prefix,
                Render("Duration", seconds: FormatNumber(effect._EffectSec)));
        }
    }

    private static string DescribeParameter(
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneParameter_General parameter,
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneEffect effect,
        string durationSuffix)
    {
        if (parameter is null)
        {
            return null;
        }

        var target = parameter._ParamTarget;
        var (template, valueStyle) = target switch
        {
            ParamTarget.HEALTH or
            ParamTarget.RIKIDO or
            ParamTarget.ONI_ENERGY => ("Value", ValueStyle.Recovery),
            ParamTarget.ONI_CHANGE_ENERGY or
            ParamTarget.JUST_DODGE => ("Value", ValueStyle.SignedTyped),
            ParamTarget.MEDICINE_BAG_USE_COUNT => ($"ParamType.{target}", ValueStyle.Signed),
            ParamTarget.SOUL_LOOTBOX_AMOUNT => ("Value", ValueStyle.Signed),
            _ => (null, default),
        };
        if (template is null)
        {
            return null;
        }

        var result = Text(template);
        if (valueStyle == ValueStyle.Recovery &&
            effect._EffectType ==
                app.user_data.ItemAdditionalParam.cGeneralParam.EFFECT_TYPE.REGENERATION)
        {
            var interval = effect._RegenerationIntervalSec;
            var cadence = interval > 0.0f
                ? Render(
                    "PerSeconds",
                    seconds: MathF.Abs(interval - 1.0f) < 0.0005f
                        ? string.Empty
                        : FormatNumber(interval))
                : string.Empty;
            result = Text("Regeneration");
            Replace(ref result, "{cadence}", cadence);
            Replace(ref result, "{duration}", durationSuffix);
        }

        Replace(ref result, "{target}", Text($"ParamTarget.{target}"));
        Replace(
            ref result,
            "{value}",
            FormatRuleValue(valueStyle, parameter._ParamValue, parameter._ValueType));
        Replace(ref result, "{duration}", string.Empty);
        return result;
    }

    private static string DescribeBuff(
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneParameter_Buff buff,
        string durationSuffix)
    {
        if (buff is null ||
            buff._BuffTarget ==
                BuffType.INVALID)
        {
            return null;
        }

        var target = buff._BuffTarget;
        var value = buff._ParamValue;
        (string Template, ValueStyle ValueStyle)? rule = target switch
        {
            BuffType.RIKIDO_DAMAGE_DISABLE or
            BuffType.SOUL_GENERATE_ATTACK or
            BuffType.ALWAYS_JUST_ACTION or
            BuffType.ALL_IMMUNE =>
                ($"BuffType.{target}", ValueStyle.None),
            BuffType.SOUL_ADDITION_UP =>
                ("Value", ValueStyle.ScaledPercent),
            BuffType.GAS_IMMUNE =>
                ("EffectReduction", ValueStyle.FullReduction),
            BuffType.HP_DAMAGE_CUT or
            BuffType.GAS_STATUS_DAMAGE_CUT =>
                ("EffectReduction", ValueStyle.Reduction),
            BuffType.RIKIDO_DAMAGE_CUT =>
                ($"BuffType.{target}", ValueStyle.Reduction),
            BuffType.DODGE_ATTAK_GAUGE_UP or
            BuffType.SOUL_BOOST_GAUGE_UP =>
                ("Value", ValueStyle.MultiplierIncrease),
            BuffType.HP_DAMAGE_UP or
            BuffType.RIKIDO_DAMAGE_UP =>
                ("DamageUp", ValueStyle.ScaledPercent),
            _ => null,
        };
        if (rule is not { } format)
        {
            return Text("UnknownBuff");
        }

        if (format.ValueStyle == ValueStyle.Reduction && value < -1000.0f)
        {
            return null;
        }

        return Render(
            format.Template,
            target: Text($"BuffTarget.{target}"),
            value: FormatRuleValue(
                format.ValueStyle,
                value,
                ParamValueType.CONSTANT),
            duration: durationSuffix);
    }

    private static string FormatRuleValue(
        ValueStyle style,
        float value,
        ParamValueType valueType) =>
        style switch
        {
            ValueStyle.None => null,
            ValueStyle.Signed => FormatSigned(value),
            ValueStyle.Recovery or
            ValueStyle.SignedTyped => FormatSignedTypedValue(value, valueType),
            ValueStyle.ScaledPercent => FormatPercent(value * 100.0f),
            ValueStyle.MultiplierIncrease => FormatMultiplierIncrease(value),
            ValueStyle.Reduction => FormatPercent((1.0f - value) * 100.0f),
            ValueStyle.FullReduction => FormatPercent(100.0f),
            _ => null,
        };

    private static string FormatMultiplierIncrease(float multiplier) =>
        FormatPercent((multiplier - 1.0f) * 100.0f);

    private static string DurationSuffix(float seconds) =>
        seconds > 0.0f
            ? " " + Render("Duration", seconds: FormatNumber(seconds))
            : string.Empty;

    private static string FormatSignedTypedValue(
        float value,
        ParamValueType valueType) =>
        valueType == ParamValueType.PERCENT
            ? $"{FormatSigned(value)}%"
            : FormatSigned(value);

    private static string FormatPercent(float value) =>
        $"{FormatNumber(value)}%";

    private static string FormatSigned(float value) =>
        value >= 0.0f
            ? $"+{FormatNumber(value)}"
            : FormatNumber(value);

    private static string FormatNumber(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static bool AddLine(
        System.Collections.Generic.List<string> lines,
        string prefix,
        string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var line = prefix + description;
        if (lines.Contains(line))
        {
            return false;
        }

        lines.Add(line);
        return true;
    }

    private enum ValueStyle
    {
        None,
        Recovery,
        Signed,
        SignedTyped,
        ScaledPercent,
        MultiplierIncrease,
        Reduction,
        FullReduction,
    }

    private readonly struct PendingUpdate
    {
        public PendingUpdate(ulong ownerAddress, int itemId, bool isValid)
        {
            OwnerAddress = ownerAddress;
            ItemId = itemId;
            IsValid = isValid;
        }

        public ulong OwnerAddress { get; }

        public int ItemId { get; }

        public bool IsValid { get; }
    }
}
