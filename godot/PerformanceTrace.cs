using System.Diagnostics;
using System.Globalization;
using System.Text;

internal readonly record struct GodotTickPerformanceSample(
    ulong TickId, long SimTimeNs, long MonoTicks, long CallbackNs, double EnginePhysicsMs,
    long SensorRaycastNs, long SensorSerializationNs, long SensorTransportNs,
    long TickSerializationNs, long TickTransportNs, long TickAckWaitNs, long AckArrivalWaitNs, long AckDispatchDelayNs,
    long EventDrainNs, long TorqueNs, long SnapshotNs, long DiagnosticsNs,
    int HardwareQueueDepth, int HardwareQueueHighWatermark, int PhysicsBodyCount, double ChassisX, double ChassisZ,
    double ChassisSpeedMps, double LeftDuty, double RightDuty, int PhysicsCallbacksDuringAckWait,
    bool PhysicsCallbackCommit,
    double LeftWheelX, double LeftWheelY, double LeftWheelZ, double LeftWheelAngularX, double LeftWheelAngularY, double LeftWheelAngularZ,
    double RightWheelX, double RightWheelY, double RightWheelZ, double RightWheelAngularX, double RightWheelAngularY, double RightWheelAngularZ);

internal readonly record struct GodotPhysicsCallbackSample(
    long CallbackSequence, long MonoTicks, double DeltaSeconds, bool TransportPaused, bool AckPending,
    bool PhysicsStepGranted, bool UserPaused, bool ClockFaulted, ulong ClockTickIndex, long PhysicsTickCount,
    ulong PhysicsStateFingerprint);

internal readonly record struct GodotFramePerformanceSample(long MonoTicks, long FrameIntervalNs, long ProcessCallbackNs, double EngineProcessMs, double Fps, double ChassisX, double ChassisZ, int PhysicsCallbacksSincePreviousFrame);

internal sealed class GodotPerformanceTrace : IDisposable
{
    private readonly StreamWriter _ticks;
    private readonly StreamWriter _frames;
    private readonly StreamWriter _physicsCallbacks;
    private readonly StreamWriter _viewportUpdates;
    private readonly bool _flushTicksEachRecord;
    private int _ticksSinceFlush;

    public string DirectoryPath { get; }
    public string Scenario { get; }

