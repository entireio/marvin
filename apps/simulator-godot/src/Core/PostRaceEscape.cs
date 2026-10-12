using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Cooldown leads into continuous, physical town driving, never a camera movie.
/// PORT: Swift arrays are values. The arrays below are mutated element-wise, so a copy of this
/// struct must be made with <see cref="Clone"/> (a plain C# copy shares them). The outer route
/// arrays are only ever replaced element-wise, never mutated inside, so routes share inner arrays.
public struct PostRaceEscape
{
    public bool active { get; private set; } = false;
    public Double2[][] routes { get; private set; } = Array.Empty<Double2[]>();
    public int[] waypoint { get; private set; } = Enumerable.Repeat(-1, 4).ToArray();
    public bool[] departed { get; private set; } = new bool[4];
    public int[] tours { get; private set; } = new int[4];
    public readonly bool complete => active && departed.All(d => d);
    private Double2[][] plans = Array.Empty<Double2[]>();
    private int released = 0;
    private int[] order = { 0, 1, 2, 3 };
    private Double2?[] pullouts = new Double2?[4];
    private double[] pulloutUntil = new double[4];
    private int?[] yieldingTo = new int?[4];
    private Double2[] yieldDirection = new Double2[4];
    private double[] nextYieldAttempt = new double[4];
    public int[] yields { get; private set; } = new int[4];
    private bool planningAttempted = false;
    private double elapsed = 0.0;
    private double[] stopped = new double[4];
    private Double2?[] rejoinPoints = new Double2?[4];
    private double[] backingUntil = new double[4];
    public int[] reversals { get; private set; } = new int[4];
    private double[] rests = new double[4];
    public PostRaceEscape() : this(Array.Empty<Double2[]>()) { }
    public PostRaceEscape(Double2[][] routes) { this.routes = (Double2[][])routes.Clone(); plans = (Double2[][])routes.Clone(); }

    /// Deep copy with Swift value semantics.
    public readonly PostRaceEscape Clone()
    {
        var copy = this;
        copy.routes = (Double2[][])routes.Clone();
        copy.waypoint = (int[])waypoint.Clone();
        copy.departed = (bool[])departed.Clone();
        copy.tours = (int[])tours.Clone();
        copy.plans = (Double2[][])plans.Clone();
        copy.order = (int[])order.Clone();
        copy.pullouts = (Double2?[])pullouts.Clone();
        copy.pulloutUntil = (double[])pulloutUntil.Clone();
        copy.yieldingTo = (int?[])yieldingTo.Clone();
        copy.yieldDirection = (Double2[])yieldDirection.Clone();
        copy.nextYieldAttempt = (double[])nextYieldAttempt.Clone();
        copy.yields = (int[])yields.Clone();
        copy.stopped = (double[])stopped.Clone();
        copy.rejoinPoints = (Double2?[])rejoinPoints.Clone();
        copy.backingUntil = (double[])backingUntil.Clone();
        copy.reversals = (int[])reversals.Clone();
        copy.rests = (double[])rests.Clone();
        return copy;
    }

