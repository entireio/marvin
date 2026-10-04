using System;
using System.Collections.Generic;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Race-only acting. It observes driving without changing steering or physics.
/// All timing uses the simulation clock so focus loss, pause and braking do not
/// produce wall-clock animation jumps.
/// PORT: Swift arrays/dictionaries are values; use <see cref="Clone"/> to copy this struct.
public struct RacePerformance
{
    /// PORT: Swift `enum Character: Int, CaseIterable`. Raw value is <c>(int)character</c>;
    /// <c>Character.allCases</c> is <see cref="CharacterAllCases"/>.
    public enum Character { marvin = 0, r2d2 = 1, bb8 = 2, wallE = 3 }
    /// Swift <c>RacePerformance.Character.allCases</c> (a fresh array each call).
    public static Character[] CharacterAllCases => new[] { Character.marvin, Character.r2d2, Character.bb8, Character.wallE };

    public struct Actor
    {
        public readonly double x, z, heading, speed, elapsed;
        public Actor(double x, double z, double heading, double speed, double elapsed)
        {
            this.x = x; this.z = z; this.heading = heading; this.speed = speed; this.elapsed = elapsed;
        }
        public Actor(Simulation state) : this(state.x, state.z, state.heading, state.speed, state.elapsed) { }
    }
    public struct Pose : IEquatable<Pose>
    {
        public double yaw = 0.0, pitch = 0.0, roll = 0.0;
        public double leftArm = 0.0, rightArm = 0.0, focus = 0.0;
        public Pose() { }
        public static bool operator ==(Pose a, Pose b) =>
            a.yaw == b.yaw && a.pitch == b.pitch && a.roll == b.roll && a.leftArm == b.leftArm && a.rightArm == b.rightArm && a.focus == b.focus;
        public static bool operator !=(Pose a, Pose b) => !(a == b);
        public readonly bool Equals(Pose other) => this == other;
        public override readonly bool Equals(object obj) => obj is Pose other && this == other;
        public override readonly int GetHashCode() => HashCode.Combine(yaw, pitch, roll, leftArm, rightArm, focus);
    }
    public readonly Character character;
    // PORT: Swift `public private(set) var pose`; a field so its members can be updated in place.
    public Pose pose = new Pose();
    public int? lookingAt { get; private set; } = null;
    public double curveYaw { get; private set; } = 0.0;
    private double lastTime = 0.0, glanceEnd = 0.0, nextGlance = 0.0;
    private Dictionary<int, double> previousAlong = new Dictionary<int, double>();
    private HashSet<int> nearby = new HashSet<int>();
    public RacePerformance(Character character) { this.character = character; }
    private static double angle(double value) => atan2(sin(value), cos(value));
    private static double clamp(double value, double limit) => max(-limit, min(limit, value));

    /// Deep copy with Swift value semantics.
    public readonly RacePerformance Clone()
    {
        var copy = this;
        copy.previousAlong = new Dictionary<int, double>(previousAlong);
        copy.nearby = new HashSet<int>(nearby);
        return copy;
    }

