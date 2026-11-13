using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RoboWorld.RobotService;

internal readonly record struct WorkerTickPerformanceSample(
    ulong TickId, long SimTimeNs, long MonoTicks, long RequestedDeltaNs,
    long FrameReadNs, long ProtobufParseNs, long ChannelWriteBlockedNs, long QueueWaitNs, int QueueDepth, int QueueHighWatermark,
    int ControllerCount, long SchedulerNs, long RenodeRunForNs, long MonitorQueryNs,
    long EchoInjectionNs, long PeripheralEventNs, long ClockVerificationNs, long AckSerializationNs, long AckTransportNs,
    long ExpectedRenodeTimeNs, long ManagedHeapBytes, long ManagedHeapCommittedBytes, long ManagedHeapFragmentedBytes,
    int Gen0CollectionCount, int Gen1CollectionCount, int Gen2CollectionCount);

internal sealed class RobotServicePerformanceTrace : IDisposable
{
    private readonly StreamWriter _ticks;
    private int _sinceFlush;

    public RobotServicePerformanceTrace(string directoryPath, string scenario)
    {
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        _ticks = new StreamWriter(Path.Combine(directory, "robot-service-ticks.csv"), false, new UTF8Encoding(false), 64 * 1024);
        _ticks.WriteLine("tick_id,sim_time_ns,mono_ticks,requested_delta_ns,frame_read_ns,protobuf_parse_ns,channel_write_blocked_ns,queue_wait_ns,queue_depth,queue_high_watermark,controller_count,scheduler_ns,renode_runfor_ns,monitor_query_ns,echo_injection_ns,peripheral_event_ns,clock_verification_ns,ack_serialization_ns,ack_transport_ns,expected_renode_time_ns,managed_heap_bytes,managed_heap_committed_bytes,managed_heap_fragmented_bytes,gc_gen0_count,gc_gen1_count,gc_gen2_count,scenario,stopwatch_frequency");
        Console.WriteLine($"PERF_WORKER_TRACE scenario={scenario} path={Path.Combine(directory, "robot-service-ticks.csv")} stopwatch_frequency={Stopwatch.Frequency}");
    }

    public void Record(in WorkerTickPerformanceSample sample, string scenario)
    {
        _ticks.WriteLine(FormattableString.Invariant($"{sample.TickId},{sample.SimTimeNs},{sample.MonoTicks},{sample.RequestedDeltaNs},{sample.FrameReadNs},{sample.ProtobufParseNs},{sample.ChannelWriteBlockedNs},{sample.QueueWaitNs},{sample.QueueDepth},{sample.QueueHighWatermark},{sample.ControllerCount},{sample.SchedulerNs},{sample.RenodeRunForNs},{sample.MonitorQueryNs},{sample.EchoInjectionNs},{sample.PeripheralEventNs},{sample.ClockVerificationNs},{sample.AckSerializationNs},{sample.AckTransportNs},{sample.ExpectedRenodeTimeNs},{sample.ManagedHeapBytes},{sample.ManagedHeapCommittedBytes},{sample.ManagedHeapFragmentedBytes},{sample.Gen0CollectionCount},{sample.Gen1CollectionCount},{sample.Gen2CollectionCount},{scenario},{Stopwatch.Frequency}"));
        if (++_sinceFlush >= 120) { _ticks.Flush(); _sinceFlush = 0; }
    }

    public void Dispose() => _ticks.Dispose();
}
