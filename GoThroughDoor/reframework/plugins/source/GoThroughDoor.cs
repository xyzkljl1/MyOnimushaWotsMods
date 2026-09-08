using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 25417359db8c70a84c6f557d62440b857d2d6419
// Source commit: 537a2d80892067d2e33016d8c6f521f922b1013c
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
// Source blob SHA-1: 8aa5d2fa84e234fecb2249961835e33ad1f1c1de
// Source commit: 537a2d80892067d2e33016d8c6f521f922b1013c
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
        string key = null) =>
        AddConfig(
            name,
            defaultValue,
            static (string label, ref bool value) =>
                Hexa.NET.ImGui.ImGui.Checkbox(label, ref value),
            key);

    protected ModConfig<ModColor> AddColorConfig(
        string name,
        ModColor defaultValue,
        float maximumComponent = 4.0f,
        string key = null)
    {
        if (!float.IsFinite(maximumComponent) || maximumComponent <= 0.0f)
        {
            throw new System.ArgumentOutOfRangeException(nameof(maximumComponent));
        }

        return AddConfig(
            name,
            defaultValue,
            (string label, ref ModColor value) =>
                DrawColorPicker(label, ref value, maximumComponent),
            key);
    }

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

    private static bool DrawColorPicker(
        string label,
        ref ModColor value,
        float maximumComponent)
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
        Hexa.NET.ImGui.ImGui.TextUnformatted(visibleLabel);
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
        changed |= Hexa.NET.ImGui.ImGui.SliderFloat(
            label, ref value, minimum, maximum, "",
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

// END copied source: Util/ModBase.Config.cs

// BEGIN copied source: Util/ModBase.Hotkey.cs
// Source blob SHA-1: 9aba964758d536ae9257a9953438bf77a5b3af9a
// Source commit: 537a2d80892067d2e33016d8c6f521f922b1013c
// Module: Persistent keyboard/gamepad shortcuts and their ImGui editor.
// Requires: Util/ModBase.cs and Util/ModBase.Config.cs from the same commit.
// Add a binding with AddHotkeyConfig(), then call IsHotkeyPressed() once per frame.
public struct ModHotkey
{
    public ModHotkey(
        Hexa.NET.ImGui.ImGuiKey key,
        bool ctrl = false,
        bool shift = false,
        bool alt = false)
    {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public Hexa.NET.ImGui.ImGuiKey Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public bool IsValid =>
        Key == Hexa.NET.ImGui.ImGuiKey.None || IsBindableKey(Key);

    public bool IsDown(bool allowWhenKeyboardCaptured = false)
    {
        if (Key == Hexa.NET.ImGui.ImGuiKey.None || !IsBindableKey(Key))
        {
            return false;
        }

        if (!allowWhenKeyboardCaptured && REFrameworkNET.API.IsDrawingUI())
        {
            return false;
        }

        return IsKeyDown(Key) &&
               IsCurrentProcessForeground() &&
               Ctrl == IsNativeModifierDown(
                   via.hid.KeyboardKey.LControl,
                   via.hid.KeyboardKey.RControl) &&
               Shift == IsNativeModifierDown(
                   via.hid.KeyboardKey.LShift,
                   via.hid.KeyboardKey.RShift) &&
               Alt == IsNativeModifierDown(
                   via.hid.KeyboardKey.LMenu,
                   via.hid.KeyboardKey.RMenu);
    }

    public override string ToString()
    {
        if (Key == Hexa.NET.ImGui.ImGuiKey.None)
        {
            return "None";
        }

        return $"{(Ctrl ? "Ctrl+" : string.Empty)}" +
               $"{(Shift ? "Shift+" : string.Empty)}" +
               $"{(Alt ? "Alt+" : string.Empty)}{Key}";
    }

    internal static bool IsBindableKey(Hexa.NET.ImGui.ImGuiKey key)
    {
        var value = (int)key;
        var isKeyboard = value >= (int)Hexa.NET.ImGui.ImGuiKey.Tab &&
                         value <= (int)Hexa.NET.ImGui.ImGuiKey.AppForward;
        var isGamepad = value >= (int)Hexa.NET.ImGui.ImGuiKey.GamepadStart &&
                        value <= (int)Hexa.NET.ImGui.ImGuiKey.GamepadRStickDown;
        return (isKeyboard && !IsModifierKey(key)) || isGamepad;
    }

    private static bool IsKeyDown(Hexa.NET.ImGui.ImGuiKey key)
    {
        if (TryGetNativeKeyboardKey(key, out var keyboardKey))
        {
            return IsVirtualKeyDown(keyboardKey);
        }

        if (TryGetNativeGamePadButton(key, out var gamePadButton))
        {
            var device = via.hid.GamePad.MergedDevice;
            return device is not null &&
                   (device.ButtonDown & gamePadButton) != via.hid.GamePadButton.None;
        }

        return Hexa.NET.ImGui.ImGui.IsKeyDown(key);
    }

    private static bool IsNativeModifierDown(
        via.hid.KeyboardKey left,
        via.hid.KeyboardKey right)
    {
        return IsVirtualKeyDown(left) || IsVirtualKeyDown(right);
    }

    private static bool IsVirtualKeyDown(via.hid.KeyboardKey key) =>
        (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static bool IsCurrentProcessForeground()
    {
        var window = GetForegroundWindow();
        if (window == System.IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)System.Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern System.IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        System.IntPtr window,
        out uint processId);

    private static bool TryGetNativeKeyboardKey(
        Hexa.NET.ImGui.ImGuiKey key,
        out via.hid.KeyboardKey keyboardKey)
    {
        var name = key switch
        {
            Hexa.NET.ImGui.ImGuiKey.LeftArrow => "Left",
            Hexa.NET.ImGui.ImGuiKey.RightArrow => "Right",
            Hexa.NET.ImGui.ImGuiKey.UpArrow => "Up",
            Hexa.NET.ImGui.ImGuiKey.DownArrow => "Down",
            Hexa.NET.ImGui.ImGuiKey.PageUp => "Prior",
            Hexa.NET.ImGui.ImGuiKey.PageDown => "Next",
            Hexa.NET.ImGui.ImGuiKey.Backspace => "Back",
            Hexa.NET.ImGui.ImGuiKey.LeftSuper => "LWin",
            Hexa.NET.ImGui.ImGuiKey.RightSuper => "RWin",
            Hexa.NET.ImGui.ImGuiKey.Menu => "Apps",
            Hexa.NET.ImGui.ImGuiKey.Key0 => "Alpha0",
            Hexa.NET.ImGui.ImGuiKey.Key1 => "Alpha1",
            Hexa.NET.ImGui.ImGuiKey.Key2 => "Alpha2",
            Hexa.NET.ImGui.ImGuiKey.Key3 => "Alpha3",
            Hexa.NET.ImGui.ImGuiKey.Key4 => "Alpha4",
            Hexa.NET.ImGui.ImGuiKey.Key5 => "Alpha5",
            Hexa.NET.ImGui.ImGuiKey.Key6 => "Alpha6",
            Hexa.NET.ImGui.ImGuiKey.Key7 => "Alpha7",
            Hexa.NET.ImGui.ImGuiKey.Key8 => "Alpha8",
            Hexa.NET.ImGui.ImGuiKey.Key9 => "Alpha9",
            Hexa.NET.ImGui.ImGuiKey.Apostrophe => "OEM_7",
            Hexa.NET.ImGui.ImGuiKey.Comma => "OEM_Comma",
            Hexa.NET.ImGui.ImGuiKey.Minus => "OEM_Minus",
            Hexa.NET.ImGui.ImGuiKey.Period => "OEM_Period",
            Hexa.NET.ImGui.ImGuiKey.Slash => "OEM_2",
            Hexa.NET.ImGui.ImGuiKey.Semicolon => "OEM_1",
            Hexa.NET.ImGui.ImGuiKey.Equal => "OEM_Plus",
            Hexa.NET.ImGui.ImGuiKey.LeftBracket => "OEM_4",
            Hexa.NET.ImGui.ImGuiKey.Backslash => "OEM_5",
            Hexa.NET.ImGui.ImGuiKey.RightBracket => "OEM_6",
            Hexa.NET.ImGui.ImGuiKey.GraveAccent => "OEM_3",
            Hexa.NET.ImGui.ImGuiKey.CapsLock => "Capital",
            Hexa.NET.ImGui.ImGuiKey.ScrollLock => "Scroll",
            Hexa.NET.ImGui.ImGuiKey.PrintScreen => "SnapShot",
            Hexa.NET.ImGui.ImGuiKey.Keypad0 => "NumPad0",
            Hexa.NET.ImGui.ImGuiKey.Keypad1 => "NumPad1",
            Hexa.NET.ImGui.ImGuiKey.Keypad2 => "NumPad2",
            Hexa.NET.ImGui.ImGuiKey.Keypad3 => "NumPad3",
            Hexa.NET.ImGui.ImGuiKey.Keypad4 => "NumPad4",
            Hexa.NET.ImGui.ImGuiKey.Keypad5 => "NumPad5",
            Hexa.NET.ImGui.ImGuiKey.Keypad6 => "NumPad6",
            Hexa.NET.ImGui.ImGuiKey.Keypad7 => "NumPad7",
            Hexa.NET.ImGui.ImGuiKey.Keypad8 => "NumPad8",
            Hexa.NET.ImGui.ImGuiKey.Keypad9 => "NumPad9",
            Hexa.NET.ImGui.ImGuiKey.KeypadDecimal => "Decimal",
            Hexa.NET.ImGui.ImGuiKey.KeypadDivide => "Divide",
            Hexa.NET.ImGui.ImGuiKey.KeypadMultiply => "Multiply",
            Hexa.NET.ImGui.ImGuiKey.KeypadSubtract => "Subtract",
            Hexa.NET.ImGui.ImGuiKey.KeypadAdd => "Add",
            Hexa.NET.ImGui.ImGuiKey.KeypadEnter => "NumPadEnter",
            Hexa.NET.ImGui.ImGuiKey.Oem102 => "OEM_102",
            _ => key.ToString(),
        };
        return System.Enum.TryParse(name, out keyboardKey) &&
               keyboardKey != via.hid.KeyboardKey.None;
    }

    private static bool TryGetNativeGamePadButton(
        Hexa.NET.ImGui.ImGuiKey key,
        out via.hid.GamePadButton button)
    {
        button = key switch
        {
            Hexa.NET.ImGui.ImGuiKey.GamepadFaceLeft => via.hid.GamePadButton.RLeft,
            Hexa.NET.ImGui.ImGuiKey.GamepadFaceRight => via.hid.GamePadButton.RRight,
            Hexa.NET.ImGui.ImGuiKey.GamepadFaceUp => via.hid.GamePadButton.RUp,
            Hexa.NET.ImGui.ImGuiKey.GamepadFaceDown => via.hid.GamePadButton.RDown,
            Hexa.NET.ImGui.ImGuiKey.GamepadDpadLeft => via.hid.GamePadButton.LLeft,
            Hexa.NET.ImGui.ImGuiKey.GamepadDpadRight => via.hid.GamePadButton.LRight,
            Hexa.NET.ImGui.ImGuiKey.GamepadDpadUp => via.hid.GamePadButton.LUp,
            Hexa.NET.ImGui.ImGuiKey.GamepadDpadDown => via.hid.GamePadButton.LDown,
            Hexa.NET.ImGui.ImGuiKey.GamepadL1 => via.hid.GamePadButton.LTrigTop,
            Hexa.NET.ImGui.ImGuiKey.GamepadR1 => via.hid.GamePadButton.RTrigTop,
            Hexa.NET.ImGui.ImGuiKey.GamepadL2 => via.hid.GamePadButton.LTrigBottom,
            Hexa.NET.ImGui.ImGuiKey.GamepadR2 => via.hid.GamePadButton.RTrigBottom,
            Hexa.NET.ImGui.ImGuiKey.GamepadL3 => via.hid.GamePadButton.LStickPush,
            Hexa.NET.ImGui.ImGuiKey.GamepadR3 => via.hid.GamePadButton.RStickPush,
            Hexa.NET.ImGui.ImGuiKey.GamepadLStickLeft => via.hid.GamePadButton.EmuLleft,
            Hexa.NET.ImGui.ImGuiKey.GamepadLStickRight => via.hid.GamePadButton.EmuLright,
            Hexa.NET.ImGui.ImGuiKey.GamepadLStickUp => via.hid.GamePadButton.EmuLup,
            Hexa.NET.ImGui.ImGuiKey.GamepadLStickDown => via.hid.GamePadButton.EmuLdown,
            Hexa.NET.ImGui.ImGuiKey.GamepadRStickLeft => via.hid.GamePadButton.EmuRleft,
            Hexa.NET.ImGui.ImGuiKey.GamepadRStickRight => via.hid.GamePadButton.EmuRright,
            Hexa.NET.ImGui.ImGuiKey.GamepadRStickUp => via.hid.GamePadButton.EmuRup,
            Hexa.NET.ImGui.ImGuiKey.GamepadRStickDown => via.hid.GamePadButton.EmuRdown,
            _ => via.hid.GamePadButton.None,
        };
        return button != via.hid.GamePadButton.None;
    }

    private static bool IsModifierKey(Hexa.NET.ImGui.ImGuiKey key) =>
        key == Hexa.NET.ImGui.ImGuiKey.LeftCtrl ||
        key == Hexa.NET.ImGui.ImGuiKey.LeftShift ||
        key == Hexa.NET.ImGui.ImGuiKey.LeftAlt ||
        key == Hexa.NET.ImGui.ImGuiKey.LeftSuper ||
        key == Hexa.NET.ImGui.ImGuiKey.RightCtrl ||
        key == Hexa.NET.ImGui.ImGuiKey.RightShift ||
        key == Hexa.NET.ImGui.ImGuiKey.RightAlt ||
        key == Hexa.NET.ImGui.ImGuiKey.RightSuper;
}

public abstract partial class ModBase
{
    private string _capturingHotkeyId;
    private readonly System.Collections.Generic.Dictionary<ModConfig<ModHotkey>, bool>
        _hotkeyDownStates = new();

    protected ModConfig<ModHotkey> AddHotkeyConfig(
        string name,
        Hexa.NET.ImGui.ImGuiKey defaultKey,
        bool ctrl = false,
        bool shift = false,
        bool alt = false,
        string key = null) =>
        AddHotkeyConfig(
            name,
            new ModHotkey(defaultKey, ctrl, shift, alt),
            key);

    protected ModConfig<ModHotkey> AddHotkeyConfig(
        string name,
        ModHotkey defaultValue,
        string key = null)
    {
        if (!defaultValue.IsValid)
        {
            throw new System.ArgumentOutOfRangeException(
                nameof(defaultValue),
                "The default hotkey must be a keyboard or gamepad key.");
        }

        return AddConfig(name, defaultValue, DrawHotkeyConfig, key);
    }

    protected bool IsHotkeyPressed(
        ModConfig<ModHotkey> hotkey,
        bool allowWhenKeyboardCaptured = false)
    {
        System.ArgumentNullException.ThrowIfNull(hotkey);
        var isDown = hotkey.Value.IsDown(allowWhenKeyboardCaptured);
        var wasDown = _hotkeyDownStates.TryGetValue(hotkey, out var previous) && previous;
        _hotkeyDownStates[hotkey] = isDown;
        return isDown && !wasDown;
    }

    private bool DrawHotkeyConfig(string label, ref ModHotkey value)
    {
        var isCapturing = string.Equals(
            _capturingHotkeyId,
            label,
            System.StringComparison.Ordinal);
        var changed = false;
        if (isCapturing && TryReadPressedHotkey(out var captured))
        {
            value = captured;
            _capturingHotkeyId = null;
            isCapturing = false;
            changed = true;
        }

        var separator = label.IndexOf("##", System.StringComparison.Ordinal);
        var name = separator >= 0 ? label[..separator] : label;
        var id = separator >= 0 ? label[separator..] : $"##{label}";
        var buttonText = isCapturing
            ? $"{name}: press a key...{id}.Capture"
            : $"{name}: {value}{id}.Capture";
        if (Hexa.NET.ImGui.ImGui.Button(buttonText))
        {
            _capturingHotkeyId = isCapturing ? null : label;
            isCapturing = !isCapturing;
        }

        if (isCapturing)
        {
            Hexa.NET.ImGui.ImGui.SameLine();
            Hexa.NET.ImGui.ImGui.TextDisabled(
                "click the binding again to cancel");
        }

        return changed;
    }

    private static bool TryReadPressedHotkey(out ModHotkey hotkey)
    {
        for (var value = (int)Hexa.NET.ImGui.ImGuiKey.Tab;
             value <= (int)Hexa.NET.ImGui.ImGuiKey.AppForward;
             value++)
        {
            if (TryCaptureKey((Hexa.NET.ImGui.ImGuiKey)value, out hotkey))
            {
                return true;
            }
        }

        for (var value = (int)Hexa.NET.ImGui.ImGuiKey.GamepadStart;
             value <= (int)Hexa.NET.ImGui.ImGuiKey.GamepadRStickDown;
             value++)
        {
            if (TryCaptureKey((Hexa.NET.ImGui.ImGuiKey)value, out hotkey))
            {
                return true;
            }
        }

        hotkey = default;
        return false;
    }

    private static bool TryCaptureKey(
        Hexa.NET.ImGui.ImGuiKey key,
        out ModHotkey hotkey)
    {
        if (!ModHotkey.IsBindableKey(key) ||
            !Hexa.NET.ImGui.ImGui.IsKeyPressed(key, false))
        {
            hotkey = default;
            return false;
        }

        hotkey = new ModHotkey(
            key,
            Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModCtrl),
            Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModShift),
            Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModAlt));
        return true;
    }
}

