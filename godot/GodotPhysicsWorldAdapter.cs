using Godot;
using RoboWorld.Domain;

/// <summary>Maps canonical component IDs to Godot rigid bodies. Godot owns stepping through fixed physics callbacks.</summary>
public sealed class GodotPhysicsWorldAdapter : IPhysicsWorldAdapter
{
    private readonly Dictionary<string, RigidBody3D> _bodies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MotorBinding> _motors = new(StringComparer.Ordinal);

    private sealed record MotorBinding(RigidBody3D Parent, RigidBody3D Child, Vector3 AxisLocal, double StallTorqueNm, double NoLoadRpm, double SignedDuty);

    public void BindBody(string componentId, RigidBody3D body)
    {
        if (!_bodies.TryAdd(componentId, body)) throw new InvalidOperationException($"Body already bound for {componentId}.");
    }

    public void RegisterBody(string componentId, double massKg)
    {
        if (!_bodies.TryGetValue(componentId, out var body)) throw new KeyNotFoundException($"No physics body is bound for {componentId}.");
        if (!double.IsFinite(massKg) || massKg <= 0) throw new ArgumentOutOfRangeException(nameof(massKg));
        body.Mass = (float)massKg;
    }

    public void UnbindBody(string componentId)
    {
        _bodies.Remove(componentId, out var body);
        _motors.Remove(componentId);
        if (body is null) return;
        foreach (var actuatorId in _motors.Where(pair => pair.Value.Parent == body || pair.Value.Child == body)
            .Select(pair => pair.Key).ToArray())
            _motors.Remove(actuatorId);
    }

    public void ApplyActuatorForces(IReadOnlyList<ActuatorForce> forces, long simTimeNs)
    {
        foreach (var force in forces)
        {
            if (!_bodies.TryGetValue(force.ComponentId, out var body)) throw new KeyNotFoundException($"Actuator maps to unknown physics body {force.ComponentId} at {simTimeNs} ns.");
            if (force.ForceN.Length != 3 || force.ApplicationPointM.Length != 3 || force.ForceN.Any(value => !double.IsFinite(value)))
                throw new ArgumentException($"Invalid actuator force for {force.ComponentId}.", nameof(forces));
            body.Freeze = false;
            body.ApplyForce(ToVector(force.ForceN), ToVector(force.ApplicationPointM));
        }
    }

    public void RegisterMotor(string actuatorId, RigidBody3D parent, RigidBody3D child, Vector3 axisLocal, ComponentDefinition profile)
    {
        if (profile.Model != "dc_motor" || profile.StallTorqueNm is null or <= 0 || profile.NoLoadRpm is null or <= 0)
            throw new InvalidDataException($"Motor {actuatorId} lacks a valid versioned DC motor profile.");
        if (!float.IsFinite(axisLocal.X) || !float.IsFinite(axisLocal.Y) || !float.IsFinite(axisLocal.Z) || axisLocal.LengthSquared() < 1e-8f)
            throw new InvalidDataException($"Motor {actuatorId} has an invalid joint axis.");
        if (!_motors.TryAdd(actuatorId, new MotorBinding(parent, child, axisLocal.Normalized(), profile.StallTorqueNm.Value, profile.NoLoadRpm.Value, 0)))
            throw new InvalidOperationException($"Motor {actuatorId} is already bound.");
    }

    public void SetMotorDuty(string actuatorId, double signedDuty)
    {
        if (!double.IsFinite(signedDuty) || signedDuty is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(signedDuty));
        if (!_motors.TryGetValue(actuatorId, out var binding)) throw new KeyNotFoundException($"No motor is bound for actuator {actuatorId}.");
        _motors[actuatorId] = binding with { SignedDuty = signedDuty };
    }

    public bool IsMotorBound(string actuatorId) => _motors.ContainsKey(actuatorId);

    public void ApplyMotorTorques()
    {
        foreach (var binding in _motors.Values)
        {
            var axis = (binding.Parent.GlobalBasis * binding.AxisLocal).Normalized();
            var relativeSpeed = (binding.Child.AngularVelocity - binding.Parent.AngularVelocity).Dot(axis);
            var point = DcMotorModel.Evaluate(binding.SignedDuty, relativeSpeed, binding.StallTorqueNm, binding.NoLoadRpm);
            var torque = axis * (float)point.TorqueNm;
            binding.Child.ApplyTorque(torque);
            binding.Parent.ApplyTorque(-torque);
        }
    }

    public PhysicalSnapshot CaptureState(long physicsTick, long simTimeNs) => new(physicsTick, simTimeNs,
        _bodies.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var body = pair.Value;
            var q = body.GlobalBasis.GetRotationQuaternion();
            return new BodyState(pair.Key,
                [body.GlobalPosition.X, body.GlobalPosition.Y, body.GlobalPosition.Z],
                [q.X, q.Y, q.Z, q.W],
                [body.LinearVelocity.X, body.LinearVelocity.Y, body.LinearVelocity.Z],
                [body.AngularVelocity.X, body.AngularVelocity.Y, body.AngularVelocity.Z]);
        }).ToArray());

    public void RestoreState(PhysicalSnapshot snapshot)
    {
        if (snapshot.PhysicsTick < 0 || snapshot.SimTimeNs < 0) throw new InvalidDataException("Physical snapshot tick and simulation time must be nonnegative.");
        var states = snapshot.Bodies.ToDictionary(state => state.ComponentId, StringComparer.Ordinal);
        if (states.Count != _bodies.Count || _bodies.Keys.Any(id => !states.ContainsKey(id)))
            throw new InvalidDataException("Physical snapshot body IDs do not match the instantiated Godot physics bodies.");
        foreach (var (componentId, state) in states)
        {
            if (state.PositionM.Length != 3 || state.RotationXyzw.Length != 4 || state.LinearVelocityMps.Length != 3 || state.AngularVelocityRadps.Length != 3
                || state.PositionM.Concat(state.RotationXyzw).Concat(state.LinearVelocityMps).Concat(state.AngularVelocityRadps).Any(value => !double.IsFinite(value)))
                throw new InvalidDataException($"Physical snapshot contains invalid pose or velocity values for {componentId}.");
            var rotationLength = Math.Sqrt(state.RotationXyzw.Sum(value => value * value));
            if (Math.Abs(rotationLength - 1d) > 1e-6) throw new InvalidDataException($"Physical snapshot rotation for {componentId} must be a unit quaternion.");
        }

        foreach (var (componentId, state) in states)
        {
            var body = _bodies[componentId];
            body.GlobalTransform = new Transform3D(new Basis(ToQuaternion(state.RotationXyzw)), ToVector(state.PositionM));
            body.LinearVelocity = ToVector(state.LinearVelocityMps);
            body.AngularVelocity = ToVector(state.AngularVelocityRadps);
            body.Sleeping = false;
        }
    }

    private static Vector3 ToVector(IReadOnlyList<double> values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Quaternion ToQuaternion(IReadOnlyList<double> values) => new Quaternion((float)values[0], (float)values[1], (float)values[2], (float)values[3]).Normalized();
}
