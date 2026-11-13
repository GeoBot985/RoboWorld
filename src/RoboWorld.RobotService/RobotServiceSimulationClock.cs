namespace RoboWorld.RobotService;

/// <summary>Shared startup-time contract for Exact Renode and validated-model controller sessions.</summary>
public static class RobotServiceSimulationClock
{
    /// <summary>
    /// Exact mode advances Renode through this bounded startup interval so real firmware emits its boot line.
    /// Validated models start at the same authoritative epoch after their characterized initial state is ready.
    /// </summary>
    public const long ControllerStartupEpochNs = 10_000_000;
}