// END copied source: Util/ModBase.Hotkey.cs

public sealed class GoThroughDoor : ModBase
{
    private static readonly GoThroughDoor Instance = new();
    private readonly ModConfig<ModHotkey> _hotkey;
    private GoThroughDoor() : base("GoThroughDoor", "1.0") =>
        _hotkey = AddConfig("Hotkey", new ModHotkey(ImGuiKey.F8), DrawPassageBinding);
    private sealed class Target
    {
        public ulong Address, Context, Env, Bits;
        public bool EnvEnabled, PressEnabled;
        public int StateStamp;
        public readonly List<(ulong Address, bool Enabled)> Effectors = new();
    }
    private static readonly object Sync = new();
    private static readonly Dictionary<ulong, long> Nearby = new();
    private static Target[] _targets = Array.Empty<Target>();
    private static bool Active => _targets.Length > 0;
    private static (ulong Player, ulong Context, float X, float Z) _anchor;
    private static long _deadline;
    private const long DurationMs = 5000;
    private const float MaxDoorDistance = 3.5f, MaxTravelDistance = 8f;

    [PluginEntryPoint]
    public static void Main()
    {
        if (!Instance._hotkey.Value.IsValid) Instance._hotkey.Reset();
        Instance.InitializeMod();
        Instance.IsHotkeyPressed(Instance._hotkey);
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        lock (Sync)
        {
            Stop("unload");
            Nearby.Clear();
            Instance.UnloadMod();
        }
    }

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI()
    {
        lock (Sync) Instance.DrawPassageHotkeyUI(Instance._hotkey);
    }

