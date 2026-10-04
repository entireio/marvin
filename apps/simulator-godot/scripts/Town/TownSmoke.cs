using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of TownSmoke.swift's `checkTown(at:)` and `saveTownFrame(_:at:)` (AppController extensions).
// PORT: TownFrameMeter and the town benchmark (startTownBenchmark/tickTownBenchmark, part of the app's frame loop)
// are not ported. Random state, as on macOS: reset() shuffles the starting grid and applies BinaryDaylight.random(),
// so the town-smoke captures use a random sun; the test pins of reset(_:) (TestPins in AppModes.cs:
// MARVIN_TOWN_GRID="a,b,c,d", MARVIN_TOWN_DAYLIGHT="fraction,phase") fix them for 1:1 comparison with a macOS run.

public partial class AppController
{
    public sealed class RenderAuditException : Exception { public RenderAuditException(string message) : base(message) { } }

    public void saveTownFrame(string name, string directory)
    {
        var tiff = view.snapshot()?.tiffRepresentation;
        var bitmap = tiff == null ? null : NSBitmapImageRep.data(tiff);
        var png = bitmap?.representation(NSBitmapImageFileType.png);
        if (png == null) { throw new IOException("fileWriteUnknown"); }
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), png);
        var magenta = 0;
        for (var y = 0; y < bitmap.pixelsHigh; y += 8)
        {
            for (var x = 0; x < bitmap.pixelsWide; x += 8)
            {
                if (bitmap.colorAt(x, y)?.usingColorSpace(NSColorSpace.deviceRGB) is NSColor c && c.redComponent > 0.9 && c.blueComponent > 0.9 && c.greenComponent < 0.15) { magenta += 1; }
            }
        }
        if (!(magenta < 20)) { throw new RenderAuditException($"Shader failure colour in {name}: {magenta} sampled pixels"); }
    }

    public bool checkTown(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
            var valid = dirtWorld.town.validate();
            SCNVector3 target = new SCNVector3(0, 1, -15), blocked = new SCNVector3(0, 1, -22);
            var safe = dirtWorld.town.clearCamera(from: target, to: blocked);
            var cameraPassed = safe.z > blocked.z + 1 && safe.z < target.z;
            var crowd = dirtWorld.town.residents?.walkers.Select(w => w.node).ToList() ?? new List<SCNNode>();
            var before = crowd.Select(n => n.transform).ToList();
            dirtWorld.town.update(dt: 0, camera: world.camera.position, player: Double2.zero);
            var pausePassed = before.Zip(crowd).All(pair => pair.First == pair.Second.transform);
            raceHUD.isHidden = true;
            for (var frame = 0; frame < 90; frame++)
            {
                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                dirtWorld.town.update(dt: 1.0 / 60, camera: new SCNVector3(0, 5, -12), player: new Double2(simulation.x, simulation.z), robots: robotBodies(),
                    visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
            }
            var cameras = new List<(string, SCNVector3, SCNVector3)> {
                ("town-overview", new SCNVector3(0, 46, -52), new SCNVector3(0, 0, 0)),
                ("town-grandstand", new SCNVector3(5, 4.8, -10), new SCNVector3(0, 1.9, -20.5)),
                ("town-citizens", new SCNVector3(-6.0, 1.35, -28.1), new SCNVector3(-6.7, 0.65, -25.4)),
                ("town-spectators", new SCNVector3(3.3, 2.5, DirtCourse.point(0).z - 1.5), new SCNVector3(3, 2.03, DirtCourse.point(0).z - 4.18)),
                ("town-market", new SCNVector3(-14, 2.6, -27.8), new SCNVector3(0, 1.3, -24.5)),
                ("town-ramp-ground", new SCNVector3(-7.5, 0.58, -8.9), new SCNVector3(-7.5, 0.26, -12.3)),
                ("town-ramp-side", new SCNVector3(-10.0, 0.85, -10.0), new SCNVector3(-7.5, 0.20, -11.5)),
                ("town-service-access", new SCNVector3(-2, 5.8, -18), new SCNVector3(-7.5, 0.15, -10.8)),
                ("town-repair", new SCNVector3(-3.6, 2.9, -13.0), new SCNVector3(-7.5, 0.95, -7.5)),
                ("town-outskirts", new SCNVector3(-46, 13, -26), new SCNVector3(-21, 3, -3)),
                ("town-spaceport", new SCNVector3(17, 9, 0), new SCNVector3(34, 1.5, 13)),
                ("town-skyline", new SCNVector3(-10, 13, 19), new SCNVector3(10, 5, 43)),
                ("town-game-overview", new SCNVector3(0, 38, -33), new SCNVector3(0, 0, 0)) };
            foreach (var (name, eye, aim) in cameras)
            {
                world.camera.position = eye; world.camera.look(at: aim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
                saveTownFrame(name, directory);
            }
            updateCamera(snap: true);
            saveTownFrame("town-racing", directory);
            // Inspect from just ahead of Marvin's head, at its actual eye height.
            var forward = new Double2(sin(simulation.heading), cos(simulation.heading));
            var robotEye = new SCNVector3(simulation.x + forward.x * 0.34, simulation.groundY + 0.46, simulation.z + forward.y * 0.34);
            world.camera.position = robotEye;
            world.camera.look(at: new SCNVector3(simulation.x + forward.x * 12, simulation.groundY + 0.46, simulation.z + forward.y * 12), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveTownFrame("town-robot-pov", directory);
            var detailsPassed = true;
            foreach (var fraction in new[] { 0.25, 0.60, 0.90 })
            {
                Double2 p = dirtWorld.town.explorationSurveyPoint(fraction), ahead = dirtWorld.town.explorationSurveyPoint(fraction + 0.03);
                world.camera.position = new SCNVector3(p.x, 1.5, p.y);
                world.camera.look(at: new SCNVector3(ahead.x, 1.5, ahead.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                foreach (var enabled in new[] { false, true })
                {
                    dirtWorld.town.explorationDetailEnabled = enabled;
                    dirtWorld.town.updateExplorationDetail(camera: world.camera.position, player: p);
                    detailsPassed = detailsPassed && (enabled ? dirtWorld.town.activeExplorationCells > 0 : dirtWorld.town.activeExplorationCells == 0);
                    saveTownFrame($"outer-{(int)(fraction * 100)}-{(enabled ? "detailed" : "simple")}", directory);
                }
            }
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(0, 38, -33), player: new Double2(90, 45));
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(25, 2, -10), player: Double2.zero);
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            var count = dirtWorld.town.statistics;
            var children = dirtWorld.town.root.childNodes.Count;
            reset(null);
            var after = dirtWorld.town.statistics;
            var resetPassed = after.Count == count.Count && count.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value) && dirtWorld.town.root.childNodes.Count == children && race.countdown == 3;
            var passed = valid && resetPassed && cameraPassed && pausePassed && detailsPassed;
            var report = new Dictionary<string, object>
            {
                ["passed"] = passed, ["explorationDetailPassed"] = detailsPassed, ["layoutClearancePassed"] = valid, ["cityCoveragePassed"] = dirtWorld.town.cityCoveragePassed, ["streetNetworkPassed"] = dirtWorld.town.streetNetworkPassed, ["resetPassed"] = resetPassed, ["cameraObstructionPassed"] = cameraPassed, ["crowdPausePassed"] = pausePassed,
                ["town"] = count, ["images"] = cameras.Select(c => c.Item1).Concat(new[] { "town-racing", "town-robot-pov" }).ToList(),
            };
            File.WriteAllText(Path.Combine(directory, "town-smoke.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            return passed;
        }
        catch (Exception error) { Godot.GD.Print($"Town smoke: {error}"); return false; }
    }

    [GameMode("--town-smoke-test")]
    public static async System.Threading.Tasks.Task RunTownSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkTown(at: dir);
        print($"Town smoke: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }
}
