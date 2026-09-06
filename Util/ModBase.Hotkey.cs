// Module: Persistent keyboard/gamepad shortcuts and their ImGui editor.
// Requires: Util/ModBase.cs and Util/ModBase.Config.cs from the same commit.
// Add a binding with AddHotkeyConfig(), then call IsHotkeyPressed() once per ImGui frame.
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

    public bool IsPressed(bool allowWhenKeyboardCaptured = false)
    {
        if (Key == Hexa.NET.ImGui.ImGuiKey.None || !IsBindableKey(Key))
        {
            return false;
        }

        if (!allowWhenKeyboardCaptured &&
            Hexa.NET.ImGui.ImGui.GetIO().WantCaptureKeyboard)
        {
            return false;
        }

        return Hexa.NET.ImGui.ImGui.IsKeyPressed(Key, false) &&
               Ctrl == Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModCtrl) &&
               Shift == Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModShift) &&
               Alt == Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.ModAlt);
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

    protected static bool IsHotkeyPressed(
        ModConfig<ModHotkey> hotkey,
        bool allowWhenKeyboardCaptured = false)
    {
        System.ArgumentNullException.ThrowIfNull(hotkey);
        return hotkey.Value.IsPressed(allowWhenKeyboardCaptured);
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

        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button($"clear{id}.Clear"))
        {
            value = default;
            if (isCapturing)
            {
                _capturingHotkeyId = null;
            }

            changed = true;
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