    public GodotPerformanceTrace(string directoryPath, string scenario, bool flushTicksEachRecord = false)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
        Scenario = scenario;
        _flushTicksEachRecord = flushTicksEachRecord;
        Directory.CreateDirectory(DirectoryPath);
        _ticks = new StreamWriter(Path.Combine(DirectoryPath, "godot-ticks.csv"), false, new UTF8Encoding(false), 64 * 1024);
        _ticks.WriteLine("tick_id,sim_time_ns,mono_ticks,callback_ns,engine_physics_ms,sensor_raycast_ns,sensor_serialization_ns,sensor_transport_ns,tick_serialization_ns,tick_transport_ns,tick_ack_wait_ns,ack_arrival_wait_ns,ack_dispatch_delay_ns,event_drain_ns,torque_apply_ns,snapshot_ns,diagnostics_ns,hardware_queue_depth,hardware_queue_high_watermark,physics_body_count,physics_callbacks_during_ack_wait,physics_callback_commit,chassis_x_m,chassis_z_m,chassis_speed_mps,left_duty,right_duty,left_wheel_x_m,left_wheel_y_m,left_wheel_z_m,left_wheel_angular_x_radps,left_wheel_angular_y_radps,left_wheel_angular_z_radps,right_wheel_x_m,right_wheel_y_m,right_wheel_z_m,right_wheel_angular_x_radps,right_wheel_angular_y_radps,right_wheel_angular_z_radps");
        _frames = new StreamWriter(Path.Combine(DirectoryPath, "godot-frames.csv"), false, new UTF8Encoding(false), 32 * 1024);
        _frames.WriteLine("mono_ticks,frame_interval_ns,process_callback_ns,engine_process_ms,fps,chassis_x_m,chassis_z_m,physics_callbacks_since_previous_frame");
        _physicsCallbacks = new StreamWriter(Path.Combine(DirectoryPath, "godot-physics-callbacks.csv"), false, new UTF8Encoding(false), 32 * 1024);
        _physicsCallbacks.WriteLine("callback_sequence,mono_ticks,delta_seconds,transport_paused,ack_pending,physics_step_granted,user_paused,clock_faulted,clock_tick_index,physics_tick_count,physics_state_fingerprint_fnv64");
        _viewportUpdates = new StreamWriter(Path.Combine(DirectoryPath, "godot-viewport-update-requests.csv"), false, new UTF8Encoding(false), 8 * 1024);
        _viewportUpdates.WriteLine("loop_frame_index,mono_ticks,update_mode");
    }

    public void RecordTick(in GodotTickPerformanceSample sample)
    {
        _ticks.WriteLine(FormattableString.Invariant($"{sample.TickId},{sample.SimTimeNs},{sample.MonoTicks},{sample.CallbackNs},{sample.EnginePhysicsMs:F6},{sample.SensorRaycastNs},{sample.SensorSerializationNs},{sample.SensorTransportNs},{sample.TickSerializationNs},{sample.TickTransportNs},{sample.TickAckWaitNs},{sample.AckArrivalWaitNs},{sample.AckDispatchDelayNs},{sample.EventDrainNs},{sample.TorqueNs},{sample.SnapshotNs},{sample.DiagnosticsNs},{sample.HardwareQueueDepth},{sample.HardwareQueueHighWatermark},{sample.PhysicsBodyCount},{sample.PhysicsCallbacksDuringAckWait},{sample.PhysicsCallbackCommit},{sample.ChassisX:F6},{sample.ChassisZ:F6},{sample.ChassisSpeedMps:F6},{sample.LeftDuty:F3},{sample.RightDuty:F3},{sample.LeftWheelX:F6},{sample.LeftWheelY:F6},{sample.LeftWheelZ:F6},{sample.LeftWheelAngularX:F6},{sample.LeftWheelAngularY:F6},{sample.LeftWheelAngularZ:F6},{sample.RightWheelX:F6},{sample.RightWheelY:F6},{sample.RightWheelZ:F6},{sample.RightWheelAngularX:F6},{sample.RightWheelAngularY:F6},{sample.RightWheelAngularZ:F6}"));
        if (_flushTicksEachRecord || ++_ticksSinceFlush >= 120) { _ticks.Flush(); _ticksSinceFlush = 0; }
    }

    public void RecordFrame(in GodotFramePerformanceSample sample) =>
        _frames.WriteLine(FormattableString.Invariant($"{sample.MonoTicks},{sample.FrameIntervalNs},{sample.ProcessCallbackNs},{sample.EngineProcessMs:F6},{sample.Fps:F2},{sample.ChassisX:F6},{sample.ChassisZ:F6},{sample.PhysicsCallbacksSincePreviousFrame}"));

    public void RecordPhysicsCallback(in GodotPhysicsCallbackSample sample) =>
        _physicsCallbacks.WriteLine(FormattableString.Invariant($"{sample.CallbackSequence},{sample.MonoTicks},{sample.DeltaSeconds:R},{sample.TransportPaused},{sample.AckPending},{sample.PhysicsStepGranted},{sample.UserPaused},{sample.ClockFaulted},{sample.ClockTickIndex},{sample.PhysicsTickCount},{sample.PhysicsStateFingerprint:X16}"));

    public void RecordViewportUpdateRequest(long monoTicks, long loopFrameIndex)
    {
        _viewportUpdates.WriteLine(FormattableString.Invariant($"{loopFrameIndex},{monoTicks},once"));
        _viewportUpdates.Flush();
    }

    public void Dispose()
    {
        _ticks.Dispose();
        _frames.Dispose();
        _physicsCallbacks.Dispose();
        _viewportUpdates.Dispose();
    }
}
