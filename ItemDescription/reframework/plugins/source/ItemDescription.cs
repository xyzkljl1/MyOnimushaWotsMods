using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using ParamValueType = app.user_data.ItemAdditionalParam.cGeneralParam.VALUE_TYPE;

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

public sealed class ItemDescription : ModBase
{
    private static readonly System.Text.Json.JsonSerializerOptions
        LocalizationJsonOptions = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        };
    private static readonly System.Text.RegularExpressions.Regex
        DynamicPlaceholderRegex = new(
            @"\{@(?<index>[1-9][0-9]*):(?<format>[^{}:]+)\}");
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
    [System.ThreadStatic]
    private static PendingUpdate _medicineBagUpdate;

    private ItemDescription() : base("ItemDescription", "1.0")
    {
        _localizationDirectory = GetLocalizationDirectory();
    }

    private static string GetLocalizationDirectory()
    {
        var pluginPath = API.GetPluginDirectory(typeof(ItemDescription).Assembly);
        var directory = new System.IO.DirectoryInfo(
            pluginPath ?? System.Environment.CurrentDirectory);
        while (directory is not null &&
               !string.Equals(
                   directory.Name,
                   "reframework",
                   StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        var reframeworkPath = directory?.FullName ??
                              System.IO.Path.Combine(
                                  System.Environment.CurrentDirectory,
                                  "reframework");
        return System.IO.Path.Combine(
            reframeworkPath,
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
        _medicineBagUpdate = default;
        Instance.ResetErrorReporting();
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

    [MethodHook(
        typeof(app.GUI030109),
        "updateSelectMedicineBagDisp",
        MethodHookType.Pre)]
    public static PreHookResult BeforeMedicineBagSelection(Span<ulong> args)
    {
        _medicineBagUpdate = CaptureDirectItem(args);
        return PreHookResult.Continue;
    }

    [MethodHook(
        typeof(app.GUI030109),
        "updateSelectMedicineBagDisp",
        MethodHookType.Post)]
    public static void AfterMedicineBagSelection(ref ulong returnValue)
    {
        var update = _medicineBagUpdate;
        _medicineBagUpdate = default;

        try
        {
            if (!update.IsValid)
            {
                return;
            }

            var window = GetManagedObject<app.GUI030109>(update.OwnerAddress);
            var itemId = ResolveMedicineBagId(window, update.ItemId);
            ApplyDescription(window?._TextCaption, itemId);
            Instance.ResetErrorReporting();
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce(
                "Failed to update the medicine bag description",
                exception);
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

    private static int ResolveMedicineBagId(
        app.GUI030109 window,
        int candidate)
    {
        var source = app.GA.VariousData?.ItemAdditionalParam;
        if (source?.getMedicineBagParam(candidate) is not null)
        {
            return candidate;
        }

        var bags = window?._BagList;
        if (bags is null)
        {
            return candidate;
        }

        var index = candidate >= 0 && candidate < bags.Count
            ? candidate
            : window._SelectedIndex;
        return index >= 0 && index < bags.Count
            ? bags[index]
            : candidate;
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

        var messageName = _gameMessageNames.TryGetValue(
            key,
            out var configuredMessageName)
                ? configuredMessageName
                : null;
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

    private static string Text(string key) => Instance.GetText(key);

    private static string GetDetails(int itemId)
    {
        if (DetailCache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var template = Text($"Item.{itemId}");
        if (string.IsNullOrWhiteSpace(template))
        {
            DetailCache[itemId] = string.Empty;
            return string.Empty;
        }

        var source = app.GA.VariousData?.ItemAdditionalParam;
        if (source is null)
        {
            return null;
        }

        var values = new System.Collections.Generic.List<DynamicValue>();
        CollectGeneralItem(values, source.getItemParam(itemId));
        CollectEffects(values, source.getMedicineBagParam(itemId)?._Effects);

        string error = null;
        var details = DynamicPlaceholderRegex.Replace(
            template,
            match =>
            {
                if (!int.TryParse(
                        match.Groups["index"].Value,
                        out var index) ||
                    index > values.Count)
                {
                    error ??= $"value #{match.Groups["index"].Value} was not found";
                    return string.Empty;
                }

                var value = values[index - 1];
                var format = match.Groups["format"].Value;
                var formatted = FormatDynamicValue(value, format);
                if (formatted is null)
                {
                    error ??= $"format '{format}' is invalid for value #{index}";
                    return string.Empty;
                }

                return formatted;
            });
        if (error is not null)
        {
            Instance.Log(
                $"Ignored Item.{itemId}: {error}.",
                ModLogLevel.Warning);
            details = string.Empty;
        }
        else
        {
            details = details.Trim();
        }

        DetailCache[itemId] = details;
        return details;
    }

    private static void CollectGeneralItem(
        System.Collections.Generic.List<DynamicValue> values,
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneItem item)
    {
        if (item?._Params is not { } consumes)
        {
            return;
        }

        foreach (var consume in consumes)
        {
            if (consume?._Effects is not { } effects)
            {
                continue;
            }

            CollectEffects(values, effects);
        }
    }

    private static void CollectEffects(
        System.Collections.Generic.List<DynamicValue> values,
        app.user_data.ItemAdditionalParam.cGeneralParam.cOneEffect_Array1D effects)
    {
        if (effects is null)
        {
            return;
        }

        foreach (var effect in effects)
        {
            if (effect is null)
            {
                continue;
            }

            if (effect._ParamTargets is { } parameters)
            {
                foreach (var parameter in parameters)
                {
                    if (parameter is null)
                    {
                        continue;
                    }

                    values.Add(new DynamicValue(
                        parameter._ParamValue,
                        parameter._ValueType,
                        effect._EffectSec,
                        effect._RegenerationIntervalSec));
                }
            }

            if (effect._BuffTargets is { } buffs)
            {
                foreach (var buff in buffs)
                {
                    if (buff is null)
                    {
                        continue;
                    }

                    values.Add(new DynamicValue(
                        buff._ParamValue,
                        ParamValueType.CONSTANT,
                        effect._EffectSec,
                        effect._RegenerationIntervalSec));
                }
            }
        }
    }

    private static string FormatDynamicValue(
        DynamicValue value,
        string format)
    {
        const string number = "0.###";
        const string signed = "+0.###;-0.###;+0";
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return format switch
        {
            "number" => value.Value.ToString(number, culture),
            "signed" => value.Value.ToString(signed, culture),
            "typed" => value.ValueType == ParamValueType.PERCENT
                ? $"{value.Value.ToString(signed, culture)}%"
                : value.Value.ToString(signed, culture),
            "percent" => $"{(value.Value * 100.0f).ToString(number, culture)}%",
            "increase" =>
                $"{((value.Value - 1.0f) * 100.0f).ToString(number, culture)}%",
            "reduction" when value.Value >= -1000.0f =>
                $"{((1.0f - value.Value) * 100.0f).ToString(number, culture)}%",
            "immunity" => "100%",
            "duration" => value.Duration > 0.0f
                ? Text("Duration").Replace(
                    "{seconds}",
                    value.Duration.ToString(number, culture))
                : string.Empty,
            "cadence" => value.Interval > 0.0f
                ? Text("PerSeconds").Replace(
                    "{seconds}",
                    MathF.Abs(value.Interval - 1.0f) < 0.0005f
                        ? string.Empty
                        : value.Interval.ToString(number, culture))
                : string.Empty,
            _ => null,
        };
    }

    private readonly record struct DynamicValue(
        float Value,
        ParamValueType ValueType,
        float Duration,
        float Interval);

    private readonly record struct PendingUpdate(
        ulong OwnerAddress,
        int ItemId,
        bool IsValid);
}