    // Observe only doors already considered by the game's local interaction system.
    // No scene-wide enumeration, and no native object references retained.
    [MethodHook(typeof(app.GimmickDoor), "onGmInteract_CheckOpenPopIcon", MethodHookType.Pre)]
    public static PreHookResult ObserveDoor(Span<ulong> args)
    {
        if (args.Length < 2) return PreHookResult.Continue;
        lock (Sync)
        {
            if (Nearby.Count >= 16 && !Nearby.ContainsKey(args[1]))
            {
                ulong oldest = 0; long time = long.MaxValue;
                foreach (var item in Nearby) if (item.Value < time) { oldest = item.Key; time = item.Value; }
                Nearby.Remove(oldest);
            }
            Nearby[args[1]] = Environment.TickCount64;
        }
        return PreHookResult.Continue;
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void BeforeUpdate() => Tick(true);
    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void AfterUpdate() => Tick(false);

    private static void Tick(bool pollKeys)
    {
        lock (Sync)
        {
            try
            {
                if (pollKeys && Instance.IsHotkeyPressed(Instance._hotkey))
                {
                    if (Active) Stop("OFF.", true);
                    else Start();
                }
                if (!Active) return;
                var now = Environment.TickCount64;
                var player = CurrentPlayer();
                if (!MatchesAnchor(player))
                {
                    Stop("Scene changed. OFF."); return;
                }
                if (player.IsEventStart)
                {
                    Stop("Event started. OFF."); return;
                }
                var pos = player.GameObject.Transform.Position;
                if (!Finite(pos.x, pos.y, pos.z))
                {
                    Stop("Invalid position. OFF."); return;
                }
                if (HorizontalDistanceSquared(pos.x, pos.z, _anchor.X, _anchor.Z) > MaxTravelDistance * MaxTravelDistance)
                {
                    Stop("Out of range. OFF."); return;
                }
                if (now >= _deadline) { Stop("Timed out. OFF.", true); return; }
                foreach (var target in _targets)
                {
                    if (!Apply(target)) { Stop("Door changed. OFF."); return; }
                }
            }
            catch (Exception error)
            {
                Report(error);
                Stop("Error. Stopped.");
            }
        }
    }

    private static void Start()
    {
        if (Active) return;
        var player = CurrentPlayer();
        var transform = player?.GameObject?.Transform;
        if (!Alive(transform) || player.CharacterPhysics?.IsLanded != true || player.IsEventStart)
        { SetStatus("Stand on solid ground outside events.", true); return; }
        var pos = transform.Position;
        if (!Finite(pos.x, pos.y, pos.z)) return;
        app.GimmickDoor nearest = null;
        var best = MaxDoorDistance * MaxDoorDistance;
        var now = Environment.TickCount64;
        foreach (var item in Nearby)
        {
            if (now - item.Value > 3000) continue;
            var door = Get<app.GimmickDoor>(item.Key);
            if (door?.GimmickContext is null || door._IsEventMode ||
                door.CurrentState != app.GimmickDoor.GM_DOOR_STATE.CLOSED || !Alive(door.GameObject?.Transform)) continue;
            var location = door.GameObject.Transform.Position;
            if (!Finite(location.x, location.y, location.z) || Math.Abs(location.y - pos.y) > 3f) continue;
            var distance = HorizontalDistanceSquared(pos.x, pos.z, location.x, location.z);
            if (distance <= best) { best = distance; nearest = door; }
        }
        if (nearest is null) { SetStatus("No closed door within 3.5 m.", true); return; }

        // Snapshot every affected object before changing anything. Never disable
        // the player's controller, gravity, terrain or a scene-wide collider.
        var doorTarget = Capture(nearest);
        var bolt = Get<app.Gm028>(AddressOf(nearest._LockGmCtrl?.LockedGimmick));
        var prepared = bolt is null ? new[] { doorTarget } : new[] { doorTarget, Capture(bolt) };
        _anchor = (AddressOf(player), AddressOf(player.Context), pos.x, pos.z);
        _targets = prepared; _deadline = now + DurationMs;
        foreach (var target in _targets) if (!Apply(target)) throw new InvalidOperationException("Door changed during activation.");
        SetStatus($"ON ({DurationMs / 1000}s).", true);
    }

    private static Target Capture(app.AppGimmickBase gimmick)
    {
        var bits = Get<ace.Bitset>(AddressOf(gimmick._EnableCollisionFlag));
        if (gimmick.GimmickContext is null || bits is null)
            throw new InvalidOperationException("Cannot safely snapshot the door collision flags.");
        var env = gimmick.EnvCollidersComponent;
        var target = new Target { Address = AddressOf(gimmick), Context = AddressOf(gimmick.GimmickContext),
            Env = AddressOf(env), EnvEnabled = env?.Enabled == true, Bits = AddressOf(bits),
            PressEnabled = bits.isOn((int)app.GimmickDef.COLLISION_TYPE.PRESS), StateStamp = StateStamp(gimmick) };
        var effectors = gimmick.GimmickContext.AIMapEffectorRegister?._AIMapEffectors;
        var count = effectors is null ? 0 : ((_System.Array)effectors).Length;
        if (count > 16) throw new InvalidOperationException("Too many door effectors to snapshot safely.");
        for (int i = 0; i < count; i++)
        {
            var effector = effectors[i];
            if (Alive(effector)) target.Effectors.Add((AddressOf(effector), effector._Enabled));
        }
        return target;
    }

    private static bool Apply(Target target)
    {
        var gimmick = Resolve(target);
        if (gimmick is null ||
            AddressOf(gimmick.EnvCollidersComponent) != target.Env ||
            AddressOf(gimmick._EnableCollisionFlag) != target.Bits ||
            Get<app.GimmickDoor>(target.Address)?._IsEventMode == true) return false;
        if (target.Env != 0 && gimmick.EnvCollidersComponent.Enabled) gimmick.changeEnvCollisionEnable(false);
        var bits = Get<ace.Bitset>(target.Bits);
        if (bits is null) return false;
        if (bits.isOn((int)app.GimmickDef.COLLISION_TYPE.PRESS))
            gimmick.changeCollisionEnable(app.GimmickDef.COLLISION_TYPE.PRESS, false);
        foreach (var entry in target.Effectors)
        {
            var effector = Get<app.AIMapEffectorController>(entry.Address);
            if (effector is null) return false;
            if (effector._Enabled) effector.changeEnable(false);
        }
        return true;
    }

    private static void Stop(string reason, bool announce = false)
    {
        var targets = _targets;
        _targets = Array.Empty<Target>();
        foreach (var target in targets)
        {
            app.AppGimmickBase gimmick;
            try
            {
                gimmick = Resolve(target);
                if (gimmick is null) continue;
            }
            catch (Exception error) { Report(error); continue; }
            // A native opening or lock break owns the new collision state.
            // Do not restore an old closed/locked snapshot over that operation.
            // Best effort per property: one destroyed effector must not prevent
            // restoration of a still-valid door collider or another target.
            try { if (target.Env != 0 && AddressOf(gimmick.EnvCollidersComponent) == target.Env) gimmick.changeEnvCollisionEnable(target.EnvEnabled); }
            catch (Exception error) { Report(error); }
            try { if (AddressOf(gimmick._EnableCollisionFlag) == target.Bits) gimmick.changeCollisionEnable(app.GimmickDef.COLLISION_TYPE.PRESS, target.PressEnabled); }
            catch (Exception error) { Report(error); }
            foreach (var entry in target.Effectors)
            {
                try { Get<app.AIMapEffectorController>(entry.Address)?.changeEnable(entry.Enabled); }
                catch (Exception error) { Report(error); }
            }
        }
        _anchor = default;
        if (targets.Length > 0 || announce) SetStatus(reason, announce);
    }

    private static app.AppGimmickBase Resolve(Target target)
    {
        var gimmick = Get<app.AppGimmickBase>(target.Address);
        return gimmick is not null && AddressOf(gimmick.GimmickContext) == target.Context &&
            StateStamp(gimmick) == target.StateStamp ? gimmick : null;
    }

    private static app.CharacterBase CurrentPlayer() =>
        API.GetManagedSingletonT<app.PlayerManager>()?.getControllingPlayerInfo()?.Character;
    private static bool MatchesAnchor(app.CharacterBase player) => Alive(player) &&
        AddressOf(player) == _anchor.Player && AddressOf(player.Context) == _anchor.Context &&
        Alive(player.GameObject?.Transform) && Alive(player.CharacterPhysics);
    private static T Get<T>(ulong address) where T : class => GetObject(address)?.TryAs<T>();
    private static bool Alive(object value) => GetObject(AddressOf(value)) is not null;
    private static ManagedObject GetObject(ulong address)
    {
        if (address == 0 || !ManagedObject.IsManagedObject(address)) return null;
        var obj = ManagedObject.ToManagedObject(address);
        return obj is not null && !obj.IsGoingToBeDestroyed() ? obj : null;
    }
    private static ulong AddressOf(object value) => (value as IProxyable)?.GetAddress() ?? 0;
    private static float HorizontalDistanceSquared(float x, float z, float otherX, float otherZ) =>
        (x - otherX) * (x - otherX) + (z - otherZ) * (z - otherZ);
    private static bool Finite(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    private static int StateStamp(app.AppGimmickBase gimmick)
    {
        var door = Get<app.GimmickDoor>(AddressOf(gimmick));
        if (door is not null) return (int)door.CurrentState;
        var bolt = Get<app.Gm028>(AddressOf(gimmick));
        return bolt is not null ? (int)bolt._State : -1;
    }

    // Restore before normal door motion starts, so native code can apply its
    // own open collision state after ours. These hooks never request opening.
    [MethodHook(typeof(app.GimmickDoor), "requestOpen", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorOpens(Span<ulong> args) => RestoreBeforeOpening(args);

    [MethodHook(typeof(app.GimmickDoor), "forceOpen", MethodHookType.Pre)]
    public static PreHookResult BeforeDoorForcedOpen(Span<ulong> args) => RestoreBeforeOpening(args);

    private static PreHookResult RestoreBeforeOpening(Span<ulong> args)
    {
        lock (Sync)
        {
            if (Active && args.Length > 1 && _targets[0].Address == args[1])
                Stop("Door opening. OFF.");
        }
        return PreHookResult.Continue;
    }

    private static void Report(Exception error) => Instance.LogErrorOnce("Door passage failed", error);

    private static void SetStatus(string text, bool announce)
    {
        Instance.Log(text);
        if (!announce) return;
        try
        {
            using var obj = ace.cGUIMessageInfo.REFType.CreateInstance(0);
            var message = obj?.As<ace.cGUIMessageInfo>();
            if (message is null) return;
            message.setMessageInfo("GoThroughDoor: " + text);
            API.GetManagedSingletonT<app.GUIManager>()?.requestAnnounce(message, 3f);
        }
        catch (Exception error) { Report(error); }
    }
}

// GoThroughDoor's single-setting layout; shared utility sources above stay verbatim.
public abstract partial class ModBase
{
    protected void DrawPassageHotkeyUI(ModConfig<ModHotkey> hotkey)
    {
        if (ImGui.TreeNode(ModName))
        {
            try { hotkey.Draw(ModName + ".Hotkey"); }
            finally { ImGui.TreePop(); }
        }
        else _capturingHotkeyId = null;
        if (_configDirty) SaveConfig();
    }

    protected bool DrawPassageBinding(string label, ref ModHotkey value)
    {
        var capturing = _capturingHotkeyId == label;
        var changed = false;
        if (capturing && TryReadPressedHotkey(out var captured))
        {
            value = captured;
            _capturingHotkeyId = null;
            capturing = false;
            changed = true;
        }
        if (ImGui.Button($"Hotkey: {(capturing ? "..." : value.ToString())}##{label}"))
            _capturingHotkeyId = capturing ? null : label;
        return changed;
    }
}
