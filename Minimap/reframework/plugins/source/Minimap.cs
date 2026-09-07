using System;
using System.Collections.Generic;
using System.Threading;
using Hexa.NET.ImGui;
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

// BEGIN copied source: Util/ModBase.Hotkey.cs
// Source blob SHA-1: 9aba964758d536ae9257a9953438bf77a5b3af9a
// Source commit: 7eadaa1411ca922a2fbbf34f067928275e4c53ec
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

public sealed class Minimap : ModBase
{
    private const int TilePixels = 2048;
    private const float WorldToMapPixels = 6.4f;
    private const int TileSlotCount = 16;
    private const string GuiResourcePath = "GUI/Minimap/MinimapMissionAreas.gui";
    private const string GuiGameObjectName = "Minimap_GUI";
    private const string WindowName = "Minimap_Window";
    private const string GroupName = "Minimap_Group";
    private const string OverlayGroupName = "Minimap_OverlayGroup";
    private const string PlayerGroupName = "Minimap_PlayerGroup";
    private const string CircleMaskName = "Minimap_CircleMask";
    private const string RectangleMaskName = "Minimap_RectangleMask";
    private const string CircleBorderName = "Minimap_CircleBorder";
    private const string BorderNamePrefix = "Minimap_Border_";
    private const string PlayerMarkerPrefix = "Minimap_PlayerMarker_";
    private const string CameraArrowPrefix = "Minimap_CameraArrow_";
    private const string WallMarkerPrefix = "Minimap_WallMarker_";
    private const string HiddenChestMarkerPrefix = "Minimap_HiddenChestMarker_";
    private const string ChestMarkerPrefix = "Minimap_ChestMarker_";
    private const string LockedGateMarkerPrefix = "Minimap_LockedGateMarker_";
    private const string MissionRangePrefix = "Minimap_MissionRange_";
    private const string EntranceMarkerPrefix = "Minimap_EntranceMarker_";
    private const string LadderMarkerPrefix = "Minimap_LadderMarker_";
    private const string CollectibleMarkerPrefix = "Minimap_CollectibleMarker_";
    private const string MissionMarkerPrefix = "Minimap_MissionMarker_";
    private const string FootprintMarkerPrefix = "Minimap_FootprintMarker_";
    private const string TileNamePrefix = "Minimap_Tile_";
    private const long RetryDelayMilliseconds = 1000;
    private const long MarkerRefreshMilliseconds = 1000;
    private const long GuiLoadTimeoutMilliseconds = 10000;
    private const long GuiResolveTimeoutMilliseconds = 10000;
    private const int MaxGuiPlayObjectsToInspect = 512;
    private const int WallMarkerCount = 12;
    private const int HiddenChestMarkerCount = 24;
    private const int MaxChestObjectsToInspect = 1024;
    private const int MaxChestContextsToInspect = 4096;
    private const int ChestTakenSaveState = 15;
    private const int ChestMarkerCount = 16;
    private const int LockedGateMarkerCount = 24;
    private const int MissionRangeCount = 16;
    private const int EntranceMarkerCount = 16;
    private const int LadderMarkerCount = 32;
    private const int CollectibleMarkerCount = 32;
    private const int MissionMarkerCount = 40;
    private const int MaxMissionBeaconsToInspect = 512;
    private const int FootprintMarkerCount = 16;
    private const ushort MapDrawPriority = ushort.MaxValue - 1;
    private const ushort OverlayDrawPriority = ushort.MaxValue;
    private const ushort PlayerDrawPriority = ushort.MaxValue;
    private const float BorderThickness = 1.5f;
    private const float CameraTipDistance = 38.0f;
    private const float CameraRearDistance = 24.0f;
    private const float CameraHalfWidth = 7.0f;
    private const float CameraArrowThickness = 2.0f;
    private const int CameraArrowSlotCount = 9; // Existing prefab slots; only two are drawn.
    private const float PlayerMarkerSize = 50.0f;
    private const float WallMarkerSize = 48.0f;
    private const float ChestMarkerSize = 48.0f;
    private const float EntranceMarkerSize = 48.0f;
    private const float LadderMarkerSize = 48.0f;
    private const float CollectibleMarkerSize = 48.0f;
    private const float MissionMarkerSize = 48.0f;
    private const float FootprintMarkerSize = 9.0f;
    private const float MaximumOffset = 16383.0f;

    // UV patterns in uvs000122.uvs, not cGUIMapListIcon.ICON_ID values.
    private const uint ArmBreakWallIconPattern = 3;
    private const uint EyeHideWallIconPattern = 2;
    private const uint InvasionWallIconPattern = 8;
    private const uint LockedGateIconPattern = 10; // Native red padlock in uvs000122.
    private const uint ChestIconPattern = 2;
    private const uint EntranceIconPattern = 7;
    private const uint LadderIconPattern = 9;

    // lib000122's native map-object animation: SUB_MISTERY frame 4 and
    // MEDICINE_BAG_MATERIAL frame 24 use these uvs000120 patterns.
    private const uint SubMysteryIconPattern = 2;
    private const uint MedicineBagMaterialIconPattern = 24;

    // lib000121 uses uvs000121: sequence 0 for active missions, sequence 2
    // for prefaces; patterns 0/1/2 are main/character/side missions.
    private const uint MissionPrefaceSequence = 2;
    private const uint MainMissionIconPattern = 0;
    private const uint CharacterMissionIconPattern = 1;
    private const uint SideMissionIconPattern = 2;

    // Native ColorPreset values from lib000121/122. Keep color selection
    // separate from UV patterns: the same atlas can contain both categories.
    private const string MapSymbolColorPresetId = "1e50f708-849d-48fe-95bd-0919f7287e35";
    private const string MissionColorPresetId = "a2f047c3-da95-4bd3-ad51-10bcba41297f";

    private enum MarkerColor
    {
        Original,
        MapSymbol,
        Mission,
    }

    private const int MapFixed = 0;
    private const int PlayerFixed = 1;
    private const int CameraFixed = 2;
    private const int RectangleShape = 0;
    private const int CircleShape = 1;

    private static readonly string[] OrientationNames =
    {
        "North",
        "Face",
        "sight",
    };

    private static readonly string[] ShapeNames =
    {
        "Rectangle",
        "Circle",
    };

    private static readonly Minimap Instance = new();
    private static readonly List<MapTile> Tiles = new();
    private static readonly List<MarkerPosition> WallPositions = new();
    private static readonly List<MarkerPosition> HiddenChestPositions = new();
    private static readonly List<MarkerPosition> ChestPositions = new();
    private static readonly List<MarkerPosition> LockedGatePositions = new();
    private static readonly List<MissionRange> MissionRanges = new();
    private static readonly List<MarkerPosition> EntrancePositions = new();
    private static readonly List<MarkerPosition> LadderPositions = new();
    private static readonly List<MarkerPosition> CollectiblePositions = new();
    private static readonly List<MarkerPosition> MissionPositions = new();
    private static readonly Dictionary<ulong, MarkerColor> MarkerColors = new();
    private static _System.Guid[] _markerColorPresets = Array.Empty<_System.Guid>();

    private readonly ModConfig<ModHotkey> _toggleHotkey;
    private readonly ModConfig<int> _orientation;
    private readonly ModConfig<int> _shape;
    private readonly ModConfig<float> _width;
    private readonly ModConfig<float> _height;
    private readonly ModConfig<float> _pixelsPerMeter;
    private readonly ModConfig<float> _rightOffset;
    private readonly ModConfig<float> _topOffset;
    private readonly ModConfig<bool> _showOniWalls;
    private readonly ModConfig<bool> _showChests;
    private readonly ModConfig<bool> _showHiddenChests;
    private readonly ModConfig<bool> _showLockedGates;
    private readonly ModConfig<bool> _showEntrances;
    private readonly ModConfig<bool> _showLadders;
    private readonly ModConfig<bool> _showCollectibles;
    private readonly ModConfig<bool> _showMissions;
    private readonly ModConfig<bool> _showFootprints;
    private bool _isVisible = true;

    private static MapDefinition _map;
    private static REFrameworkNET.Resource _guiResource;
    private static ManagedObject _guiHolderObject;
    private static via.GameObject _guiGameObject;
    private static via.gui.GUI _gui;
    private static via.gui.View _guiView;
    private static via.gui.Window _guiWindow;
    private static via.gui.Panel _mapGroup;
    private static via.gui.Panel _overlayGroup;
    private static via.gui.Circle _circleMask;
    private static via.gui.Circle _circleBorder;
    private static via.gui.Texture _rectangleMask;
    private static via.gui.Rect[] _rectangleBorders = Array.Empty<via.gui.Rect>();
    private static via.gui.Texture _playerMarker;
    private static via.gui.Rect[] _cameraArrows = Array.Empty<via.gui.Rect>();
    private static via.gui.Texture[] _wallMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _hiddenChestMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _chestMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _lockedGateMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Circle[] _missionRanges = Array.Empty<via.gui.Circle>();
    private static via.gui.Texture[] _entranceMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _ladderMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _collectibleMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _missionMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _footprintMarkers = Array.Empty<via.gui.Texture>();
    private static via.gui.Texture[] _tileSlots = Array.Empty<via.gui.Texture>();
    private static long _guiLoadStartedAt;
    private static long _guiReadyAt;
    private static long _nextRetryTick;
    private static long _nextMarkerRefreshTick;
    private static int _errorReported;
    private static int _markerErrorReported;
    private static int _missionErrorReported;
    private static int _chestErrorReported;
    private static int _cleanupErrorReported;

    private Minimap() : base("Minimap", "1.1")
    {
        _toggleHotkey = AddHotkeyConfig("Toggle hotkey", ImGuiKey.F6);
        _orientation = AddRadioGroupConfig(
            "Orientation",
            MapFixed,
            OrientationNames);
        _shape = AddRadioGroupConfig(
            "Shape",
            RectangleShape,
            ShapeNames);
        _width = AddFloatConfig("Width", 420.0f, 20.0f, 800.0f, "%.0f");
        _height = AddFloatConfig("Height", 280.0f, 20.0f, 540.0f, "%.0f");
        _pixelsPerMeter = AddFloatConfig(
            "Zoom", 4.8f, 0.5f, 10.0f, "%.1f px/m");
        _rightOffset = AddPixelInputConfig(
            "Right offset (px)", 36.0f, 0.0f, MaximumOffset, key: "Right margin");
        _topOffset = AddPixelInputConfig(
            "Top offset (px)", 80.0f, 0.0f, MaximumOffset, key: "Top margin");
        _showOniWalls = AddBoolConfig("Show Oni walls", true);
        _showChests = AddBoolConfig("Show chests", true);
        _showHiddenChests = AddBoolConfig("Show hidden chests", true);
        _showLockedGates = AddBoolConfig("Show locked doors", true);
        _showEntrances = AddBoolConfig("Show area entrances/exits", true);
        _showLadders = AddBoolConfig("Show ladders", true);
        _showCollectibles = AddBoolConfig("Show collectibles", true);
        _showMissions = AddBoolConfig("Show missions", true);
        _showFootprints = AddBoolConfig("Show footprints", true);
    }

