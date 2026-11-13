namespace RoboWorld.Domain;

public sealed record DcMotorOperatingPoint(double RequestedDuty, double MotorSpeedRadPerSecond, double TorqueNm, double TargetSpeedRadPerSecond);

/// <summary>First-order, generic brushed DC motor approximation for the versioned functional component profile.</summary>
public static class DcMotorModel
{
    public static DcMotorOperatingPoint Evaluate(double signedDuty, double motorSpeedRadPerSecond, double stallTorqueNm, double noLoadRpm)
    {
        if (!double.IsFinite(signedDuty) || signedDuty is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(signedDuty));
        if (!double.IsFinite(motorSpeedRadPerSecond)) throw new ArgumentOutOfRangeException(nameof(motorSpeedRadPerSecond));
        if (!double.IsFinite(stallTorqueNm) || stallTorqueNm <= 0) throw new ArgumentOutOfRangeException(nameof(stallTorqueNm));
        if (!double.IsFinite(noLoadRpm) || noLoadRpm <= 0) throw new ArgumentOutOfRangeException(nameof(noLoadRpm));

        var noLoadRadPerSecond = noLoadRpm * 2 * Math.PI / 60;
        var targetSpeed = signedDuty * noLoadRadPerSecond;
        // Back-EMF makes torque depend on signed relative speed, not its absolute
        // magnitude. Nonzero duty therefore converges to duty * no-load speed;
        // zero duty models an open H-bridge and lets the wheel coast.
        var torque = signedDuty == 0
            ? 0
            : Math.Clamp(stallTorqueNm * (signedDuty - motorSpeedRadPerSecond / noLoadRadPerSecond), -stallTorqueNm, stallTorqueNm);
        return new DcMotorOperatingPoint(signedDuty, motorSpeedRadPerSecond, torque, targetSpeed);
    }
}
