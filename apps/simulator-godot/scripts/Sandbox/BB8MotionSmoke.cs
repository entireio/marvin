// Port of Sources/MarvinSimulator/BB8MotionSmoke.swift (an AppController extension).
// `--bb8-motion-smoke-test DIR`: BB-8 driven in the sandbox and on the race track with a fixed world-space camera;
// sandbox-N.png / race-N.png every 8 frames and motion.json, as on macOS.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--bb8-motion-smoke-test")]
    public static async Task RunBB8MotionSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkBB8RenderedMotion(at: dir);
        print($"BB-8 rendered motion: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }

    public bool checkBB8RenderedMotion(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                window.windowNumber, null, "b", "b", false, 0);
            mainMenu.keyDown(@event);
            var report = new List<object>();
            var passed = true;
            foreach (var dirt in new[] { false, true })
            {
                if (dirt) { startDirtTrack(); dirtIntro = null; race.countDown(dt: 3); }
                else { startSandbox(); }
                // Fix the camera in world space; following the robot can mask
                // a reversed roll. Inspect the source lens and rendered nodes.
                simulation = new Simulation(seed: 0, dirtTrack: dirt, dirtStartPhase: 0, character: RacePerformance.Character.bb8);
                updatePlayerModel();
                var origin = float3(bb8.root.simdPosition);
                world.camera.position = new SCNVector3(origin.x + 1.2, origin.y + 0.6, origin.z + 0.75);
                world.camera.look(at: new SCNVector3(origin.x, origin.y + 0.27, origin.z + 0.3));
                Float3? previousContact = null, contactInBall = null, previousCenter = null;
                for (int frame = 0; frame <= 48; frame++)
                {
                    if (frame > 0)
                    {
                        var input = new DriveInput(); input.throttle = 0.35; input.turn = 0.2;
                        simulation.advance(input, dt: 1.0 / 60); updatePlayerModel();
                    }
                    // PORT: Swift snapshots every frame and writes every eighth; the snapshot does not change the
                    // checked state, so only the written frames are rendered.
                    var center = float3(bb8.ball.presentation.simdWorldPosition);
                    var slip = 0f;
                    if (previousContact is Float3 pc && contactInBall is Float3 cib && previousCenter is Float3 pce && !simulation.airborne)
                    {
                        var moved = float3(bb8.ball.presentation.simdConvertPosition(simd(cib), to: null)) - pc;
                        slip = hypot(moved.x, moved.z);
                        var travel = hypot(center.x - pce.x, center.z - pce.z);
                        passed = passed && slip < max(0.00001f, travel * 0.03f);
                    }
                    previousCenter = center;
                    previousContact = center + new Float3(0, -(float)bb8.ballRadius, 0);
                    contactInBall = float3(bb8.ball.presentation.simdConvertPositionFrom(simd(previousContact.Value), from: null));
                    var orientation = quat(bb8.ball.simdWorldOrientation);
                    var rendered = quat(bb8.ball.presentation.simdWorldOrientation);
                    var eye = float3(bb8.head.simdConvertVector(simd(new Float3(0, 0, 1)), to: null));
                    var forward = new Float3((float)sin(simulation.heading), 0, (float)cos(simulation.heading));
                    var match = abs(Simd.dot(orientation.vector, rendered.vector));
                    passed = passed && match > 0.999f && Simd.dot(eye, forward) > 0.999f;
                    if (frame % 8 == 0)
                    {
                        saveSnapshot($"{(dirt ? "race" : "sandbox")}-{frame}.png", directory);
                        report.Add(new Dictionary<string, object> { ["dirt"] = dirt, ["frame"] = frame, ["x"] = simulation.x, ["z"] = simulation.z,
                            ["contactSlip"] = slip, ["modelVsPresentation"] = match, ["eyeForward"] = Simd.dot(eye, forward) });
                    }
                }
            }
            File.WriteAllText(Path.Combine(directory, "motion.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            return passed;
        }
        catch (Exception error) { print($"BB-8 motion check failed: {error}"); return false; }
    }
}
