using System;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;

namespace Marvin;

public partial class AppController
{
    /// `--race-world-views DIR`: the race-world cameras of the macOS `--town-smoke-test` (TownSmoke.swift), same names
    /// and positions, for checking the service embankment, city-exit apron and walls against reference/mac/town, plus
    /// race-exit-ramp and CityEscapeSmoke.swift's escape-gate-closed view of the closed city gate.
    /// PORT: a Godot-only QA mode. MARVIN_BUILD_OFF_MAIN=1 builds the DirtWorld on a pool thread first, as
    /// LevelLoading.swift does on a global queue, and hands it to the app as its cachedDirtWorld.
    [GameMode("--race-world-views")]
    public static async Task RunRaceWorldViews(string dir, SceneTree tree)
    {
        bool passed = false;
        try
        {
            var app = await launchSmoke(tree, dir);
            if (System.Environment.GetEnvironmentVariable("MARVIN_BUILD_OFF_MAIN") == "1")
            {
                var started = DateTime.Now;
                var task = Task.Run(() => new DirtWorld(progress: (fraction, label) => GD.Print($"loading {fraction:0.000} {label}")));
                // Keep the main loop running: Godot answers some of the builder's RenderingServer calls on the main thread.
                while (!task.IsCompleted) { await frame(tree); }
                app.cachedDirtWorld = task.Result;
                GD.Print($"DirtWorld built off the main thread in {(DateTime.Now - started).TotalSeconds:0.0} s");
            }
            app.startDirtTrack(); app.dirtIntro = null; app.race.countDown(dt: 3);
            app.raceHUD.isHidden = true;
            // TownSmoke.swift races for 90 frames before its captures.
            for (int frame = 0; frame < 90; frame++)
            {
                app.advanceRacePhysics(DirtOpponent.driveInput(app.simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                app.updateOpponents(); app.updateRaceWorld(dt: 1.0 / 60);
                app.dirtWorld.town.update(dt: 1.0 / 60, camera: new SCNVector3(0, 5, -12), player: new Double2(app.simulation.x, app.simulation.z), robots: app.robotBodies(),
                    visible: node => app.view.isNode(node, insideFrustumOf: app.world.camera), shadowCamera: app.world.camera, viewportAspect: (double)(app.view.bounds.width / app.view.bounds.height));
            }
            var exitPoint = CityExit.point(0, 4.5);
            var gate = CityExit.point(0, -0.5);
            // CityEscapeSmoke.swift exitShot: the closed city gate and its apron.
            var escapeEye = CityExit.point(5, 7); var escapeTarget = CityExit.point(0, 0);
            var cameras = new (string, SCNVector3, SCNVector3)[]
            {
                ("town-overview", new SCNVector3(0, 46, -52), new SCNVector3(0, 0, 0)),
                ("town-ramp-ground", new SCNVector3(-7.5, 0.58, -8.9), new SCNVector3(-7.5, 0.26, -12.3)),
                ("town-ramp-side", new SCNVector3(-10.0, 0.85, -10.0), new SCNVector3(-7.5, 0.20, -11.5)),
                ("town-service-access", new SCNVector3(-2, 5.8, -18), new SCNVector3(-7.5, 0.15, -10.8)),
                ("town-repair", new SCNVector3(-3.6, 2.9, -13.0), new SCNVector3(-7.5, 0.95, -7.5)),
                ("race-exit-ramp", new SCNVector3(exitPoint.x, 1.6, exitPoint.y), new SCNVector3(gate.x, 0.3, gate.y)),
                ("escape-gate-closed", new SCNVector3(escapeEye.x, 3.2, escapeEye.y), new SCNVector3(escapeTarget.x, 0.55, escapeTarget.y)),
            };
            foreach (var (name, eye, target) in cameras)
            {
                app.world.camera.position = eye;
                app.world.camera.look(at: target, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                _ = app.view.prepare(app.dirtWorld.scene, shouldAbortBlock: null);
                app.saveTownFrame(name, dir);
            }
            passed = true;
        }
        catch (Exception error) { GD.PrintErr($"Race world views failed: {error}"); }
        exit(passed ? 0 : 1);
    }
}
