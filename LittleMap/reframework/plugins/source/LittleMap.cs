using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
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

public sealed class LittleMap : ModBase
{
    private const int TilePixels = 2048;
    private const float WorldToMapPixels = 6.40000009536743f;
    private const string TileNamePrefix = "LittleMap_";
    private const long SetPlayObjectNameAddress = 0x148afd3a0;
    private const long RetryDelayMilliseconds = 1000;

    private const uint BorderColor = 0xE0D0B070;
    private const uint PlayerOutlineColor = 0xF0000000;
    private const uint PlayerColor = 0xFF50E8FF;

    private static readonly LittleMap Instance = new();
    private static readonly List<MapTile> Tiles = new();

    private static bool _enabled = true;
    private static float _width = 420.0f;
    private static float _height = 280.0f;
    private static float _pixelsPerMeter = 4.8f;
    private static float _rightMargin = 36.0f;
    private static float _topMargin = 80.0f;

    private static MapDefinition _map;
    private static OverlaySnapshot _overlay;
    private static ulong _hudRootAddress;
    private static long _nextRetryTick;
    private static int _errorReported;
    private static int _cleanupErrorReported;

    private LittleMap() : base("LittleMap", "1.0")
    {
    }

    [PluginEntryPoint]
    public static void Main() =>
        Instance.Log("Loaded with the game's native map textures.");

    [PluginExitPoint]
    public static void OnUnload()
    {
        Volatile.Write(ref _overlay, null);
        ResetMap();
        _nextRetryTick = 0;
        _errorReported = 0;
        _cleanupErrorReported = 0;
        Instance.Log("Unloaded and removed native map textures.");
    }

