// Port of Sources/MarvinSimulator/RobotCollisionSmoke.swift (an AppController extension).
using System.IO;
using System.Linq;
using Marvin.Core;

namespace Marvin;

public partial class AppController
{
    public bool checkSandboxContact(string at)
    {
        var directory = at;
        var contact = new Simulation(seed: 0); var input = new DriveInput(); input.throttle = 1;
        for (int i = 0; i < 600; i++) { contact.advance(input, dt: 1.0 / 60); }
        var face = 3.3 - 0.65 / 2;
        var clearance = face - contact.z - Simulation.bodyHalfDepth;
        robot.update(contact);
        world.camera.position = new SCNVector3(1.4, 1.2, contact.z - 1.1);
        world.camera.look(at: new SCNVector3(0, 0.28, contact.z + 0.15), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        saveSnapshot("sandbox-contact.png", directory);
        robot.update(simulation); updateCamera(snap: true);
        return contact.contacting && clearance >= Simulation.collisionClearance - 1e-8 && clearance < 0.025
            && Simulation.isFree(x: contact.x, z: contact.z, heading: contact.heading);
    }

    public bool checkRobotContacts(string at)
    {
        var directory = at;
        // Deliberately crowded setup exercises separation through the same race
        // controller used by interactive play, then renders the resulting poses.
        var player = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: 0, dirtStartPhase: -0.055);
        var clock = new DirtRace(startPhase: -0.055); var physics = new DirtRacePhysics();
        var rivals = new[] { new DirtOpponent(slot: (phase: -0.055, offset: 0.35)),
                             new DirtOpponent(slot: (phase: -0.14, offset: -0.72), laneOffset: 0),
                             new DirtOpponent(slot: (phase: -0.14, offset: 0.72), laneOffset: -0.65) };
        clock.countDown(dt: 3);
        var input = new DriveInput(); input.throttle = 1;
        for (int i = 0; i < 30; i++) { physics.advance(input, ref player, ref clock, rivals, dt: 1.0 / 60, raceDT: 1.0 / 60); }
        var states = new[] { player }.Concat(rivals.Select(r => r.simulation)).ToArray();
        var bodies = states.Select((state, i) =>
            new RobotCollisions.Body(position: new Double3(state.x, state.groundY, state.z), velocity: state.velocity, heading: state.heading, angularVelocity: state.angularVelocity, profile: RobotCollisions.profiles[i])).ToArray();
        var passed = physics.contactCount > 0 && states.All(s => s.robotDynamics);
        for (int i = 0; i < bodies.Length; i++)
        {
            for (int j = i + 1; j < bodies.Length; j++)
            {
                passed = passed && (RobotCollisions.contact(bodies[i], bodies[j])?.penetration ?? 0) < 0.003;
            }
        }
        robot.update(player); r2d2.update(rivals[0].simulation);
        bb8.update(rivals[1].simulation); wallE.update(rivals[2].simulation);
        var heading = player.heading;
        world.camera.position = new SCNVector3(player.x + Swift.sin(heading) * 3.8 + Swift.cos(heading) * 1.4, player.groundY + 1.8, player.z + Swift.cos(heading) * 3.8 - Swift.sin(heading) * 1.4);
        world.camera.look(at: new SCNVector3(player.x, player.groundY + 0.35, player.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        saveSnapshot("robot-contact.png", directory);
        var before = states.Select(s => new Double3(s.x, s.groundY, s.z)).ToArray();
        var velocities = states.Select(s => s.velocity).ToArray();
        player.paused = true;
        physics.advance(input, ref player, ref clock, rivals, dt: 0.1, raceDT: 0.1);
        var after = new[] { player }.Concat(rivals.Select(r => r.simulation)).ToArray();
        passed = passed && before.SequenceEqual(after.Select(s => new Double3(s.x, s.groundY, s.z)));
        passed = passed && velocities.SequenceEqual(after.Select(s => s.velocity));
        robot.update(simulation); updateOpponents(); updateCamera(snap: true);
        return passed;
    }
}
