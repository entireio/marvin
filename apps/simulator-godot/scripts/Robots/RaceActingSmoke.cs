// Port of Sources/MarvinSimulator/RaceActingSmoke.swift (an AppController extension).
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    /// Exercise actual articulated nodes as well as the pure attention logic.
    /// Captures neutral, anticipated bend and passing poses in the same lineup.
    public bool checkRaceActing(string at)
    {
        var directory = at;
        var offsets = new[] { -1.35, -0.45, 0.35, 1.25 };
        var states = offsets.Select(o => new Simulation(dirtTrack: true, dirtStartOffset: o, dirtStartPhase: 0)).ToArray();
        var characters = new[] { RacePerformance.Character.marvin, RacePerformance.Character.r2d2, RacePerformance.Character.bb8, RacePerformance.Character.wallE };
        var phase = Enumerable.Range(0, 360).Select(i => (double)i * 2 * Math.PI / 360).First(p =>
        {
            var delta = DirtCourse.heading(p + 0.172) - DirtCourse.heading(p);
            return atan2(sin(delta), cos(delta)) > 0.5;
        });
        var valid = true;
        for (int mode = 0; mode < 3; mode++)
        {
            robot.update(states[0]); r2d2.update(states[1]); bb8.update(states[2]); wallE.update(states[3]);
            var originalBall = quat(bb8.ball.simdOrientation);
            var originalArms = wallE.arms.Select(a => a.position).ToArray();
            var poses = new List<RacePerformance.Pose>();
            foreach (var character in characters)
            {
                var performer = new RacePerformance(character);
                if (mode > 0)
                {
                    var p = DirtCourse.point(mode == 1 ? phase : 0);
                    var heading = DirtCourse.heading(mode == 1 ? phase : 0);
                    for (int frame = 1; frame <= 40; frame++)
                    {
                        var time = (double)frame / 60;
                        var actor = new RacePerformance.Actor(x: p.x, z: p.z, heading: heading, speed: 6, elapsed: time);
                        var along = 2.4 - time * 3;
                        var peer = new RacePerformance.Actor(x: p.x + cos(heading) + sin(heading) * along, z: p.z - sin(heading) + cos(heading) * along, heading: heading, speed: 3, elapsed: time);
                        performer.update(index: 0, actors: mode == 1 ? new[] { actor } : new[] { actor, peer });
                    }
                    valid = valid && performer.pose.yaw > 0.2;
                    if (mode == 2) { valid = valid && performer.lookingAt == 1; }
                }
                poses.Add(performer.pose);
            }
            robot.applyExpression(poses[0], state: states[0]); r2d2.applyExpression(poses[1]);
            bb8.applyExpression(poses[2], heading: states[2].heading);
            wallE.applyExpression(poses[3], heading: states[3].heading);
            valid = valid && abs((double)robot.yawNode.eulerAngles.y - poses[0].yaw) < 1e-6
                && abs((double)r2d2.head.rotation.w - poses[1].yaw) < 1e-6
                && abs((double)bb8.head.eulerAngles.y - poses[2].yaw) < 1e-6
                && abs((double)wallE.head.eulerAngles.y - poses[3].yaw) < 1e-6
                && abs(Simd.dot(originalBall.vector, quat(bb8.ball.simdOrientation).vector)) > 0.999999f
                && wallE.arms.Count == 2;
            foreach (var (arm, anchor) in wallE.arms.Zip(originalArms))
            {
                valid = valid && arm.position.x == anchor.x && arm.position.y == anchor.y && arm.position.z == anchor.z;
            }
            var name = new[] { "neutral", "curve", "passing" }[mode];
            var start = DirtCourse.point(0); var startHeading = DirtCourse.heading(0);
            world.camera.position = new SCNVector3(start.x + sin(startHeading) * 4.5 + cos(startHeading) * 0.7, 1.7, start.z + cos(startHeading) * 4.5 - sin(startHeading) * 0.7);
            world.camera.look(at: new SCNVector3(start.x, 0.4, start.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveSnapshot($"acting-{name}.png", directory);
        }
        // Restore real race state; acting checks must not contaminate dirt or
        // the rolling body's displacement history before the driving checks.
        robot.update(simulation); r2d2.update(opponent.simulation);
        bb8.update(bb8Opponent.simulation); wallE.update(wallEOpponent.simulation);
        updateOpponents(); updateCamera(snap: true);
        return valid;
    }
}
