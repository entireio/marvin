using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marvin;

/// The TownWorld half of the macOS `--town-smoke-test` (TownSmoke.swift `checkTown`): builds TownWorld and
/// writes `town-statistics.json` with the smoke report's own keys and the same TownWorld calls:
/// `town` (TownWorld.statistics), `layoutClearancePassed` (validate()), `cityCoveragePassed`,
/// `streetNetworkPassed`, `cameraObstructionPassed` (clearCamera from (0,1,-15) to (0,1,-22)),
/// `explorationDetailPassed` (the survey-point exploration toggles) and `resetPassed` (statistics and
/// root children unchanged by reset(); the smoke test also requires race.countdown == 3, which belongs
/// to the race app). The crowd-pause check, the 90 race frames and the images need the race app.
/// The town log lines ("Pedestrian access: ...", ...) are printed by TownWorld as on macOS.
///
/// When the macOS capture is available (MARVIN_MAC_REFERENCE = directory holding town-smoke.json,
/// default res://reference/mac/town), every key is compared with the macOS value and the result is
/// written to the report as `comparison` and printed; `identical` is true only when all keys match.
///   tools/godot -- --town-statistics DIR
public static class TownStatisticsCheck
{
    [GameMode("--town-statistics")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var world = new TownWorld();
        var valid = world.validate();
        SCNVector3 target = new SCNVector3(0, 1, -15), blocked = new SCNVector3(0, 1, -22);
        var safe = world.clearCamera(from: target, to: blocked);
        var cameraPassed = safe.z > blocked.z + 1 && safe.z < target.z;
        var detailsPassed = true;
        foreach (var fraction in new[] { 0.25, 0.60, 0.90 })
        {
            var p = world.explorationSurveyPoint(fraction);
            // PORT: the smoke test reads world.camera.position back from SceneKit, which stores Float.
            var camera = new SCNVector3((float)p.x, 1.5f, (float)p.y);
            foreach (var enabled in new[] { false, true })
            {
                world.explorationDetailEnabled = enabled;
                world.updateExplorationDetail(camera: camera, player: p);
                detailsPassed = detailsPassed && (enabled ? world.activeExplorationCells > 0 : world.activeExplorationCells == 0);
            }
        }
        world.updateExplorationDetail(camera: new SCNVector3(0, 38, -33), player: new Marvin.Core.Double2(90, 45));
        detailsPassed = detailsPassed && world.activeExplorationCells == 0;
        world.updateExplorationDetail(camera: new SCNVector3(25, 2, -10), player: Marvin.Core.Double2.zero);
        detailsPassed = detailsPassed && world.activeExplorationCells == 0;
        var statistics = world.statistics;
        var children = world.root.childNodes.Count;
        world.reset();
        var after = world.statistics;
        var resetPassed = after.Count == statistics.Count && statistics.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value) && world.root.childNodes.Count == children;
        var town = new JsonObject();
        foreach (var key in statistics.Keys.OrderBy(k => k, StringComparer.Ordinal)) { town[key] = statistics[key]; }
        var report = new JsonObject
        {
            ["cameraObstructionPassed"] = cameraPassed,
            ["cityCoveragePassed"] = world.cityCoveragePassed,
            ["explorationDetailPassed"] = detailsPassed,
            ["layoutClearancePassed"] = valid,
            ["resetPassed"] = resetPassed,
            ["streetNetworkPassed"] = world.streetNetworkPassed,
            ["town"] = town,
        };
        var referenceDirectory = Environment.GetEnvironmentVariable("MARVIN_MAC_REFERENCE") ?? Godot.ProjectSettings.GlobalizePath("res://reference/mac/town");
        var referencePath = Path.Combine(referenceDirectory, "town-smoke.json");
        if (File.Exists(referencePath))
        {
            var mac = JsonNode.Parse(File.ReadAllText(referencePath));
            var comparison = new JsonObject(); var identical = true;
            void compare(string name, JsonNode macValue, JsonNode godotValue)
            {
                var match = macValue?.ToJsonString() == godotValue?.ToJsonString();
                identical = identical && match;
                comparison[name] = new JsonObject { ["mac"] = macValue?.DeepClone(), ["godot"] = godotValue?.DeepClone(), ["match"] = match };
                Godot.GD.Print($"{(match ? "same" : "DIFF")}  {name}: macOS {macValue?.ToJsonString() ?? "-"}, Godot {godotValue?.ToJsonString() ?? "-"}");
            }
            var macTown = mac?["town"] as JsonObject ?? new JsonObject();
            foreach (var key in macTown.Select(pair => pair.Key).Union(town.Select(pair => pair.Key)).OrderBy(k => k, StringComparer.Ordinal))
            {
                compare("town." + key, macTown[key], town[key]);
            }
            foreach (var key in new[] { "cameraObstructionPassed", "cityCoveragePassed", "explorationDetailPassed", "layoutClearancePassed", "resetPassed", "streetNetworkPassed" }) { compare(key, mac?[key], report[key]); }
            report["comparison"] = comparison;
            report["identical"] = identical;
            report["reference"] = referencePath;
            Godot.GD.Print($"Town statistics: {(identical ? "IDENTICAL to" : "differ from")} {referencePath}");
        }
        else { Godot.GD.Print($"Town statistics: no macOS reference at {referencePath}"); }
        var json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "town-statistics.json"), json + "\n");
        Godot.GD.Print($"Town statistics: {Path.Combine(dir, "town-statistics.json")}");
        tree.Quit();
    }
}
