using System;
using System.Numerics;
using System.Threading;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// BEGIN copied source: Util/ModBase.cs
// Source blob SHA-1: 80a2903200cedb41bdff1539bacd6232e31eca02
// Source commit: bf9441cbd6d766904d046ceea6750b80212a516b
public enum ModLogLevel
{
    Info,
    Warning,
    Error,
}

public abstract class ModBase
{
    protected ModBase(string modName, string modVersion)
    {
        ModName = modName;
        ModVersion = modVersion;
    }

    public string ModName { get; }

    public string ModVersion { get; }

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

public sealed class ShowHP : ModBase
{
    // Font height in the game's virtual 1920x1080 UI coordinate system.
    private const float VirtualFontSize = 12.0f;

    private const uint TextColor = 0xFFFFFFFF;
    private const uint OutlineColor = 0xE0000000;

    private static readonly ShowHP Instance = new();
    private static readonly object SnapshotLock = new();

    [ThreadStatic]
    private static ulong _pendingEnemyDamageAddress;

    [ThreadStatic]
    private static bool _pendingEnemyGaugeDamage;

    [ThreadStatic]
    private static int _pendingEnemyDamage;

    [ThreadStatic]
    private static int _pendingEnemyRemainingValue;

    private static HudSnapshot _snapshot;
    private static string _healthText;
    private static string _staminaText;
    private static int _lastHealth = int.MinValue;
    private static int _lastMaxHealth = int.MinValue;
    private static int _lastStamina = int.MinValue;
    private static int _lastMaxStamina = int.MinValue;
    private static int _updateErrorReported;
    private static int _renderErrorReported;
    private static int _enemyErrorReported;

    private ShowHP() : base("ShowHP", "1.0")
    {
    }

    [PluginEntryPoint]
    public static void Main() => Instance.Log("Loaded.");

    [PluginExitPoint]
    public static void OnUnload()
    {
        HideHud();
        _healthText = null;
        _staminaText = null;
        _lastHealth = int.MinValue;
        _lastMaxHealth = int.MinValue;
        _lastStamina = int.MinValue;
        _lastMaxStamina = int.MinValue;
        _updateErrorReported = 0;
        _renderErrorReported = 0;
        _enemyErrorReported = 0;
        ClearPendingEnemyDamage();
    }