    [Callback(typeof(ImGuiDrawUI), CallbackType.Post)]
    public static void OnDrawUI()
    {
        if (!ImGui.TreeNode("LittleMap v1.0"))
        {
            return;
        }

        try
        {
            ImGui.Checkbox("Enabled##LittleMap", ref _enabled);
            ImGui.SliderFloat("Width##LittleMap", ref _width, 240.0f, 800.0f, "%.0f");
            ImGui.SliderFloat("Height##LittleMap", ref _height, 160.0f, 540.0f, "%.0f");
            ImGui.SliderFloat(
                "Zoom##LittleMap", ref _pixelsPerMeter, 2.0f, 10.0f, "%.1f px/m");
            ImGui.SliderFloat(
                "Right margin##LittleMap", ref _rightMargin, 0.0f, 500.0f, "%.0f");
            ImGui.SliderFloat(
                "Top margin##LittleMap", ref _topMargin, 0.0f, 500.0f, "%.0f");
            ImGui.TextWrapped(
                "Uses the current area's original map artwork and keeps the player centered. " +
                "The large-map screen itself is never created or controlled.");
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdate()
    {
        try
        {
            if (!_enabled || !TryGetGameContext(
                    out var guiManager,
                    out var root,
                    out var fixedStage,
                    out var playerTransform))
            {
                HideMap();
                return;
            }

            var rootAddress = GetAddress(root);
            var stageKey = unchecked((int)(uint)fixedStage);
            if (_map is null || _map.StageKey != stageKey || _hudRootAddress != rootAddress)
            {
                if (Environment.TickCount64 < _nextRetryTick)
                {
                    HideMap();
                    return;
                }

                ResetMap();
                RemoveNamedTiles(root);
                if (!TryBuildMap(guiManager, fixedStage, out var map))
                {
                    _nextRetryTick = Environment.TickCount64 + RetryDelayMilliseconds;
                    HideMap();
                    return;
                }

                _map = map;
                _hudRootAddress = rootAddress;
            }

            if (Tiles.Count < _map.Tiles.Length)
            {
                if (Environment.TickCount64 < _nextRetryTick ||
                    !TryCreateNextTile(root, _map))
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
                    $"root ({_map.RootX:0.#}, {_map.RootY:0.#}).");
            }

            var screen = root.ScreenSize;
            if (screen.w <= 0.0f || screen.h <= 0.0f)
            {
                HideMap();
                return;
            }

            var displayWidth = Math.Clamp(_width, 1.0f, screen.w);
            var displayHeight = Math.Clamp(_height, 1.0f, screen.h);
            var left = Math.Clamp(
                screen.w - _rightMargin - displayWidth,
                0.0f,
                screen.w - displayWidth);
            var top = Math.Clamp(_topMargin, 0.0f, screen.h - displayHeight);
            var position = playerTransform.Position;
            var mapScale = _map.IsFlipSideUp ? -WorldToMapPixels : WorldToMapPixels;
            var mapX = _map.RootX + position.x * mapScale;
            var mapY = _map.RootY + position.z * mapScale;
            UpdateTiles(mapX, mapY, left, top, displayWidth, displayHeight);

            var forward = playerTransform.AxisZ;
            var present = root.Component?.SceneView?.PresentRect ?? default;
            Volatile.Write(ref _overlay, new OverlaySnapshot(
                left,
                top,
                displayWidth,
                displayHeight,
                screen.w,
                screen.h,
                present.l,
                present.t,
                present.w,
                present.h,
                forward.x * MathF.Sign(mapScale),
                forward.z * MathF.Sign(mapScale)));
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

    [Callback(typeof(ImGuiRender), CallbackType.Post)]
    public static void OnImGuiRender()
    {
        var snapshot = Volatile.Read(ref _overlay);
        if (snapshot is null)
        {
            return;
        }

        try
        {
            var viewport = ImGui.GetMainViewport();
            var origin = viewport.Pos;
            var targetSize = viewport.Size;
            if (snapshot.PresentWidth > 0.0f && snapshot.PresentHeight > 0.0f)
            {
                origin += new Vector2(snapshot.PresentLeft, snapshot.PresentTop);
                targetSize = new Vector2(snapshot.PresentWidth, snapshot.PresentHeight);
            }

            if (targetSize.X <= 0.0f || targetSize.Y <= 0.0f ||
                snapshot.VirtualWidth <= 0.0f || snapshot.VirtualHeight <= 0.0f)
            {
                return;
            }

            var scale = new Vector2(
                targetSize.X / snapshot.VirtualWidth,
                targetSize.Y / snapshot.VirtualHeight);
            var minimum = origin + new Vector2(snapshot.Left, snapshot.Top) * scale;
            var maximum = minimum + new Vector2(snapshot.Width, snapshot.Height) * scale;
            var center = (minimum + maximum) * 0.5f;
            var uiScale = MathF.Max(0.5f, scale.Y);
            var drawList = ImGui.GetForegroundDrawList(viewport);
            drawList.AddRect(minimum, maximum, BorderColor, 2.0f * uiScale, 1.5f * uiScale);
            DrawPlayer(
                drawList,
                center,
                snapshot.ForwardX,
                snapshot.ForwardY,
                uiScale);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"Overlay rendering failed: {exception}", ModLogLevel.Error);
            }
        }
    }

    private static bool TryGetGameContext(
        out app.GUIManager guiManager,
        out via.gui.View root,
        out app.EnvDef.StageID_Fixed fixedStage,
        out via.Transform playerTransform)
    {
        guiManager = null;
        root = null;
        fixedStage = default;
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
        return IsAlive(playerTransform) &&
            IsAlive(environment) &&
            Enum.TryParse(environment.StageID.ToString(), out fixedStage);
    }

    private static bool TryBuildMap(
        app.GUIManager guiManager,
        app.EnvDef.StageID_Fixed fixedStage,
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
                if (IsAlive(candidate) && candidate.StageID?.Value == stageKey)
                {
                    area = candidate;
                    break;
                }
            }
        }

