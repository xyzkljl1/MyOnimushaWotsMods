// Module: ModBase configuration, persistence, and ImGui helpers.
// Requires: Util/ModBase.cs from the same committed Git revision.
public delegate bool ModConfigRenderer<T>(string label, ref T value);

public struct ModColor
{
    public ModColor(float red, float green, float blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    public float Red { get; set; }

    public float Green { get; set; }

    public float Blue { get; set; }
}

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
        string key = null,
        bool animatedTitle = false) =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref bool value) =>
                DrawCheckbox(label, ref value, animatedTitle),
            key);

    protected ModConfig<ModColor> AddColorConfig(
        string name,
        ModColor defaultValue,
        float maximumComponent = 4.0f,
        string key = null,
        bool animatedTitle = false)
    {
        if (!float.IsFinite(maximumComponent) || maximumComponent <= 0.0f)
        {
            throw new System.ArgumentOutOfRangeException(nameof(maximumComponent));
        }

        return AddConfig(
            name,
            defaultValue,
            (string label, ref ModColor value) =>
                DrawColorPicker(label, ref value, maximumComponent, animatedTitle),
            key);
    }

    protected ModConfig<int> AddRadioGroupConfig(
        string name,
        int defaultValue,
        string[] options,
        bool sameLine = true,
        string key = null,
        bool animatedTitle = false)
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
                DrawRadioGroup(label, ref value, labels, sameLine, animatedTitle),
            key);
    }

    protected ModConfig<int> AddIntConfig(
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        string format = "%d",
        string key = null,
        bool animatedTitle = false) =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref int value) =>
                DrawIntSlider(label, ref value, minimum, maximum, format, animatedTitle),
            key);

    protected ModConfig<float> AddFloatConfig(
        string name,
        float defaultValue,
        float minimum,
        float maximum,
        string format = "%.2f",
        string key = null,
        bool animatedTitle = false) =>
        AddConfig(
            name,
            defaultValue,
            (string label, ref float value) =>
                DrawFloatSlider(label, ref value, minimum, maximum, format, animatedTitle),
            key);

    protected ModConfig<float> AddPixelInputConfig(
        string name,
        float defaultValue,
        float minimum,
        float maximum,
        string key = null,
        bool animatedTitle = false)
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
                DrawPixelInput(label, ref value, minimum, maximum, animatedTitle),
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

    // A shared two-second orange/blue cycle, evaluated only while drawing a title.
    // Presentation never changes the stored configuration value or its dirty state.
    private static System.Numerics.Vector4 GetAnimatedTitleColor()
    {
        var phase = (float)(Hexa.NET.ImGui.ImGui.GetTime() % 2.0) * System.MathF.PI;
        var blend = 0.5f - 0.5f * System.MathF.Cos(phase);
        return System.Numerics.Vector4.Lerp(
            new System.Numerics.Vector4(1.0f, 0.65f, 0.2f, 1.0f),
            new System.Numerics.Vector4(0.3f, 0.7f, 1.0f, 1.0f),
            blend);
    }

    protected static void DrawConfigTitle(string text, bool animatedTitle = false)
    {
        if (!animatedTitle)
        {
            Hexa.NET.ImGui.ImGui.TextUnformatted(text);
            return;
        }

        Hexa.NET.ImGui.ImGui.PushStyleColor(
            Hexa.NET.ImGui.ImGuiCol.Text, GetAnimatedTitleColor());
        try
        {
            Hexa.NET.ImGui.ImGui.TextUnformatted(text);
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.PopStyleColor();
        }
    }

    private static bool DrawCheckbox(string label, ref bool value, bool animatedTitle)
    {
        if (!animatedTitle)
        {
            return Hexa.NET.ImGui.ImGui.Checkbox(label, ref value);
        }

        // Checkbox text has its own style color; the check mark is unaffected.
        Hexa.NET.ImGui.ImGui.PushStyleColor(
            Hexa.NET.ImGui.ImGuiCol.Text, GetAnimatedTitleColor());
        try
        {
            return Hexa.NET.ImGui.ImGui.Checkbox(label, ref value);
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.PopStyleColor();
        }
    }

    private static void DrawSliderTitle(string label)
    {
        var separator = label.IndexOf("##", System.StringComparison.Ordinal);
        var title = separator < 0 ? label : label[..separator];
        Hexa.NET.ImGui.ImGui.SameLine(
            0.0f, Hexa.NET.ImGui.ImGui.GetStyle().ItemInnerSpacing.X);
        Hexa.NET.ImGui.ImGui.AlignTextToFramePadding();
        DrawConfigTitle(title, animatedTitle: true);
    }

    private static bool DrawIntSlider(
        string label, ref int value, int minimum, int maximum,
        string format, bool animatedTitle)
    {
        if (!animatedTitle)
        {
            return Hexa.NET.ImGui.ImGui.SliderInt(label, ref value, minimum, maximum, format);
        }

        // Hide the built-in label so the slider's numeric value keeps its normal color.
        // The widget ID is independent of the animated color.
        Hexa.NET.ImGui.ImGui.BeginGroup();
        try
        {
            var changed = Hexa.NET.ImGui.ImGui.SliderInt(
                $"##{label}", ref value, minimum, maximum, format);
            DrawSliderTitle(label);
            return changed;
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.EndGroup();
        }
    }

    private static bool DrawFloatSlider(
        string label, ref float value, float minimum, float maximum,
        string format, bool animatedTitle,
        Hexa.NET.ImGui.ImGuiSliderFlags flags = Hexa.NET.ImGui.ImGuiSliderFlags.None)
    {
        if (!animatedTitle)
        {
            return Hexa.NET.ImGui.ImGui.SliderFloat(label, ref value, minimum, maximum, format, flags);
        }

        Hexa.NET.ImGui.ImGui.BeginGroup();
        try
        {
            var changed = Hexa.NET.ImGui.ImGui.SliderFloat(
                $"##{label}", ref value, minimum, maximum, format, flags);
            DrawSliderTitle(label);
            return changed;
        }
        finally
        {
            Hexa.NET.ImGui.ImGui.EndGroup();
        }
    }

    private static bool DrawColorPicker(
        string label,
        ref ModColor value,
        float maximumComponent,
        bool animatedTitle)
    {
        var preview = new System.Numerics.Vector3(
            NormalizeColorComponent(value.Red, maximumComponent),
            NormalizeColorComponent(value.Green, maximumComponent),
            NormalizeColorComponent(value.Blue, maximumComponent));
        var changed = preview.X != value.Red ||
                      preview.Y != value.Green ||
                      preview.Z != value.Blue;
        var flags = Hexa.NET.ImGui.ImGuiColorEditFlags.Float |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.Hdr |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.PickerHueBar |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.NoSidePreview |
                    Hexa.NET.ImGui.ImGuiColorEditFlags.NoLabel;
        var idSeparator = label.IndexOf("##", System.StringComparison.Ordinal);
        var visibleLabel = idSeparator < 0 ? label : label[..idSeparator];
        DrawConfigTitle(visibleLabel, animatedTitle);
        changed |= Hexa.NET.ImGui.ImGui.ColorPicker3(label, ref preview, flags);

        var red = NormalizeColorComponent(preview.X, maximumComponent);
        var green = NormalizeColorComponent(preview.Y, maximumComponent);
        var blue = NormalizeColorComponent(preview.Z, maximumComponent);
        changed |= red != preview.X || green != preview.Y || blue != preview.Z;
        if (changed)
        {
            value = new ModColor(red, green, blue);
        }

        return changed;
    }

    private static float NormalizeColorComponent(float value, float maximum) =>
        float.IsFinite(value) ? System.Math.Clamp(value, 0.0f, maximum) : 0.0f;

    private static bool DrawRadioGroup(
        string label,
        ref int value,
        string[] options,
        bool sameLine,
        bool animatedTitle)
    {
        var separator = label.IndexOf("##", System.StringComparison.Ordinal);
        var name = separator >= 0 ? label[..separator] : label;
        var id = separator >= 0 ? label[(separator + 2)..] : label;
        DrawConfigTitle($"{name}:", animatedTitle);
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
        float maximum,
        bool animatedTitle)
    {
        var original = value;
        if (!float.IsFinite(value))
        {
            value = minimum;
        }

        value = System.Math.Clamp(System.MathF.Round(value), minimum, maximum);

        // Keep direct entry and a slider visible together within one item width.
        // Zero input steps remove the +/- buttons without losing keyboard input.
        var width = Hexa.NET.ImGui.ImGui.CalcItemWidth();
        var spacing = Hexa.NET.ImGui.ImGui.GetStyle().ItemInnerSpacing.X;
        var inputWidth = System.MathF.Min(
            Hexa.NET.ImGui.ImGui.GetFontSize() * 6.0f, width * 0.4f);
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(inputWidth);
        var changed = Hexa.NET.ImGui.ImGui.InputFloat(
            $"##{label}.Input", ref value, 0.0f, 0.0f, "%.0f");
        if (!float.IsFinite(value))
        {
            value = minimum;
        }
        value = System.Math.Clamp(System.MathF.Round(value), minimum, maximum);

        Hexa.NET.ImGui.ImGui.SameLine(0.0f, spacing);
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(
            System.MathF.Max(1.0f, width - inputWidth - spacing));
        changed |= DrawFloatSlider(
            label, ref value, minimum, maximum, "", animatedTitle,
            Hexa.NET.ImGui.ImGuiSliderFlags.AlwaysClamp);
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
