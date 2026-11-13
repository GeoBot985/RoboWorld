using Godot;
using RoboWorld.Domain;

/// <summary>Godot projection and pointer tools for the canonical engine-independent authoring draft.</summary>
public partial class RobotAuthoringViewport : Node3D
{
    private const uint PickLayer = 1u << 19;
    private const float SnapPixels = 18f;
    private readonly IReadOnlyDictionary<string, ComponentDefinition> _catalogue;
    private readonly Dictionary<string, Node3D> _partNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeshInstance3D> _geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, MeshInstance3D>> _markers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _baseColors = new(StringComparer.Ordinal);
    private Node3D _parts = null!;
    private Node3D _cameraPivot = null!;
    private Camera3D _camera = null!;
    private RobotAuthoringDraft _draft;
    private RobotAuthoringDraft? _dragStartDraft;
    private RobotAuthoringDraft? _freePreviewDraft;
    private RobotAuthoringDraft? _previewDraft;
    private string? _selectedId;
    private string? _dragId;
    private string? _snapMarkerKey;
    private Vector2 _dragStartPointer;
    private Vector3 _dragStartWorldPosition;
    private Quaternion _dragStartWorldRotation = Quaternion.Identity;
    private float _dragPlaneY;
    private bool _rotateMode;
    private bool _snapEnabled = true;
    private string _jointType = "revolute";
    private string? _cameraGesture;
    private Vector2 _lastPointer;

    public RobotAuthoringViewport(IReadOnlyDictionary<string, ComponentDefinition> catalogue, RobotAuthoringDraft draft)
    {
        _catalogue = catalogue;
        _draft = draft;
    }

    public event Action<RobotAuthoringDraft>? DraftChanged;
    public event Action<string?>? SelectionChanged;
    public event Action<string>? Feedback;
    public RobotAuthoringDraft Draft => _draft;
    public string? SelectedId => _selectedId;
    public bool SnapEnabled { get => _snapEnabled; set => _snapEnabled = value; }
    public string JointType { get => _jointType; set => _jointType = value is "fixed" or "revolute" ? value : "fixed"; }

    public void ApplySelectedWorldPose(Vector3 position, Vector3 rotationDegrees)
    {
        if (_selectedId is null || _draft.Assembly is null) throw new InvalidDataException("Select a component before editing its transform.");
        var rotation = Quaternion.FromEuler(rotationDegrees * Mathf.Pi / 180f);
        _draft = SetWorldPose(_draft, _selectedId, position, rotation, IsRootBase(_draft.Assembly, _selectedId));
        UpdateProjection(_draft);
        DraftChanged?.Invoke(_draft);
    }

    public (Vector3 Position, Vector3 RotationDegrees)? GetSelectedWorldPose()
    {
        if (_selectedId is null || _draft.Assembly is null) return null;
        var component = _draft.Assembly.Components.Single(item => item.Id == _selectedId);
        var link = _draft.Assembly.Links.Single(item => item.Id == component.LinkId);
        var position = ToVector(link.PositionM) + ToQuaternion(link.RotationXyzw) * ToVector(component.PositionM);
        var rotation = (ToQuaternion(link.RotationXyzw) * ToQuaternion(component.RotationXyzw)).Normalized();
        return (position, rotation.GetEuler() * (180f / Mathf.Pi));
    }

    public override void _Ready()
    {
        _parts = new Node3D { Name = "AuthoringParts" };
        AddChild(_parts);
        _cameraPivot = new Node3D { Name = "AuthoringCameraPivot", Position = new Vector3(0, 0.28f, 0), Rotation = new Vector3(0, 0.28f, 0) };
        AddChild(_cameraPivot);
        _camera = new Camera3D { Name = "AuthoringCamera", Position = new Vector3(0, 1.5f, 4.6f), RotationDegrees = new Vector3(-15, 0, 0), Current = true, Fov = 58 };
        _cameraPivot.AddChild(_camera);
        UpdateProjection(_draft);
    }

