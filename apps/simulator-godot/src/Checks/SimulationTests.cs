using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Marvin.Core;
using static Marvin.Core.Swift;
using Actor = Marvin.Core.RacePerformance.Actor;

namespace Marvin.Checks;

// Port of apps/simulator-macos/Tests/SimulationCoreTests/SimulationTests.swift.
// PORT: Swift's global check helpers are static members of SimulationTests so every
// `extension SimulationTests` file (a C# partial struct) calls them unqualified.
// Swift `#filePath`/`#line` are [CallerFilePath]/[CallerLineNumber], so a failure names the C# line.
public partial struct SimulationTests
{
    // Dependency-free checks also run with standalone Apple Command Line Tools.
    public static void require(bool value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!value) { Console.Out.Flush(); Console.Error.Write($"Check failed at {file}:{line}\n"); Environment.Exit(1); }
    }
    // PORT: Swift `equal<T: Equatable>` compares with `==`; one overload per compared type keeps IEEE `==`
    // (NaN != NaN) where C#'s Equals would treat NaNs as equal.
    public static void equal(double a, double b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(int a, int b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(bool a, bool b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(Double2 a, Double2 b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(Double3 a, Double3 b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(Checkpoint a, Checkpoint b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(RacePerformance.Character a, RacePerformance.Character b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    public static void equal(RacePerformance.Pose a, RacePerformance.Pose b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => require(a == b, file, line);
    /// Swift `[Double] == [Double]`: same count and element-wise `==`.
    public static void equal(IReadOnlyList<double> a, IReadOnlyList<double> b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        require(a.Count == b.Count && Enumerable.Range(0, a.Count).All(i => a[i] == b[i]), file, line);
    public static void equal(IReadOnlyList<int> a, IReadOnlyList<int> b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        require(a.Count == b.Count && Enumerable.Range(0, a.Count).All(i => a[i] == b[i]), file, line);
    public static void equal(IReadOnlyList<Checkpoint> a, IReadOnlyList<Checkpoint> b, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        require(same(a, b), file, line);
    public static void near(double a, double b, double accuracy, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (abs(a - b) > accuracy) { Console.Out.Flush(); Console.Error.Write($"Expected {description(a)} near {description(b)}, tolerance {description(accuracy)}\n"); }
        require(abs(a - b) <= accuracy, file, line);
    }
    public static void greater(double a, double b) { require(a > b); }
    public static void less(double a, double b) { require(a < b); }

    // PORT: helpers for Swift collection expressions with no direct C# counterpart.
    /// Swift `[Checkpoint] == [Checkpoint]`.
    private static bool same(IReadOnlyList<Checkpoint> a, IReadOnlyList<Checkpoint> b) =>
        a.Count == b.Count && Enumerable.Range(0, a.Count).All(i => a[i] == b[i]);
    /// Swift `values.reduce(0, +)`: left-to-right sum from 0.
    private static double sum(IEnumerable<double> values)
    {
        var total = 0.0;
        foreach (var value in values) total = total + value;
        return total;
    }
    /// Swift `print(...)`.
    private static void print(string text) => Console.WriteLine(text);

    public void testPlayableFootprints()
    {
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            var profile = RobotCollisions.profiles[(int)character];
            var edge = Simulation.halfWidth - profile.halfWidth - Simulation.collisionClearance;
            require(Simulation.isFree(edge - 0.001, 0, 0, character));
            require(!Simulation.isFree(edge + 0.001, 0, 0, character));
            var state = new Simulation(seed: 0, character: character);
            var input = new DriveInput(); input.throttle = 1;
            for (var i = 0; i < 60; i++) { state.advance(input, 1.0 / 60); }
            require(state.distance > 0.1);
            state.reset();
            equal(state.character, character); equal(state.distance, 0);
        }
    }
    public void testSandboxFootprint()
    {
        var o = Simulation.obstacles[0]; var skin = Simulation.collisionClearance;
        // Straight approach ends within a small skin of the scaled body.
        foreach (var heading in new[] { 0.0, Math.PI / 2, Math.PI / 4 })
        {
            var extent = abs(sin(heading)) * (Simulation.bodyHalfWidth + skin) + abs(cos(heading)) * (Simulation.bodyHalfDepth + skin);
            var z = o.z - o.depth / 2 - extent;
            require(Simulation.isFree(o.x, z - 0.001, heading));
            require(!Simulation.isFree(o.x, z + 0.001, heading));
        }
        // Turning a corner into an obstacle is blocked even when the center is stationary.
        // PORT: block scope; Swift lets this function-level `z` coexist with the loop's `z`.
        {
            var z = o.z - o.depth / 2 - 0.30;
            require(Simulation.isFree(o.x, z, 0));
            require(!Simulation.isFree(o.x, z, Math.PI / 4));
        }
        require(Simulation.isFree(Simulation.halfWidth - Simulation.bodyHalfWidth - skin - 0.001, 0, 0));
        require(!Simulation.isFree(Simulation.halfWidth - Simulation.bodyHalfWidth, 0, 0));
    }
    public void testStaggeredGrid()
    {
        var a = DirtCourse.playerGrid; var b = DirtCourse.opponentGrid;
        var player = new Simulation(dirtTrack: true, dirtStartOffset: a.offset, dirtStartPhase: a.phase);
        var rival = new DirtOpponent().simulation;
        require(a.phase < 0 && b.phase < a.phase && a.offset * b.offset < 0);
        greater(hypot(player.x - rival.x, player.z - rival.z), 1.2);
        var start = player;
        var input = new DriveInput(); input.throttle = 1;
        player.advance(input, 0.1); player.reset();
        equal(player.x, start.x); equal(player.z, start.z);
        var race = new DirtRace(startPhase: a.phase); race.countDown(3);
        for (var i = 0; i <= 20; i++)
        {
            var p = DirtCourse.point(a.phase + (0.04 - a.phase) * (double)i / 20);
            race.advance(p.x, p.z, 0.1);
        }
        require(race.laps.Length == 0 && !race.wrongWay);
        near(race.progress, 0.04, 0.001);
    }
    public void testRacePerformance()
    {
        foreach (var character in new[] { RacePerformance.Character.marvin, RacePerformance.Character.r2d2, RacePerformance.Character.bb8, RacePerformance.Character.wallE })
        {
            // Both bend directions, before the chassis has started turning.
            foreach (var direction in new[] { -1.0, 1.0 })
            {
                var phase = Enumerable.Range(0, 360).Select(k => (double)k * 2 * Math.PI / 360).First(candidate =>
                {
                    var delta = DirtCourse.heading(candidate + 0.172) - DirtCourse.heading(candidate);
                    return atan2(sin(delta), cos(delta)) * direction > 0.35;
                });
                var p = DirtCourse.point(phase); var heading = DirtCourse.heading(phase);
                var results = new List<double>();
                foreach (var fps in new[] { 30.0, 60.0, 120.0 })
                {
                    var actor = new RacePerformance(character);
                    for (var frame = 1; frame <= (int)fps; frame++)
                    {
                        actor.update(0, new[] { new Actor(p.x, p.z, heading, 6, (double)frame / fps) });
                    }
                    require(actor.pose.yaw * direction > 0.25);
                    results.Add(actor.pose.yaw);
                    if (character == RacePerformance.Character.wallE)
                    {
                        require((actor.pose.leftArm - actor.pose.rightArm) * direction > 0.1);
                    }
                    if (character == RacePerformance.Character.r2d2) { equal(actor.pose.pitch, 0); equal(actor.pose.roll, 0); }
                }
                near(minElement(results).Value, maxElement(results).Value, 1e-8);
            }
            foreach (var side in new[] { -1.0, 1.0 })
            {
                var actor = new RacePerformance(character);
                var p = DirtCourse.point(0); var heading = DirtCourse.heading(0);
                Actor[] scene(double time)
                {
                    var along = 2.4 - time * 3;
                    return new[] { new Actor(p.x, p.z, heading, 5, time),
                        new Actor(p.x + cos(heading) * side + sin(heading) * along, p.z - sin(heading) * side + cos(heading) * along, heading, 2, time) };
                }
                var glanced = false;
                for (var frame = 1; frame <= 50; frame++)
                {
                    actor.update(0, scene((double)frame / 60));
                    if (actor.lookingAt == 1 && frame > 25) { glanced = true; require(actor.pose.yaw * side > 0.2); }
                }
                require(glanced);
                var held = actor.pose;
                actor.update(0, scene(50.0 / 60)); equal(actor.pose, held);
                for (var frame = 51; frame <= 120; frame++) { actor.update(0, scene((double)frame / 60)); }
                require(actor.lookingAt == null);
                actor.update(0, scene(0)); equal(actor.pose, new RacePerformance.Pose());
                require(actor.lookingAt == null);
            }
            var parked = new RacePerformance(character);
            for (var frame = 1; frame <= 120; frame++)
            {
                parked.update(0, new[] { new Actor(0, 0, 0, 0, (double)frame / 60), new Actor(1, 0, 0, 0, (double)frame / 60) });
            }
            equal(parked.pose, new RacePerformance.Pose()); require(parked.lookingAt == null);
        }
    }
    // PORT: Swift declares this generator inside testFourRacerGrid; C# has no local types.
    private struct FourRacerRandom : RandomNumberGenerator
    {
        public ulong seed;
        public FourRacerRandom() { seed = 42; }
        public ulong next() { seed = unchecked(seed * 6364136223846793005 + 1442695040888963407); return seed; }
    }
    public void testFourRacerGrid()
    {
        var rng = new FourRacerRandom(); var allocations = new HashSet<string>();
        for (var n = 0; n < 60; n++)
        {
            var slots = DirtCourse.shuffledGrid(ref rng);
            equal(new HashSet<double>(slots.Select(s => s.phase)).Count, 4);
            require(new HashSet<double>(slots.Select(s => s.phase)).SetEquals(DirtCourse.startingGrid.Select(s => s.phase)));
            allocations.Add(string.Join(",", slots.Select(s => description(s.phase))));
            var racers = slots.Select(s => new Simulation(dirtTrack: true, dirtStartOffset: s.offset, dirtStartPhase: s.phase)).ToArray();
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    greater(hypot(racers[i].x - racers[j].x, racers[i].z - racers[j].z), 1.0);
                }
            }
        }
        greater((double)allocations.Count, 12);
        foreach (var lane in new[] { -0.65, 0, 0.65 })
        {
            foreach (var slot in DirtCourse.startingGrid)
            {
                var times = new List<double>();
                foreach (var fps in new[] { 30.0, 60.0, 120.0 })
                {
                    var rival = new DirtOpponent(slot, laneOffset: lane);
                    for (var k = 0; k < (int)(180 * fps); k++)
                    {
                        rival.advance(1 / fps, 1 / fps);
                        require(DirtCourse.projection(rival.simulation.x, rival.simulation.z).distance < DirtCourse.fenceOffset);
                        if (rival.race.finished) break;
                    }
                    require(rival.race.finished); equal(rival.race.laps.Length, 3);
                    times.Add(rival.race.elapsed);
                }
                near(minElement(times).Value, maxElement(times).Value, 0.001);
            }
        }
    }
    public void testBoostSteeringDuringAcceleration()
    {
        foreach (var fps in new[] { 30.0, 60.0, 120.0 })
        {
            foreach (var throttle in new[] { -1.0, 1.0 })
            {
                foreach (var turn in new[] { -1.0, 1.0 })
                {
                    foreach (var rolling in new[] { false, true })
                    {
                        var sim = new Simulation(dirtTrack: true); var input = new DriveInput();
                        input.throttle = throttle;
                        if (rolling)
                        {
                            for (var k = 0; k < (int)(fps * 0.4); k++) { sim.advance(input, 1 / fps); }
                        }
                        var heading = sim.heading;
                        input.boost = true; input.turn = turn;
                        for (var k = 0; k < (int)(fps * 0.2); k++)
                        {
                            var left = sim.leftSpeed; var right = sim.rightSpeed;
                            sim.advance(input, 1 / fps);
                            require(abs(sim.leftSpeed - left) <= 11.4 / fps + 1e-9);
                            require(abs(sim.rightSpeed - right) <= 11.4 / fps + 1e-9);
                        }
                        var angle = atan2(sin(sim.heading - heading), cos(sim.heading - heading));
                        require(angle * turn < -0.04);
                        require(sim.speed * throttle > 0.5);
                        input.brake = true;
                        sim.advance(input, 1 / fps);
                        equal(sim.leftSpeed, 0); equal(sim.rightSpeed, 0);
                    }
                }
            }
        }
    }
    public void testDirtOpponent()
    {
        var finishTimes = new List<double>();
        foreach (var fps in new[] { 30.0, 60.0, 120.0 })
        {
            var opponent = new DirtOpponent();
            var start = opponent.simulation;
            opponent.advance(double.NaN, 1);
            opponent.advance(1, double.PositiveInfinity);
            equal(opponent.simulation.distance, 0);
            greater(hypot(start.x - DirtCourse.point(0).x, start.z - DirtCourse.point(0).z), 0.7);
            var airborne = false;
            for (var k = 0; k < (int)(fps * 240); k++)
            {
                opponent.advance(1 / fps, 1 / fps);
                airborne = airborne || opponent.simulation.airborne;
                require(DirtCourse.projection(opponent.simulation.x, opponent.simulation.z).distance < DirtCourse.fenceOffset);
                if (opponent.race.finished) break;
            }
            require(opponent.race.finished); require(airborne);
            equal(opponent.race.laps.Length, 3); greater(opponent.simulation.speed, 0);
            near(sum(opponent.race.laps), opponent.race.elapsed, 1e-8);
            equal(opponent.playerPosition(new DirtRace()), 2);
            var end = opponent.simulation;
            opponent.advance(1 / fps, 1 / fps);
            greater(opponent.simulation.distance, end.distance);
            finishTimes.Add(opponent.race.elapsed);
            opponent = new DirtOpponent();
            equal(opponent.race.elapsed, 0); equal(opponent.simulation.x, start.x);
            equal(opponent.simulation.z, start.z);
        }
        near(maxElement(finishTimes).Value, minElement(finishTimes).Value, 0.001);
        print($"R2-D2 three-lap totals at 30/60/120 fps: {description(finishTimes)}");
    }
    public void testWiderTerrainAndFence()
    {
        near(DirtCourse.width * 2, 3.9, 1e-12);
        var center = DirtCourse.surfacePoints(0);
        var length = 0.0;
        for (var i = 0; i + 1 < center.Length; i++)
        {
            length = length + hypot(center[i + 1].x - center[i].x, center[i + 1].y - center[i].y);
        }
        // Regression baseline from the previous, narrower route.
        near(length, 136.25642662849137, 1e-8);
        near(DirtCourse.elevation(0), 0, 1e-12);
        greater(DirtCourse.elevation(0.69 * 2 * Math.PI), 1.25);
        greater(DirtCourse.elevation(0.17 * 2 * Math.PI), 0.6);
        greater(DirtCourse.elevation(0.40 * 2 * Math.PI), 0.85);
        for (var i = 0; i < 160; i++)
        {
            var phase = (double)i * 2 * Math.PI / 160;
            foreach (var edge in new[] { DirtCourse.width, DirtCourse.width + DirtCourse.bermWidth, DirtCourse.shoulderEdge, DirtCourse.terrainEdge })
            {
                foreach (var side in new[] { -1.0, 1.0 })
                {
                    near(DirtCourse.surfaceHeight(phase, side * (edge - 1e-7)),
                         DirtCourse.surfaceHeight(phase, side * (edge + 1e-7)), 1e-5);
                }
            }
        }
        static double cross(Double2 a, Double2 b) => a.x * b.y - a.y * b.x;
        foreach (var offset in new[] { -DirtCourse.terrainEdge, -DirtCourse.fenceOffset, -DirtCourse.width,
                                       DirtCourse.width, DirtCourse.fenceOffset, DirtCourse.terrainEdge })
        {
            var points = DirtCourse.surfacePoints(offset); var count = DirtCourse.sampleCount;
            equal(points[0], points[points.Length - 1]);
            for (var i = 0; i < count; i++)
            {
                var p = points[i];
                near(DirtCourse.projection(p.x, p.y).distance, abs(offset), 0.012);
                var r = points[i + 1] - p;
                if (i + 2 >= count) continue;
                for (var j = i + 2; j < count; j++)
                {
                    if (i == 0 && j == count - 1) continue;
                    var q = points[j]; var v = points[j + 1] - q; var denominator = cross(r, v);
                    if (abs(denominator) < 1e-12) continue;
                    var t = cross(q - p, v) / denominator; var u = cross(q - p, r) / denominator;
                    require(!(t > 1e-7 && t < 1 - 1e-7 && u > 1e-7 && u < 1 - 1e-7));
                }
            }
        }
    }
    public void testDirtRaceAndScores()
    {
        var race = new DirtRace();
        var start = DirtCourse.point(0);
        race.advance(start.x, start.z, 1);
        equal(race.elapsed, 0);
        race.countDown(3);
        // Repeated forward/backward finish crossings cannot manufacture a lap.
        for (var k = 0; k < 30; k++)
        {
            foreach (var angle in new[] { -0.02, 0.02, 0.0 })
            {
                var p = DirtCourse.point(angle); race.advance(p.x, p.z, 0.1);
            }
        }
        equal(race.laps.Length, 0);
        race = new DirtRace(); race.countDown(3);
        for (var i = 1; i <= 1080; i++)
        {
            var p = DirtCourse.point((double)i * 2 * Math.PI / 360);
            race.advance(p.x, p.z, 0.1);
        }
        require(race.finished); equal(race.laps.Length, 3);
        foreach (var lap in race.laps) { near(lap, 36, 0.003); }
        near(race.elapsed, 108, 0.003);
        race.advance(0, -5, 2); near(race.elapsed, 108, 0.003);
        var reverse = new DirtRace(); reverse.countDown(3);
        for (var i = 1; i <= 400; i++)
        {
            var p = DirtCourse.point(-(double)i * 0.02); reverse.advance(p.x, p.z, 0.1);
        }
        equal(reverse.laps.Length, 0); require(reverse.wrongWay);
        require(DirtCourse.contains(0, 0)); require(!DirtCourse.contains(20, 0));
        for (var i = 0; i < 360; i++)
        {
            var p = DirtCourse.point((double)i * Math.PI / 180);
            require(DirtCourse.contains(p.x, p.z, margin: Simulation.radius));
        }
        var sim = new Simulation(dirtTrack: true); var input = new DriveInput(); input.throttle = 1;
        for (var k = 0; k < 1200; k++) { sim.advance(input, 1.0 / 60); }
        require(DirtCourse.projection(sim.x, sim.z).distance < DirtCourse.fenceOffset); require(sim.contacting);
        sim.reset(); near(sim.z, -15, 1e-9); near(sim.heading, DirtCourse.heading(0), 1e-9);
        var edge = DirtCourse.point(0, offset: DirtCourse.width + 0.05);
        var edgeMove = DirtCourse.resolveMove(edge.x, edge.z, DirtCourse.heading(0));
        require(!edgeMove.contact);
        less(DirtCourse.traction(edge.x, edge.z), 0.5);
        var outside = DirtCourse.point(0, offset: DirtCourse.fenceOffset + 0.2);
        require(DirtCourse.resolveMove(outside.x, outside.z, DirtCourse.heading(0)).contact);
        var retreat = DirtCourse.point(0, offset: DirtCourse.width - 0.1);
        require(!DirtCourse.resolveMove(retreat.x, retreat.z, DirtCourse.heading(0)).contact);
        var driver = new Simulation(dirtTrack: true); var drivenRace = new DirtRace();
        drivenRace.countDown(3);
        var maxHeight = 0.0; var airborneFrames = 0;
        for (var k = 0; k < 12000; k++)
        {
            if (drivenRace.finished) break;
            var phase = DirtCourse.phase(driver.x, driver.z);
            var target = DirtCourse.point(phase + 0.05);
            var angle = atan2(target.x - driver.x, target.z - driver.z);
            var error = atan2(sin(angle - driver.heading), cos(angle - driver.heading));
            var command = new DriveInput(); command.throttle = max(0.15, 1 - abs(error) * 1.5); command.boost = abs(error) < 0.08;
            command.turn = -error * 3;
            driver.advance(command, 1.0 / 60);
            drivenRace.advance(driver.x, driver.z, 1.0 / 60);
            maxHeight = max(maxHeight, driver.groundY);
            if (driver.airborne) { airborneFrames += 1; }
        }
        require(drivenRace.finished); greater(maxHeight, 0.6); require(airborneFrames > 0);
        near(sum(drivenRace.laps), drivenRace.elapsed, 1e-8);
        // PORT: FileManager.temporaryDirectory/UUID/scores.json as a file-system path.
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString().ToUpperInvariant());
        var url = Path.Combine(directory, "scores.json");
        try
        {
            equal(DirtScores.load(url).Length, 0);
            var scores = new[] { new DirtScore(laps: new double[] { 40, 39, 38 }), new DirtScore(laps: new double[] { 36, 35, 34 }), new DirtScore(laps: new double[] { 0, 1, 2 }) };
            DirtScores.save(scores, url);
            var loaded = DirtScores.load(url);
            equal(loaded.Length, 2); near(loaded[0].total, 105, 1e-9);
        }
        catch (Exception error)
        {
            // PORT: Swift fatalError traps; the port prints Swift's message and exits like a trap (SIGTRAP, 133).
            Console.Out.Flush(); Console.Error.Write($"Fatal error: Score round trip failed: {error.Message}\n"); Environment.Exit(133);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
    public void testForwardReverseAndBrake()
    {
        var sim = new Simulation(); var input = new DriveInput();
        var start = sim.z;
        input.throttle = 1;
        for (var k = 0; k < 60; k++) { sim.advance(input, 1.0 / 60); }
        greater(sim.z, start + 0.8);
        input.brake = true;
        var z = sim.z;
        sim.advance(input, 1.0 / 60);
        equal(sim.speed, 0);
        equal(sim.z, z);
        input.brake = false; input.throttle = -1;
        for (var k = 0; k < 60; k++) { sim.advance(input, 1.0 / 60); }
        less(sim.z, z - 0.8);
    }
    public void testTurningInPlace()
    {
        var sim = new Simulation(); var input = new DriveInput();
        input.turn = 1;
        for (var k = 0; k < 30; k++) { sim.advance(input, 1.0 / 60); }
        less(sim.heading, -0.5);
        near(sim.x, 0, 1e-9);
        near(sim.z, -2.6, 1e-9);
    }
    public void testCollisionAndCheckpoint()
    {
        var sim = new Simulation(); var input = new DriveInput();
        input.throttle = 1; input.boost = true;
        for (var k = 0; k < 600; k++) { sim.advance(input, 1.0 / 60); }
        require(sim.contacting);
        require(sim.checkpoint >= 0 && sim.checkpoint <= CourseLayout.count);
        near(sim.z, 3.3 - 0.65 / 2 - Simulation.bodyHalfDepth - Simulation.collisionClearance, 0.02);
        require(Simulation.isFree(sim.x, sim.z, sim.heading));
        require(!Simulation.isFree(6, 0));
        require(!Simulation.isFree(-2.2, -0.4));
    }
    public void testPauseResetAndHeadLimits()
    {
        var sim = new Simulation(); var input = new DriveInput();
        input.throttle = 1; input.headYaw = 1; input.headPitch = -1;
        sim.paused = true;
        sim.advance(input, 1);
        equal(sim.elapsed, 0);
        sim.paused = false;
        for (var k = 0; k < 300; k++) { sim.advance(input, 1.0 / 60); }
        require(sim.yaw < 0);
        require(HeadClearance.isClear(sim.yaw, sim.pitch));
        require(sim.pitch > -45 * Math.PI / 180);
        sim.reset();
        equal(sim.distance, 0);
        equal(sim.yaw, 0);
        equal(sim.z, -2.6);
    }
    public void testHeadClearanceSweepAndEscape()
    {
        require(HeadClearance.isClear(0, 0));
        require(!HeadClearance.isClear(0, -Math.PI / 4));
        foreach (var direction in new[] { -1.0, 0.0, 1.0 })
        {
            var sim = new Simulation(); var input = new DriveInput();
            input.headYaw = direction; input.headPitch = -1;
            for (var k = 0; k < 400; k++)
            {
                sim.advance(input, 1.0 / 60);
                require(HeadClearance.isClear(sim.yaw, sim.pitch));
            }
            var stopped = sim.pitch;
            input.headYaw = -direction; input.headPitch = 1;
            for (var k = 0; k < 60; k++)
            {
                sim.advance(input, 1.0 / 60);
                require(HeadClearance.isClear(sim.yaw, sim.pitch));
            }
            greater(sim.pitch, stopped);
        }
        foreach (var direction in new[] { -1.0, 1.0 })
        {
            var sim = new Simulation(); var input = new DriveInput();
            input.turn = direction; input.headYaw = direction;
            for (var k = 0; k < 30; k++) { sim.advance(input, 1.0 / 60); }
            require(sim.heading * direction < 0);
            require(sim.yaw * direction < 0);
        }
    }
    public void testRandomCourseClearancesAndReset()
    {
        var layouts = new HashSet<string>();
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var sim = new Simulation(seed: seed);
            equal(sim.checkpoints.Length, 5);
            equal(sim.checkpoints, new Simulation(seed: seed).checkpoints);
            layouts.Add(string.Join(";", sim.checkpoints.Select(c => $"{description(c.x)},{description(c.z)}")));
            for (var i = 0; i < sim.checkpoints.Length; i++)
            {
                var p = sim.checkpoints[i];
                require(CourseLayout.isClear(p, sim.checkpoints.Take(i).ToArray()));
                require(Simulation.isFree(p.x, p.z));
                // Independently sample the full clearance perimeter, including
                // the maximum pulse, against walls, rectangles, and prior rings.
                for (var step = 0; step < 72; step++)
                {
                    var angle = (double)step * 2 * Math.PI / 72;
                    var r = CourseLayout.outerRadius + CourseLayout.clearance;
                    var x = p.x + cos(angle) * r; var z = p.z + sin(angle) * r;
                    require(abs(x) < Simulation.halfWidth && abs(z) < Simulation.halfDepth);
                    foreach (var box in Simulation.obstacles.Append(CourseLayout.launch))
                    {
                        require(abs(x - box.x) > box.width / 2 || abs(z - box.z) > box.depth / 2);
                    }
                    foreach (var other in sim.checkpoints.Take(i))
                    {
                        require(hypot(x - other.x, z - other.z) > CourseLayout.outerRadius);
                    }
                }
            }
        }
        equal(layouts.Count, 1000);
        // PORT: block scope; Swift lets these function-level names (`sim`, `seed`) coexist with the loop's.
        {
            var sim = new Simulation(seed: 42);
            var original = sim.checkpoints;
            sim.reset();
            require(!same(sim.checkpoints, original));
            equal(sim.checkpoint, 0);
            // Choose a reproducible first target in the unobstructed forward lane.
            var seed = Enumerable.Range(0, 1000).Select(s => (ulong)s).First(candidate =>
            {
                var p = new Simulation(seed: candidate).checkpoints[0];
                return abs(p.x) < 0.3 && p.z > -1 && p.z < 1;
            });
            sim = new Simulation(seed: seed);
            var input = new DriveInput(); input.throttle = 1;
            for (var k = 0; k < 240; k++) { sim.advance(input, 1.0 / 60); }
            require(sim.checkpoint >= 1);
        }
    }
    public void testCourseApproachRoutes()
    {
        // Direct approach needs no detour; an obstacle-crossing route must bend.
        equal(CourseRoute.path(new Checkpoint(0, -2.6), new Checkpoint(0, -1)).Length, 2);
        greater((double)CourseRoute.path(new Checkpoint(-4, -0.4), new Checkpoint(0, -0.4)).Length, 2);
        for (ulong seed = 0; seed < 30; seed++)
        {
            var course = new Simulation(seed: seed).checkpoints;
            var start = new Checkpoint(0, -2.6);
            foreach (var goal in course)
            {
                var route = CourseRoute.path(start, goal);
                equal(route[0], start); equal(route[route.Length - 1], goal);
                for (var k = 0; k + 1 < route.Length; k++)
                {
                    var a = route[k]; var b = route[k + 1];
                    for (var step = 0; step <= 100; step++)
                    {
                        var t = (double)step / 100;
                        require(Simulation.isFree(a.x + (b.x - a.x) * t, a.z + (b.z - a.z) * t));
                    }
                }
                var approach = route[route.Length - 2]; var yaw = CourseRoute.labelYaw(start, goal);
                var distance = hypot(approach.x - goal.x, approach.z - goal.z);
                near(sin(yaw), (approach.x - goal.x) / distance, 1e-9);
                near(cos(yaw), (approach.z - goal.z) / distance, 1e-9);
                start = goal;
            }
        }
    }
    public void testNeckConcentricDuringPan()
    {
        // Compare opposite points on the CAD's lower circular neck section.
        var left = new Double3(-0.1325, 0.295, -0.01886);
        var right = new Double3(0.1325, 0.295, -0.01886);
        foreach (var degrees in stride(-80.0, 80.0, 5.0))
        {
            var angle = degrees * Math.PI / 180;
            var a = HeadRig.headPoint(left, angle, 0);
            var b = HeadRig.headPoint(right, angle, 0);
            var center = (a + b) / 2;
            near(center.x, 0, 1e-9);
            near(center.y, 0.295, 1e-9);
            near(center.z, -0.01886, 1e-9);
            near(hypot(a.x, a.z + 0.01886), 0.1325, 1e-9);
            require(HeadClearance.isClear(angle, 0));
        }
        var neutral = HeadRig.headPoint(new Double3(0.2, 0.6, 0.3), 0, 0);
        near(neutral.x, 0.2, 1e-9);
        near(neutral.y, 0.6, 1e-9);
        near(neutral.z, 0.3, 1e-9);
    }
    public void testTrackTravelAndContact()
    {
        var phase = TrackLoop.straight / 2;
        var bottom = TrackLoop.sample(phase);
        near(bottom.y - 0.0055, 0, 1e-9); // outer rib touches the floor
        foreach (var throttle in new[] { -1.0, 1.0 })
        {
            var sim = new Simulation(); var input = new DriveInput();
            input.throttle = throttle;
            sim.advance(input, 0.1);
            require(sim.leftTravel * throttle > 0 && sim.rightTravel * throttle > 0);
            var tread = TrackLoop.sample(phase + sim.leftTravel);
            // Ground-facing rubber travels opposite the chassis.
            require((tread.z - bottom.z) * throttle < 0);
            near(tread.z - bottom.z + sim.z + 2.6, 0, 1e-9);
            input.brake = true;
            var stopped = sim.leftTravel;
            sim.advance(input, 0.1);
            equal(sim.leftTravel, stopped);
            sim.reset(); equal(sim.leftTravel, 0); equal(sim.rightTravel, 0);
        }
        foreach (var turn in new[] { -1.0, 1.0 })
        {
            var sim = new Simulation(); var input = new DriveInput();
            input.turn = turn;
            sim.advance(input, 0.1);
            require(sim.leftTravel * turn > 0 && sim.rightTravel * turn < 0);
            require((TrackLoop.sample(phase + sim.leftTravel).z - bottom.z) * turn < 0);
            require((TrackLoop.sample(phase + sim.rightTravel).z - bottom.z) * turn > 0);
        }
        foreach (var boundary in new[] { 0, TrackLoop.straight, TrackLoop.straight + Math.PI * TrackLoop.radius,
                                         2 * TrackLoop.straight + Math.PI * TrackLoop.radius, TrackLoop.circumference })
        {
            var a = TrackLoop.sample(boundary - 1e-7); var b = TrackLoop.sample(boundary + 1e-7);
            near(a.y, b.y, 3e-7); near(a.z, b.z, 3e-7);
        }
        foreach (var distance in new[] { -10.0, -0.1, 0.0, 0.1, 10.0 })
        {
            var a = TrackLoop.sample(distance); var b = TrackLoop.sample(distance + TrackLoop.circumference);
            near(a.y, b.y, 1e-9); near(a.z, b.z, 1e-9);
        }
    }
    public void testFrameRateIndependentAndStallBounded()
    {
        var a = new Simulation(); var b = new Simulation(); var input = new DriveInput();
        input.throttle = 1; input.turn = 0.2;
        for (var k = 0; k < 30; k++) { a.advance(input, 1.0 / 30); }
        for (var k = 0; k < 120; k++) { b.advance(input, 1.0 / 120); }
        near(a.x, b.x, 0.0001);
        near(a.z, b.z, 0.0001);
        var elapsed = a.elapsed;
        a.advance(input, 30);
        near(a.elapsed - elapsed, 0.1, 1e-9);
    }
}

/// Swift `@main struct CheckRunner`.
public static class CheckRunner
{
    public static int Main(string[] args)
    {
        // Godot-port only: the managed Darwin hypot used on Windows against Darwin libm (PortableMathChecks.cs).
        if (args.Contains("--portable-math")) return PortableMathChecks.Run(args);
        // Godot-port only: the platform's libm results for the functions the simulation calls (LibmProbe.cs).
        if (args.Contains("--libm-probe")) return LibmProbe.Run(args);
        var checks = new SimulationTests();
        checks.testTownSpeedAndGaze();
        checks.testStormLanding();
        checks.testSandstorm();
        checks.testCooldownLap();
        checks.testDesertTerrain();
        checks.testSandDeformation();
        checks.testNavigationMap();
        if (args.Contains("--dunes-only")) return 0;
        checks.testCityEscape();
        if (args.Contains("--escape-only")) return 0;
        checks.testPoweredRolling();
        checks.testBrakingAuthority();
        checks.testDrivingAssists();
        checks.testAssistedCornering();
        if (args.Contains("--assists-only")) return 0;
        checks.testExactCourseProjection();
        checks.testSandboxFootprint();
        checks.testPlayableFootprints();
        checks.testOptionalRobotCollisions();
        checks.testCollisionRecovery();
        checks.testRobotCollisionImpulses();
        checks.testCollisionPileupsAndClock();
        checks.testCoupledRacePhysics();
        checks.testStaggeredGrid();
        checks.testFourRacerGrid();
        checks.testRacePerformance();
        checks.testBoostSteeringDuringAcceleration();
        checks.testWiderTerrainAndFence();
        checks.testServiceAccess();
        checks.testInfieldObstacles();
        checks.testDirtOpponent();
        checks.testDirtRaceAndScores();
        checks.testForwardReverseAndBrake();
        checks.testTurningInPlace();
        checks.testCollisionAndCheckpoint();
        checks.testPauseResetAndHeadLimits();
        checks.testFrameRateIndependentAndStallBounded();
        checks.testHeadClearanceSweepAndEscape();
        checks.testTrackTravelAndContact();
        checks.testNeckConcentricDuringPan();
        checks.testRandomCourseClearancesAndReset();
        checks.testCourseApproachRoutes();
        Console.WriteLine("PASS: simulation checks (playable characters, drive/brake, steering, collision/course, pause/head/reset, time integration)");
        return 0;
    }
}
