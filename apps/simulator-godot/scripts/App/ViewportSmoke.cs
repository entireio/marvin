// Port of Sources/MarvinSimulator/ViewportSmoke.swift (an AppController extension): `--viewport-smoke-test DIR`.
// Hiding the race controls after the finish must not resize the 3D view or move its projection, at three window sizes,
// and a restart must keep the window and view. Writes viewport.json, before-hiding-controls.png and
// after-hiding-controls.png.
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--viewport-smoke-test")]
    public static async System.Threading.Tasks.Task RunViewportSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkDepartureViewport(at: URL.fileURLWithPath(dir));
        exit(passed ? 0 : 1);
    }

    public bool checkDepartureViewport(URL at)
    {
        var directory = at;
        try
        {
            FileManager.@default.createDirectory(directory, withIntermediateDirectories: true);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
            for (int i = 1; i <= 601; i++)
            {
                var p = DirtCourse.point((double)i * Math.PI / 100);
                race.advance(x: p.x, z: p.z, dt: 0.1);
            }
            updateCamera(snap: true);
            var landmarks = new[] { SCNVector3Zero, new SCNVector3(-12, 0, -10), new SCNVector3(12, 0, 10) };
            void settle()
            {
                (window.contentView as SCNView)?.layoutSubtreeIfNeeded(); window.displayIfNeeded();
            }
            void capture(string name)
            {
                var image = view.snapshot();
                if (NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png) is byte[] png)
                {
                    png.write(directory.appendingPathComponent(name + ".png"));
                }
            }
            var results = new List<Dictionary<string, object>>(); var passed = race.finished;
            foreach (var (index, size) in new[] { new NSSize(1280, 820), new NSSize(900, 640), new NSSize(1600, 900) }.Select((s, i) => (i, s)))
            {
                window.setContentSize(size); setRaceControlsHidden(false); settle();
                var before = view.bounds; var windowBefore = window.frame; var transform = world.camera.transform;
                var pixels = landmarks.Select(l => view.projectPoint(l)).ToList();
                var safe = view.convert(window.contentLayoutRect, from: null);
                var overlaySafe = abs(raceHUD.frame.maxY - safe.maxY) < 0.5 && abs(raceHUD.frame.minY - safe.minY) < 0.5;
                if (index == 0) { capture("before-hiding-controls"); }
                setRaceControlsHidden(true); settle(); updateCamera(snap: false);
                var after = view.bounds; var newPixels = landmarks.Select(l => view.projectPoint(l)).ToList();
                var drift = pixels.Zip(newPixels).Select(pair => hypot((double)(pair.First.x - pair.Second.x), (double)(pair.First.y - pair.Second.y))).DefaultIfEmpty(100).Max();
                var hidden = raceHUD.isHidden && hud.isHidden && window.toolbar?.isVisible == false;
                var stable = before == after && window.frame == windowBefore && world.camera.transform.Equals(transform) && drift < 0.01;
                if (index == 0) { capture("after-hiding-controls"); }
                setRaceControlsHidden(false); settle();
                var restored = view.bounds == before && !raceHUD.isHidden && window.toolbar?.isVisible == true;
                passed = passed && stable && hidden && restored && overlaySafe;
                results.Add(new Dictionary<string, object>
                {
                    ["width"] = before.width, ["height"] = before.height, ["afterHeight"] = after.height, ["restoredHeight"] = view.bounds.height, ["restoredWidth"] = view.bounds.width,
                    ["stableViewport"] = stable, ["landmarkDriftPixels"] = drift, ["controlsHidden"] = hidden, ["restored"] = restored, ["overlayBelowToolbar"] = overlaySafe,
                });
            }
            // Verify normal resizes still reach the renderer; only chrome changes are isolated.
            var resizeWorks = !Equals(results[0]["width"], results[1]["width"]);
            passed = passed && resizeWorks;
            setRaceControlsHidden(true); settle();
            var restartBounds = view.bounds; var restartFrame = window.frame;
            reset(null); settle();
            var restartStable = view.bounds == restartBounds && window.frame == restartFrame;
            passed = passed && restartStable && !raceHUD.isHidden && window.toolbar?.isVisible == true;
            var report = new Dictionary<string, object> { ["passed"] = passed, ["sizes"] = results, ["normalResizeWorks"] = resizeWorks, ["restartStable"] = restartStable };
            JSONSerialization.data(report, JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("viewport.json"));
            print(JSONSerialization.@string(report, JSONSerialization.WritingOptions.sortedKeys)); return passed;
        }
        catch (Exception error) { print($"Viewport check: {error}"); return false; }
    }
}
