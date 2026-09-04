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
