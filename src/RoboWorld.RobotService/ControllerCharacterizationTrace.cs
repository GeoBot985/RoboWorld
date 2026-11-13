using System.Diagnostics;
using System.Text.Json;

namespace RoboWorld.RobotService;

/// <summary>Optional UART, per-boundary peripheral output, and physical sensor stimulus trace.</summary>
internal sealed class ControllerCharacterizationTrace : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private long _sequence;

    public ControllerCharacterizationTrace(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 32 * 1024) { AutoFlush = true };
    }

    public void Record(string controllerId, string direction, long completedBoundaryNs, long hostMonotonicTicks, ReadOnlySpan<byte> bytes)
        => Record(controllerId, direction, completedBoundaryNs, hostMonotonicTicks, bytes, null);

    public void Record(string controllerId, string direction, long completedBoundaryNs, long hostMonotonicTicks,
        ReadOnlySpan<byte> bytes, long? scheduledSimTimeNs)
    {
        lock (_gate)
        {
            var item = new UartChunk(1, ++_sequence, "UartChunk", controllerId, direction,
                completedBoundaryNs, hostMonotonicTicks, Convert.ToBase64String(bytes), scheduledSimTimeNs);
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void RecordPwm(string controllerId, long simTimeNs, double channel1SignedDuty, double channel2SignedDuty,
        bool channel1DirectionForward, bool channel2DirectionForward, double channel1DutyFraction, double channel2DutyFraction)
    {
        lock (_gate)
        {
            var item = new PwmObservation(1, ++_sequence, "PwmObservation", controllerId, simTimeNs,
                Stopwatch.GetTimestamp(), channel1SignedDuty, channel2SignedDuty,
                channel1DirectionForward, channel2DirectionForward, channel1DutyFraction, channel2DutyFraction);
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void RecordRenodeTimeBoundary(long simTimeNs)
    {
        lock (_gate)
        {
            var item = new RenodeTimeBoundary(1, ++_sequence, "RenodeTimeBoundary", simTimeNs, Stopwatch.GetTimestamp());
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void RecordDistanceSample(long simTimeNs, double distanceMeters)
    {
        lock (_gate)
        {
            var item = new DistanceSample(1, ++_sequence, "DistanceSample", simTimeNs, Stopwatch.GetTimestamp(), distanceMeters);
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void RecordDistanceEchoEdge(long simTimeNs, bool high)
    {
        lock (_gate)
        {
            var item = new DistanceEchoEdge(1, ++_sequence, "DistanceEchoEdge", simTimeNs, Stopwatch.GetTimestamp(), high);
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void RecordFault(string controllerId, long simTimeNs, string faultCode, string phase)
    {
        lock (_gate)
        {
            var item = new FaultObservation(1, ++_sequence, "FaultObservation", controllerId, simTimeNs,
                Stopwatch.GetTimestamp(), faultCode, phase);
            _writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void Dispose() { lock (_gate) _writer.Dispose(); }

    private sealed record UartChunk(int SchemaVersion, long Sequence, string RecordType, string ControllerId, string Direction,
        long LastCompletedBoundaryNsLowerBound, long HostMonotonicTicks, string BytesBase64, long? ScheduledSimTimeNs);
    private sealed record PwmObservation(int SchemaVersion, long Sequence, string RecordType, string ControllerId, long SimTimeNs,
        long HostMonotonicTicks, double Channel1SignedDuty, double Channel2SignedDuty,
        bool Channel1DirectionForward, bool Channel2DirectionForward, double Channel1DutyFraction, double Channel2DutyFraction);
    private sealed record RenodeTimeBoundary(int SchemaVersion, long Sequence, string RecordType, long SimTimeNs, long HostMonotonicTicks);
    private sealed record DistanceSample(int SchemaVersion, long Sequence, string RecordType, long SimTimeNs, long HostMonotonicTicks, double DistanceMeters);
    private sealed record DistanceEchoEdge(int SchemaVersion, long Sequence, string RecordType, long SimTimeNs, long HostMonotonicTicks, bool High);
    private sealed record FaultObservation(int SchemaVersion, long Sequence, string RecordType, string ControllerId,
        long SimTimeNs, long HostMonotonicTicks, string FaultCode, string Phase);
}
