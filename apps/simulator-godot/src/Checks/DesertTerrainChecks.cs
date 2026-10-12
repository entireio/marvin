using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/DesertTerrainChecks.swift.
public partial struct SimulationTests
{
    public void testDesertTerrain()
    {
        // Keep every existing city foundation and both track entrances level.
        foreach (var z in stride(-154.0, 154, 7))
        {
            foreach (var x in stride(-154.0, 154, 7))
            {
                near(DesertTerrain.height(x, z), -0.025, 1e-12);
            }
        }
        var maxSlope = 0.0; var maxHeight = 0.0;
        foreach (var z in stride(-700.0, 700, 7.3))
        {
            foreach (var x in stride(-700.0, 700, 7.7))
            {
                var h = DesertTerrain.height(x, z); var g = DesertTerrain.gradient(x, z);
                require(double.IsFinite(h) && h >= -0.0250001);
                maxSlope = max(maxSlope, Simd.length(g)); maxHeight = max(maxHeight, h);
                // Independent barycentric construction at the exact mesh vertices.
                var x0 = floor(x / 2) * 2; var z0 = floor(z / 2) * 2; var u = (x - x0) / 2; var v = (z - z0) / 2;
                var a = DesertTerrain.vertexHeight(x0, z0); var b = DesertTerrain.vertexHeight(x0 + 2, z0);
                var c = DesertTerrain.vertexHeight(x0, z0 + 2); var d = DesertTerrain.vertexHeight(x0 + 2, z0 + 2);
                var rendered = u + v <= 1 ? (1 - u - v) * a + u * b + v * c : (u + v - 1) * d + (1 - u) * c + (1 - v) * b;
                near(h, rendered, 1e-10);
            }
        }
        require(maxHeight > 8 && maxSlope < 0.67);
        // No vertical seam at the town apron, tile boundaries or field perimeter.
        foreach (var x in new[] { 156.0, 160, 192, 256, 320, 640, 768 })
        {
            foreach (var z in new[] { 0.0, 51, 181, 321 })
            {
                near(DesertTerrain.height(x - 1e-5, z), DesertTerrain.height(x + 1e-5, z), 0.00002);
            }
        }
        // Drive every playable chassis across a rising dune with real coupled
        // dynamics; verify ground contact, slope response and successful travel.
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            var desired = new Double2(171, 57); var p = DirtCourse.projection(desired.x, desired.y);
            var player = new Simulation(dirtTrack: true, dirtStartOffset: p.distance, dirtStartPhase: p.phase, character: character);
            var others = RacePerformance.CharacterAllCases.Where(other => other != character).ToArray();
            var physics = new DirtRacePhysics(characters: new[] { character }.Concat(others).ToArray());
            var rivals = new[] { new DirtOpponent(), new DirtOpponent(laneOffset: 0), new DirtOpponent(laneOffset: -0.65) };
            var race = new DirtRace(); race.countDown(3);
            var target = new Double2(250, 74);
            var highest = player.groundY; var pitch = 0.0; var arrived = false;
            for (var k = 0; k < 6000; k++)
            {
                var d = target - new Double2(player.x, player.z);
                if (Simd.length(d) < 0.5) { arrived = true; break; }
                var error = atan2(sin(atan2(d.x, d.y) - player.heading), cos(atan2(d.x, d.y) - player.heading));
                var input = new DriveInput(); input.turn = max(-1, min(1, -error * 2.5)); input.throttle = abs(error) < 0.3 ? 0.8 : 0;
                physics.advance(input, ref player, ref race, rivals, 1.0 / 60, 1.0 / 60);
                require(player.groundY >= DirtCourse.height(player.x, player.z) - 0.005);
                require(double.IsFinite(player.x) && double.IsFinite(player.bodyPitch) && double.IsFinite(player.bodyRoll));
                highest = max(highest, player.groundY); pitch = max(pitch, abs(player.bodyPitch));
            }
            require(arrived && highest > 2 && pitch > 0.03);
            require(player.groundY < highest - 0.25);
        }
        print($"Dunes: PASS, peak {description(maxHeight)}m, maximum sampled slope {description(atan(maxSlope) * 180 / Math.PI)} degrees");
    }
}
