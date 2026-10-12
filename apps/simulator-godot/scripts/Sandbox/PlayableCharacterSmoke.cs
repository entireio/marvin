// Port of Sources/MarvinSimulator/PlayableCharacterSmoke.swift (an AppController extension).
// `--character-smoke-test DIR`: each robot chosen on the main menu (hidden B/R/W/M keys) drives in the sandbox and
// in the race; same files ({key}-sandbox.png, {key}-race.png, characters.json) as reference/mac/character.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--character-smoke-test")]
    public static async Task RunCharacterSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        // Snapshots can pump the run loop; keep this synchronous
        // character check from re-entering the general smoke test.
        var passed = app.checkPlayableCharacters(at: dir);
        print($"Playable character smoke test: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }

    public bool checkPlayableCharacters(string at)
    {
        var directory = at;
        var results = new Dictionary<string, bool>();
        try
        {
            if (!checkPoweredRolling()) { return false; }
            Directory.CreateDirectory(directory);
            foreach (var (key, character) in new[] { ("b", RacePerformance.Character.bb8), ("r", RacePerformance.Character.r2d2), ("w", RacePerformance.Character.wallE), ("m", RacePerformance.Character.marvin) })
            {
                showMainMenu(null);
                var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                    window.windowNumber, null, key, key, false, 0);
                mainMenu.keyDown(@event);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
                startSandbox();
                var root = modelRoot(character);
                var passed = playerCharacter == character && simulation.character == character
                    && root.parent == world.scene.rootNode
                    && RacePerformance.CharacterAllCases.Count(c => modelRoot(c).parent == world.scene.rootNode) == 1;
                if (character == RacePerformance.Character.bb8) { passed = checkBB8SandboxRolling() && passed; }
                var input = new DriveInput(); input.throttle = 1; input.headYaw = 0.3; input.headPitch = 0.1;
                for (int i = 0; i < 45; i++) { simulation.advance(input, dt: 1.0 / 60); updatePlayerModel(); }
                passed = passed && simulation.distance > 0.1 && abs((double)root.position.z - simulation.z) < 1e-6;
                updateCamera(snap: true);
                saveCharacterFrame($"{key}-sandbox", directory);
                reset(null);
                passed = passed && playerCharacter == character && simulation.character == character && simulation.distance == 0;
                showMainMenu(null);
                passed = passed && mainMenu.character == character && root.parent == mainMenu.stage.rootNode;
                startDirtTrack();
                dirtIntro = null; race.countDown(dt: 3);
                passed = passed && racePhysics.characters.SequenceEqual(lineup) && lineup.Distinct().Count() == 4 && lineup[0] == character
                    && raceHUD.playerName == character.displayName()
                    && raceHUD.racerNames.SequenceEqual(lineup.Skip(1).Select(c => c.displayName()));
                for (int i = 0; i < 120; i++)
                {
                    var drive = DirtOpponent.driveInput(simulation);
                    advanceRacePhysics(drive, dt: 1.0 / 60, raceDT: 1.0 / 60);
                    updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                }
                var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
                passed = passed && simulation.distance > 0.1 && race.elapsed > 1.9;
                var order = lineup;
                for (int i = 0; i < order.Length; i++)
                {
                    var node = modelRoot(order[i]); var state = states[i];
                    passed = passed && node.parent == dirtWorld.scene.rootNode
                        && abs((double)node.position.x - state.x) < 1e-5
                        && abs((double)node.position.z - state.z) < 1e-5;
                }
                passed = passed && dirtWorld.trailCounts.All(c => c > 0);
                updateCamera(snap: true);
                saveCharacterFrame($"{key}-race", directory);
                reset(null);
                passed = passed && playerCharacter == character && simulation.character == character
                    && racePhysics.characters.SequenceEqual(lineup) && race.countdown == 3 && simulation.distance == 0;
                results[character.displayName()] = passed;
            }
            File.WriteAllText(Path.Combine(directory, "characters.json"), JSONSerialization.prettyPrintedSortedKeys(results));
        }
        catch (Exception error) { print($"Character smoke failed: {error}"); return false; }
        return results.Count == 4 && results.Values.All(v => v);
    }

    private bool checkBB8SandboxRolling()
    {
        try
        {
            // Follow a material point initially touching the floor. Its rotation
            // must cancel translation, regardless of the menu's model orientation.
            foreach (var throttle in new[] { 1.0, -1.0 })
            {
                reset(null);
                var input = new DriveInput(); input.throttle = throttle; input.turn = 0.35;
                for (int i = 0; i < 45; i++)
                {
                    var before = new Float3((float)simulation.x, 0, (float)simulation.z);
                    var contact = quat(bb8.ball.simdWorldOrientation).inverse.act(new Float3(0, -(float)bb8.ballRadius, 0));
                    simulation.advance(input, dt: 1.0 / 60); updatePlayerModel();
                    var movement = new Float3((float)simulation.x, 0, (float)simulation.z) - before;
                    var rotated = quat(bb8.ball.simdWorldOrientation).act(contact);
                    var slip = movement + new Float3(rotated.x, 0, rotated.z);
                    if (Simd.length(movement) > 1e-6f && Simd.length(slip) > Simd.length(movement) * 0.01f) { return false; }
                }
            }
            return true;
        }
        finally { reset(null); }
    }

    private void saveCharacterFrame(string name, string directory)
    {
        var png = NSBitmapImageRep.data(view.snapshot().tiffRepresentation)?.representation(NSBitmapImageFileType.png) ?? throw new IOException("fileWriteUnknown");
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), png);
    }
}
