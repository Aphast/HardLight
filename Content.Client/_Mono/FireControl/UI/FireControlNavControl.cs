// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 RikuTheKiller
// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Shared._Mono.FireControl;
using Content.Shared.Physics;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Content.Shared._Crescent.ShipShields;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Client._Mono.FireControl.UI;

public sealed class FireControlNavControl : BaseShuttleControl
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    private readonly SharedShuttleSystem _shuttles;
    private readonly SharedTransformSystem _transform;
    private readonly RadarBlipsSystem _blips;
    private readonly SharedPhysicsSystem _physics;

    private EntityCoordinates? _coordinates;
    private EntityUid? _consoleEntity;
    private Angle? _rotation;
    private Dictionary<NetEntity, List<DockingPortState>> _docks = new();

    private EntityUid? _activeConsole;
    private FireControllableEntry[]? _controllables;
    private HashSet<NetEntity> _selectedWeapons = new();

    private List<Entity<MapGridComponent>> _grids = new();

    #region Mono

    private static readonly float RadarRequestInterval = (float) RadarBlipsSystem.RequestThrottle.TotalSeconds;
    private float _requestAccumulator = 0f;
    #endregion

    private bool _isMouseDown;
    private bool _isMouseInside;
    private Vector2 _lastMousePos;
    private float _lastFireTime;
    private const float FireRateLimit = 0.1f;
    private float _lastCursorUpdateTime;
    private const float CursorUpdateInterval = 0.05f;

    // HardLight: perpendicular offsets (metres) for the targeting-line safety corridor. Only the
    // centre line is drawn, and only when all offsets are clear (margin against clipping own hull).
    private static readonly float[] TargetingCorridorOffsets = { -0.2f, -0.1f, 0f, 0.1f, 0.2f };

    public Action<EntityCoordinates>? OnRadarClick;
    public bool ShowIFF { get; set; } = true;

    public FireControlNavControl() : base(64f, 1500f, 512f)
    {
        IoCManager.InjectDependencies(this);
        _shuttles = EntManager.System<SharedShuttleSystem>();
        _transform = EntManager.System<SharedTransformSystem>();
        _blips = EntManager.System<RadarBlipsSystem>();
        _physics = EntManager.System<SharedPhysicsSystem>();

        OnMouseEntered += HandleMouseEntered;
        OnMouseExited += HandleMouseExited;
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_isMouseInside)
        {
            _lastMousePos = args.RelativePosition;

            // Continuously update the cursor position for guided missiles
            TryUpdateCursorPosition(_lastMousePos);
        }
    }

    private void HandleMouseEntered(GUIMouseHoverEventArgs args)
    {
        _isMouseInside = true;
        _lastMousePos = UserInterfaceManager.MousePositionScaled.Position - GlobalPosition;
    }

    private void HandleMouseExited(GUIMouseHoverEventArgs args)
    {
        _isMouseInside = false;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        _isMouseDown = true;
        _lastMousePos = args.RelativePosition;
        TryFireAtPosition(_lastMousePos);
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        _isMouseDown = false;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        _requestAccumulator += args.DeltaSeconds;

        if (_requestAccumulator >= RadarRequestInterval)
        {
            _requestAccumulator = 0;

            if (_consoleEntity != null)
                _blips.RequestBlips((EntityUid)_consoleEntity);
        }

        if (_isMouseDown && _isMouseInside)
        {
            var currentTime = IoCManager.Resolve<IGameTiming>().CurTime.TotalSeconds;
            if (currentTime - _lastFireTime >= FireRateLimit)
            {
                var mousePos = UserInterfaceManager.MousePositionScaled.Position - GlobalPosition;
                if (mousePos != _lastMousePos)
                {
                    _lastMousePos = mousePos;
                }
                TryFireAtPosition(_lastMousePos);
                _lastFireTime = (float)currentTime;
            }
        }
    }

    private void TryFireAtPosition(Vector2 relativePosition)
    {
        var coords = GetMouseEntityCoordinates(relativePosition);
        OnRadarClick?.Invoke(coords);
    }

    public void SetMatrix(EntityCoordinates? coordinates, Angle? angle)
    {
        _coordinates = coordinates;
        _rotation = angle;
    }

    public void SetConsole(EntityUid? consoleEntity)
    {
        if (_consoleEntity == consoleEntity)
            return;

        _consoleEntity = consoleEntity;
        _requestAccumulator = 0f;

        if (_consoleEntity != null)
            _blips.RequestBlips(_consoleEntity.Value, force: true);
    }

    public void UpdateState(NavInterfaceState state)
    {
        SetMatrix(EntManager.GetCoordinates(state.Coordinates), state.Angle);
        _docks = state.Docks;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        DrawBacking(handle);
        DrawCircles(handle);

        if (_coordinates == null || _rotation == null)
        {
            DrawNoSignal(handle);
            return;
        }

        var xformQuery = EntManager.GetEntityQuery<TransformComponent>();
        var fixturesQuery = EntManager.GetEntityQuery<FixturesComponent>();
        var bodyQuery = EntManager.GetEntityQuery<PhysicsComponent>();

        if (!xformQuery.TryGetComponent(_coordinates.Value.EntityId, out var xform)
            || xform.MapID == MapId.Nullspace)
        {
            return;
        }

        // HardLight: follow the ship's live rotation, not the console's fixed local angle (which froze
        // the radar facing north and desynced click inversion in GetMouseEntityCoordinates).
        var coordEnt = _coordinates.Value.EntityId;
        _rotation = _transform.GetWorldRotation(coordEnt);

        var worldRot = _rotation.Value;

        var mapPos = _transform.ToMapCoordinates(_coordinates.Value).Offset(_rotation.Value.RotateVec(Offset));
        var mapCoord = _transform.ToCoordinates(mapPos);
        var worldToShuttle = Matrix3Helpers.CreateTranslation(-mapCoord.Position) * Matrix3Helpers.CreateRotation(-worldRot);
        Matrix3x2.Invert(worldToShuttle, out var shuttleToWorld);
        var shuttleToView = Matrix3x2.CreateScale(new Vector2(MinimapScale, -MinimapScale)) * Matrix3x2.CreateTranslation(MidPointVector);
        var worldToView = worldToShuttle * shuttleToView;
        Matrix3x2.Invert(worldToView, out var viewToWorld);

        var ourGridId = xform.GridUid;
        if (EntManager.TryGetComponent<MapGridComponent>(ourGridId, out var ourGrid) &&
            fixturesQuery.HasComponent(ourGridId.Value))
        {
            var ourGridToWorld = _transform.GetWorldMatrix(ourGridId.Value);
            var ourGridToShuttle = Matrix3x2.Multiply(ourGridToWorld, worldToShuttle);
            var ourGridToView = ourGridToShuttle * shuttleToView;
            var color = _shuttles.GetIFFColor(ourGridId.Value, self: true);

            DrawGrid(handle, ourGridToView, (ourGridId.Value, ourGrid), color);
        }

        const float radarVertRadius = 2f;
        var radarPosVerts = new Vector2[]
        {
            ScalePosition(new Vector2(0f, -radarVertRadius)),
            ScalePosition(new Vector2(radarVertRadius / 2f, 0f)),
            ScalePosition(new Vector2(0f, radarVertRadius)),
            ScalePosition(new Vector2(radarVertRadius / -2f, 0f)),
        };

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, radarPosVerts, Color.Lime);

        // Draw shields
        DrawShields(handle, xform, worldToShuttle);

        // Draw safe zone ring
        DrawSafeZoneRing(handle);

        _grids.Clear();
        var maxRange = new Vector2(WorldRange, WorldRange);
        _mapManager.FindGridsIntersecting(xform.MapID, new Box2(mapPos.Position - maxRange, mapPos.Position + maxRange), ref _grids, approx: true, includeMap: false);

        foreach (var grid in _grids)
        {
            var gUid = grid.Owner;
            if (gUid == ourGridId || !fixturesQuery.HasComponent(gUid))
                continue;

            var gridBody = bodyQuery.GetComponent(gUid);
            EntManager.TryGetComponent<IFFComponent>(gUid, out var iff);

            if (!_shuttles.CanDraw(gUid, gridBody, iff))
                continue;

            var curGridToWorld = _transform.GetWorldMatrix(gUid);
            var curGridToView = curGridToWorld * worldToView;

            var labelColor = _shuttles.GetIFFColor(grid, self: false, iff);
            var coordColor = new Color(labelColor.R * 0.8f, labelColor.G * 0.8f, labelColor.B * 0.8f, 0.5f);

            DrawGrid(handle, curGridToView, grid, labelColor);

            if (ShowIFF)
            {
                var labelName = _shuttles.GetIFFLabel(grid, self: false, iff);
                if (labelName != null)
                {
                    var gridBounds = grid.Comp.LocalAABB;
                    var gridCentre = Vector2.Transform(gridBody.LocalCenter, curGridToView);

                    var distance = gridCentre.Length();
                    var labelText = Loc.GetString("shuttle-console-iff-label", ("name", labelName),
                        ("distance", $"{distance:0.0}"));

                    var mapCoords = _transform.GetWorldPosition(gUid);
                    var coordsText = $"({mapCoords.X:0.0}, {mapCoords.Y:0.0})";

                    var labelDimensions = handle.GetDimensions(Font, labelText, 1f);
                    var coordsDimensions = handle.GetDimensions(Font, coordsText, 0.7f);

                    var yOffset = Math.Max(gridBounds.Height, gridBounds.Width) * MinimapScale / 1.8f;

                    var gridScaledPosition = gridCentre - new Vector2(0, -yOffset);

                    var gridOffset = gridScaledPosition / PixelSize - new Vector2(0.5f, 0.5f);
                    var offsetMax = Math.Max(Math.Abs(gridOffset.X), Math.Abs(gridOffset.Y)) * 2f;
                    if (offsetMax > 1)
                    {
                        gridOffset = new Vector2(gridOffset.X / offsetMax, gridOffset.Y / offsetMax);
                        gridScaledPosition = (gridOffset + new Vector2(0.5f, 0.5f)) * PixelSize;
                    }

                    var labelUiPosition = gridScaledPosition - new Vector2(labelDimensions.X / 2f, 0);
                    var coordUiPosition = gridScaledPosition - new Vector2(coordsDimensions.X / 2f, -labelDimensions.Y);

                    var controlExtents = PixelSize - new Vector2(labelDimensions.X, labelDimensions.Y);
                    labelUiPosition = Vector2.Clamp(labelUiPosition, Vector2.Zero, controlExtents);

                    handle.DrawString(Font, labelUiPosition, labelText, labelColor);

                    if (offsetMax < 1)
                    {
                        handle.DrawString(Font, coordUiPosition, coordsText, 0.7f, coordColor);
                    }
                }
            }
        }

        #region Mono

        foreach (var blipData in _blips.GetCurrentBlips())
        {
            var mapPosition = _transform.ToMapCoordinates(blipData.Position).Position;
            var viewPosition = Vector2.Transform(mapPosition, worldToView);
            var config = blipData.Config;
            var shape = config.Shape;
            var color = config.Color;
            var scale = (config.Bounds.Width + config.Bounds.Height) / 6f;

            if (shape == RadarBlipShape.Ring)
            {
                DrawShieldRing(handle, viewPosition, scale, color.WithAlpha(0.8f));
            }
            else
            {
                DrawBlipShape(handle, viewPosition, scale * 3f, color.WithAlpha(0.8f), shape);
            }

            if (_isMouseInside && _controllables != null)
            {
                var worldPosition = mapPosition;

                // Find the selected weapon (turret) whose position matches this blip.
                NetEntity? matchedWeapon = null;
                foreach (var c in _controllables)
                {
                    if (!_selectedWeapons.Contains(c.NetEntity))
                        continue;

                    var entityMapPos = _transform.ToMapCoordinates(EntManager.GetCoordinates(c.Coordinates));
                    if (Vector2.Distance(entityMapPos.Position, worldPosition) < 0.1f)
                    {
                        matchedWeapon = c.NetEntity;
                        break;
                    }
                }

                if (matchedWeapon is { } weaponNet)
                {
                    var cursorViewPos = InverseScalePosition(_lastMousePos);
                    cursorViewPos = ScalePosition(cursorViewPos);

                    var cursorWorldPos = Vector2.Transform(cursorViewPos, viewToWorld);

                    var toCursor = cursorWorldPos - worldPosition;
                    var distance = toCursor.Length();

                    if (distance > 0.01f)
                    {
                        var dir = toCursor / distance;
                        var perp = new Vector2(-dir.Y, dir.X);

                        // Ignore only the firing turret (it doesn't collide with its own shell); own
                        // walls still block. Cast the corridor and draw the line only if all rays are clear.
                        var turret = EntManager.GetEntity(weaponNet);

                        var clear = true;
                        foreach (var offset in TargetingCorridorOffsets)
                        {
                            var origin = worldPosition + perp * offset;
                            var ray = new CollisionRay(origin, dir, (int)CollisionGroup.Impassable);
                            if (_physics.IntersectRay(xform.MapID, ray, distance, ignoredEnt: turret).Any())
                            {
                                clear = false;
                                break;
                            }
                        }

                        if (clear)
                            handle.DrawLine(viewPosition, cursorViewPos, color.WithAlpha(0.3f));
                    }
                }
            }
        }

        // Draw hitscan lines from the radar blips system
        var hitscanLines = _blips.GetRawHitscanLines();
        foreach (var line in hitscanLines)
        {
            Vector2 startPosInView;
            Vector2 endPosInView;

            // Handle differently based on if there's a grid
            if (line.Grid == null)
            {
                // For world-space lines without a grid, use standard world transformation
                startPosInView = Vector2.Transform(line.Start, worldToShuttle * shuttleToView);
                endPosInView = Vector2.Transform(line.End, worldToShuttle * shuttleToView);
            }
            else
            {
                // For grid-relative lines, we need to transform from grid space to world space first
                var gridEntity = EntManager.GetEntity(line.Grid.Value);
                if (EntManager.TryGetComponent<TransformComponent>(gridEntity, out var gridXform))
                {
                    var gridToWorld = _transform.GetWorldMatrix(gridEntity);
                    var gridStartWorld = Vector2.Transform(line.Start, gridToWorld);
                    var gridEndWorld = Vector2.Transform(line.End, gridToWorld);

                    startPosInView = Vector2.Transform(gridStartWorld, worldToShuttle * shuttleToView);
                    endPosInView = Vector2.Transform(gridEndWorld, worldToShuttle * shuttleToView);
                }
                else
                {
                    // Fallback to treating as world coordinates if grid transform is not available
                    startPosInView = Vector2.Transform(line.Start, worldToShuttle * shuttleToView);
                    endPosInView = Vector2.Transform(line.End, worldToShuttle * shuttleToView);
                }
            }

            // Check if the line is within the view bounds before drawing
            var viewBounds = new Box2(-3f, -3f, Size.X + 3f, Size.Y + 3f);
            var lineBounds = new Box2(
                Math.Min(startPosInView.X, endPosInView.X),
                Math.Min(startPosInView.Y, endPosInView.Y),
                Math.Max(startPosInView.X, endPosInView.X),
                Math.Max(startPosInView.Y, endPosInView.Y)
            );

            if (viewBounds.Intersects(lineBounds))
            {
                handle.DrawLine(startPosInView, endPosInView, line.Color.WithAlpha(0.8f));
            }
        }

        ClearShader(handle);
        #endregion
    }

    private void ClearShader(DrawingHandleScreen handle)
    {
        // No-op placeholder to maintain compatibility with previous shader clearing behavior.
    }

    private void DrawShields(DrawingHandleScreen handle, TransformComponent consoleXform, Matrix3x2 worldToShuttle)
    {
        var shields = EntManager.AllEntityQueryEnumerator<ShipShieldVisualsComponent, FixturesComponent, TransformComponent>();
        while (shields.MoveNext(out _, out var visuals, out var fixtures, out var xform))
        {
            if (xform.GridUid == null || xform.MapID != consoleXform.MapID)
                continue;

            if (EntManager.HasComponent<FTLComponent>(xform.GridUid.Value))
                continue;

            if (!fixtures.Fixtures.TryGetValue("shield", out var fixture)
                && !fixtures.Fixtures.TryGetValue("internalShield", out fixture))
                continue;

            var center = xform.LocalPosition;
            var parentWorldMatrix = _transform.GetWorldMatrix(xform.GridUid.Value);

            var count = 0;
            Vector2[] vertices;

            switch (fixture.Shape)
            {
                case ChainShape chain:
                    count = chain.Count;
                    vertices = chain.Vertices;
                    break;
                case PolygonShape poly:
                    count = poly.VertexCount + 1;
                    vertices = new Vector2[count];
                    for (var i = 0; i < poly.VertexCount; i++)
                    {
                        vertices[i] = poly.Vertices[i];
                    }

                    vertices[count - 1] = poly.Vertices[0];
                    break;
                default:
                    continue;
            }

            if (count < 2)
                continue;

            for (var i = 1; i < count; i++)
            {
                var v1 = Vector2.Add(center, vertices[i - 1]);
                v1 = Vector2.Transform(v1, parentWorldMatrix);
                v1 = Vector2.Transform(v1, worldToShuttle);
                v1.Y = -v1.Y;
                v1 = ScalePosition(v1);

                var v2 = Vector2.Add(center, vertices[i]);
                v2 = Vector2.Transform(v2, parentWorldMatrix);
                v2 = Vector2.Transform(v2, worldToShuttle);
                v2.Y = -v2.Y;
                v2 = ScalePosition(v2);

                handle.DrawLine(v1, v2, visuals.ShieldColor);
            }
        }
    }

    private void DrawShieldRing(DrawingHandleScreen handle, Vector2 position, float radius, Color color)
    {
        // Draw a ring with consistent thickness
        handle.DrawCircle(position, radius, color, false);
    }

    public void UpdateControllables(EntityUid console, FireControllableEntry[] controllables)
    {
        _activeConsole = console;
        _controllables = controllables;
    }

    public void UpdateSelectedWeapons(HashSet<NetEntity> selectedWeapons)
    {
        _selectedWeapons = selectedWeapons;
    }

    private Vector2 InverseScalePosition(Vector2 value)
    {
        var scaledValue = value * UIScale;
        return (scaledValue - MidPointVector) / MinimapScale;
    }

    // Mono
    private EntityCoordinates GetMouseEntityCoordinates(Vector2 relativePosition)
    {
        if (_coordinates is not { } cord || _rotation is not { } rot)
            return new();

        // HardLight: convert virtual UI pixels to physical pixels (InverseMapPosition works in those)
        // so aiming stays correct at non-100% UI scale.
        var physicalPosition = relativePosition * UIScale;

        var screenRelativeWorldPos = InverseMapPosition(physicalPosition);
        var relativeWorldPos = rot.RotateVec(screenRelativeWorldPos);
        var coordEntRot = _transform.GetWorldRotation(cord.EntityId);
        var coords = cord.Offset((-coordEntRot).RotateVec(relativeWorldPos));

        return coords;
    }

    private void DrawBlipShape(DrawingHandleScreen handle, Vector2 position, float size, Color color, RadarBlipShape shape)
    {
        switch (shape)
        {
            case RadarBlipShape.Circle:
                handle.DrawCircle(position, size, color);
                break;
            case RadarBlipShape.Square:
                var halfSize = size / 2;
                var rect = new UIBox2(
                    position.X - halfSize,
                    position.Y - halfSize,
                    position.X + halfSize,
                    position.Y + halfSize
                );
                handle.DrawRect(rect, color);
                break;
            case RadarBlipShape.Triangle:
                var points = new Vector2[]
                {
                    position + new Vector2(0, -size),
                    position + new Vector2(-size * 0.866f, size * 0.5f),
                    position + new Vector2(size * 0.866f, size * 0.5f)
                };
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, points, color);
                break;
            case RadarBlipShape.Star:
                DrawStar(handle, position, size, color);
                break;
            case RadarBlipShape.Diamond:
                var diamondPoints = new Vector2[]
                {
                    position + new Vector2(0, -size),
                    position + new Vector2(size, 0),
                    position + new Vector2(0, size),
                    position + new Vector2(-size, 0)
                };
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, diamondPoints, color);
                break;
            case RadarBlipShape.Hexagon:
                DrawHexagon(handle, position, size, color);
                break;
            case RadarBlipShape.Arrow:
                DrawArrow(handle, position, size, color);
                break;
            // Ring shapes are handled by DrawShieldRing for constant thickness
        }
    }

    private void DrawStar(DrawingHandleScreen handle, Vector2 position, float size, Color color)
    {
        var outerRadius = size;
        var innerRadius = size * 0.4f;
        var points = new List<Vector2>();

        for (var i = 0; i < 10; i++)
        {
            var angle = i * MathF.PI / 5;
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            points.Add(position + new Vector2(
                radius * MathF.Sin(angle),
                -radius * MathF.Cos(angle)
            ));
        }

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, points.ToArray(), color);
    }

    private void DrawHexagon(DrawingHandleScreen handle, Vector2 position, float size, Color color)
    {
        var points = new List<Vector2>();

        for (var i = 0; i < 6; i++)
        {
            var angle = i * MathF.PI / 3;
            points.Add(position + new Vector2(
                size * MathF.Cos(angle),
                size * MathF.Sin(angle)
            ));
        }

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, points.ToArray(), color);
    }

    private void DrawArrow(DrawingHandleScreen handle, Vector2 position, float size, Color color)
    {
        var points = new Vector2[]
        {
            position + new Vector2(0, -size),
            position + new Vector2(size * 0.5f, 0),
            position + new Vector2(0, size),
            position + new Vector2(-size * 0.5f, 0)
        };

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, points, color);
    }

    private void DrawSafeZoneRing(DrawingHandleScreen handle)
    {
        const float SafeZoneRadius = 5000f;
        var safeZoneColor = Color.LimeGreen.WithAlpha(0.8f);
        
        // Calculate the center position
        var centerPos = ScalePosition(Vector2.Zero);
        
        // Scale the radius according to the minimap scale
        var scaledRadius = SafeZoneRadius * MinimapScale;
        
        // Draw the ring
        handle.DrawCircle(centerPos, scaledRadius, safeZoneColor, filled: false);
    }

    private void TryUpdateCursorPosition(Vector2 relativePosition)
    {
        var currentTime = IoCManager.Resolve<IGameTiming>().CurTime.TotalSeconds;
        if (currentTime - _lastCursorUpdateTime < CursorUpdateInterval)
            return;

        _lastCursorUpdateTime = (float)currentTime;

        var coords = GetMouseEntityCoordinates(relativePosition);
        // This will update the server of our cursor position without triggering actual firing
        OnRadarClick?.Invoke(coords);
    }

    /// <summary>
    /// Returns true if the mouse button is currently pressed down
    /// </summary>
    public bool IsMouseDown() => _isMouseDown;
}