    public static Double2[][] makeRoutes(CityCollisionWorld city)
    {
        var apron = CityExit.point(0, 8);
        // Distinct circuits pass close to the track, then visit outer streets.
        Double2[][] visits =
        {
            new[] { new Double2(33, 3), new Double2(29, 23), new Double2(8, 27), new Double2(-18, 24), new Double2(-26, 0), new Double2(-15, -27), new Double2(19, -26) },
            new[] { new Double2(43, 7), new Double2(36, 28), new Double2(12, 30), new Double2(-23, 23), new Double2(-29, -13), new Double2(-8, -30), new Double2(29, -21) },
            new[] { new Double2(29, 14), new Double2(19, 27), new Double2(-8, 26), new Double2(-27, 10), new Double2(-24, -20), new Double2(3, -28), new Double2(32, -20) },
            new[] { new Double2(39, 3), new Double2(32, 30), new Double2(1, 32), new Double2(-29, 20), new Double2(-31, -10), new Double2(-14, -30), new Double2(21, -29) },
        };
        var planner = new TownEscapeRoute(city, Double2.zero);
        var plans = new List<Double2[]>();
        var avoiding = CityExit.posts.Append(new CityGate().body(100 * Math.PI / 180)).ToArray();
        for (var i = 0; i < visits.Length; i++)
        {
            var goals = visits[i];
            var lane = new[] { -0.32, 0.27, -0.12, 0.38 }[i];
            var path = new List<Double2> { CityExit.point(lane, -1.15), CityExit.point(lane * 0.6, 2.8), apron };
            var current = apron;
            foreach (var goal in goals.Append(apron))
            {
                var leg = planner.route(current, goal, rounded: true, clearance: 0.7, goalTolerance: 10, avoiding: avoiding);
                if (!(leg != null && leg.Length > 1)) { Console.WriteLine($"Town circuit {i} unreachable: {current} -> {goal}"); return Array.Empty<Double2[]>(); }
                path.AddRange(leg.Skip(1)); current = leg[leg.Length - 1];
            }
            // The last goal may have been snapped to a free cell. Close exactly.
            if (Simd.distance(current, apron) > 0.01) { path.Add(apron); }
            plans.Add(path.ToArray());
        }
        return plans.ToArray();
    }
    public void advance(IReadOnlyList<DirtRace> races, IReadOnlyList<Simulation> states, CityCollisionWorld? city, double gateAngle, double dt = 1.0 / 240)
    {
        if (!(races.Count == 4 && states.Count == 4)) return;
        if (!active)
        {
            if (!(races.All(race => race.finished && race.completedCooldownLap) && city is CityCollisionWorld world)) return;
            if (routes.Length == 0 && !planningAttempted) { planningAttempted = true; routes = makeRoutes(world); plans = (Double2[][])routes.Clone(); }
            if (!(routes.Length == 4)) return;
            double remaining(int i)
            {
                var phase = DirtCourse.phase(states[i].x, states[i].z);
                return (CityExit.phase - phase + 4 * Math.PI) % (2 * Math.PI);
            }
            order = sorted(Enumerable.Range(0, states.Count), (a, b) => remaining(a) < remaining(b));
            active = true;
        }
        elapsed += dt;
        if (!(gateAngle > 99 * Math.PI / 180)) return;
        if (released < 3 && departed[order[released]]) { released += 1; }
        // Local aliases: lambdas in struct methods cannot capture `this`; the arrays are shared references.
        var departedNow = departed;
        var pulloutsNow = pullouts;
        foreach (var i in order.Take(released + 1).ToArray())
        {
            rests[i] = max(0, rests[i] - dt);
            var p = new Double2(states[i].x, states[i].z);
            if (waypoint[i] < 0)
            {
                var phase = DirtCourse.phase(p.x, p.y);
                var before = atan2(sin(CityExit.phase - phase), cos(CityExit.phase - phase));
                if (before >= -0.025 && before < 0.10 + (double)i * 0.004) { waypoint[i] = 0; }
            }
            if (!(waypoint[i] >= 0 && rests[i] == 0)) continue;
            var safelyWaiting = pullouts[i] is Double2 waiting ? Simd.distance(p, waiting) < 0.25 : false;
            if (departed[i] && !safelyWaiting)
            {
                stopped[i] = states[i].groundSpeed < 0.06 ? stopped[i] + dt : 0;
                if (stopped[i] > 4 + (double)(3 - i) * 0.6)
                {
                    stopped[i] = 0; backingUntil[i] = elapsed + 2.5; pullouts[i] = null; rejoinPoints[i] = null; reversals[i] += 1;
                }
                if (backingUntil[i] > elapsed) continue;
                if (backingUntil[i] > 0 && city is CityCollisionWorld backingCity)
                {
                    backingUntil[i] = 0;
                    if (recoveryWaypoint(routes[i], waypoint[i], p, backingCity) is int j)
                    {
                        waypoint[i] = j; rejoinPoints[i] = routes[i][j];
                    }
                }
            }
            if (rejoinPoints[i] is Double2 rejoinTarget)
            {
                if (Simd.distance(p, rejoinTarget) < 0.25) { rejoinPoints[i] = null; }
                else continue;
            }
            if (yieldingTo[i] is int other && pullouts[i] != null)
            {
                var delta = new Double2(states[other].x, states[other].z) - p;
                // Stay in the pullout until the priority vehicle has actually
                // passed. A fixed short timer sends us back into its path.
                if (Simd.dot(delta, yieldDirection[i]) < -1.5 || Simd.length(delta) > 9 || elapsed >= pulloutUntil[i])
                {
                    pullouts[i] = null; yieldingTo[i] = null; stopped[i] = 0;
                }
            }
            if (departed[i] && pullouts[i] == null && elapsed >= nextYieldAttempt[i] && city is CityCollisionWorld yieldCity)
            {
                var forward = new Double2(sin(states[i].heading), cos(states[i].heading));
                int? opposing = null;
                for (var j = 0; j < states.Count; j++)
                {
                    var delta = new Double2(states[j].x, states[j].z) - p;
                    var heading = new Double2(sin(states[j].heading), cos(states[j].heading));
                    if (j < i && departedNow[j] && Simd.length(delta) < 7 && Simd.dot(delta, forward) > 0.2
                        && abs(delta.x * forward.y - delta.y * forward.x) < 1.3 && Simd.dot(forward, heading) < (-0.3))
                    {
                        opposing = j; break;
                    }
                }
                if (opposing is int opposingIndex)
                {
                    nextYieldAttempt[i] = elapsed + 2;
                    var right = new Double2(forward.y, -forward.x);
                    // Yield into checked street space before meeting nose to nose.
                    // No teleport or collision bypass: the normal drive input gets us there.
                    foreach (var offset in new[] { right * 1.3, right * 1.3 - forward * 1.5, -right * 1.3, -right * 1.3 - forward * 1.5 })
                    {
                        var candidate = p + offset;
                        var spaced = true;
                        for (var j = 0; j < states.Count; j++)
                        {
                            if (!(j == i || (Simd.distance(candidate, new Double2(states[j].x, states[j].z)) > 1.1
                                && (pulloutsNow[j] is Double2 pullout ? Simd.distance(candidate, pullout) > 1.3 : true)))) { spaced = false; break; }
                        }
                        if (!spaced) continue;
                        var free = true;
                        for (var step = 1; step <= 16; step++)
                        {
                            var q = p + offset * (double)step / 16;
                            var projection = DirtCourse.projection(q.x, q.y);
                            if (!(projection.offset > 0 && projection.distance > DirtCourse.fenceOffset + 0.8)) { free = false; break; }
                            var body = new RobotCollisions.Body(position: new Double3(q.x, DirtCourse.height(q.x, q.y), q.y), profile: new RobotCollisions.Profile(mass: 18, halfWidth: 0.65, halfDepth: 0.65, height: 1, round: true));
                            var blockers = yieldCity.nearby(body).Concat(CityExit.posts).Append(new CityGate().body(gateAngle));
                            if (!blockers.All(blocker => RobotCollisions.contact(body, blocker) == null)) { free = false; break; }
                        }
                        if (free)
                        {
                            pullouts[i] = candidate; pulloutUntil[i] = elapsed + 18;
                            yieldingTo[i] = opposingIndex; yieldDirection[i] = forward;
                            yields[i] += 1; break;
                        }
                    }
                }
            }
            if (pullouts[i] != null) continue;
            var reach = waypoint[i] < 3 ? 0.35 : 0.62;
            while (waypoint[i] < routes[i].Length && Simd.distance(p, routes[i][waypoint[i]]) < reach)
            {
                waypoint[i] += 1;
                if (waypoint[i] >= 3) { departed[i] = true; }
            }
            if (waypoint[i] >= routes[i].Length)
            {
                tours[i] += 1;
                routes[i] = plans[(i + tours[i] + tours[i] / 3) % plans.Length];
                waypoint[i] = 3;
                rests[i] = 1.2 + (double)((i * 7 + tours[i] * 3) % 9) * 0.35;
            }
        }
    }
    /// Reversing can leave the robot behind an earlier bend. Rejoin a reachable
    /// earlier sample before resuming lookahead, never aim through that corner.
    public static int? recoveryWaypoint(IReadOnlyList<Double2> route, int waypoint, Double2 from, CityCollisionWorld city)
    {
        var p = from;
        if (!(waypoint >= 3 && waypoint < route.Count)) return null;
        var lower = max(3, waypoint - 24);
        var candidates = sorted(Enumerable.Range(lower, waypoint - lower + 1), (a, b) => Simd.distance(p, route[a]) < Simd.distance(p, route[b]));
        foreach (var j in candidates)
        {
            var delta = route[j] - p;
            var distance = Simd.length(delta);
            if (!(distance < 5)) continue;
            var steps = max(1, (int)ceil(distance / 0.08));
            var clear = true;
            for (var step = 1; step <= steps; step++)
            {
                var q = p + delta * (double)step / (double)steps;
                var body = new RobotCollisions.Body(position: new Double3(q.x, DirtCourse.height(q.x, q.y), q.y), profile: new RobotCollisions.Profile(mass: 85, halfWidth: 0.5, halfDepth: 0.5, height: 1, round: true));
                if (!city.nearby(body).All(obstacle => RobotCollisions.contact(body, obstacle) == null)) { clear = false; break; }
            }
            if (clear) return j;
        }
        return null;
    }
    public readonly DriveInput? input(int index, IReadOnlyList<Simulation> states)
    {
        if (!(active && index < states.Count)) return null;
        var input = new DriveInput();
        if (backingUntil[index] > elapsed) { input.throttle = -0.20; return input; }
        if (rests[index] > 0) { input.brake = true; return input; }
        if (!(waypoint[index] >= 0)) return DirtOpponent.driveInput(states[index], cruising: true);
        var state = states[index];
        var p = new Double2(state.x, state.z);
        var target = rejoinPoints[index] ?? pullouts[index] ?? routes[index][waypoint[index]];
        if (pullouts[index] != null && Simd.distance(p, target) < 0.25) { input.brake = true; return input; }
        // Look through closely spaced samples on rounded corners rather than
        // stopping and pivoting at every navigation-grid vertex.
        if (waypoint[index] >= 3 && pullouts[index] == null && rejoinPoints[index] == null)
        {
            Double2 a = routes[index][waypoint[index] - 1], b = target;
            var segment = b - a;
            var along = max(0, min(1, Simd.dot(p - a, segment) / max(0.0001, Simd.length_squared(segment))));
            target = a + segment * along;
            // Rejoin the local corridor after yielding. Aiming straight at a
            // distant vertex can cut through the building on the inside bend.
            var remaining = 0.55 + min(0.25, state.groundSpeed * 0.1);
            var j = waypoint[index];
            while (j < routes[index].Length)
            {
                var next = routes[index][j];
                var length = Simd.distance(target, next);
                if (length > remaining) { target += (next - target) * (remaining / length); break; }
                remaining -= length; target = next; j += 1;
            }
        }
        var delta = target - p;
        var distance = Simd.length(delta);
        var direction = delta / max(0.001, distance);
        var error = atan2(sin(atan2(delta.x, delta.y) - state.heading), cos(atan2(delta.x, delta.y) - state.heading));
        input.turn = max(-1, min(1, -error * 1.8));
        var pace = new[] { 1.75, 2.15, 1.9, 2.4 }[index] * (1 + 0.08 * sin(elapsed * (0.13 + (double)index * 0.017) + (double)index * 2));
        input.throttle = abs(error) < 1.15 ? pace / 6 * max(0.18, cos(error) * cos(error)) : 0;
        if (pullouts[index] != null) { input.throttle = min(input.throttle, distance * 0.3); }
        if (waypoint[index] < 3) { input.throttle = min(input.throttle, 0.24 + (double)index * 0.015); }
        for (var i = 0; i < states.Count; i++)
        {
            if (i == index) continue;
            var other = new Double2(states[i].x, states[i].z) - p;
            if (Simd.length(other) < 1.35 && Simd.dot(other, direction) > 0.15 && abs(other.x * direction.y - other.y * direction.x) < 0.65)
            {
                input.throttle = 0; input.brake = true;
            }
        }
        return input;
    }
}
