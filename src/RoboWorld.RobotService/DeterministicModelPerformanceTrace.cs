using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RoboWorld.RobotService;

internal readonly record struct DeterministicModelTickPerformanceSample(
    ulong TickId, long SimTimeNs, long EventReadMonoTicks, long ProcessingMonoTicks,
    long EventReadParseNs, long QueueWaitNs, long ModelPipelineNs, long AckWriteNs,
    int QueueDepthAfterRead, long ChannelWriteBlockedNs, int ControllerCount);

internal sealed class DeterministicModelPerformanceTrace : IDisposable
{
    private readonly StreamWriter _ticks;

    public DeterministicModelPerformanceTrace(string directoryPath, string scenario)
    {
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deterministic-model-ticks.csv");
        _ticks = new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024);
        _ticks.WriteLine("tick_id,sim_time_ns,event_read_mono_ticks,processing_mono_ticks,event_read_parse_ns,fast_model_queue_wait_ns,model_pipeline_ns,ack_write_ns,queue_depth_after_read,channel_write_blocked_ns,controller_count,scenario,stopwatch_frequency");
        Console.WriteLine($"PERF_MODEL_TRACE scenario={scenario} path={path} stopwatch_frequency={Stopwatch.Frequency}");
    }

    public void Record(in DeterministicModelTickPerformanceSample sample, string scenario)
    {
        _ticks.WriteLine(FormattableString.Invariant($"{sample.TickId},{sample.SimTimeNs},{sample.EventReadMonoTicks},{sample.ProcessingMonoTicks},{sample.EventReadParseNs},{sample.QueueWaitNs},{sample.ModelPipelineNs},{sample.AckWriteNs},{sample.QueueDepthAfterRead},{sample.ChannelWriteBlockedNs},{sample.ControllerCount},{scenario},{Stopwatch.Frequency}"));
        if (sample.TickId % 120 == 0) _ticks.Flush();
    }

    public void Dispose() => _ticks.Dispose();
}
