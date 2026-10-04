using System;
using System.IO;
using System.Linq;
using Marvin.Core;

namespace Marvin;

// TEMPORARY (not committed): renders the town-overview camera for a list of daylights to estimate the
// random daylight of a macOS town-smoke run. MARVIN_FIT="f,p;f,p;..."
public static class TmpDaylightFit
{
    [GameMode("--tmp-daylight-fit")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var app = new TownSmokeApp(tree); app.weatherOverride = false;
        app.startDirtTrack(); app.dirtIntro = null;
        var list = Environment.GetEnvironmentVariable("MARVIN_FIT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var view = Environment.GetEnvironmentVariable("MARVIN_FIT_VIEW") ?? "overview";
        foreach (var item in list)
        {
            var v = item.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            app.dirtWorld.sky.apply(new BinaryDaylight(v[0], v[1]));
            if (view == "overview") { app.world.camera.position = new SCNVector3(0, 46, -52); app.world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1)); }
            else { app.world.camera.position = new SCNVector3(-10, 13, 19); app.world.camera.look(at: new SCNVector3(10, 5, 43), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1)); }
            app.saveTownFrame($"fit-{v[0]:0.000}-{v[1]:0.000}", dir);
        }
        tree.Quit();
    }
}
