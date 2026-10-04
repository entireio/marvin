using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/DrivingAssistChecks.swift.
public partial struct SimulationTests
{
    // PORT: Swift declares this struct inside testAssistedCornering; C# has no local types.
    private struct CorneringResult { public double error, peak; public int contacts; public double progress; }

    public void testAssistedCornering()
    {
        CorneringResult drive(DirtDrivingAssists assists, double phase, bool handsOff = false, bool manualBrakes = true)
        {
            var state = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: phase);
            var race = new DirtRace(startPhase: phase); var physics = new DirtRacePhysics();
            var rivals = Enumerable.Range(0, 3).Select(k => new DirtOpponent((phase: phase - 1 - (double)k * 0.15, offset: 0.0))).ToArray();
            race.countDown(3);
            var result = new CorneringResult(); var input = new DriveInput();
            for (var frame = 0; frame < 360; frame++)
            {
                // Independent centerline driver with 150 ms reaction time.
                // Never consult the assist's line or corner-speed recommendation.
                if (frame % 9 == 0)
                {
                    var at = DirtCourse.phase(state.x, state.z); var target = DirtCourse.point(at + 0.075);
                    var desired = atan2(target.x - state.x, target.z - state.z);
                    var error = atan2(sin(desired - state.heading), cos(desired - state.heading));
                    var delta = DirtCourse.heading(at + 0.15) - DirtCourse.heading(at);
                    var bend = abs(atan2(sin(delta), cos(delta)));
                    input.throttle = 1;
                    if (!handsOff)
                    {
                        input.turn = abs(error) < 0.08 ? 0.0 : error > 0 ? -1.0 : 1.0;
                        input.brake = manualBrakes && state.groundSpeed > (bend > 0.4 ? 3.0 : 5.5);
                    }
                }
                physics.advance(input, ref state, ref race, rivals, 1.0 / 60, 1.0 / 60,
                                robotCollisionsEnabled: false, assists: assists);
                var distance = DirtCourse.projection(state.x, state.z).distance;
                result.error += distance / 360; result.peak = max(result.peak, distance);
                if (state.contacting) { result.contacts += 1; }
            }
            result.progress = race.progress - phase;
            return result;
        }
        var settings = new[] { DirtDrivingAssists.off, new DirtDrivingAssists(braking: false),
                               new DirtDrivingAssists(steering: false), new DirtDrivingAssists() };
        for (var i = 0; i < 12; i++)
        {
            var phase = (double)i * 2 * Math.PI / 12;
            var runs = settings.Select(setting => drive(setting, phase)).ToArray();
            foreach (var run in runs)
            {
                require(run.contacts == 0 && run.peak < DirtCourse.width);
                require(run.progress > 0.5);
            }
            print(format("Corner %.3f: manual %.3f, assisted %.3f; no fence contacts", phase, runs[0].error, runs[3].error));
        }
        // Centerline error is descriptive, not the objective: the driver must
        // retain room to choose a different line. Test missed braking instead.
        var unbrakedContacts = 0; var helpedContacts = 0; var unbrakedPeak = 0.0; var helpedPeak = 0.0;
        for (var i = 0; i < 12; i++)
        {
            var phase = (double)i * 2 * Math.PI / 12;
            var a = drive(DirtDrivingAssists.off, phase, manualBrakes: false);
            var b = drive(new DirtDrivingAssists(), phase, manualBrakes: false);
            unbrakedContacts += a.contacts; helpedContacts += b.contacts;
            unbrakedPeak += a.peak; helpedPeak += b.peak;
        }
        print($"Missed braking: contacts {unbrakedContacts} -> {helpedContacts}, summed peak error {description(unbrakedPeak)} -> {description(helpedPeak)}");
        require(helpedContacts < unbrakedContacts && helpedPeak < unbrakedPeak);
        var manual = drive(DirtDrivingAssists.off, 2.4, handsOff: true);
        var handsOff = drive(new DirtDrivingAssists(braking: false), 2.4, handsOff: true);
        near(manual.error, handsOff.error, 1e-10);
        near(manual.progress, handsOff.progress, 1e-10);
        equal(manual.contacts, handsOff.contacts);
        // Active assists re-evaluate at the physics clock, not the renderer.
        Simulation scripted(double fps, DirtDrivingAssists assists)
        {
            var state = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: 2.4);
            var race = new DirtRace(startPhase: 2.4); var physics = new DirtRacePhysics();
            var rivals = Enumerable.Range(0, 3).Select(k => new DirtOpponent((phase: 1.0 - (double)k * 0.15, offset: 0.0))).ToArray();
            race.countDown(3);
            for (var frame = 0; frame < (int)(fps * 4); frame++)
            {
                var input = new DriveInput(); input.throttle = 1; input.turn = -0.6;
                input.brake = frame >= (int)(fps * 2);
                physics.advance(input, ref state, ref race, rivals, 1 / fps, 1 / fps,
                                robotCollisionsEnabled: false, assists: assists);
            }
            return state;
        }
        var slow = scripted(30, new DirtDrivingAssists());
        var fast = scripted(120, new DirtDrivingAssists());
        var unassisted = scripted(60, DirtDrivingAssists.off);
        near(slow.x, fast.x, 1e-8); near(slow.z, fast.z, 1e-8);
        near(slow.heading, fast.heading, 1e-8);
        near(slow.speed, 0, 0.01);
        require(hypot(fast.x - unassisted.x, fast.z - unassisted.z) > 0.1);
        print("PASS: predictive braking reduces missed-braking contacts; steering-only hands-off behavior is unchanged");
    }

    public void testBrakingAuthority()
    {
        (double distance, double heading, double speed) stop(bool assisted, double turn = 0)
        {
            var state = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: -0.15);
            var race = new DirtRace(startPhase: -0.15); var physics = new DirtRacePhysics();
            var rivals = Enumerable.Range(0, 3).Select(k => new DirtOpponent((phase: -1 - (double)k * 0.15, offset: 0.0))).ToArray();
            race.countDown(3);
            var input = new DriveInput(); input.throttle = 1; input.boost = true;
            for (var k = 0; k < 30; k++)
            {
                physics.advance(input, ref state, ref race, rivals, 1.0 / 60, 1.0 / 60, robotCollisionsEnabled: false);
            }
            var initial = state;
            require(state.groundSpeed > 4);
            input.brake = true; input.turn = turn;
            for (var k = 0; k < 60; k++)
            {
                var before = state.groundSpeed;
                physics.advance(input, ref state, ref race, rivals, 1.0 / 60, 1.0 / 60,
                                robotCollisionsEnabled: false, assists: new DirtDrivingAssists(steering: false, braking: assisted));
                require(state.groundSpeed <= before + 0.00001);
            }
            var delta = state.heading - initial.heading;
            return (hypot(state.x - initial.x, state.z - initial.z), atan2(sin(delta), cos(delta)), state.groundSpeed);
        }
        var manual = stop(assisted: false); var assisted = stop(assisted: true);
        require(assisted.distance <= manual.distance + 0.001);
        near(assisted.speed, 0, 0.001);
        foreach (var turn in new[] { -0.5, 0.5 })
        {
            var result = stop(assisted: true, turn: turn);
            require(result.heading * turn < -0.01);
            near(result.speed, 0, 0.001);
        }
        print("PASS: brake help preserves stopping strength and steering in both directions");
    }

    public void testDrivingAssists()
    {
        var assists = new DirtDrivingAssists();
        // Check the entire reference line, including interpolation at the seam.
        for (var i = 0; i < 768; i++)
        {
            var p = DirtRacingLine.point((double)i * 2 * Math.PI / 768);
            require(DirtCourse.contains(p.x, p.z, margin: 0.9));
        }
        var start = DirtRacingLine.point(0); var end = DirtRacingLine.point(2 * Math.PI);
        near(start.x, end.x, 1e-9); near(start.z, end.z, 1e-9);
        var changed = 0;
        foreach (var phase in new[] { 0.6, 1.2, 1.8, 2.4, 3.0, 3.6, 4.2, 4.8, 5.4 })
        {
            var state = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: phase);
            var input = new DriveInput(); input.throttle = 1;
            for (var k = 0; k < 8; k++) { state.advance(input, 0.05); }
            // No steering input stays neutral; corner braking may reduce throttle.
            var idle = assists.apply(input, state);
            equal(idle.turn, 0); equal(idle.brake, false); require(idle.throttle > 0 && idle.throttle <= 1);
            foreach (var turn in new[] { -1.0, 1.0 })
            {
                input.turn = turn; input.brake = true;
                var manual = DirtDrivingAssists.off.apply(input, state);
                equal(manual.turn, input.turn); equal(manual.brake, input.brake);
                var brakeOnly = new DirtDrivingAssists(steering: false).apply(input, state);
                equal(brakeOnly.turn, turn);
                var assisted = assists.apply(input, state);
                require(assisted.turn * turn >= 0.8 && abs(assisted.turn) <= 1);
                if (abs(assisted.turn - turn) > 0.05) { changed += 1; }
                var steerOnly = new DirtDrivingAssists(braking: false).apply(input, state);
                equal(steerOnly.turn, assisted.turn);
                // Braking must reduce speed without the manual instant wheel stop.
                var braking = state; var manualBraking = state;
                braking.advance(brakeOnly, 0.02);
                manualBraking.advance(manual, 0.02);
                if (!state.airborne && state.speed > 0.5)
                {
                    less(braking.speed, state.speed);
                    require(braking.speed >= manualBraking.speed);
                }
            }
        }
        require(changed > 0);
        // Sweep through the reference turn's sign change. A held steering
        // key must not jump back to full strength as the curve straightens.
        foreach (var direction in new[] { -1.0, 1.0 })
        {
            double? previous = null;
            for (var i = -200; i <= 200; i++)
            {
                var state = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: (double)i * 0.005);
                var input = new DriveInput(); input.throttle = 1;
                state.advance(input, 0.1); input.turn = direction;
                var output = assists.apply(input, state).turn;
                if (previous is double prior) { require(abs(output - prior) < 0.04); }
                require(output * direction > 0);
                previous = output;
            }
        }
        // PORT: block scope; Swift lets this function-level `input` coexist with the loops' `input`.
        {
            // Sandbox controls stay exact, regardless of the two settings.
            var sandbox = new Simulation(seed: 0); var input = new DriveInput();
            input.throttle = 1; input.turn = 0.6; input.brake = true;
            var untouched = assists.apply(input, sandbox);
            sandbox.advance(untouched, 0.1);
            equal(untouched.turn, input.turn); equal(sandbox.speed, 0);
            var reverse = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: 2.4);
            var reverseInput = new DriveInput(); reverseInput.throttle = -1;
            reverse.advance(reverseInput, 0.1); reverseInput.turn = 0.6; reverseInput.brake = true;
            equal(assists.apply(reverseInput, reverse).turn, reverseInput.turn);
            reverse.paused = true;
            var paused = reverse;
            reverse.advance(assists.apply(reverseInput, reverse), 0.1);
            equal(reverse.distance, paused.distance); equal(reverse.heading, paused.heading);
        }
        print("PASS: assist input gates, independent switches, line bounds, brake deceleration");
    }
}