    public void SetDraft(RobotAuthoringDraft draft, string? selectedId = null)
    {
        _draft = draft;
        _selectedId = selectedId is not null && draft.Assembly?.Components.Any(component => component.Id == selectedId) == true
            ? selectedId : draft.Assembly?.Components.LastOrDefault()?.Id;
        UpdateProjection(_draft);
        SelectionChanged?.Invoke(_selectedId);
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (input is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.Escape && _dragStartDraft is not null) { CancelGesture(); GetViewport().SetInputAsHandled(); return; }
            if (key.Keycode == Key.R && _dragStartDraft is null && _selectedId is not null)
            {
                _rotateMode = !_rotateMode;
                Feedback?.Invoke(_rotateMode ? "Rotate mode · drag left/right; press R again for Move." : "Move mode · drag on the ground plane; press R for Rotate.");
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (input is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Right || mouseButton.ButtonIndex == MouseButton.Middle)
            {
                if (mouseButton.Pressed) { _cameraGesture = mouseButton.ButtonIndex == MouseButton.Right ? "orbit" : "pan"; _lastPointer = mouseButton.Position; }
                else _cameraGesture = null;
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed) { Zoom(0.88f); GetViewport().SetInputAsHandled(); return; }
            if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed) { Zoom(1.14f); GetViewport().SetInputAsHandled(); return; }
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed) BeginPointerAction(mouseButton.Position);
                else if (_dragStartDraft is not null) EndPointerAction();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (input is InputEventMouseMotion motion)
        {
            if (_cameraGesture is not null) { NavigateCamera(motion.Position); GetViewport().SetInputAsHandled(); return; }
            if (_dragStartDraft is not null) { UpdatePointerAction(motion.Position); GetViewport().SetInputAsHandled(); }
        }
    }

    private void BeginPointerAction(Vector2 pointer)
    {
        var hitId = PickComponent(pointer);
        Select(hitId);
        if (hitId is null || _draft.Assembly is null) return;
        var component = _draft.Assembly.Components.Single(item => item.Id == hitId);
        var link = _draft.Assembly.Links.Single(item => item.Id == component.LinkId);
        var worldPosition = ToVector(link.PositionM) + ToQuaternion(link.RotationXyzw) * ToVector(component.PositionM);
        var worldRotation = (ToQuaternion(link.RotationXyzw) * ToQuaternion(component.RotationXyzw)).Normalized();
        var isRootBase = component.LinkId == _draft.Assembly.RootLinkId && component.Id == link.BaseComponentId;
        if (!isRootBase && (_draft.Assembly.Joints.Any(joint => joint.ParentComponentId == hitId || joint.ChildComponentId == hitId)
            || (_draft.Assembly.ComponentMountConnections ?? []).Any(connection => connection.ParentComponentId == hitId || connection.ChildComponentId == hitId)))
        {
            Feedback?.Invoke($"{hitId} is mechanically connected. Drag the chassis to move the whole assembly or remove the component.");
            return;
        }
        _dragId = hitId;
        _dragStartDraft = _draft;
        _freePreviewDraft = _draft;
        _previewDraft = _draft;
        _dragStartPointer = pointer;
        _dragStartWorldPosition = worldPosition;
        _dragStartWorldRotation = worldRotation;
        _dragPlaneY = worldPosition.Y;
        _snapMarkerKey = null;
        Feedback?.Invoke(_rotateMode ? "Rotating selected component · Esc cancels." : "Moving selected component · release to place, Esc cancels.");
    }

    private void UpdatePointerAction(Vector2 pointer)
    {
        if (_dragStartDraft?.Assembly is null || _dragId is null) return;
        var rootBase = IsRootBase(_dragStartDraft.Assembly, _dragId);
        RobotAuthoringDraft freeDraft;
        try
        {
            var position = _dragStartWorldPosition;
            var rotation = _dragStartWorldRotation;
            if (_rotateMode)
            {
                var angle = (pointer.X - _dragStartPointer.X) * 0.01f;
                rotation = (new Quaternion(Vector3.Up, angle) * _dragStartWorldRotation).Normalized();
            }
            else if (IntersectGroundPlane(pointer, _dragPlaneY) is Vector3 point)
                position = new Vector3(point.X, _dragStartWorldPosition.Y, point.Z);
            freeDraft = SetWorldPose(_dragStartDraft, _dragId, position, rotation, rootBase);
        }
        catch (Exception exception) { Feedback?.Invoke($"Move preview rejected: {exception.Message}"); return; }

        _freePreviewDraft = freeDraft;
        _previewDraft = freeDraft;
        _snapMarkerKey = null;
        if (!_rotateMode && _snapEnabled && TryFindSnapTarget(freeDraft, _dragId, pointer, out var parentId, out var parentPort, out var childPort))
        {
            try
            {
                _previewDraft = freeDraft.AttachExistingComponent(_catalogue, parentId, parentPort, _dragId, childPort, _jointType,
                    null);
                _snapMarkerKey = parentId + "." + parentPort;
                Feedback?.Invoke($"Snap preview · {_dragId}.{childPort} → {parentId}.{parentPort} ({_jointType}). Release to connect; Esc cancels.");
            }
            catch (Exception exception) { Feedback?.Invoke($"Snap unavailable: {exception.Message}"); }
        }
        UpdateProjection(_previewDraft);
    }

    private void EndPointerAction()
    {
        if (_dragStartDraft is null || _dragId is null) return;
        var changed = _previewDraft ?? _freePreviewDraft ?? _dragStartDraft;
        var attached = changed.Assembly?.Joints.Count > _dragStartDraft.Assembly?.Joints.Count;
        _dragStartDraft = null;
        _freePreviewDraft = null;
        _previewDraft = null;
        _draft = changed;
        var selected = _dragId;
        _dragId = null;
        UpdateProjection(_draft);
        DraftChanged?.Invoke(_draft);
        Feedback?.Invoke(attached == true ? $"Attached {selected} with a {_jointType} joint." : $"Placed {selected}.");
    }

    private void CancelGesture()
    {
        if (_dragStartDraft is null) return;
        _draft = _dragStartDraft;
        _dragStartDraft = null;
        _freePreviewDraft = null;
        _previewDraft = null;
        _dragId = null;
        _snapMarkerKey = null;
        UpdateProjection(_draft);
        Feedback?.Invoke("Placement canceled; the saved assembly was unchanged.");
    }

    private void Select(string? componentId)
    {
        _selectedId = componentId;
        RefreshSelectionVisuals();
        SelectionChanged?.Invoke(componentId);
    }

    private string? PickComponent(Vector2 pointer)
    {
        var origin = _camera.ProjectRayOrigin(pointer);
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + _camera.ProjectRayNormal(pointer) * 100f);
        query.CollisionMask = PickLayer;
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (!result.TryGetValue("collider", out var collider)) return null;
        var area = collider.AsGodotObject() as Area3D;
        return area?.HasMeta("component_id") == true ? area.GetMeta("component_id").AsString() : null;
    }

    private bool TryFindSnapTarget(RobotAuthoringDraft draft, string childId, Vector2 pointer,
        out string parentId, out string parentPortId, out string childPortId)
    {
        parentId = parentPortId = childPortId = "";
        var graph = draft.Assembly!;
        var child = graph.Components.Single(component => component.Id == childId);
        var childDefinition = _catalogue[child.CatalogueId];
        var bestDistance = SnapPixels;
        foreach (var parent in graph.Components.Where(component => component.Id != childId))
        {
            var parentDefinition = _catalogue[parent.CatalogueId];
            foreach (var parentPort in parentDefinition.MountPorts)
            {
                if (!RobotAssemblyEditor.IsMountPortAvailable(graph, parent.Id, parentPort.Id)) continue;
                foreach (var childPort in childDefinition.MountPorts)
                {
                    if (!RobotGraphValidator.CanMate(parentPort, childPort)) continue;
                    var worldPort = GetPortWorldPosition(graph, parent, parentDefinition, parentPort);
                    if (_camera.IsPositionBehind(worldPort)) continue;
                    var distance = _camera.UnprojectPosition(worldPort).DistanceTo(pointer);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        parentId = parent.Id; parentPortId = parentPort.Id; childPortId = childPort.Id;
                    }
                }
            }
        }
        return parentId.Length > 0;
    }

    private void UpdateProjection(RobotAuthoringDraft draft)
    {
        foreach (var child in _parts.GetChildren()) { _parts.RemoveChild(child); child.QueueFree(); }
        _partNodes.Clear(); _geometry.Clear(); _markers.Clear(); _baseColors.Clear();
        if (draft.Assembly is null) { RefreshSelectionVisuals(); return; }
        var links = draft.Assembly.Links.ToDictionary(link => link.Id, StringComparer.Ordinal);
        foreach (var component in draft.Assembly.Components)
        {
            var definition = _catalogue[component.CatalogueId];
            var link = links[component.LinkId];
            var node = new Node3D { Name = "Part_" + component.Id };
            node.Position = ToVector(link.PositionM) + ToQuaternion(link.RotationXyzw) * ToVector(component.PositionM);
            node.Quaternion = (ToQuaternion(link.RotationXyzw) * ToQuaternion(component.RotationXyzw)).Normalized();
            var size = component.SizeOverrideM ?? definition.SizeM;
            var mesh = MakeMesh(definition, size);
            node.AddChild(mesh);
            var color = ComponentColor(definition);
            _baseColors[component.Id] = color;
            _geometry[component.Id] = mesh;
            _partNodes[component.Id] = node;
            var area = new Area3D { Name = "Pick_" + component.Id, CollisionLayer = PickLayer, CollisionMask = 0 };
            area.SetMeta("component_id", component.Id);
            area.AddChild(new CollisionShape3D { Shape = MakeShape(definition, size) });
            node.AddChild(area);
            var portMarkers = new Dictionary<string, MeshInstance3D>(StringComparer.Ordinal);
            foreach (var port in definition.MountPorts)
            {
                var frame = RobotGraphValidator.GetComponentMountFrame(component, definition, port);
                var marker = new MeshInstance3D
                {
                    Name = "Connector_" + port.Id,
                    Mesh = new SphereMesh { Radius = 0.013f, Height = 0.026f },
                    Position = ToVector(frame.PositionM),
                    Quaternion = ToQuaternion(frame.RotationXyzw),
                    MaterialOverride = ConnectorMaterial(new Color(0.73f, 0.81f, 0.88f))
                };
                node.AddChild(marker);
                portMarkers.Add(port.Id, marker);
            }
            _markers.Add(component.Id, portMarkers);
            _parts.AddChild(node);
        }
        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var (id, mesh) in _geometry)
        {
            var selected = id == _selectedId;
            var material = new StandardMaterial3D { AlbedoColor = selected ? new Color(0.16f, 0.95f, 0.79f) : _baseColors[id], ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel };
            mesh.MaterialOverride = material;
            foreach (var (portId, marker) in _markers[id])
            {
                var visible = selected || IsCompatibleTarget(id, portId);
                marker.Visible = visible;
                marker.MaterialOverride = ConnectorMaterial(id + "." + portId == _snapMarkerKey
                    ? new Color(0.18f, 0.98f, 0.46f) : visible && id != _selectedId ? new Color(0.25f, 0.72f, 0.92f) : new Color(0.73f, 0.81f, 0.88f));
            }
        }
    }

    private bool IsCompatibleTarget(string componentId, string portId)
    {
        if (_selectedId is null || _draft.Assembly is null || _selectedId == componentId) return false;
        var target = _draft.Assembly.Components.SingleOrDefault(component => component.Id == componentId);
        var selected = _draft.Assembly.Components.SingleOrDefault(component => component.Id == _selectedId);
        if (target is null || selected is null || !RobotAssemblyEditor.IsMountPortAvailable(_draft.Assembly, componentId, portId)) return false;
        var parentPort = _catalogue[target.CatalogueId].MountPorts.Single(port => port.Id == portId);
        return _catalogue[selected.CatalogueId].MountPorts.Any(childPort => RobotGraphValidator.CanMate(parentPort, childPort));
    }

    private void NavigateCamera(Vector2 pointer)
    {
        var delta = pointer - _lastPointer;
        _lastPointer = pointer;
        if (_cameraGesture == "orbit")
        {
            _cameraPivot.RotateY(-delta.X * 0.006f);
            _camera.RotationDegrees = new Vector3(Mathf.Clamp(_camera.RotationDegrees.X - delta.Y * 0.35f, -75, -5), 0, 0);
        }
        else
        {
            var scale = _camera.Position.Z * 0.0015f;
            _cameraPivot.Position += (-_camera.GlobalBasis.X * delta.X + _camera.GlobalBasis.Y * delta.Y) * scale;
        }
    }

    private void Zoom(float scale)
    {
        _camera.Position = new Vector3(_camera.Position.X, _camera.Position.Y, Mathf.Clamp(_camera.Position.Z * scale, 2.2f, 12f));
    }

    private Vector3? IntersectGroundPlane(Vector2 pointer, float y)
    {
        var plane = new Plane(Vector3.Up, y);
        return plane.IntersectsRay(_camera.ProjectRayOrigin(pointer), _camera.ProjectRayNormal(pointer));
    }

    private RobotAuthoringDraft SetWorldPose(RobotAuthoringDraft draft, string componentId, Vector3 worldPosition, Quaternion worldRotation, bool rootBase)
    {
        if (draft.Assembly is null) throw new InvalidDataException("The empty draft has no component to move.");
        if (rootBase) return draft.SetRootPose(_catalogue, ToArray(worldPosition), ToArray(worldRotation));
        var component = draft.Assembly.Components.Single(item => item.Id == componentId);
        var link = draft.Assembly.Links.Single(item => item.Id == component.LinkId);
        var linkRotation = ToQuaternion(link.RotationXyzw);
        var localPosition = linkRotation.Inverse() * (worldPosition - ToVector(link.PositionM));
        var localRotation = (linkRotation.Inverse() * worldRotation).Normalized();
        return draft.SetComponentPose(_catalogue, componentId, ToArray(localPosition), ToArray(localRotation));
    }

    private bool IsRootBase(AssemblyGraph graph, string componentId)
    {
        var component = graph.Components.Single(item => item.Id == componentId);
        return component.LinkId == graph.RootLinkId && graph.Links.Single(link => link.Id == graph.RootLinkId).BaseComponentId == componentId;
    }

    private Vector3 GetPortWorldPosition(AssemblyGraph graph, ComponentInstance component, ComponentDefinition definition, MountPortDefinition port)
    {
        var link = graph.Links.Single(item => item.Id == component.LinkId);
        var linkRotation = ToQuaternion(link.RotationXyzw);
        var frame = RobotGraphValidator.GetComponentMountFrame(component, definition, port);
        return ToVector(link.PositionM) + linkRotation * (ToVector(component.PositionM)
            + ToQuaternion(component.RotationXyzw) * ToVector(frame.PositionM));
    }

    private static MeshInstance3D MakeMesh(ComponentDefinition definition, double[] size)
    {
        var color = ComponentColor(definition);
        Mesh mesh = definition.Primitive switch
        {
            "sphere" => new SphereMesh { Radius = (float)(size[0] / 2), Height = (float)size[0] },
            "cylinder" => new CylinderMesh { TopRadius = (float)(size[0] / 2), BottomRadius = (float)(size[0] / 2), Height = (float)size[1] },
            _ => new BoxMesh { Size = ToVector(size) }
        };
        if (definition.Model == "wheel")
        {
            var radius = (float)(definition.RadiusM ?? size[2] / 2);
            mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = (float)size[1] };
        }
        var visual = new MeshInstance3D { Name = "Geometry", Mesh = mesh, MaterialOverride = ConnectorMaterial(color) };
        if (definition.Model == "wheel") visual.RotationDegrees = new Vector3(0, 0, 90);
        return visual;
    }

    private static Shape3D MakeShape(ComponentDefinition definition, double[] size)
    {
        if (definition.Model == "wheel") return new CylinderShape3D { Radius = (float)(definition.RadiusM ?? size[2] / 2), Height = (float)size[1] };
        return definition.Primitive switch
        {
            "sphere" => new SphereShape3D { Radius = (float)(size[0] / 2) },
            "cylinder" => new CylinderShape3D { Radius = (float)(size[0] / 2), Height = (float)size[1] },
            _ => new BoxShape3D { Size = ToVector(size) }
        };
    }

    private static Color ComponentColor(ComponentDefinition definition) => definition.Id switch
    {
        "rw.primitive.box" => new Color(0.81f, 0.56f, 0.22f),
        "rw.primitive.cylinder" => new Color(0.18f, 0.24f, 0.29f),
        "rw.primitive.sphere" => new Color(0.2f, 0.64f, 0.68f),
        _ => definition.Category switch { "mobility" => new Color(0.18f, 0.24f, 0.29f), "actuator" => new Color(0.78f, 0.45f, 0.16f), "electronics" => new Color(0.22f, 0.48f, 0.82f), "sensor" => new Color(0.46f, 0.78f, 0.93f), _ => new Color(0.72f, 0.67f, 0.3f) }
    };

    private static StandardMaterial3D ConnectorMaterial(Color color) => new() { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel };
    private static Vector3 ToVector(double[] values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Quaternion ToQuaternion(double[] values) => new Quaternion((float)values[0], (float)values[1], (float)values[2], (float)values[3]).Normalized();
    private static double[] ToArray(Vector3 value) => [value.X, value.Y, value.Z];
    private static double[] ToArray(Quaternion value) => [value.X, value.Y, value.Z, value.W];
}
