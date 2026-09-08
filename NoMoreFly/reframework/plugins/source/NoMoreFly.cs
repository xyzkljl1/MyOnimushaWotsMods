using System;
using System.Collections.Generic;
using System.Threading;
using REFrameworkNET;
using REFrameworkNET.Attributes;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 25417359db8c70a84c6f557d62440b857d2d6419
// Source commit: 031e7b66ecf487e0cfe8b09e7a24c60792565c65
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

public sealed class NoMoreFly : ModBase
{
    private const int LayoutArgumentIndex = 4;
    private const int MaximumReplacementLogs = 8;
    private static readonly NoMoreFly Instance = new();
    private static int _replacementCount;

    // Hooks can run on different native threads, or be nested on the same thread.
    [ThreadStatic]
    private static Stack<ReplacementFrame> _frames;

    private NoMoreFly() : base("NoMoreFly", "1.0") { }

    [PluginEntryPoint]
    public static void Main()
    {
        Instance.Log("Loaded. Replaces Kubi Akari with Hitotsume Gasa when enemy contexts are created.");
        Instance.Log("Existing enemies need an area/save reload to be generated again.");
    }

    [PluginExitPoint]
    public static void OnUnload() =>
        Instance.Log($"Unloaded. Replaced {Volatile.Read(ref _replacementCount)} enemy contexts.");

    [MethodHook(typeof(app.cEnemyContextParam), "setup", MethodHookType.Pre)]
    public static PreHookResult BeforeEnemySetup(Span<ulong> args)
    {
        var frames = _frames ??= new Stack<ReplacementFrame>();
        frames.Push(default);
        try
        {
            var layout = GetHookArgument<app.cContextLayoutEnemy>(args, LayoutArgumentIndex);
            if (layout is null || !TryGetReplacement(layout.EmID, out var replacementID))
                return PreHookResult.Continue;

            var context = GetHookArgument<app.cEnemyContextParam>(args, 1);
            if (context is null)
                return PreHookResult.Continue;

            var replacement = CreateReplacementLayout(layout, replacementID);
            frames.Pop();
            frames.Push(new ReplacementFrame(replacement, context, replacementID));
            args[LayoutArgumentIndex] = ((IProxyable)replacement).GetAddress();
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Could not prepare enemy replacement", exception);
        }
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.cEnemyContextParam), "setup", MethodHookType.Post)]
    public static void AfterEnemySetup(ref ulong returnValue)
    {
        if (_frames is null || _frames.Count == 0)
            return;
        var frame = _frames.Pop();
        if (frame.Layout is null)
            return;

        try
        {
            if (frame.Context.EnemyID != frame.ReplacementID)
                throw new InvalidOperationException("The enemy context did not use the replacement ID.");

            var count = Interlocked.Increment(ref _replacementCount);
            if (count <= MaximumReplacementLogs)
                Instance.Log($"Replacement {count}: enemy context initialized as {frame.ReplacementID}.");
        }
        catch (Exception exception)
        {
            Instance.LogErrorOnce("Could not verify enemy replacement", exception);
        }
        finally
        {
            // The game retains any references it needs during setup. Release only our owner.
            frame.Layout.Dispose();
        }
    }

    private static bool TryGetReplacement(
        app.EnemyDef.EnemyID_Fixed original,
        out app.EnemyDef.EnemyID_Fixed replacement)
    {
        // Enemy-book GUIDs identify EM102_00 as Kubi Akari and EM101_00 as Hitotsume Gasa.
        // Keep the corresponding variant for EM102_50.
        replacement = original switch
        {
            app.EnemyDef.EnemyID_Fixed.EM102_00 => app.EnemyDef.EnemyID_Fixed.EM101_00,
            app.EnemyDef.EnemyID_Fixed.EM102_50 => app.EnemyDef.EnemyID_Fixed.EM101_50,
            _ => original,
        };
        return replacement != original;
    }

    private static ManagedObject CreateReplacementLayout(
        app.cContextLayoutEnemy source,
        app.EnemyDef.EnemyID_Fixed replacementID)
    {
        var owner = CloneNative(source);
        try
        {
            var copy = owner.As<app.cContextLayoutEnemy>();
            copy._EmID = unchecked((int)(uint)replacementID);
            copy._WeaponType = unchecked((int)(uint)app.WeaponDef.PACK_TYPE_Fixed.EM101_SWORD);

            // These four values control species-specific variations. A shallow layout
            // clone still shares this array, so clone it before resetting the values.
            if (source._SetParams is null || source._SetParams.Length != 4)
                throw new InvalidOperationException("Unexpected enemy variation parameter array.");
            using var parameters = CloneNative(source._SetParams);
            var copiedParameters = parameters.As<app.EnemyDef.SET_PARAM_Array1D>();
            for (var index = 0; index < 4; ++index)
                copiedParameters[index] = app.EnemyDef.SET_PARAM.DEFAULT;
            copy._SetParams = copiedParameters;
            copy._RuntimeSetParamType = app.EnemyDef.SET_PARAM.DEFAULT;
            copy._SetChangeParamType = app.EnemyDef.SET_PARAM.DEFAULT;

            // Leave placement, difficulty, encounter GUIDs, save identifiers and mission
            // conditions on the clone intact; the original map layout is never edited.
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static ManagedObject CloneNative(object source)
    {
        var sourceAddress = (source as IProxyable)?.GetAddress() ?? 0;
        var native = GetManagedObject<_System.Object>(sourceAddress)
            ?? throw new InvalidOperationException("Invalid native object to clone.");
        var raw = native.MemberwiseClone();
        var address = (raw as IProxyable)?.GetAddress() ?? 0;
        if (address == 0 || !ManagedObject.IsManagedObject(address))
            throw new InvalidOperationException("Native layout clone failed.");
        return ManagedObject.ToManagedObject(address).Globalize();
    }

    private readonly struct ReplacementFrame
    {
        public ReplacementFrame(
            ManagedObject layout,
            app.cEnemyContextParam context,
            app.EnemyDef.EnemyID_Fixed replacementID)
        {
            Layout = layout;
            Context = context;
            ReplacementID = replacementID;
        }

        public ManagedObject Layout { get; }
        public app.cEnemyContextParam Context { get; }
        public app.EnemyDef.EnemyID_Fixed ReplacementID { get; }
    }
}