    [MethodHook(typeof(app.cGUIGaugeDamage), "setDamage", MethodHookType.Pre)]
    public static PreHookResult BeforeEnemyDamageText(Span<ulong> args)
    {
        ClearPendingEnemyDamage();
        try
        {
            var damage = ManagedObject.ToManagedObject(args[1])?.As<app.cGUIGaugeDamage>();
            if (TryGetEnemyRemainingGaugeValue(damage, out var remainingValue))
            {
                _pendingEnemyDamageAddress = args[1];
                _pendingEnemyGaugeDamage = true;
                _pendingEnemyDamage = (int)args[2];
                _pendingEnemyRemainingValue = remainingValue;
            }
        }
        catch (Exception exception)
        {
            LogEnemyError(exception);
        }

        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.cGUIGaugeDamage), "setDamage", MethodHookType.Post)]
    public static void AfterEnemyDamageText(ref ulong returnValue)
    {
        try
        {
            if (!_pendingEnemyGaugeDamage ||
                !ManagedObject.IsManagedObject(_pendingEnemyDamageAddress))
            {
                return;
            }

            var damage = ManagedObject.ToManagedObject(_pendingEnemyDamageAddress)
                ?.As<app.cGUIGaugeDamage>();
            SetEnemyDamageText(
                damage?._DamageText,
                _pendingEnemyDamage,
                _pendingEnemyRemainingValue);
        }
        catch (Exception exception)
        {
            LogEnemyError(exception);
        }
        finally
        {
            ClearPendingEnemyDamage();
        }
    }

    private static bool TryGetEnemyRemainingGaugeValue(
        app.cGUIGaugeDamage damage,
        out int remainingValue)
    {
        remainingValue = 0;

        var damageAddress = GetAddress(damage);
        var ownerAddress = GetAddress(damage?._Owner);
        if (damageAddress == 0 || ownerAddress == 0 || !ManagedObject.IsManagedObject(ownerAddress))
        {
            return false;
        }

        var ownerObject = ManagedObject.ToManagedObject(ownerAddress);
        var ownerType = ownerObject?.GetTypeDefinition()?.FullName;
        if (ownerType == "app.GUI020200")
        {
            var owner = ownerObject.As<app.GUI020200>();
            var controls = owner?._GaugeControlArray;
            var count = app.GUI020200.MAX_GAUGE_NUM;
            for (var index = 0; controls is not null && index < count; ++index)
            {
                var control = controls[index];
                var character = control?._Target?.Context?.Chara;
                if (GetAddress(control?._HpDamage) == damageAddress)
                {
                    var health = character?.HealthManager;
                    if (health is null)
                    {
                        return false;
                    }

                    remainingValue = Math.Max(0, health.Health);
                    return true;
                }

                if (GetAddress(control?._RikidoDamage) != damageAddress)
                {
                    continue;
                }

                var stamina = character?.RikidoSupporter;
                if (stamina is null)
                {
                    return false;
                }

                remainingValue = Math.Max(0, stamina.getRikidoValue());
                return true;
            }
        }
        else if (ownerType == "app.GUI020207")
        {
            var owner = ownerObject.As<app.GUI020207>();
            var controls = owner?._GaugeControls;
            var count = Math.Clamp(owner?.MAX_GAUGE_NUM ?? 0, 0, 64);
            for (var index = 0; controls is not null && index < count; ++index)
            {
                var control = controls[index];
                var isHealth = GetAddress(control?._HpDamage) == damageAddress;
                var isStamina = GetAddress(control?._RikidoDamage) == damageAddress;
                if (!isHealth && !isStamina)
                {
                    continue;
                }

                var enemy = control?._EnemyInfo;
                if (enemy is null)
                {
                    return false;
                }

                remainingValue = Math.Max(
                    0,
                    isHealth ? enemy.CurrentHp : enemy.CurrentRikido);
                return true;
            }
        }

        return false;
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        try
        {
            if (!TryCaptureHud(out var snapshot))
            {
                HideHud();
                return;
            }

            lock (SnapshotLock)
            {
                _snapshot = snapshot;
            }

            Volatile.Write(ref _updateErrorReported, 0);
        }
        catch (Exception exception)
        {
            HideHud();
            if (Interlocked.Exchange(ref _updateErrorReported, 1) == 0)
            {
                Instance.Log($"HUD update failed: {exception}", ModLogLevel.Error);
            }
        }
    }

    [Callback(typeof(ImGuiRender), CallbackType.Post)]
    public static void OnImGuiRender()
    {
        try
        {
            HudSnapshot snapshot;
            lock (SnapshotLock)
            {
                snapshot = _snapshot;
            }

            if (!snapshot.Visible)
            {
                return;
            }

            var viewport = ImGui.GetMainViewport();
            Vector2 origin;
            Vector2 screenScale;
            {
                var targetPosition = viewport.Pos;
                var targetSize = viewport.Size;

                if (targetSize.X <= 0.0f || targetSize.Y <= 0.0f ||
                    snapshot.VirtualWidth <= 0.0f || snapshot.VirtualHeight <= 0.0f)
                {
                    return;
                }

                if (snapshot.PresentWidth > 0.0f && snapshot.PresentHeight > 0.0f)
                {
                    targetPosition += new Vector2(snapshot.PresentLeft, snapshot.PresentTop);
                    targetSize = new Vector2(snapshot.PresentWidth, snapshot.PresentHeight);
                }

                // RE Engine stretches the virtual HUD canvas to the render area, while
                // applying its own aspect correction in each control's world matrix.
                origin = targetPosition;
                screenScale = new Vector2(
                    targetSize.X / snapshot.VirtualWidth,
                    targetSize.Y / snapshot.VirtualHeight);
            }

            var healthCenter = origin + snapshot.HealthCenter * screenScale;
            var staminaCenter = origin + snapshot.StaminaCenter * screenScale;
            var fontSize = MathF.Max(10.0f, VirtualFontSize * screenScale.Y * snapshot.VerticalHudScale);
            var outline = MathF.Max(1.0f, MathF.Round(screenScale.Y * snapshot.VerticalHudScale));

            ImGui.PushFont(ImGui.GetFont(), fontSize);
            try
            {
                var drawList = ImGui.GetForegroundDrawList(viewport);
                DrawCenteredText(drawList, snapshot.HealthText, healthCenter, outline);
                DrawCenteredText(drawList, snapshot.StaminaText, staminaCenter, outline);
            }
            finally
            {
                ImGui.PopFont();
            }

            Volatile.Write(ref _renderErrorReported, 0);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _renderErrorReported, 1) == 0)
            {
                Instance.Log($"Rendering failed: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static bool TryCaptureHud(out HudSnapshot snapshot)
    {
        snapshot = default;

        var gameFlowManager = API.GetManagedSingletonT<app.GameFlowManager>();
        if (gameFlowManager is null || !gameFlowManager.IsIngameStable)
        {
            return false;
        }

        var guiManager = API.GetManagedSingletonT<app.GUIManager>();
        if (guiManager is null ||
            guiManager.isVisibleGUIApp(app.GUIID.ID.UI010200) ||
            !guiManager.isVisibleGUIApp(app.GUIID.ID.UI020206))
        {
            return false;
        }

        var playerManager = API.GetManagedSingletonT<app.PlayerManager>();
        var character = playerManager?.getControllingPlayerInfo()?.Context?.Chara;
        var healthManager = character?.HealthManager;
        var staminaSupporter = character?.RikidoSupporter;
        if (healthManager is null || staminaSupporter is null)
        {
            return false;
        }

        // GUI controls are owned by the current scene and are destroyed during
        // transitions. Keep every proxy local to this game-update callback.
        var rawHud = (guiManager as IObject)?.Call("getGUI", (int)app.GUIID.ID.UI020206) as ManagedObject;
        var lifeGauge = rawHud?.As<app.GUI020206>()?.LifeGauge;
        var healthPanel = lifeGauge?._HpIncrease?._PanelGauge;
        var staminaPanel = lifeGauge?._RikidoIncrease?._PanelGauge;
        var healthTexture = FindTexture(healthPanel, "tex_Bar");
        var staminaTexture = FindTexture(staminaPanel, "tex_Rikido");
        var rootView = (rawHud?.GetField("_Root") as ManagedObject)?.As<via.gui.View>();
        if (rootView is null || healthPanel is null || staminaPanel is null ||
            healthTexture is null || staminaTexture is null)
        {
            return false;
        }

        var virtualSize = rootView.ScreenSize;
        if (virtualSize.w <= 0.0f || virtualSize.h <= 0.0f)
        {
            return false;
        }

        var healthScale = GetWorldScale(healthPanel.WorldMatrix);
        var staminaScale = GetWorldScale(staminaPanel.WorldMatrix);
        var sceneView = rootView.Component?.SceneView;
        var presentRect = sceneView?.PresentRect ?? default;

        UpdateValueText(
            healthManager.Health,
            healthManager.MaxHealth,
            staminaSupporter.getRikidoValue(),
            (int)MathF.Round(staminaSupporter.getRikidoMaxValue()));

        snapshot = new HudSnapshot
        {
            Visible = true,
            HealthText = _healthText,
            StaminaText = _staminaText,
            VirtualWidth = virtualSize.w,
            VirtualHeight = virtualSize.h,
            PresentLeft = presentRect.l,
            PresentTop = presentRect.t,
            PresentWidth = presentRect.w,
            PresentHeight = presentRect.h,
            // tex_Bar is left-bottom anchored; tex_Rikido is left-top anchored.
            HealthCenter = GetTextureCenter(healthTexture, healthScale, -1.0f),
            StaminaCenter = GetTextureCenter(staminaTexture, staminaScale, 1.0f),
            VerticalHudScale = (healthScale.Y + staminaScale.Y) * 0.5f,
        };
        return true;
    }

    private static void UpdateValueText(int health, int maxHealth, int stamina, int maxStamina)
    {
        if (health != _lastHealth || maxHealth != _lastMaxHealth)
        {
            _lastHealth = health;
            _lastMaxHealth = maxHealth;
            _healthText = $"{health} / {maxHealth}";
        }

        if (stamina != _lastStamina || maxStamina != _lastMaxStamina)
        {
            _lastStamina = stamina;
            _lastMaxStamina = maxStamina;
            _staminaText = $"{stamina} / {maxStamina}";
        }
    }

    private static void HideHud()
    {
        lock (SnapshotLock)
        {
            _snapshot = default;
        }
    }

    private static void SetEnemyDamageText(
        via.gui.Text damageText,
        int damage,
        int remainingValue)
    {
        if (damageText is null)
        {
            return;
        }

        damageText.Message = $"{damage}({remainingValue})";
        Volatile.Write(ref _enemyErrorReported, 0);
    }

    private static void LogEnemyError(Exception exception)
    {
        if (Interlocked.Exchange(ref _enemyErrorReported, 1) == 0)
        {
            Instance.Log($"Enemy HP display failed: {exception}", ModLogLevel.Error);
        }
    }

    private static void ClearPendingEnemyDamage()
    {
        _pendingEnemyDamageAddress = 0;
        _pendingEnemyGaugeDamage = false;
        _pendingEnemyDamage = 0;
        _pendingEnemyRemainingValue = 0;
    }

    private static ulong GetAddress(object proxy) =>
        (proxy as IProxyable)?.GetAddress() ?? 0;

    private static via.gui.Texture FindTexture(via.gui.Panel panel, string name)
    {
        var child = panel?.Child;
        for (var count = 0; child is not null && count++ < 64; child = child.Next)
        {
            if (child.Name == name)
            {
                var address = (child as IProxyable)?.GetAddress() ?? 0;
                return ManagedObject.ToManagedObject(address)?.TryAs<via.gui.Texture>();
            }
        }

        return null;
    }

    private static Vector2 GetWorldScale(via.mat4 matrix)
    {
        var x = MathF.Sqrt(matrix.m00 * matrix.m00 + matrix.m01 * matrix.m01);
        var y = MathF.Sqrt(matrix.m10 * matrix.m10 + matrix.m11 * matrix.m11);
        return new Vector2(MathF.Max(x, 0.01f), MathF.Max(y, 0.01f));
    }

    private static Vector2 GetTextureCenter(
        via.gui.Texture texture,
        Vector2 worldScale,
        float verticalDirection)
    {
        var position = texture.GlobalPosition;
        var size = texture.Size;
        return new Vector2(
            position.x + size.w * worldScale.X * 0.5f,
            position.y + size.h * worldScale.Y * 0.5f * verticalDirection);
    }

    private static void DrawCenteredText(ImDrawListPtr drawList, string text, Vector2 center, float outline)
    {
        var position = center - ImGui.CalcTextSize(text) * 0.5f;

        drawList.AddText(position + new Vector2(-outline, 0.0f), OutlineColor, text);
        drawList.AddText(position + new Vector2(outline, 0.0f), OutlineColor, text);
        drawList.AddText(position + new Vector2(0.0f, -outline), OutlineColor, text);
        drawList.AddText(position + new Vector2(0.0f, outline), OutlineColor, text);
        drawList.AddText(position, TextColor, text);
    }

    private struct HudSnapshot
    {
        public bool Visible;
        public string HealthText;
        public string StaminaText;
        public float VirtualWidth;
        public float VirtualHeight;
        public float PresentLeft;
        public float PresentTop;
        public float PresentWidth;
        public float PresentHeight;
        public Vector2 HealthCenter;
        public Vector2 StaminaCenter;
        public float VerticalHudScale;
    }
}
