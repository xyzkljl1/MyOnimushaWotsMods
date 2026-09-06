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