        var textureRows = area?.Textures;
        if (!IsAlive(area) || !IsAlive(textureRows))
        {
            return false;
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
            area.Root.x,
            area.Root.y,
            area.IsFlipSideUp,
            textureRows.Count,
            columns,
            definitions.ToArray());
        return true;
    }

    private static bool TryCreateNextTile(
        via.gui.View root,
        MapDefinition map)
    {
        try
        {
            var definition = map.Tiles[Tiles.Count];
            var resourceManager = API.GetResourceManager();
            var resource = resourceManager.CreateResource(
                "via.render.TextureResource", definition.ResourcePath);
            var holderObject = resource?.CreateHolder(
                "via.render.TextureResourceHolder");
            var holder = holderObject?.TryAs<via.render.TextureResourceHolder>();
            var textureObject = via.gui.Texture.REFType.CreateInstance(0);
            var texture = textureObject?.TryAs<via.gui.Texture>();
            if (resource is null || !IsAlive(holder) || !IsAlive(texture))
            {
                throw new InvalidOperationException(
                    $"Could not create native texture {definition.ResourcePath}.");
            }

            texture.Visible = false;
            texture.AssetType = via.gui.TextureAssetType.Texture;
            texture.UVType = via.gui.UVValueType.Rect;
            texture.ControlPoint = via.gui.ControlPoint.LeftTop;
            texture.setTexture(holder);
            SetPlayObjectName(
                texture,
                $"{TileNamePrefix}{definition.Row}_{definition.Column}");
            if (!root.addChildByScript(texture))
            {
                throw new InvalidOperationException(
                    $"addChildByScript returned false for " +
                    $"tile [{definition.Row},{definition.Column}].");
            }

            Tiles.Add(new MapTile(
                definition.Row,
                definition.Column,
                resource,
                holderObject,
                textureObject,
                texture));
            return true;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                Instance.Log($"Native map creation will retry: {exception}", ModLogLevel.Error);
            }

            return false;
        }
    }

    private static void UpdateTiles(
        float centerX,
        float centerY,
        float left,
        float top,
        float displayWidth,
        float displayHeight)
    {
        var pixelsPerMeter = Math.Max(_pixelsPerMeter, 0.1f);
        var displayPerSourcePixel = pixelsPerMeter / WorldToMapPixels;
        var sourceWidth = displayWidth / displayPerSourcePixel;
        var sourceHeight = displayHeight / displayPerSourcePixel;
        var sourceLeft = centerX - sourceWidth * 0.5f;
        var sourceTop = centerY - sourceHeight * 0.5f;
        var sourceRight = sourceLeft + sourceWidth;
        var sourceBottom = sourceTop + sourceHeight;
        foreach (var tile in Tiles)
        {
            var tileLeft = tile.Column * TilePixels;
            var tileTop = tile.Row * TilePixels;
            var intersectionLeft = MathF.Max(sourceLeft, tileLeft);
            var intersectionTop = MathF.Max(sourceTop, tileTop);
            var intersectionRight = MathF.Min(sourceRight, tileLeft + TilePixels);
            var intersectionBottom = MathF.Min(sourceBottom, tileTop + TilePixels);
            var targetX = left;
            var targetY = top;
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
                targetX = left +
                    (intersectionLeft - sourceLeft) * displayPerSourcePixel;
                targetY = top +
                    (intersectionTop - sourceTop) * displayPerSourcePixel;
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
            tile.Texture.Visible = true;
        }
    }

    private static void SetPlayObjectName(via.gui.Texture texture, string name)
    {
        var text = Marshal.StringToHGlobalUni(name);
        try
        {
            var setter = Marshal.GetDelegateForFunctionPointer<SetPlayObjectNameDelegate>(
                new IntPtr(SetPlayObjectNameAddress));
            setter(IntPtr.Zero, new IntPtr((long)GetAddress(texture)), ref text);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetPlayObjectNameDelegate(
        IntPtr context,
        IntPtr instance,
        ref IntPtr value);

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

    private static void DrawPlayer(
        ImDrawListPtr drawList,
        Vector2 center,
        float forwardX,
        float forwardY,
        float uiScale)
    {
        var direction = new Vector2(forwardX, forwardY);
        if (direction.LengthSquared() < 0.0001f)
        {
            direction = new Vector2(0.0f, -1.0f);
        }
        else
        {
            direction = Vector2.Normalize(direction);
        }

        var right = new Vector2(-direction.Y, direction.X);
        var tip = center + direction * (13.0f * uiScale);
        var rear = center - direction * (8.0f * uiScale);
        var left = rear + right * (7.0f * uiScale);
        var rightPoint = rear - right * (7.0f * uiScale);
        drawList.AddTriangle(tip, left, rightPoint, PlayerOutlineColor, 4.0f * uiScale);
        drawList.AddTriangleFilled(tip, left, rightPoint, PlayerColor);
    }

    private static void HideMap()
    {
        foreach (var tile in Tiles)
        {
            if (IsAlive(tile.Texture))
            {
                tile.Texture.Visible = false;
            }
        }

        Volatile.Write(ref _overlay, null);
    }

    private static void ResetMap()
    {
        RemoveTiles(Tiles);
        Tiles.Clear();
        _map = null;
        _hudRootAddress = 0;
    }

    private static void RemoveTiles(IEnumerable<MapTile> tiles)
    {
        foreach (var tile in tiles)
        {
            try
            {
                RemoveTexture(tile.Texture);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _cleanupErrorReported, 1) == 0)
                {
                    Instance.Log($"Map cleanup warning: {exception}", ModLogLevel.Warning);
                }
            }
        }
    }

    private static void RemoveNamedTiles(via.gui.View root)
    {
        if (!IsAlive(root))
        {
            return;
        }

        var matches = new List<via.gui.Texture>();
        var inspected = 0;
        for (var child = root.Child;
             child is not null && inspected++ < 2048;
             child = child.Next)
        {
            if (child.Name?.StartsWith(TileNamePrefix, StringComparison.Ordinal) != true)
            {
                continue;
            }

            var texture = ManagedObject.ToManagedObject(GetAddress(child))
                ?.TryAs<via.gui.Texture>();
            if (IsAlive(texture))
            {
                matches.Add(texture);
            }
        }

        foreach (var texture in matches)
        {
            try
            {
                RemoveTexture(texture);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _cleanupErrorReported, 1) == 0)
                {
                    Instance.Log(
                        $"Stale map cleanup warning: {exception}",
                        ModLogLevel.Warning);
                }
            }
        }
    }

    private static void RemoveTexture(via.gui.Texture texture)
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
        texture.remove();
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
            float rootX,
            float rootY,
            bool isFlipSideUp,
            int rows,
            int columns,
            TileDefinition[] tiles)
        {
            StageKey = stageKey;
            RootX = rootX;
            RootY = rootY;
            IsFlipSideUp = isFlipSideUp;
            Rows = rows;
            Columns = columns;
            Tiles = tiles;
        }

        public int StageKey { get; }
        public float RootX { get; }
        public float RootY { get; }
        public bool IsFlipSideUp { get; }
        public int Rows { get; }
        public int Columns { get; }
        public TileDefinition[] Tiles { get; }
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

    private sealed class MapTile
    {
        public MapTile(
            int row,
            int column,
            REFrameworkNET.Resource resource,
            ManagedObject holderObject,
            ManagedObject textureObject,
            via.gui.Texture texture)
        {
            Row = row;
            Column = column;
            Resource = resource;
            HolderObject = holderObject;
            TextureObject = textureObject;
            Texture = texture;
        }

        public int Row { get; }
        public int Column { get; }
        public REFrameworkNET.Resource Resource { get; }
        public ManagedObject HolderObject { get; }
        public ManagedObject TextureObject { get; }
        public via.gui.Texture Texture { get; }
    }

    private sealed class OverlaySnapshot
    {
        public OverlaySnapshot(
            float left,
            float top,
            float width,
            float height,
            float virtualWidth,
            float virtualHeight,
            float presentLeft,
            float presentTop,
            float presentWidth,
            float presentHeight,
            float forwardX,
            float forwardY)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            VirtualWidth = virtualWidth;
            VirtualHeight = virtualHeight;
            PresentLeft = presentLeft;
            PresentTop = presentTop;
            PresentWidth = presentWidth;
            PresentHeight = presentHeight;
            ForwardX = forwardX;
            ForwardY = forwardY;
        }

        public float Left { get; }
        public float Top { get; }
        public float Width { get; }
        public float Height { get; }
        public float VirtualWidth { get; }
        public float VirtualHeight { get; }
        public float PresentLeft { get; }
        public float PresentTop { get; }
        public float PresentWidth { get; }
        public float PresentHeight { get; }
        public float ForwardX { get; }
        public float ForwardY { get; }
    }
}
