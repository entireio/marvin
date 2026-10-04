// Port of Sources/MarvinSimulator/PoweredRollingSmoke.swift (an AppController extension).
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    public bool checkPoweredRolling()
    {
        void update(Simulation state)
        {
            foreach (var character in RacePerformance.CharacterAllCases) { updateModel(character, state: state); }
        }
        bool spins(Simulation from, Simulation to)
        {
            update(new Simulation(seed: 0));
            update(from);
            var marvin = robot.wheels.Select(w => w.node.eulerAngles.x).ToArray();
            var r2 = r2d2.wheels.Select(w => w.node.eulerAngles.x).ToArray();
            var sphere = quat(bb8.ball.simdOrientation);
            var links = wallE.links.Select(l => float3(l.node.simdPosition)).ToArray();
            update(to);
            return robot.wheels.Zip(marvin).All(p => abs(p.First.node.eulerAngles.x - p.Second) > 1e-5)
                && r2d2.wheels.Zip(r2).All(p => abs(p.First.node.eulerAngles.x - p.Second) > 1e-5)
                && abs(Simd.dot(sphere.vector, quat(bb8.ball.simdOrientation).vector)) < 0.99999f
                && wallE.links.Zip(links).All(p => Simd.distance(float3(p.First.node.simdPosition), p.Second) > 1e-5f);
        }
        var input = new DriveInput(); input.throttle = 1;
        var blocked = new Simulation(seed: 0);
        for (int i = 0; i < 600; i++) { blocked.advance(input, dt: 1.0 / 60); }
        var before = blocked;
        blocked.advance(input, dt: 0.1);
        if (!(blocked.contacting && blocked.x == before.x && blocked.z == before.z
              && spins(from: before, to: blocked))) { return false; }
        var fence = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: 0.6); var fencePassed = false;
        for (int i = 0; i < 600; i++)
        {
            var prior = fence; fence.advance(input, dt: 1.0 / 60);
            if (prior.contacting && fence.contacting)
            {
                fencePassed = spins(from: prior, to: fence); break;
            }
        }
        var jumper = new DirtOpponent(); var jumpPassed = false;
        for (int i = 0; i < 600; i++)
        {
            var prior = jumper.simulation;
            jumper.advance(dt: 1.0 / 60, raceDT: 1.0 / 60);
            if (prior.airborne && jumper.simulation.airborne)
            {
                jumpPassed = spins(from: prior, to: jumper.simulation); break;
            }
        }
        update(new Simulation(seed: 0));
        print($"All robot running gear: sandbox true, fence {(fencePassed ? "true" : "false")}, jump {(jumpPassed ? "true" : "false")}");
        return fencePassed && jumpPassed;
    }
}
