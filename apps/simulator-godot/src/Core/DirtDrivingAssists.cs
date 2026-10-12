using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Predictive corner braking and light, player-directed steering correction.
/// Manual braking always takes priority; steering never starts on its own.
public struct DirtDrivingAssists
{
    public bool steering, braking;
    public DirtDrivingAssists() : this(true, true) { }
    public DirtDrivingAssists(bool steering = true, bool braking = true)
    {
        this.steering = steering; this.braking = braking;
    }
    public static readonly DirtDrivingAssists off = new DirtDrivingAssists(steering: false, braking: false);

    public readonly DriveInput apply(DriveInput input, Simulation state)
    {
        if (!((steering || braking) && state.dirtTrack && !state.airborne && !state.paused &&
              state.forwardSpeed > 0.5 && input.throttle >= 0)) return input;
        var result = input;
        if (braking && input.brake) { result.assistedBraking = true; }
        var projection = DirtCourse.projection(state.x, state.z);
        var tangent = DirtCourse.heading(projection.phase);
        if (!(projection.distance < DirtCourse.width &&
              cos(tangent - state.heading) > 0.5)) return result;
        if (braking && !input.brake && input.throttle > 0)
        {
            var speed = state.groundSpeed;
            var grip = DirtCourse.traction(state.x, state.z) * max(0.55, 1 - state.storm.depth(state.x, state.z) * 2.5);
            var target = DirtRacingLine.safeSpeed(projection.phase, speed, grip);
            result.throttle = min(input.throttle, target / (input.boost ? 12 : 6));
            result.assistedBrakePressure = max(0, min(1, (speed - target) * 0.65));
            result.assistedBraking = result.assistedBrakePressure > 0;
        }
        if (!(steering && abs(input.turn) > 0.01)) return result;
        var targetTurn = DirtRacingLine.steering(state.x, state.z, state.heading,
                                                 state.groundSpeed, projection.phase);
        var manual = max(-1, min(1, input.turn));
        var direction = manual > 0 ? 1.0 : -1.0;
        var aligned = max(0, targetTurn * direction);
        var helped = manual * 0.80 + direction * aligned * 0.20 * min(1, abs(manual) * 3);
        // At a curve exit the ideal turn crosses zero. Fade back to manual
        // control as the driver steers away from it; never jump from softened to
        // 100% steering at the sign change, and never reverse the chosen turn.
        var opposition = max(0, min(1, -targetTurn * direction / 0.4));
        var @override = opposition * opposition * (3 - 2 * opposition);
        result.turn = helped + (manual - helped) * @override;
        return result;
    }
}

/// A bounded, smoothed reference line through the compacted lane. This is a
/// forgiving arcade racing line, not an optimal vehicle-dynamics lap solution.
public static class DirtRacingLine
{
    private const int count = DirtCourse.sampleCount;
    private static readonly Double2[] points = buildPoints();
    private static Double2[] buildPoints()
    {
        var centers = new Double2[count];
        var normals = new Double2[count];
        for (var i = 0; i < count; i++)
        {
            var p = DirtCourse.point((double)i * 2 * Math.PI / (double)count);
            centers[i] = new Double2(p.x, p.z);
        }
        for (var i = 0; i < count; i++)
        {
            var h = DirtCourse.heading((double)i * 2 * Math.PI / (double)count);
            normals[i] = new Double2(cos(h), -sin(h));
        }
        var line = (Double2[])centers.Clone();
        // Shorten/smooth bends while retaining generous room for every racer.
        for (var pass = 0; pass < 240; pass++)
        {
            var old = (Double2[])line.Clone();
            for (var i = 0; i < count; i++)
            {
                var midpoint = (old[(i + count - 1) % count] + old[(i + 1) % count]) * 0.5;
                var delta = midpoint - centers[i];
                var normal = normals[i];
                var offset = max(-0.8, min(0.8, delta.x * normal.x + delta.y * normal.y));
                line[i] = centers[i] + normal * offset;
            }
        }
        return line;
    }
    /// Braking envelope integrates distance to future bends. Limits account for
    /// both available lateral grip and the chassis' maximum yaw rate.
    public static double safeSpeed(double phase, double speed, double grip)
    {
        var start = (int)((((phase / (2 * Math.PI)) % 1 + 1) % 1) * (double)count) % count;
        double distance = 0.0, limit = 12.0;
        var index = start;
        double deceleration = max(3, 8 * grip), preview = max(8, min(24, speed * speed / (2 * deceleration) + 5));
        do
        {
            Double2 before = points[(index + count - 3) % count], at = points[index], after = points[(index + 3) % count];
            Double2 a = at - before, b = after - at;
            var bend = abs(atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y));
            var curvature = bend / max(0.1, (length(a) + length(b)) * 0.5);
            var corner = min(12, min(0.95 / max(0.02, curvature), sqrt(9 * grip / max(0.02, curvature))));
            limit = min(limit, sqrt(corner * corner + 2 * deceleration * max(0, distance - speed * 0.18)));
            var next = (index + 1) % count; distance += length(points[next] - points[index]); index = next;
        } while (distance < preview && index != start);
        return max(1.8, limit);
    }
    private static double length(Double2 v) => hypot(v.x, v.y);
    public static (double x, double z) point(double phase)
    {
        var t = (((phase / (2 * Math.PI)) % 1 + 1) % 1) * (double)count;
        var i = (int)t % count;
        var f = t - (double)(int)t;
        var p = points[i] * (1 - f) + points[(i + 1) % count] * f;
        return (p.x, p.y);
    }
    public static double steering(double x, double z, double heading, double speed, double phase)
    {
        var t = (((phase / (2 * Math.PI)) % 1 + 1) % 1) * (double)count;
        var i = (int)t % count;
        var target = i;
        var distance = 0.0;
        var lookAhead = max(1.0, min(3.5, abs(speed) * 0.35));
        while (distance < lookAhead)
        {
            var next = (target + 1) % count;
            distance += length(points[next] - points[target]); target = next;
            if (target == i) break;
        }
        var delta = points[target] - new Double2(x, z);
        var error = atan2(sin(atan2(delta.x, delta.y) - heading), cos(atan2(delta.x, delta.y) - heading));
        var omega = 2 * max(0, speed) * sin(error) / max(0.5, length(delta));
        return max(-1, min(1, -omega / 1.15));
    }
}
