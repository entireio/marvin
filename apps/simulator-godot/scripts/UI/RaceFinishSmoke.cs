// Port of Sources/MarvinSimulator/RaceFinishSmoke.swift (an AppController extension).
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    public bool checkRaceFinish(string at)
    {
        var directory = at;
        double finishTime = race.elapsed, distance = simulation.distance;
        dirtIntro = null;
        for (int i = 0; i < 210; i++) { advanceRaceFrame(step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: true); updateCamera(snap: false); }
        var passed = race.finished && race.elapsed == finishTime && simulation.distance > distance + 0.2
            && dirtOutro == dirtIntroDuration && abs(world.camera.position.y - 38) < 0.001;
        double before = simulation.elapsed; var animation = dirtOutro;
        advanceRaceFrame(step: 0.1, raceDelta: 0.1, advancing: false);
        passed = passed && simulation.elapsed == before && dirtOutro == animation;
        var frame = raceHUD.frame;
        try
        {
            raceHUD.frame = new NSRect(0, 0, 900, 550);
            void capture(string name)
            {
                robot.update(simulation); updateOpponents();
                raceHUD.race = race; raceHUD.introducing = false;
                var image = new NSImage(raceHUD.bounds.size);
                image.lockFocus();
                view.snapshot().draw(raceHUD.bounds);
                // Draw the overlay directly: AppKit cacheDisplay cannot capture the
                // Metal layer beneath it and would composite an opaque black base.
                // PORT: the facade's cacheDisplay draws the HUD over a transparent base, so the overlay is cached
                // and drawn over the snapshot (Swift draws raceHUD.draw(_:) into the image through a flipped context).
                var overlay = raceHUD.bitmapImageRepForCachingDisplay(raceHUD.bounds);
                raceHUD.cacheDisplay(raceHUD.bounds, overlay);
                overlay.draw(raceHUD.bounds);
                image.unlockFocus();
                var png = NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png) ?? throw new System.IO.IOException("fileWriteUnknown");
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name), png);
            }
            capture("race-finish-live.png");
            for (int i = 0; i < 60 * 180; i++)
            {
                if (opponents.All(o => o.race.finished)) { break; }
                advanceRaceFrame(step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: true);
            }
            passed = passed && opponents.All(o => o.race.finished);
            capture("race-results.png");
            reset(null);
            passed = passed && dirtOutro == null;
            capture("race-start.png");
            raceHUD.paused = true;
            capture("race-pause.png");
            return passed;
        }
        finally { raceHUD.frame = frame; raceHUD.paused = false; }
    }
}
