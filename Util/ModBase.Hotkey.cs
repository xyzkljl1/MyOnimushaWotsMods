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

        return IsKeyTriggered(Key) &&
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

    private static bool IsKeyTriggered(Hexa.NET.ImGui.ImGuiKey key)
    {
        if (TryGetNativeKeyboardKey(key, out var keyboardKey))
        {
            return via.hid.Keyboard.MergedDevice?.isTrigger(keyboardKey) == true;
        }

        if (TryGetNativeGamePadButton(key, out var gamePadButton))
        {
            var device = via.hid.GamePad.MergedDevice;
            return device is not null &&
                   (device.ButtonDown & gamePadButton) != via.hid.GamePadButton.None;
        }

        return Hexa.NET.ImGui.ImGui.IsKeyPressed(key, false);
    }

    private static bool IsNativeModifierDown(
        via.hid.KeyboardKey left,
        via.hid.KeyboardKey right)
    {
        var keyboard = via.hid.Keyboard.MergedDevice;
        return keyboard is not null &&
               (keyboard.isDown(left) || keyboard.isDown(right));
    }

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