    [PluginEntryPoint]
    public static void Main()
    {
        Instance.InitializeMod();
        Instance.Log("Using the game's native map textures.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        ResetMap();
        DestroyNativeGui();
        _nextRetryTick = 0;
        _nextMarkerRefreshTick = 0;
        _errorReported = 0;
        _markerErrorReported = 0;
        _missionErrorReported = 0;
        _chestErrorReported = 0;
        _cleanupErrorReported = 0;
        Instance.UnloadMod();
        Instance.Log("Unloaded and removed native map textures.");
    }

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI() => Instance.DrawConfigUI();

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        try
        {
            Instance.ProcessToggleHotkey();
            if (!Instance._isVisible || !TryGetGameContext(
                    out var guiManager,
                    out var root,
                    out var fixedStage,
                    out var currentArea,
                    out var currentFloor,
                    out var playerTransform))
            {
                HideMap();
                return;
            }

            if (Environment.TickCount64 < _nextRetryTick || !TryEnsureNativeGui())
            {
                HideMap();
                return;
            }

            var stageKey = unchecked((int)(uint)fixedStage);
            if (_map is null || _map.StageKey != stageKey ||
                _map.PlayerArea != currentArea || _map.PlayerFloor != currentFloor)
            {
                if (Environment.TickCount64 < _nextRetryTick)
                {
                    HideMap();
                    return;
                }

                if (!TryBuildMap(guiManager, fixedStage, currentArea, currentFloor, out var map))
                {
                    _nextRetryTick = Environment.TickCount64 + RetryDelayMilliseconds;
                    HideMap();
                    return;
                }

                if (map.Tiles.Length > _tileSlots.Length)
                {
                    throw new InvalidOperationException(
                        $"Map needs {map.Tiles.Length} texture slots, " +
                        $"but the prefab provides {_tileSlots.Length}.");
                }

                // Area zones may change within one map sheet. Reuse its
                // textures unless the selected sheet actually changes.
                if (_map is null || _map.StageKey != map.StageKey ||
                    _map.MapIndex != map.MapIndex || _map.AreaIndex != map.AreaIndex)
                {
                    ResetMap();
                }
                _map = map;
                _nextMarkerRefreshTick = 0;
            }

            if (Tiles.Count < _map.Tiles.Length)
            {
                if (Environment.TickCount64 < _nextRetryTick ||
                    !TryCreateNextTile(_map))
                {
                    _nextRetryTick = Environment.TickCount64 + RetryDelayMilliseconds;
                    HideMap();
                    return;
                }

                if (Tiles.Count < _map.Tiles.Length)
                {
                    HideMap();
                    return;
                }

                Instance.Log(
                    $"Native map ready: {_map.Columns}x{_map.Rows} tiles, " +
                    $"root ({_map.RootX:0.#}, {_map.RootY:0.#}), " +
                    $"area {_map.PlayerArea}, floor {_map.PlayerFloor}, " +
                    $"sheet {_map.MapIndex}/{_map.AreaIndex}.");
            }

            var hudScreen = root.ScreenSize;
            var sceneView = _gui.SceneView;
            if (!IsAlive(sceneView) ||
                !float.IsFinite(hudScreen.h) || hudScreen.h <= 0.0f)
            {
                HideMap();
                return;
            }

            // The HUD canvas stays 16:9 on ultrawide displays. Keep its logical
            // height, but match the render area so every map element scales uniformly.
            // WindowSize can include letterboxing; Size is the actual render area.
            var renderSize = sceneView.Size;
            var screenHeight = hudScreen.h;
            var screenWidth = screenHeight * (renderSize.w / renderSize.h);
            if (!float.IsFinite(renderSize.w) || renderSize.w <= 0.0f ||
                !float.IsFinite(renderSize.h) || renderSize.h <= 0.0f ||
                !float.IsFinite(screenWidth) || screenWidth <= 0.0f)
            {
                HideMap();
                return;
            }

            var nativeScreen = _guiView.ScreenSize;
            if (nativeScreen.w != screenWidth || nativeScreen.h != screenHeight)
            {
                nativeScreen.w = screenWidth;
                nativeScreen.h = screenHeight;
                _guiView.ScreenSize = nativeScreen;
            }

            var displayWidth = Math.Clamp(Instance._width.Value, 1.0f, screenWidth);
            var displayHeight = Math.Clamp(Instance._height.Value, 1.0f, screenHeight);
            if (Instance._shape.Value == CircleShape)
            {
                displayWidth = displayHeight = MathF.Min(displayWidth, displayHeight);
            }

            var left = Math.Clamp(
                screenWidth - Instance._rightOffset.Value - displayWidth,
                0.0f,
                screenWidth - displayWidth);
            var top = Math.Clamp(
                Instance._topOffset.Value,
                0.0f,
                screenHeight - displayHeight);
            var position = playerTransform.Position;
            var mapScale = _map.IsFlipSideUp ? -WorldToMapPixels : WorldToMapPixels;
            var mapX = _map.RootX + position.x * mapScale;
            var mapY = _map.RootY + position.z * mapScale;
            var forward = playerTransform.AxisZ;
            var forwardX = forward.x * MathF.Sign(mapScale);
            var forwardY = forward.z * MathF.Sign(mapScale);
            var hasCameraDirection = TryGetCameraDirection(
                MathF.Sign(mapScale),
                out var cameraForwardX,
                out var cameraForwardY);
            var mapRotation = GetMapRotation(
                Instance._orientation.Value, forwardX, forwardY,
                hasCameraDirection, cameraForwardX, cameraForwardY);
            RotateDirection(ref forwardX, ref forwardY, mapRotation);
            RotateDirection(ref cameraForwardX, ref cameraForwardY, mapRotation);
            UpdateNativeMap(
                mapX,
                mapY,
                left,
                top,
                displayWidth,
                displayHeight,
                mapRotation,
                Instance._shape.Value == CircleShape);
            UpdateNativeMarkers(
                guiManager,
                position.x,
                position.z,
                left,
                top,
                displayWidth,
                displayHeight,
                mapScale,
                mapRotation,
                Instance._shape.Value == CircleShape);
            UpdateNativeOverlay(
                left,
                top,
                displayWidth,
                displayHeight,
                forwardX,
                forwardY,
                cameraForwardX,
                cameraForwardY,
                hasCameraDirection,
                Instance._shape.Value == CircleShape);
            Volatile.Write(ref _errorReported, 0);
        }
        catch (Exception exception)
        {
            HideMap();
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"Map update failed and will retry: {exception}", ModLogLevel.Error);
            }
        }
    }

    private void ProcessToggleHotkey()
    {
        if (!IsHotkeyPressed(_toggleHotkey))
        {
            return;
        }

        _isVisible = !_isVisible;
        Log($"Toggled {(_isVisible ? "on" : "off")} by hotkey.");
    }

    private static bool TryGetGameContext(
        out app.GUIManager guiManager,
        out via.gui.View root,
        out app.EnvDef.StageID_Fixed fixedStage,
        out app.EnvDef.AreaID_Fixed currentArea,
        out app.EnvDef.FIELD_ORDER_Fixed currentFloor,
        out via.Transform playerTransform)
    {
        guiManager = null;
        root = null;
        fixedStage = default;
        currentArea = default;
        currentFloor = default;
        playerTransform = null;

        var gameFlow = API.GetManagedSingletonT<app.GameFlowManager>();
        if (!IsAlive(gameFlow) || !gameFlow.IsIngameStable)
        {
            return false;
        }

        var pauseManager = API.GetManagedSingletonT<app.PauseManager>();
        if (!IsAlive(pauseManager) || pauseManager.IsMenuPause)
        {
            return false;
        }

        guiManager = API.GetManagedSingletonT<app.GUIManager>();
        if (!IsAlive(guiManager) ||
            guiManager.isVisibleGUIApp(app.GUIID.ID.UI010200) ||
            !guiManager.isVisibleGUIApp(app.GUIID.ID.UI020301))
        {
            return false;
        }

        var rawHost = (guiManager as IObject)?.Call(
            "getGUI", (int)app.GUIID.ID.UI020301) as ManagedObject;
        root = (rawHost?.GetField("_Root") as ManagedObject)?.As<via.gui.View>();
        if (!IsAlive(root))
        {
            return false;
        }

        playerTransform = API.GetManagedSingletonT<app.PlayerManager>()
            ?.getControllingPlayerInfo()?.Object?.Transform;
        var environment = API.GetManagedSingletonT<app.EnvironmentManager>();
        var info = environment?.EnvInfoManager;
        if (!IsAlive(playerTransform) || !IsAlive(environment) || !IsAlive(info) ||
            !Enum.TryParse(environment.StageID.ToString(), out fixedStage) ||
            !Enum.TryParse(environment._AreaID.ToString(), out currentArea))
        {
            return false;
        }
        currentFloor = info.getPlayerFieldOrder();
        return true;
    }

    private static bool TryGetCameraDirection(
        float mapSign,
        out float directionX,
        out float directionY)
    {
        directionX = 0.0f;
        directionY = -1.0f;

        var camera = API.GetManagedSingletonT<app.CameraManager>()
            ?._InGameCameraOperator
            ?.getActiveVirtualCamera();
        var forward = camera?.Forward;
        if (forward is null)
        {
            return false;
        }

        var mapX = forward.x * mapSign;
        var mapY = forward.z * mapSign;
        var lengthSquared = mapX * mapX + mapY * mapY;
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.0001f)
        {
            return false;
        }

        directionX = mapX;
        directionY = mapY;
        return true;
    }

    private static float GetMapRotation(
        int orientation, float playerX, float playerY,
        bool hasCameraDirection, float cameraX, float cameraY)
    {
        if (orientation != PlayerFixed && orientation != CameraFixed) return 0.0f;

        // Use the player's heading if the camera has no usable horizontal direction.
        var useCamera = orientation == CameraFixed && hasCameraDirection;
        var x = useCamera ? cameraX : playerX;
        var y = useCamera ? cameraY : playerY;
        return -MathF.Atan2(x, -y);
    }

    private static void RotateDirection(ref float x, ref float y, float rotation)
    {
        var cosine = MathF.Cos(rotation);
        var sine = MathF.Sin(rotation);
        var rotatedX = cosine * x - sine * y;
        y = sine * x + cosine * y;
        x = rotatedX;
    }

    private static bool TryBuildMap(
        app.GUIManager guiManager,
        app.EnvDef.StageID_Fixed fixedStage,
        app.EnvDef.AreaID_Fixed currentArea,
        app.EnvDef.FIELD_ORDER_Fixed currentFloor,
        out MapDefinition map)
    {
        map = null;
        var various = API.GetManagedSingletonT<app.VariousDataManager>();
        var mapData = various?.Setting?.MapData;
        var allMaps = mapData?._Datas;
        if (!IsAlive(various) || !IsAlive(mapData) || !IsAlive(allMaps))
        {
            return false;
        }

        app.user_data.MapData.cArea area = null;
        var selectedMapIndex = -1;
        var selectedAreaIndex = -1;
        var stageKey = unchecked((int)(uint)fixedStage);
        for (var mapIndex = 0; mapIndex < allMaps.Count && area is null; ++mapIndex)
        {
            var areas = allMaps[mapIndex]?.Areas;
            if (!IsAlive(areas))
            {
                continue;
            }

            for (var areaIndex = 0; areaIndex < areas.Count; ++areaIndex)
            {
                var candidate = areas[areaIndex];
                if (!IsAlive(candidate) || candidate.StageID?.Value != stageKey)
                {
                    continue;
                }
                var fields = candidate.AreaFields;
                if (!IsAlive(fields)) continue;
                // Native MapData.tryGetData uses an exact AreaID/Floor pair.
                // INVALID is a real floor entry, not a wildcard for all levels.
                for (var fieldIndex = 0; fieldIndex < fields.Count; ++fieldIndex)
                {
                    var field = fields[fieldIndex];
                    if (!IsAlive(field?.AreaID) || !IsAlive(field.Floor) ||
                        (app.EnvDef.AreaID_Fixed)field.AreaID.Value != currentArea ||
                        (app.EnvDef.FIELD_ORDER_Fixed)field.Floor.Value != currentFloor)
                    {
                        continue;
                    }
                    area = candidate;
                    selectedMapIndex = mapIndex;
                    selectedAreaIndex = areaIndex;
                    break;
                }
                if (area is not null) break;
            }
        }

        var textureRows = area?.Textures;
        var areaFields = area?.AreaFields;
        if (!IsAlive(area) || !IsAlive(textureRows) || !IsAlive(areaFields))
        {
            return false;
        }

        // Match the native map's AreaID/FieldOrder pairs, including INVALID floors.
        // Copy values so this definition does not retain native user-data objects.
        var markerFields = new Dictionary<
            app.EnvDef.AreaID_Fixed, HashSet<app.EnvDef.FIELD_ORDER_Fixed>>();
        for (var index = 0; index < areaFields.Count; ++index)
        {
            var field = areaFields[index];
            var areaId = field?.AreaID;
            var floor = field?.Floor;
            if (!IsAlive(field) || !IsAlive(areaId) || !IsAlive(floor))
            {
                continue;
            }

            var fixedArea = (app.EnvDef.AreaID_Fixed)areaId.Value;
            if (!markerFields.TryGetValue(fixedArea, out var floors))
            {
                floors = new HashSet<app.EnvDef.FIELD_ORDER_Fixed>();
                markerFields.Add(fixedArea, floors);
            }

            floors.Add((app.EnvDef.FIELD_ORDER_Fixed)floor.Value);
        }

        var definitions = new List<TileDefinition>();
        var columns = 0;
        for (var row = 0; row < textureRows.Count; ++row)
        {
            var textureIds = textureRows[row]?.Tex;
            if (!IsAlive(textureIds))
            {
                continue;
            }

            columns = Math.Max(columns, textureIds.Count);
            for (var column = 0; column < textureIds.Count; ++column)
            {
                var serialized = textureIds[column];
                if (!IsAlive(serialized) ||
                    (app.MapTextureResourceID.ID_Fixed)serialized.Value ==
                        app.MapTextureResourceID.ID_Fixed.INVALID)
                {
                    continue;
                }

                var prefabPath = guiManager.findTextureMap(serialized.Value)?.ResourcePath;
                var texturePath = GetTexturePath(prefabPath);
                if (texturePath is null)
                {
                    return false;
                }

                definitions.Add(new TileDefinition(row, column, texturePath));
            }
        }

        if (definitions.Count == 0 || columns == 0)
        {
            return false;
        }

        map = new MapDefinition(
            stageKey,
            currentArea,
            currentFloor,
            selectedMapIndex,
            selectedAreaIndex,
            area.Root.x,
            area.Root.y,
            area.IsFlipSideUp,
            textureRows.Count,
            columns,
            definitions.ToArray(),
            markerFields);
        return true;
    }

    private static bool TryEnsureNativeGui()
    {
        try
        {
            if (!IsAlive(_gui))
            {
                ResetMap();
                DestroyNativeGui();
                CreateNativeGui();
                return false;
            }

            if (!_gui.Ready || !IsAlive(_gui.View))
            {
                if (Environment.TickCount64 - _guiLoadStartedAt >=
                    GuiLoadTimeoutMilliseconds)
                {
                    throw new TimeoutException(
                        $"GUI resource did not become ready: {GuiResourcePath}.");
                }

                return false;
            }

            if (_guiReadyAt == 0)
            {
                _guiReadyAt = Environment.TickCount64;
                return false;
            }

            if (_tileSlots.Length == TileSlotCount)
            {
                return true;
            }

            try
            {
                ResolveNativeGui(_gui.View);
            }
            catch (InvalidOperationException)
                when (Environment.TickCount64 - _guiReadyAt <
                    GuiResolveTimeoutMilliseconds)
            {
                return false;
            }
            Instance.Log("Loaded the prebuilt native GUI resource.");
            return true;
        }
        catch (Exception exception)
        {
            ResetMap();
            DestroyNativeGui();
            _nextRetryTick = Environment.TickCount64 + RetryDelayMilliseconds;
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log(
                    $"Native GUI loading will retry: {exception}",
                    ModLogLevel.Error);
            }

            return false;
        }
    }

    private static void CreateNativeGui()
    {
        _guiResource = CreateOwnedResource(
            "via.gui.GUIResource", GuiResourcePath);
        _guiHolderObject = _guiResource?.CreateHolder("via.gui.GUIResourceHolder");
        var holder = _guiHolderObject?.TryAs<via.gui.GUIResourceHolder>();
        if (_guiResource is null || !IsAlive(holder))
        {
            throw new InvalidOperationException(
                $"Could not create GUI resource holder for {GuiResourcePath}.");
        }

        _guiGameObject = via.GameObject.create(GuiGameObjectName);
        var runtimeType = via.gui.GUI.REFType.RuntimeType?.As<_System.Type>();
        var component = _guiGameObject?.createComponent(runtimeType);
        _gui = ManagedObject.ToManagedObject(GetAddress(component))?.TryAs<via.gui.GUI>();
        if (!IsAlive(_guiGameObject) || !IsAlive(_gui))
        {
            throw new InvalidOperationException("Could not create the Minimap GUI component.");
        }

        _gui.Enabled = false;
        _gui.Asset = holder;
        _guiLoadStartedAt = Environment.TickCount64;
        _guiReadyAt = 0;
    }

    private static void ResolveNativeGui(via.gui.View view)
    {
        _guiView = view;
        _guiView.Visible = true;
        _guiView.HitVisible = false;
        _guiView.Interactive = false;
        _guiWindow = FindNamedPlayObject(view, WindowName)?.TryAs<via.gui.Window>();
        _mapGroup = FindNamedPlayObject(view, GroupName)?.TryAs<via.gui.Panel>();
        _overlayGroup = FindNamedPlayObject(view, OverlayGroupName)
            ?.TryAs<via.gui.Panel>();
        _circleMask = FindNamedPlayObject(view, CircleMaskName)?.TryAs<via.gui.Circle>();
        _circleBorder = FindNamedPlayObject(view, CircleBorderName)
            ?.TryAs<via.gui.Circle>();
        _rectangleMask = FindNamedPlayObject(view, RectangleMaskName)
            ?.TryAs<via.gui.Texture>();
        var borders = FindRects(view, BorderNamePrefix, 4);
        var playerGroup = FindNamedPlayObject(view, PlayerGroupName)
            ?.TryAs<via.gui.Panel>();
        var playerMarker = FindNamedPlayObject(view, $"{PlayerMarkerPrefix}00")
            ?.TryAs<via.gui.Texture>();
        var cameraArrows = FindRects(
            view, CameraArrowPrefix, CameraArrowSlotCount);
        var wallMarkers = FindOptionalTextures(
            view, WallMarkerPrefix, WallMarkerCount);
        var hiddenChestMarkers = FindOptionalTextures(
            view, HiddenChestMarkerPrefix, HiddenChestMarkerCount);
        var chestMarkers = FindOptionalTextures(
            view, ChestMarkerPrefix, ChestMarkerCount);
        var lockedGateMarkers = FindOptionalTextures(
            view, LockedGateMarkerPrefix, LockedGateMarkerCount);
        var missionRanges = new via.gui.Circle[MissionRangeCount];
        for (var index = 0; index < missionRanges.Length; ++index)
        {
            var name = $"{MissionRangePrefix}{index:00}";
            missionRanges[index] = FindNamedPlayObject(view, name)?.TryAs<via.gui.Circle>()
                ?? throw new InvalidOperationException($"The Minimap GUI resource is missing {name}.");
        }
        var entranceMarkers = FindOptionalTextures(
            view, EntranceMarkerPrefix, EntranceMarkerCount);
        var ladderMarkers = FindOptionalTextures(
            view, LadderMarkerPrefix, LadderMarkerCount);
        var collectibleMarkers = FindOptionalTextures(
            view, CollectibleMarkerPrefix, CollectibleMarkerCount);
        var missionMarkers = FindOptionalTextures(
            view, MissionMarkerPrefix, MissionMarkerCount);
        var footprintMarkers = FindOptionalTextures(
            view, FootprintMarkerPrefix, FootprintMarkerCount);
        var slots = new via.gui.Texture[TileSlotCount];
        for (var index = 0; index < slots.Length; ++index)
        {
            slots[index] = FindNamedPlayObject(view, $"{TileNamePrefix}{index:00}")
                ?.TryAs<via.gui.Texture>();
        }

        if (!IsAlive(_guiWindow) || !IsAlive(_mapGroup) || !IsAlive(_overlayGroup) ||
            !IsAlive(_circleMask) || !IsAlive(_circleBorder) ||
            !IsAlive(_rectangleMask) ||
            Array.Exists(borders, border => !IsAlive(border)) ||
            !IsAlive(playerGroup) || !IsAlive(playerMarker) ||
            Array.Exists(cameraArrows, part => !IsAlive(part)) ||
            Array.Exists(slots, slot => !IsAlive(slot)))
        {
            throw new InvalidOperationException(
                "The Minimap GUI resource does not contain the expected named nodes.");
        }

        _rectangleBorders = borders;
        _playerMarker = playerMarker;
        _cameraArrows = cameraArrows;
        _wallMarkers = wallMarkers;
        _chestMarkers = chestMarkers;
        _hiddenChestMarkers = hiddenChestMarkers;
        _lockedGateMarkers = lockedGateMarkers;
        _missionRanges = missionRanges;
        _entranceMarkers = entranceMarkers;
        _ladderMarkers = ladderMarkers;
        _collectibleMarkers = collectibleMarkers;
        _missionMarkers = missionMarkers;
        _footprintMarkers = footprintMarkers;
        _guiWindow.ResolutionAdjust = false;
        _guiWindow.SafeAreaAdjust = false;
        _mapGroup.Visible = false;
        _mapGroup.HitVisible = false;
        _mapGroup.Interactive = false;
        _mapGroup.MaskMode = via.gui.MaskMode.Keep;
        _mapGroup.Priority = MapDrawPriority;

        _overlayGroup.Visible = false;
        _overlayGroup.HitVisible = false;
        _overlayGroup.Interactive = false;
        _overlayGroup.MaskMode = via.gui.MaskMode.Disable;
        _overlayGroup.Priority = OverlayDrawPriority;

        // Priorities sort siblings; raise the group above every other icon group.
        playerGroup.Priority = PlayerDrawPriority;

        _circleMask.Visible = false;
        _circleMask.HitVisible = false;
        _circleMask.MaskType = via.gui.MaskType.Mask;
        _circleMask.ControlPoint = via.gui.ControlPoint.CenterCenter;

        _rectangleMask.Visible = false;
        _rectangleMask.HitVisible = false;
        _rectangleMask.AssetType = via.gui.TextureAssetType.Texture;
        _rectangleMask.UVType = via.gui.UVValueType.Rect;
        _rectangleMask.ControlPoint = via.gui.ControlPoint.LeftTop;
        _rectangleMask.MaskType = via.gui.MaskType.Mask;

        foreach (var circle in _missionRanges)
        {
            circle.Visible = false;
            circle.HitVisible = false;
            circle.ControlPoint = via.gui.ControlPoint.CenterCenter;
            circle.MaskType = via.gui.MaskType.Target;
        }

        _circleBorder.Visible = false;
        _circleBorder.HitVisible = false;
        _circleBorder.ControlPoint = via.gui.ControlPoint.CenterCenter;
        _circleBorder.MaskType = via.gui.MaskType.NonTarget;

        foreach (var rectangle in EnumerateOverlayRectangles())
        {
            rectangle.Visible = false;
            rectangle.HitVisible = false;
            rectangle.ControlPoint = via.gui.ControlPoint.CenterCenter;
            rectangle.MaskType = via.gui.MaskType.NonTarget;
        }

        _markerColorPresets = new[]
        {
            _System.Guid.Empty,
            _System.Guid.Parse(MapSymbolColorPresetId),
            _System.Guid.Parse(MissionColorPresetId),
        };
        MarkerColors.Clear();
        ConfigureMarkerTexture(_playerMarker);

        foreach (var textures in new[]
                 {
                     _wallMarkers,
                     _chestMarkers,
                     _hiddenChestMarkers,
                     _lockedGateMarkers,
                     _entranceMarkers,
                     _ladderMarkers,
                     _collectibleMarkers,
                     _missionMarkers,
                     _footprintMarkers,
                 })
        {
            foreach (var texture in textures)
            {
                ConfigureMarkerTexture(texture);
            }
        }

        // Reuse the player's uvs000122 atlas for the native chest symbol.
        foreach (var textures in new[] { _chestMarkers, _hiddenChestMarkers })
        {
            foreach (var texture in textures)
            {
                texture.UVSequence = _playerMarker.UVSequence;
                texture.UVSequenceNo = _playerMarker.UVSequenceNo;
                texture.UVPatternNo = ChestIconPattern;
            }
        }

        // Dedicated hidden-chest slots keep the tint separate from ordinary chests.
        // Apply after every GUI load, including when the prefab is already cached.
        foreach (var texture in _hiddenChestMarkers)
        {
            texture.ColorPreset = _System.Guid.Empty;
            var color = texture.Color;
            color.r = 255;
            color.g = 80;
            color.b = 80;
            color.a = 255;
            texture.Color = color;
        }

        Instance.Log(
            $"Native marker nodes: player={(IsAlive(_playerMarker) ? 1 : 0)}/1, " +
            $"walls={_wallMarkers.Length}/{WallMarkerCount}, " +
            $"chests={_chestMarkers.Length}/{ChestMarkerCount}, " +
            $"hiddenChests={_hiddenChestMarkers.Length}/{HiddenChestMarkerCount}, " +
            $"lockedDoors={_lockedGateMarkers.Length}/{LockedGateMarkerCount}, " +
            $"missionRanges={_missionRanges.Length}/{MissionRangeCount}, " +
            $"entrances={_entranceMarkers.Length}/{EntranceMarkerCount}, " +
            $"ladders={_ladderMarkers.Length}/{LadderMarkerCount}, " +
            $"collectibles={_collectibleMarkers.Length}/{CollectibleMarkerCount}, " +
            $"missions={_missionMarkers.Length}/{MissionMarkerCount}, " +
            $"footprints={_footprintMarkers.Length}/{FootprintMarkerCount}.");

        foreach (var slot in slots)
        {
            slot.Visible = false;
            slot.HitVisible = false;
            slot.AssetType = via.gui.TextureAssetType.Texture;
            slot.UVType = via.gui.UVValueType.Rect;
            slot.ControlPoint = via.gui.ControlPoint.CenterCenter;
            slot.MaskType = via.gui.MaskType.Target;
        }

        // Publish readiness only after every node has been configured successfully.
        _tileSlots = slots;
    }

    private static void ConfigureMarkerTexture(via.gui.Texture texture)
    {
        texture.Visible = false;
        texture.HitVisible = false;
        texture.ControlPoint = via.gui.ControlPoint.CenterCenter;
        texture.MaskType = via.gui.MaskType.NonTarget;
    }

    private static via.gui.Rect[] FindRects(
        via.gui.View view,
        string namePrefix,
        int count)
    {
        var rectangles = new via.gui.Rect[count];
        for (var index = 0; index < rectangles.Length; ++index)
        {
            var name = $"{namePrefix}{index:00}";
            rectangles[index] = FindNamedPlayObject(view, name)?.TryAs<via.gui.Rect>()
                ?? throw new InvalidOperationException(
                    $"The Minimap GUI resource is missing {name}.");
        }

        return rectangles;
    }

    private static via.gui.Texture[] FindOptionalTextures(
        via.gui.View view,
        string namePrefix,
        int count)
    {
        var textures = new List<via.gui.Texture>(count);
        for (var index = 0; index < count; ++index)
        {
            var name = $"{namePrefix}{index:00}";
            var texture = FindNamedPlayObject(view, name)?.TryAs<via.gui.Texture>();
            if (IsAlive(texture))
            {
                textures.Add(texture);
            }
        }

        return textures.ToArray();
    }

    private static IEnumerable<via.gui.Rect> EnumerateOverlayRectangles()
    {
        foreach (var rectangle in _rectangleBorders)
        {
            yield return rectangle;
        }

        foreach (var rectangle in _cameraArrows)
        {
            yield return rectangle;
        }
    }

    private static ManagedObject FindNamedPlayObject(
        via.gui.PlayObject root,
        string name)
    {
        var inspected = 0;
        return FindNamedPlayObject(root, name, 0, ref inspected);
    }

    private static ManagedObject FindNamedPlayObject(
        via.gui.PlayObject playObject,
        string name,
        int depth,
        ref int inspected)
    {
        if (!IsAlive(playObject) || depth >= 16 ||
            inspected++ >= MaxGuiPlayObjectsToInspect)
        {
            return null;
        }

        if (string.Equals(playObject.Name, name, StringComparison.Ordinal))
        {
            return ManagedObject.ToManagedObject(GetAddress(playObject));
        }

        var control = ManagedObject.ToManagedObject(GetAddress(playObject))
            ?.TryAs<via.gui.Control>();
        for (var child = control?.Child; IsAlive(child); child = child.Next)
        {
            var match = FindNamedPlayObject(child, name, depth + 1, ref inspected);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool TryCreateNextTile(MapDefinition map)
    {
        REFrameworkNET.Resource resource = null;
        via.gui.Texture texture = null;
        var configuredRectangleMask = false;
        try
        {
            var definition = map.Tiles[Tiles.Count];
            resource = CreateOwnedResource(
                "via.render.TextureResource", definition.ResourcePath);
            var holderObject = resource?.CreateHolder(
                "via.render.TextureResourceHolder");
            var holder = holderObject?.TryAs<via.render.TextureResourceHolder>();
            texture = _tileSlots[Tiles.Count];
            if (resource is null || !IsAlive(holder) || !IsAlive(texture))
            {
                throw new InvalidOperationException(
                    $"Could not create native texture {definition.ResourcePath}.");
            }

            texture.Visible = false;
            texture.setTexture(holder);
            if (Tiles.Count == 0)
            {
                _rectangleMask.setTexture(holder);
                SetCrop(_rectangleMask, TilePixels / 2, TilePixels / 2, 1, 1);
                configuredRectangleMask = true;
            }

            Tiles.Add(new MapTile(
                definition.Row,
                definition.Column,
                resource,
                holderObject,
                texture));
            resource = null;
            return true;
        }
        catch (Exception exception)
        {
            TryClearTexture(texture, "map tile rollback");
            if (configuredRectangleMask)
            {
                TryClearTexture(_rectangleMask, "rectangle mask rollback");
            }

            TryReleaseResource(resource, "map tile rollback");
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"Native map creation will retry: {exception}", ModLogLevel.Error);
            }

            return false;
        }
    }

    private static void UpdateNativeMap(
        float centerX,
        float centerY,
        float left,
        float top,
        float displayWidth,
        float displayHeight,
        float rotationRadians,
        bool isCircle)
    {
        var groupPosition = _mapGroup.Position;
        groupPosition.x = 0.0f;
        groupPosition.y = 0.0f;
        groupPosition.z = 0.0f;
        _mapGroup.Position = groupPosition;
        var displayCenterX = left + displayWidth * 0.5f;
        var displayCenterY = top + displayHeight * 0.5f;
        UpdateMasks(
            left,
            top,
            displayCenterX,
            displayCenterY,
            displayWidth,
            displayHeight,
            isCircle,
            MathF.Abs(rotationRadians) > 0.0001f);

        var pixelsPerMeter = Math.Max(Instance._pixelsPerMeter.Value, 0.1f);
        var displayPerSourcePixel = pixelsPerMeter / WorldToMapPixels;
        var cosine = MathF.Cos(rotationRadians);
        var sine = MathF.Sin(rotationRadians);
        var absoluteCosine = MathF.Abs(cosine);
        var absoluteSine = MathF.Abs(sine);
        var sourceWidth =
            (absoluteCosine * displayWidth + absoluteSine * displayHeight) /
            displayPerSourcePixel;
        var sourceHeight =
            (absoluteSine * displayWidth + absoluteCosine * displayHeight) /
            displayPerSourcePixel;
        var sourceLeft = centerX - sourceWidth * 0.5f;
        var sourceTop = centerY - sourceHeight * 0.5f;
        var sourceRight = sourceLeft + sourceWidth;
        var sourceBottom = sourceTop + sourceHeight;
        var rotationDegrees = rotationRadians * (180.0f / MathF.PI);
        foreach (var tile in Tiles)
        {
            var tileLeft = tile.Column * TilePixels;
            var tileTop = tile.Row * TilePixels;
            var intersectionLeft = MathF.Max(sourceLeft, tileLeft);
            var intersectionTop = MathF.Max(sourceTop, tileTop);
            var intersectionRight = MathF.Min(sourceRight, tileLeft + TilePixels);
            var intersectionBottom = MathF.Min(sourceBottom, tileTop + TilePixels);
            var targetX = displayCenterX;
            var targetY = displayCenterY;
            var targetWidth = 0.0f;
            var targetHeight = 0.0f;
            if (intersectionRight <= intersectionLeft ||
                intersectionBottom <= intersectionTop)
            {
                SetCrop(tile.Texture, 0, 0, 1, 1);
            }
            else
            {
                var sourceX = Math.Clamp(
                    (int)MathF.Floor(intersectionLeft - tileLeft), 0, TilePixels - 1);
                var sourceY = Math.Clamp(
                    (int)MathF.Floor(intersectionTop - tileTop), 0, TilePixels - 1);
                var sourceRightPixel = Math.Clamp(
                    (int)MathF.Ceiling(intersectionRight - tileLeft),
                    sourceX + 1,
                    TilePixels);
                var sourceBottomPixel = Math.Clamp(
                    (int)MathF.Ceiling(intersectionBottom - tileTop),
                    sourceY + 1,
                    TilePixels);
                SetCrop(
                    tile.Texture,
                    sourceX,
                    sourceY,
                    sourceRightPixel - sourceX,
                    sourceBottomPixel - sourceY);
                var unrotatedX =
                    ((intersectionLeft + intersectionRight) * 0.5f - centerX) *
                    displayPerSourcePixel;
                var unrotatedY =
                    ((intersectionTop + intersectionBottom) * 0.5f - centerY) *
                    displayPerSourcePixel;
                targetX = displayCenterX +
                    cosine * unrotatedX - sine * unrotatedY;
                targetY = displayCenterY +
                    sine * unrotatedX + cosine * unrotatedY;
                targetWidth =
                    (intersectionRight - intersectionLeft) * displayPerSourcePixel;
                targetHeight =
                    (intersectionBottom - intersectionTop) * displayPerSourcePixel;
            }

            var position = tile.Texture.Position;
            position.x = targetX;
            position.y = targetY;
            position.z = 0.0f;
            tile.Texture.Position = position;

            var size = tile.Texture.Size;
            size.w = targetWidth;
            size.h = targetHeight;
            tile.Texture.Size = size;

            var rotation = tile.Texture.Rotation;
            rotation.z = rotationDegrees;
            tile.Texture.Rotation = rotation;
            tile.Texture.Visible = true;
        }

        _mapGroup.Visible = true;
        _gui.Enabled = true;
    }

    private static void UpdateMasks(
        float left,
        float top,
        float centerX,
        float centerY,
        float width,
        float height,
        bool isCircle,
        bool isRotated)
    {
        if (IsAlive(_circleMask))
        {
            var circlePosition = _circleMask.Position;
            circlePosition.x = centerX;
            circlePosition.y = centerY;
            circlePosition.z = 0.0f;
            _circleMask.Position = circlePosition;
            var circleSize = _circleMask.Size;
            circleSize.w = width;
            circleSize.h = height;
            _circleMask.Size = circleSize;
            _circleMask.Visible = isCircle;
        }

        if (IsAlive(_rectangleMask))
        {
            var rectanglePosition = _rectangleMask.Position;
            rectanglePosition.x = left;
            rectanglePosition.y = top;
            rectanglePosition.z = 0.0f;
            _rectangleMask.Position = rectanglePosition;
            var rectangleSize = _rectangleMask.Size;
            rectangleSize.w = width;
            rectangleSize.h = height;
            _rectangleMask.Size = rectangleSize;
            // Range circles need clipping even when north-up tiles are pre-cropped.
            _rectangleMask.Visible = !isCircle &&
                (isRotated || Instance._showMissions.Value);
        }
    }

    private static void SetCrop(
        via.gui.Texture texture,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight)
    {
        texture.RectL = (short)sourceX;
        texture.RectT = (short)sourceY;
        texture.RectW = (short)sourceWidth;
        texture.RectH = (short)sourceHeight;
        texture.U0 = (short)sourceX;
        texture.V0 = (short)sourceY;
        texture.U1 = (short)(sourceX + sourceWidth);
        texture.V1 = (short)(sourceY + sourceHeight);
        texture.UVU = (float)sourceX / TilePixels;
        texture.UVV = (float)sourceY / TilePixels;
        texture.UVW = (float)sourceWidth / TilePixels;
        texture.UVH = (float)sourceHeight / TilePixels;
    }

    private static string GetTexturePath(string prefabPath)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return null;
        }

        var slash = Math.Max(prefabPath.LastIndexOf('/'), prefabPath.LastIndexOf('\\'));
        var dot = prefabPath.LastIndexOf('.');
        if (dot <= slash + 1)
        {
            return null;
        }

        var name = prefabPath.Substring(slash + 1, dot - slash - 1);
        return $"GUI/ui_texture/tex_map/tex_{name}_IMLM3.tex";
    }

    private static void UpdateNativeMarkers(
        app.GUIManager guiManager,
        float playerX,
        float playerZ,
        float left,
        float top,
        float width,
        float height,
        float mapScale,
        float mapRotation,
        bool isCircle)
    {
        try
        {
            if (Environment.TickCount64 >= _nextMarkerRefreshTick)
            {
                RefreshMapObjectMarkers();
                RefreshChestMarkers();
                RefreshMissionMarkers();
                _nextMarkerRefreshTick =
                    Environment.TickCount64 + MarkerRefreshMilliseconds;
            }

            var markerScale = Math.Clamp(
                MathF.Min(width, height) / 280.0f,
                0.5f,
                1.0f);
            var pixelsPerMeter = Math.Max(Instance._pixelsPerMeter.Value, 0.1f);
            var mapSign = MathF.Sign(mapScale);
            var cosine = MathF.Cos(mapRotation);
            var sine = MathF.Sin(mapRotation);
            if (Instance._showOniWalls.Value)
            {
                UpdateMapObjectMarkers(
                    WallPositions,
                    _wallMarkers,
                    WallMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_wallMarkers, false);
            }

            if (Instance._showChests.Value)
            {
                UpdateMapObjectMarkers(
                    ChestPositions,
                    _chestMarkers,
                    ChestMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_chestMarkers, false);
            }

            if (Instance._showHiddenChests.Value)
            {
                UpdateMapObjectMarkers(
                    HiddenChestPositions,
                    _hiddenChestMarkers,
                    ChestMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_hiddenChestMarkers, false);
            }

            if (Instance._showLockedGates.Value)
            {
                UpdateMapObjectMarkers(
                    LockedGatePositions,
                    _lockedGateMarkers,
                    EntranceMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_lockedGateMarkers, false);
            }

            if (Instance._showEntrances.Value)
            {
                UpdateMapObjectMarkers(
                    EntrancePositions,
                    _entranceMarkers,
                    EntranceMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_entranceMarkers, false);
            }

            if (Instance._showLadders.Value)
            {
                UpdateMapObjectMarkers(
                    LadderPositions,
                    _ladderMarkers,
                    LadderMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_ladderMarkers, false);
            }

            if (Instance._showCollectibles.Value)
            {
                UpdateMapObjectMarkers(
                    CollectiblePositions,
                    _collectibleMarkers,
                    CollectibleMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_collectibleMarkers, false);
            }

            if (Instance._showMissions.Value)
            {
                UpdateMapObjectMarkers(
                    MissionPositions,
                    _missionMarkers,
                    MissionMarkerSize,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_missionMarkers, false);
            }

            UpdateMissionRanges(
                playerX, playerZ, left, top, width, height,
                pixelsPerMeter, mapSign, cosine, sine);

            if (Instance._showFootprints.Value)
            {
                UpdateFootprintMarkers(
                    guiManager,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerScale,
                    isCircle);
            }
            else
            {
                SetVisible(_footprintMarkers, false);
            }

            Volatile.Write(ref _markerErrorReported, 0);
        }
        catch (Exception exception)
        {
            HideNativeMarkers();
            if (Interlocked.Exchange(ref _markerErrorReported, 1) == 0)
            {
                Instance.Log(
                    $"Map marker update failed; the base map remains active: {exception}",
                    ModLogLevel.Error);
            }
        }
    }

    private static void RefreshMapObjectMarkers()
    {
        // Unavailable data or a failed refresh must not leave old markers visible.
        ClearMapObjectPositions();
        var map = _map;
        if (map is null || map.AreaFields.Count == 0)
        {
            return;
        }

        var environment = API.GetManagedSingletonT<app.EnvironmentManager>();
        var infoManager = environment?.EnvInfoManager;
        var packages = infoManager?.getAllMapObjectData();
        if (!IsAlive(infoManager) || !IsAlive(packages))
        {
            return;
        }

        var walls = new List<MarkerPosition>();
        var lockedGates = new List<MarkerPosition>();
        var entrances = new List<MarkerPosition>();
        var ladders = new List<MarkerPosition>();
        var collectibles = new List<MarkerPosition>();
        for (var packageIndex = 0;
             packageIndex < packages.Count;
             ++packageIndex)
        {
            var package = packages[packageIndex];
            if (!IsAlive(package) ||
                !map.AreaFields.TryGetValue(package.AreaID, out var floors))
            {
                continue;
            }

            var objects = package.getDisplayList();
            if (!IsAlive(objects))
            {
                continue;
            }

            for (var objectIndex = 0; objectIndex < objects.Count; ++objectIndex)
            {
                var data = objects[objectIndex];
                if (!IsAlive(data) || !floors.Contains(data.FieldOrder))
                {
                    continue;
                }

                var position = data.Position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.z))
                {
                    continue;
                }

                switch (data.MapObjectType)
                {
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.ARM_BREAK_WALL:
                        walls.Add(new MarkerPosition(
                            position.x,
                            position.z,
                            ArmBreakWallIconPattern));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.EYE_HIDE_WALL:
                        walls.Add(new MarkerPosition(
                            position.x,
                            position.z,
                            EyeHideWallIconPattern));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.INVASION_WALL:
                        walls.Add(new MarkerPosition(
                            position.x,
                            position.z,
                            InvasionWallIconPattern));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.LOCKED_GATE:
                        if (data.isEnable() && !package.isReleaseObject(data.MainID, data.SubID))
                        {
                            lockedGates.Add(new MarkerPosition(
                                position.x, position.z, LockedGateIconPattern));
                        }
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.LADDER:
                        ladders.Add(new MarkerPosition(
                            position.x, position.z, LadderIconPattern));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.SUB_MISTERY:
                        collectibles.Add(new MarkerPosition(
                            position.x, position.z, SubMysteryIconPattern,
                            color: MarkerColor.MapSymbol));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.MEDICINE_BAG_MATERIAL:
                        collectibles.Add(new MarkerPosition(
                            position.x, position.z, MedicineBagMaterialIconPattern,
                            color: MarkerColor.Mission));
                        break;
                    case app.EnvDef.MAP_OBJECT_TYPE_Fixed.STAGE_TRANSITION_POINT:
                        entrances.Add(new MarkerPosition(
                            position.x,
                            position.z,
                            EntranceIconPattern));
                        break;
                }
            }
        }

        WallPositions.AddRange(walls);
        LockedGatePositions.AddRange(lockedGates);
        EntrancePositions.AddRange(entrances);
        LadderPositions.AddRange(ladders);
        CollectiblePositions.AddRange(collectibles);
    }

    private static void ClearMapObjectPositions()
    {
        WallPositions.Clear();
        ChestPositions.Clear();
        HiddenChestPositions.Clear();
        LockedGatePositions.Clear();
        MissionRanges.Clear();
        EntrancePositions.Clear();
        LadderPositions.Clear();
        CollectiblePositions.Clear();
        MissionPositions.Clear();
    }

    private enum ChestStatus
    {
        Unknown,
        Hidden,
        Available,
        Taken,
    }

    private static void RefreshChestMarkers()
    {
        ChestPositions.Clear();
        HiddenChestPositions.Clear();
        var map = _map;
        if (map is null || (!Instance._showChests.Value && !Instance._showHiddenChests.Value))
        {
            return;
        }

        try
        {
            var info = API.GetManagedSingletonT<app.EnvironmentManager>()?.EnvInfoManager;
            var packages = info?.getAllMapObjectData();
            if (!IsAlive(info) || !IsAlive(packages))
            {
                return;
            }

            var contexts = new List<ChestContext>();
            var scene = via.SceneManager.CurrentScene;
            if (GetAddress(scene) != 0)
            {
                // Read current bodies, including invisible puzzle rewards.
                // Copy values only: streaming can dispose them before the next refresh.
                var components = scene.findComponents(app.AppGimmickBase.REFType.RuntimeType.As<_System.Type>());
                if (IsAlive(components))
                {
                    for (var i = 0; i < Math.Min(components.Length, MaxChestContextsToInspect); ++i)
                    {
                        var component = components[i];
                        if (!IsAlive(component)) continue;
                        var raw = ManagedObject.ToManagedObject(GetAddress(component));
                        var box = raw.TryAs<app.GimmickTreasureBox>();
                        if (!IsAlive(box)) continue;
                        var holder = box.GameContextHolder;
                        var context = box.GimmickContext;
                        if (!IsAlive(holder?.Info) || !IsAlive(context) ||
                            !map.AreaFields.ContainsKey(holder.Info.AreaFixedID)) continue;
                        contexts.Add(new ChestContext(holder.Info.AreaFixedID,
                            context.InitPos, ReadChestStatus(context, raw)));
                    }
                }
            }

            var ordinary = new List<MarkerPosition>();
            var hidden = new List<MarkerPosition>();
            var seen = new HashSet<(app.EnvDef.AreaID_Fixed, string, string)>();
            var inspected = 0;
            for (var p = 0; p < packages.Count; ++p)
            {
                var package = packages[p];
                if (!IsAlive(package) || !map.AreaFields.TryGetValue(package.AreaID, out var floors))
                {
                    continue;
                }

                // getDisplayList omits sealed chests. Read the full list only
                // for this category, then check current context and save state.
                var objects = package.List;
                if (!IsAlive(objects)) continue;
                for (var i = 0; i < objects.Count; ++i)
                {
                    var data = objects[i];
                    if (!IsAlive(data) ||
                        data.MapObjectType != app.EnvDef.MAP_OBJECT_TYPE_Fixed.SPECIAL_CHEST ||
                        !floors.Contains(data.FieldOrder)) continue;
                    if (++inspected > MaxChestObjectsToInspect) break;

                    var position = data.Position;
                    if (!float.IsFinite(position.x) || !float.IsFinite(position.y) ||
                        !float.IsFinite(position.z) ||
                        !seen.Add((package.AreaID, data.MainID.ToString(), data.SubID.ToString()))) continue;

                    // The display bit can stay set after looting. The release
                    // bit persists after a chest streams out or scripts reload.
                    if (package.isReleaseObject(data.MainID, data.SubID)) continue;
                    var displayed = package.isDisplayObject(data.MainID, data.SubID);
                    var status = GetChestStatus(contexts, package.AreaID, data);
                    if (status == ChestStatus.Taken) continue;
                    if (status == ChestStatus.Hidden)
                    {
                        // A disabled story branch is not a hidden reward in
                        // the current world. Do not reveal unrelated missions.
                        if (data.isEnable()) hidden.Add(new MarkerPosition(
                            position.x, position.z, ChestIconPattern));
                    }
                    else if (displayed || (status == ChestStatus.Available && data.isEnable()))
                    {
                        ordinary.Add(new MarkerPosition(position.x, position.z, ChestIconPattern));
                    }
                }
                if (inspected > MaxChestObjectsToInspect) break;
            }

            ChestPositions.AddRange(ordinary);
            HiddenChestPositions.AddRange(hidden);
            Volatile.Write(ref _chestErrorReported, 0);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _chestErrorReported, 1) == 0)
            {
                Instance.Log($"Chest marker refresh will retry: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static ChestStatus GetChestStatus(
        IReadOnlyList<ChestContext> contexts,
        app.EnvDef.AreaID_Fixed area,
        app.user_data.MapObjectData.cData data)
    {
        // Match the initial placement: a suspended chest can move when unlocked.
        // Reject ambiguous matches, and include height to distinguish floors.
        ChestStatus? result = null;
        foreach (var candidate in contexts)
        {
            if (candidate.Area != area) continue;
            var origin = candidate.Position;
            var point = data.Position;
            var dx = origin.x - point.x;
            var dy = origin.y - point.y;
            var dz = origin.z - point.z;
            if (!float.IsFinite(dx + dy + dz) || dx * dx + dy * dy + dz * dz > 0.0625f)
                continue;
            if (result.HasValue) return ChestStatus.Unknown;
            result = candidate.Status;
        }
        return result ?? ChestStatus.Unknown;
    }

    private readonly struct ChestContext
    {
        public ChestContext(app.EnvDef.AreaID_Fixed area, via.vec3 position, ChestStatus status)
        {
            Area = area;
            Position = position;
            Status = status;
        }
        public app.EnvDef.AreaID_Fixed Area { get; }
        public via.vec3 Position { get; }
        public ChestStatus Status { get; }
    }

    private static ChestStatus ReadChestStatus(app.cGimmickContextParam context, ManagedObject raw)
    {
        var flags = context.SaveFlagHolder;
        if (!IsAlive(flags) || flags.Invalid) return ChestStatus.Unknown;
        var saved = (int)flags.State;
        if (saved == ChestTakenSaveState) return ChestStatus.Taken;

        var eyeBox = raw?.TryAs<app.Gm002_006>();
        if (IsAlive(eyeBox))
        {
            return eyeBox._DiscoverState == app.Gm002_006.DISCOVER_STATE.DISCOVER
                ? ChestStatus.Available : ChestStatus.Hidden;
        }

        var floatingBox = raw?.TryAs<app.Gm002_010>();
        if (IsAlive(floatingBox))
        {
            return floatingBox._FloatingState >= app.Gm002_010.FLOATING_STATE.LAND
                ? ChestStatus.Available : ChestStatus.Hidden;
        }

        // Other mechanism-controlled chest contexts can exist before their
        // bodies are enabled. Never infer this from a missing display flag alone.
        return context.State == app.GimmickDef.APP_STATE.DISABLE && saved == 0
            ? ChestStatus.Hidden : ChestStatus.Unknown;
    }

    private static void RefreshMissionMarkers()
    {
        MissionPositions.Clear();
        MissionRanges.Clear();
        if (!Instance._showMissions.Value || _map is null)
        {
            return;
        }

        try
        {
            var story = API.GetManagedSingletonT<app.StoryManager>();
            var info = API.GetManagedSingletonT<app.EnvironmentManager>()?.EnvInfoManager;
            if (!IsAlive(story) || !IsAlive(info))
            {
                return;
            }

            // This is the enabled beacon list also used by GUI060000, not the
            // full mission database. BeaconInfo is a value type; copy its data.
            var beacons = story.getObjectiveBeaconInfoAll();
            if (!IsAlive(beacons))
            {
                return;
            }

            var positions = new List<MarkerPosition>();
            var ranges = new List<MissionRange>();
            for (var index = 0;
                 index < Math.Min(beacons.Count, MaxMissionBeaconsToInspect);
                 ++index)
            {
                var beacon = beacons[index];
                if (!beacon.IsSet || !IsAlive(beacon.AreaID))
                {
                    continue;
                }

                var area = (app.EnvDef.AreaID_Fixed)beacon.AreaID.Value;
                var position = beacon.Pos;
                if (!_map.AreaFields.TryGetValue(area, out var floors) ||
                    !float.IsFinite(position.x) || !float.IsFinite(position.y) ||
                    !float.IsFinite(position.z) ||
                    !floors.Contains(info.getFieldOrder(area, position)))
                {
                    continue;
                }

                uint pattern;
                switch (beacon.MissionType)
                {
                    case app.MissionDef.MISSION_TYPE.MAIN_MISSION:
                        pattern = MainMissionIconPattern;
                        break;
                    case app.MissionDef.MISSION_TYPE.CHARACTER_MISSION:
                        pattern = CharacterMissionIconPattern;
                        break;
                    case app.MissionDef.MISSION_TYPE.SUB_MISSION:
                        pattern = SideMissionIconPattern;
                        break;
                    default:
                        continue;
                }

                // GUI060000.setupCircleIcon uses both flags. Non-range beacons
                // also carry a positive Range, so the number alone is insufficient.
                if (beacon.IsRange && beacon.IsSelected &&
                    float.IsFinite(beacon.Range) && beacon.Range > 0.0f)
                {
                    ranges.Add(new MissionRange(
                        position.x, position.z, beacon.Range, beacon.MissionType));
                }

                var preface = beacon.IsPreface &&
                    beacon.MissionType != app.MissionDef.MISSION_TYPE.MAIN_MISSION;
                positions.Add(new MarkerPosition(
                    position.x, position.z,
                    preface ? pattern + 3 : pattern,
                    preface ? MissionPrefaceSequence : 0,
                    MarkerColor.Mission));
            }

            MissionPositions.AddRange(positions);
            MissionRanges.AddRange(ranges);
            Volatile.Write(ref _missionErrorReported, 0);
        }
        catch (Exception exception)
        {
            // Mission data may disappear during a transition; other marker
            // categories can still update without retaining stale missions.
            if (Interlocked.Exchange(ref _missionErrorReported, 1) == 0)
            {
                Instance.Log($"Mission marker refresh will retry: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static void UpdateMissionRanges(
        float playerX, float playerZ, float left, float top,
        float width, float height, float pixelsPerMeter,
        float mapSign, float cosine, float sine)
    {
        var used = 0;
        if (Instance._showMissions.Value)
        {
            foreach (var range in MissionRanges)
            {
                if (used >= _missionRanges.Length) break;
                var sourceX = (range.X - playerX) * pixelsPerMeter * mapSign;
                var sourceY = (range.Z - playerZ) * pixelsPerMeter * mapSign;
                var x = left + width * 0.5f + cosine * sourceX - sine * sourceY;
                var y = top + height * 0.5f + sine * sourceX + cosine * sourceY;
                var radius = range.Radius * pixelsPerMeter;
                // Native DEFAULT clip: frame = Range * 6.4 * 0.2,
                // circle diameter = frame * 10, i.e. Range is a world radius.
                var diameter = radius * 2.0f;
                if (!float.IsFinite(x) || !float.IsFinite(y) ||
                    !float.IsFinite(diameter) || diameter <= 0.0f) continue;

                // Keep circles whose centers are outside the viewport if their
                // area still intersects it. The native map mask clips the edges.
                var dx = x - Math.Clamp(x, left, left + width);
                var dy = y - Math.Clamp(y, top, top + height);
                if (dx * dx + dy * dy > radius * radius) continue;

                var circle = _missionRanges[used++];
                var position = circle.Position;
                position.x = x;
                position.y = y;
                position.z = 0.0f;
                circle.Position = position;
                var size = circle.Size;
                size.w = diameter;
                size.h = diameter;
                circle.Size = size;
                var frame = Math.Clamp(range.Radius * WorldToMapPixels * 0.2f, 0.0f, 500.0f);
                var nativeEdge = frame <= 20.0f
                    ? 10.0f + frame : 30.0f + (frame - 20.0f) * (50.0f / 480.0f);
                circle.EdgeThickness = nativeEdge * pixelsPerMeter / WorldToMapPixels;
                // Colors from GUI060000 PNL_MissionArea's MAIN/GOSSIP/SUB clips.
                var color = circle.OuterColor;
                color.r = range.MissionType == app.MissionDef.MISSION_TYPE.CHARACTER_MISSION
                    ? (byte)164 : range.MissionType == app.MissionDef.MISSION_TYPE.SUB_MISSION
                        ? (byte)47 : (byte)255;
                color.g = range.MissionType == app.MissionDef.MISSION_TYPE.CHARACTER_MISSION
                    ? (byte)47 : range.MissionType == app.MissionDef.MISSION_TYPE.SUB_MISSION
                        ? (byte)180 : (byte)220;
                color.b = range.MissionType == app.MissionDef.MISSION_TYPE.CHARACTER_MISSION
                    ? (byte)180 : range.MissionType == app.MissionDef.MISSION_TYPE.SUB_MISSION
                        ? (byte)179 : (byte)88;
                color.a = 51;
                circle.OuterColor = color;
                circle.Visible = true;
            }
        }

        for (var index = used; index < _missionRanges.Length; ++index)
        {
            _missionRanges[index].Visible = false;
        }
    }

    private static void UpdateMapObjectMarkers(
        IReadOnlyList<MarkerPosition> positions,
        via.gui.Texture[] textures,
        float baseMarkerSize,
        float playerX,
        float playerZ,
        float left,
        float top,
        float width,
        float height,
        float pixelsPerMeter,
        float mapSign,
        float cosine,
        float sine,
        float markerScale,
        bool isCircle)
    {
        var markerSize = baseMarkerSize * markerScale;
        var used = 0;
        foreach (var marker in positions)
        {
            if (used >= textures.Length)
            {
                break;
            }

            if (!TryProjectMarker(
                    marker,
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerSize,
                    isCircle,
                    out var x,
                    out var y))
            {
                continue;
            }

            var texture = textures[used];
            var sequenceChanged = texture.UVSequenceNo != marker.IconSequenceNo;
            if (sequenceChanged)
            {
                texture.UVSequenceNo = marker.IconSequenceNo;
            }
            if (sequenceChanged || texture.UVPatternNo != marker.IconPatternNo)
            {
                texture.UVPatternNo = marker.IconPatternNo;
            }
            ApplyMarkerColor(texture, marker.Color);
            SetMarkerTexture(
                texture,
                x,
                y,
                markerSize,
                markerSize,
                0.0f);
            ++used;
        }

        HideUnused(textures, used);
    }

    private static void ApplyMarkerColor(via.gui.Texture texture, MarkerColor color)
    {
        var address = GetAddress(texture);
        // New prefab textures have no color preset. Cache only successful
        // assignments, and change it when a reused slot gets another category.
        MarkerColors.TryGetValue(address, out var appliedColor);
        if (appliedColor == color)
        {
            return;
        }

        texture.ColorPreset = _markerColorPresets[(int)color];
        MarkerColors[address] = color;
    }

    private static void UpdateFootprintMarkers(
        app.GUIManager guiManager,
        float playerX,
        float playerZ,
        float left,
        float top,
        float width,
        float height,
        float pixelsPerMeter,
        float mapSign,
        float cosine,
        float sine,
        float markerScale,
        bool isCircle)
    {
        var footprintModule = guiManager.getFootprintsModule();
        var footprints = footprintModule?.Footprints;
        if (!IsAlive(footprintModule) || !IsAlive(footprints))
        {
            SetVisible(_footprintMarkers, false);
            return;
        }

        var markerSize = FootprintMarkerSize * markerScale;
        var used = 0;
        for (var index = 0;
             index < footprints.Count && used < _footprintMarkers.Length;
             ++index)
        {
            var footprint = footprints[index];
            if (!IsAlive(footprint) || !footprint.IsValid)
            {
                continue;
            }

            var position = footprint.Pos;
            if (!TryProjectMarker(
                    new MarkerPosition(position.x, position.z),
                    playerX,
                    playerZ,
                    left,
                    top,
                    width,
                    height,
                    pixelsPerMeter,
                    mapSign,
                    cosine,
                    sine,
                    markerSize,
                    isCircle,
                    out var x,
                    out var y))
            {
                continue;
            }

            SetMarkerTexture(
                _footprintMarkers[used],
                x,
                y,
                markerSize,
                markerSize,
                0.0f);
            ++used;
        }

        HideUnused(_footprintMarkers, used);
    }

    private static bool TryProjectMarker(
        MarkerPosition marker,
        float playerX,
        float playerZ,
        float left,
        float top,
        float width,
        float height,
        float pixelsPerMeter,
        float mapSign,
        float cosine,
        float sine,
        float markerRadius,
        bool isCircle,
        out float x,
        out float y)
    {
        var centerX = left + width * 0.5f;
        var centerY = top + height * 0.5f;
        var sourceX = (marker.X - playerX) * pixelsPerMeter * mapSign;
        var sourceY = (marker.Z - playerZ) * pixelsPerMeter * mapSign;
        x = centerX + cosine * sourceX - sine * sourceY;
        y = centerY + sine * sourceX + cosine * sourceY;
        var radius = markerRadius * 0.5f;
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            x < left + radius || x > left + width - radius ||
            y < top + radius || y > top + height - radius)
        {
            return false;
        }

        if (!isCircle)
        {
            return true;
        }

        var radiusX = width * 0.5f - radius;
        var radiusY = height * 0.5f - radius;
        if (radiusX <= 0.0f || radiusY <= 0.0f)
        {
            return false;
        }

        var normalizedX = (x - centerX) / radiusX;
        var normalizedY = (y - centerY) / radiusY;
        return normalizedX * normalizedX + normalizedY * normalizedY <= 1.0f;
    }

    private static void SetMarkerTexture(
        via.gui.Texture texture,
        float centerX,
        float centerY,
        float width,
        float height,
        float rotationDegrees)
    {
        var position = texture.Position;
        position.x = centerX;
        position.y = centerY;
        position.z = 0.0f;
        texture.Position = position;
        var size = texture.Size;
        size.w = width;
        size.h = height;
        texture.Size = size;
        var rotation = texture.Rotation;
        rotation.z = rotationDegrees;
        texture.Rotation = rotation;
        texture.Visible = true;
    }

    private static void HideUnused(via.gui.Texture[] textures, int firstUnused)
    {
        for (var index = firstUnused; index < textures.Length; ++index)
        {
            textures[index].Visible = false;
        }
    }

    private static void HideNativeMarkers()
    {
        SetVisible(_lockedGateMarkers, false);
        foreach (var circle in _missionRanges)
        {
            if (IsAlive(circle)) circle.Visible = false;
        }
        SetVisible(_wallMarkers, false);
        SetVisible(_chestMarkers, false);
        SetVisible(_hiddenChestMarkers, false);
        SetVisible(_entranceMarkers, false);
        SetVisible(_ladderMarkers, false);
        SetVisible(_collectibleMarkers, false);
        SetVisible(_missionMarkers, false);
        SetVisible(_footprintMarkers, false);
    }

    private static void UpdateNativeOverlay(
        float left,
        float top,
        float width,
        float height,
        float playerForwardX,
        float playerForwardY,
        float cameraForwardX,
        float cameraForwardY,
        bool hasCameraDirection,
        bool isCircle)
    {
        var centerX = left + width * 0.5f;
        var centerY = top + height * 0.5f;
        var halfBorder = BorderThickness * 0.5f;

        SetRect(
            _rectangleBorders[0],
            left + halfBorder,
            centerY,
            BorderThickness,
            height,
            !isCircle);
        SetRect(
            _rectangleBorders[1],
            centerX,
            top + halfBorder,
            width,
            BorderThickness,
            !isCircle);
        SetRect(
            _rectangleBorders[2],
            left + width - halfBorder,
            centerY,
            BorderThickness,
            height,
            !isCircle);
        SetRect(
            _rectangleBorders[3],
            centerX,
            top + height - halfBorder,
            width,
            BorderThickness,
            !isCircle);

        var circlePosition = _circleBorder.Position;
        circlePosition.x = centerX;
        circlePosition.y = centerY;
        circlePosition.z = 0.0f;
        _circleBorder.Position = circlePosition;
        var circleSize = _circleBorder.Size;
        circleSize.w = width;
        circleSize.h = height;
        _circleBorder.Size = circleSize;
        _circleBorder.InnerRatio = Math.Clamp(
            1.0f - 2.0f * BorderThickness / MathF.Min(width, height),
            0.0f,
            1.0f);
        _circleBorder.Visible = isCircle;

        NormalizeDirection(ref playerForwardX, ref playerForwardY, true);
        var markerScale = Math.Clamp(
            MathF.Min(width, height) / 280.0f,
            0.35f,
            1.0f);
        UpdatePlayerMarker(
            centerX,
            centerY,
            playerForwardX,
            playerForwardY,
            markerScale);

        if (hasCameraDirection &&
            NormalizeDirection(ref cameraForwardX, ref cameraForwardY, false))
        {
            UpdateCameraArrow(
                centerX,
                centerY,
                cameraForwardX,
                cameraForwardY,
                markerScale);
        }
        else
        {
            SetVisible(_cameraArrows, false);
        }

        _overlayGroup.Visible = true;
    }

    private static void SetRect(
        via.gui.Rect rectangle,
        float centerX,
        float centerY,
        float width,
        float height,
        bool visible)
    {
        var position = rectangle.Position;
        position.x = centerX;
        position.y = centerY;
        position.z = 0.0f;
        rectangle.Position = position;
        var size = rectangle.Size;
        size.w = width;
        size.h = height;
        rectangle.Size = size;
        rectangle.Visible = visible;
    }

    private static void UpdatePlayerMarker(
        float centerX,
        float centerY,
        float directionX,
        float directionY,
        float scale)
    {
        var rotationDegrees =
            MathF.Atan2(directionY, directionX) * (180.0f / MathF.PI) + 90.0f;
        var markerSize = PlayerMarkerSize * scale;
        SetMarkerTexture(
            _playerMarker,
            centerX,
            centerY,
            markerSize,
            markerSize,
            rotationDegrees);
    }

    private static void UpdateCameraArrow(
        float centerX,
        float centerY,
        float directionX,
        float directionY,
        float scale)
    {
        var rightX = -directionY;
        var rightY = directionX;
        var rearX = centerX + directionX * CameraRearDistance * scale;
        var rearY = centerY + directionY * CameraRearDistance * scale;
        var tipX = centerX + directionX * CameraTipDistance * scale;
        var tipY = centerY + directionY * CameraTipDistance * scale;
        var halfWidth = CameraHalfWidth * scale;
        var thickness = MathF.Max(1.0f, CameraArrowThickness * scale);
        SetLine(_cameraArrows[0],
            rearX - rightX * halfWidth, rearY - rightY * halfWidth,
            tipX, tipY, thickness);
        SetLine(_cameraArrows[1],
            rearX + rightX * halfWidth, rearY + rightY * halfWidth,
            tipX, tipY, thickness);
        for (var index = 2; index < _cameraArrows.Length; ++index)
        {
            _cameraArrows[index].Visible = false;
        }
    }

    private static void SetLine(
        via.gui.Rect rectangle,
        float startX,
        float startY,
        float endX,
        float endY,
        float thickness)
    {
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var position = rectangle.Position;
        position.x = (startX + endX) * 0.5f;
        position.y = (startY + endY) * 0.5f;
        position.z = 0.0f;
        rectangle.Position = position;
        var size = rectangle.Size;
        size.w = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        size.h = thickness;
        rectangle.Size = size;
        var rotation = rectangle.Rotation;
        rotation.z = MathF.Atan2(deltaY, deltaX) * (180.0f / MathF.PI);
        rectangle.Rotation = rotation;
        rectangle.Visible = true;
    }

    private static void SetVisible(via.gui.Rect[] rectangles, bool visible)
    {
        foreach (var rectangle in rectangles)
        {
            rectangle.Visible = visible;
        }
    }

    private static void SetVisible(via.gui.Texture[] textures, bool visible)
    {
        foreach (var texture in textures)
        {
            texture.Visible = visible;
        }
    }

    private static bool NormalizeDirection(
        ref float directionX,
        ref float directionY,
        bool useDefault)
    {
        var lengthSquared = directionX * directionX + directionY * directionY;
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.0001f)
        {
            directionX = 0.0f;
            directionY = -1.0f;
            return useDefault;
        }

        var inverseLength = 1.0f / MathF.Sqrt(lengthSquared);
        directionX *= inverseLength;
        directionY *= inverseLength;
        return true;
    }

    private static void HideMap()
    {
        if (IsAlive(_mapGroup))
        {
            _mapGroup.Visible = false;
        }

        if (IsAlive(_overlayGroup))
        {
            _overlayGroup.Visible = false;
        }

        if (IsAlive(_gui))
        {
            _gui.Enabled = false;
        }
    }

    private static void ResetMap()
    {
        TryClearTexture(_rectangleMask, "rectangle mask");
        ClearTileTextures(Tiles);
        ReleaseTileResources(Tiles);
        Tiles.Clear();
        ClearMapObjectPositions();
        _nextMarkerRefreshTick = 0;
        _map = null;
    }

    private static void ClearTileTextures(IEnumerable<MapTile> tiles)
    {
        foreach (var tile in tiles)
        {
            TryClearTexture(tile.Texture, "map tile");
        }
    }

    private static void ReleaseTileResources(IEnumerable<MapTile> tiles)
    {
        foreach (var tile in tiles)
        {
            TryReleaseResource(tile.Resource, "map tile");
        }
    }

    private static void DestroyNativeGui()
    {
        try
        {
            if (IsAlive(_gui))
            {
                _gui.Enabled = false;
            }

            if (IsAlive(_guiGameObject))
            {
                via.GameObject.destroy(_guiGameObject);
            }
        }
        catch (Exception exception)
        {
            LogCleanupWarning("native GUI", exception);
        }

        _tileSlots = Array.Empty<via.gui.Texture>();
        _rectangleBorders = Array.Empty<via.gui.Rect>();
        _cameraArrows = Array.Empty<via.gui.Rect>();
        _wallMarkers = Array.Empty<via.gui.Texture>();
        _chestMarkers = Array.Empty<via.gui.Texture>();
        _hiddenChestMarkers = Array.Empty<via.gui.Texture>();
        _lockedGateMarkers = Array.Empty<via.gui.Texture>();
        _missionRanges = Array.Empty<via.gui.Circle>();
        _entranceMarkers = Array.Empty<via.gui.Texture>();
        _ladderMarkers = Array.Empty<via.gui.Texture>();
        _collectibleMarkers = Array.Empty<via.gui.Texture>();
        _missionMarkers = Array.Empty<via.gui.Texture>();
        _footprintMarkers = Array.Empty<via.gui.Texture>();
        _markerColorPresets = Array.Empty<_System.Guid>();
        MarkerColors.Clear();
        _playerMarker = null;
        _circleBorder = null;
        _rectangleMask = null;
        _circleMask = null;
        _overlayGroup = null;
        _mapGroup = null;
        _guiWindow = null;
        _guiView = null;
        _gui = null;
        _guiGameObject = null;
        _guiHolderObject = null;
        _guiLoadStartedAt = 0;
        _guiReadyAt = 0;
        TryReleaseResource(_guiResource, "native GUI");
        _guiResource = null;
    }

    private static void TryClearTexture(via.gui.Texture texture, string operation)
    {
        try
        {
            ClearTexture(texture);
        }
        catch (Exception exception)
        {
            LogCleanupWarning(operation, exception);
        }
    }

    private static REFrameworkNET.Resource CreateOwnedResource(
        string typeName,
        string resourcePath)
    {
        var resource = API.GetResourceManager().CreateResource(typeName, resourcePath);
        // The API does not retain the handle for us. Holders own separate references;
        // take our own reference to match the release on rollback, map reset or unload.
        resource?.AddRef();
        return resource;
    }

    private static void TryReleaseResource(
        REFrameworkNET.Resource resource,
        string operation)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Release();
        }
        catch (Exception exception)
        {
            LogCleanupWarning(operation, exception);
        }
    }

    private static void LogCleanupWarning(string operation, Exception exception)
    {
        if (Interlocked.Exchange(ref _cleanupErrorReported, 1) == 0)
        {
            Instance.Log($"{operation} cleanup warning: {exception}", ModLogLevel.Warning);
        }
    }

    private static void ClearTexture(via.gui.Texture texture)
    {
        if (!IsAlive(texture))
        {
            return;
        }

        texture.Visible = false;
        var size = texture.Size;
        size.w = 0.0f;
        size.h = 0.0f;
        texture.Size = size;
        texture.setTexture(null);
    }

    private static bool IsAlive(object proxy)
    {
        var address = GetAddress(proxy);
        return address != 0 && ManagedObject.IsManagedObject(address);
    }

    private static ulong GetAddress(object proxy) =>
        (proxy as IProxyable)?.GetAddress() ?? 0;

    private sealed class MapDefinition
    {
        public MapDefinition(
            int stageKey,
            app.EnvDef.AreaID_Fixed playerArea,
            app.EnvDef.FIELD_ORDER_Fixed playerFloor,
            int mapIndex,
            int areaIndex,
            float rootX,
            float rootY,
            bool isFlipSideUp,
            int rows,
            int columns,
            TileDefinition[] tiles,
            Dictionary<app.EnvDef.AreaID_Fixed, HashSet<app.EnvDef.FIELD_ORDER_Fixed>> areaFields)
        {
            StageKey = stageKey;
            PlayerArea = playerArea;
            PlayerFloor = playerFloor;
            MapIndex = mapIndex;
            AreaIndex = areaIndex;
            RootX = rootX;
            RootY = rootY;
            IsFlipSideUp = isFlipSideUp;
            Rows = rows;
            Columns = columns;
            Tiles = tiles;
            AreaFields = areaFields;
        }

        public int StageKey { get; }
        public app.EnvDef.AreaID_Fixed PlayerArea { get; }
        public app.EnvDef.FIELD_ORDER_Fixed PlayerFloor { get; }
        public int MapIndex { get; }
        public int AreaIndex { get; }
        public float RootX { get; }
        public float RootY { get; }
        public bool IsFlipSideUp { get; }
        public int Rows { get; }
        public int Columns { get; }
        public TileDefinition[] Tiles { get; }
        public Dictionary<app.EnvDef.AreaID_Fixed, HashSet<app.EnvDef.FIELD_ORDER_Fixed>>
            AreaFields { get; }
    }

    private readonly struct TileDefinition
    {
        public TileDefinition(int row, int column, string resourcePath)
        {
            Row = row;
            Column = column;
            ResourcePath = resourcePath;
        }

        public int Row { get; }
        public int Column { get; }
        public string ResourcePath { get; }
    }

    private readonly struct MissionRange
    {
        public MissionRange(float x, float z, float radius, app.MissionDef.MISSION_TYPE missionType)
        {
            X = x;
            Z = z;
            Radius = radius;
            MissionType = missionType;
        }

        public float X { get; }
        public float Z { get; }
        public float Radius { get; }
        public app.MissionDef.MISSION_TYPE MissionType { get; }
    }

    private readonly struct MarkerPosition
    {
        public MarkerPosition(
            float x, float z, uint iconPatternNo = 0, uint iconSequenceNo = 0,
            MarkerColor color = MarkerColor.Original)
        {
            X = x;
            Z = z;
            IconPatternNo = iconPatternNo;
            IconSequenceNo = iconSequenceNo;
            Color = color;
        }

        public float X { get; }
        public float Z { get; }
        public uint IconPatternNo { get; }
        public uint IconSequenceNo { get; }
        public MarkerColor Color { get; }
    }

    private sealed class MapTile
    {
        public MapTile(
            int row,
            int column,
            REFrameworkNET.Resource resource,
            ManagedObject holderObject,
            via.gui.Texture texture)
        {
            Row = row;
            Column = column;
            Resource = resource;
            HolderObject = holderObject;
            Texture = texture;
        }

        public int Row { get; }
        public int Column { get; }
        public REFrameworkNET.Resource Resource { get; }
        public ManagedObject HolderObject { get; }
        public via.gui.Texture Texture { get; }
    }

}