    public void update(int index, IReadOnlyList<Actor> actors, bool followingCourse = true)
    {
        if (!(index >= 0 && index < actors.Count)) return;
        var me = actors[index];
        if (me.elapsed < lastTime || me.elapsed == 0) { this = new RacePerformance(character); return; }
        var dt = min(0.1, max(0, me.elapsed - lastTime));
        if (!(dt > 0)) return;
        lastTime = me.elapsed;
        var moving = min(1, abs(me.speed) / 0.8);
        // Look beyond the driver's immediate steering target into the bend.
        var projection = DirtCourse.projection(me.x, me.z);
        var phase = projection.phase;
        var onCourse = followingCourse && projection.distance < DirtCourse.width;
        var ahead = 0.10 + min(12, abs(me.speed)) * 0.012;
        curveYaw = onCourse && me.speed > 0 ? clamp(angle(DirtCourse.heading(phase + ahead) - me.heading), 0.8) * moving : 0;
        var maxYaw = new[] { 0.95, 1.65, 1.25, 1.15 }[(int)character];
        var yaw = curveYaw;
        var glance = 0.0;
        var candidates = new List<(int index, double distance)>();
        var newNearby = new HashSet<int>();
        for (var i = 0; i < actors.Count; i++)
        {
            if (i == index) continue;
            var other = actors[i];
            double dx = other.x - me.x, dz = other.z - me.z;
            var along = dx * sin(me.heading) + dz * cos(me.heading);
            var side = dx * cos(me.heading) - dz * sin(me.heading);
            double distance = hypot(dx, dz), bearing = angle(atan2(dx, dz) - me.heading);
            var relative = previousAlong.TryGetValue(i, out var previous) ? abs(along - previous) / dt : 0;
            var close = distance < 2.8 && abs(along) < 1.9 && abs(side) > 0.28 && abs(bearing) < 2.15;
            if (close)
            {
                newNearby.Add(i);
                if (!nearby.Contains(i) && moving > 0.3 && (relative > 0.15 || abs(other.speed - me.speed) > 0.2))
                {
                    candidates.Add((i, distance));
                }
            }
            previousAlong[i] = along;
        }
        nearby = newNearby;
        if (me.elapsed >= glanceEnd) { lookingAt = null; }
        if (lookingAt == null && me.elapsed >= nextGlance && minBy(candidates, (a, b) => a.distance < b.distance, out var closest))
        {
            lookingAt = closest.index; glanceEnd = me.elapsed + 0.85; nextGlance = me.elapsed + 2.6;
        }
        if (lookingAt is int target && target >= 0 && target < actors.Count)
        {
            var other = actors[target];
            double dx = other.x - me.x, dz = other.z - me.z;
            var bearing = angle(atan2(dx, dz) - me.heading);
            if (hypot(dx, dz) < 3.5 && abs(bearing) < 2.35)
            {
                yaw = clamp(bearing, maxYaw);
                glance = sin(Math.PI * max(0, min(1, (glanceEnd - me.elapsed) / 0.85)));
            }
            else { lookingAt = null; }
        }
        var goal = new Pose(); goal.yaw = clamp(yaw, maxYaw);
        goal.focus = min(1, abs(curveYaw) * 1.2);
        switch (character)
        {
            case Character.marvin:
                goal.pitch = -0.035 * goal.focus + 0.07 * glance;
                break;
            case Character.r2d2:
                // A dome can swivel; no invented neck tilt or roll.
                break;
            case Character.bb8:
                goal.pitch = 0.025 * moving + 0.065 * glance;
                goal.roll = -0.10 * curveYaw + 0.12 * glance * (yaw < 0 ? -1 : 1);
                break;
            case Character.wallE:
                goal.pitch = -0.035 * goal.focus + 0.09 * glance;
                goal.roll = -0.07 * curveYaw + 0.09 * glance * (yaw < 0 ? -1 : 1);
                // Raise the inside hand to indicate a bend; a brief greeting while
                // alongside is layered onto that side, rather than constant waving.
                goal.leftArm = max(0, curveYaw) * 0.85 + (yaw > 0 ? glance * 0.35 : 0);
                goal.rightArm = max(0, -curveYaw) * 0.85 + (yaw < 0 ? glance * 0.35 : 0);
                break;
        }
        var blend = 1 - exp(-dt * (character == Character.r2d2 ? 5.5 : 8));
        pose.yaw += (goal.yaw - pose.yaw) * blend;
        pose.pitch += (goal.pitch - pose.pitch) * blend;
        pose.roll += (goal.roll - pose.roll) * blend;
        pose.leftArm += (goal.leftArm - pose.leftArm) * blend;
        pose.rightArm += (goal.rightArm - pose.rightArm) * blend;
        pose.focus += (goal.focus - pose.focus) * blend;
    }
}
