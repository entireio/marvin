// Port of Sources/MarvinSimulator/FramePacingSmoke.swift (an AppController extension).
//
// `--display-link-lifecycle-check DIR` is not a smoke flag on macOS: the app launches normally (no smokeDirectory, the
// normal clock, race audio) with display-linked updates, and applicationDidFinishLaunching starts this check, which
// drives the real asynchronous window transitions (menu -> loading -> race, pause, minimise, restore) and exits.
// PORT: the display link is the facade's CADisplayLink on Godot's vsync'd frame loop (see AppController); minimising is
// the Godot window's minimised mode. Run it with a real window (not --headless).
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    /// Real asynchronous window transitions with the normal display-driven clock.
    /// Unlike fixed-step smoke fixtures this must leave smokeDirectory nil.
    public void checkDisplayLinkLifecycle(URL at)
    {
        var directory = at;
        var checks = new Dictionary<string, bool> { ["displayLinkSelected"] = frameDisplayLink != null, ["normalClock"] = smokeDirectory == null };
        void later(double seconds, Action action) => DispatchQueue.main.asyncAfter(DispatchTime.now() + seconds, action);
        void finish()
        {
            try
            {
                FileManager.@default.createDirectory(directory, withIntermediateDirectories: true);
                saveTownFrame("restored-window", directory.path);
                checks["fpsReadoutLive"] = (frameRateHUD.framesPerSecond ?? 0) > 0 && !frameRateHUD.isHidden;
                checks["fpsPassesMouseInput"] = frameRateHUD.hitTest(NSPoint.zero) == null;
                // Composite the native view layers, since SCNView.snapshot omits
                // AppKit overlays. Preserve their actual layout and rendered text.
                // PORT: each overlay is cached over a transparent base and drawn at its frame (the facade's equivalent
                // of drawing overlay.draw(_:) through a translated, possibly flipped context; same pixels).
                var image = new NSImage(view.bounds.size);
                image.lockFocus(); view.snapshot().draw(view.bounds);
                foreach (var overlay in new NSView[] { raceHUD, frameRateHUD }.Where(o => !o.isHidden))
                {
                    var cached = overlay.bitmapImageRepForCachingDisplay(overlay.bounds);
                    overlay.cacheDisplay(overlay.bounds, cached);
                    cached.draw(overlay.frame);
                }
                image.unlockFocus();
                if (NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png) is byte[] png)
                {
                    png.write(directory.appendingPathComponent("fps-overlay.png"));
                }
                var report = new Dictionary<string, object> { ["checks"] = checks, ["passed"] = checks.Values.All(v => v) };
                JSONSerialization.data(report, JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("lifecycle.json"));
                print($"Display-link lifecycle: [{string.Join(", ", checks.Select(c => $"\"{c.Key}\": {(c.Value ? "true" : "false")}"))}]");
                exit(checks.Values.All(v => v) ? 0 : 1);
            }
            catch (Exception error) { print(error.ToString()); exit(1); }
        }
        later(0.5, () =>
        {
            checks["menuVisible"] = !mainMenu.isHidden;
            loadDirtTrack();
            void awaitLoaded(int remaining)
            {
                if (isLoadingDirt)
                {
                    if (remaining > 0) { later(0.25, () => awaitLoaded(remaining - 1)); }
                    else { checks["loaded"] = false; finish(); }
                    return;
                }
                checks["loaded"] = inSandbox && isDirtTrack && !view.isHidden;
                dirtIntro = null; race.countDown(dt: 3);
                var initial = race.elapsed;
                later(0.75, () =>
                {
                    checks["raceAdvances"] = race.elapsed - initial > 0.3;
                    togglePause(null); var paused = race.elapsed;
                    later(0.5, () =>
                    {
                        checks["pauseFreezesClock"] = race.elapsed == paused;
                        checks["pauseStopsAudio"] = raceAudio?.active == false;
                        togglePause(null);
                        later(0.5, () =>
                        {
                            checks["resumeAdvances"] = race.elapsed - paused > 0.2;
                            window.miniaturize(null);
                            later(0.5, () =>
                            {
                                var hidden = race.elapsed;
                                checks["windowMiniaturized"] = window.isMiniaturized;
                                checks["hiddenStopsAudio"] = raceAudio?.active == false;
                                later(1.0, () =>
                                {
                                    checks["hiddenFreezesClock"] = race.elapsed == hidden;
                                    var resumedAt = ProcessInfo.processInfo.systemUptime;
                                    window.deminiaturize(null);
                                    window.makeKeyAndOrderFront(null); NSApp.activate(ignoringOtherApps: true);
                                    later(0.75, () =>
                                    {
                                        var advance = race.elapsed - hidden;
                                        var restoredWall = ProcessInfo.processInfo.systemUptime - resumedAt;
                                        checks["restoreWithoutCatchup"] = advance > 0.15 && advance < restoredWall + 0.1;
                                        print($"Restore race delta: {Swift.description(advance)}, visible wall interval: {Swift.description(restoredWall)}");
                                        checks["restoredAudio"] = raceAudio?.active == true;
                                        finish();
                                    });
                                });
                            });
                        });
                    });
                });
            }
            later(0.25, () => awaitLoaded(240));
        });
    }
}
