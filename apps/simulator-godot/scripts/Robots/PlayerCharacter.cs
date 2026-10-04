// Port of Sources/MarvinSimulator/PlayerCharacter.swift.
// PORT: Swift extends RacePerformance.Character (an enum) with computed properties; C# 12 has no
// extension properties, so they are extension methods: `character.displayName()`, `.tint()`, `.lineup()`.
// The AppController extension is a partial class: the App.swift port must declare `partial class AppController`.
using System.Linq;
using Marvin.Core;

namespace Marvin;

public static class RacePerformanceCharacterExtensions
{
    public static string displayName(this RacePerformance.Character character) => new[] { "Marvin", "R2-D2", "BB-8", "WALL-E" }[(int)character];
    public static uint tint(this RacePerformance.Character character) => new[] { Robot.silverColor, 0x58baffu, 0xff914bu, 0xf2c94cu }[(int)character];
    /// Keep the existing opponent slots, exchanging the chosen robot and Marvin.
    public static RacePerformance.Character[] lineup(this RacePerformance.Character character)
    {
        var order = RacePerformance.CharacterAllCases;
        (order[0], order[(int)character]) = (order[(int)character], order[0]);
        return order;
    }
}

public partial class AppController
{
    public RacePerformance.Character[] lineup => playerCharacter.lineup();
    public SCNNode modelRoot(RacePerformance.Character character)
    {
        switch (character)
        {
            case RacePerformance.Character.marvin: return robot.root;
            case RacePerformance.Character.r2d2: return r2d2.root;
            case RacePerformance.Character.bb8: return bb8.root;
            default: return wallE.root;
        }
    }
    public void updateModel(RacePerformance.Character character, Simulation state) =>
        updateModel(character, state, new RacePerformance.Pose());
    public void updateModel(RacePerformance.Character character, Simulation state,
                            RacePerformance.Pose expression)
    {
        var pose = expression;
        pose.yaw += state.yaw; pose.pitch += state.pitch;
        switch (character)
        {
            case RacePerformance.Character.marvin:
                robot.update(state); robot.applyExpression(expression, state: state); break;
            case RacePerformance.Character.r2d2:
                r2d2.update(state); r2d2.applyExpression(pose); break;
            case RacePerformance.Character.bb8:
                bb8.update(state); bb8.applyExpression(pose, heading: state.heading); break;
            case RacePerformance.Character.wallE:
                wallE.update(state); wallE.applyExpression(pose, heading: state.heading); break;
        }
    }
    public void updatePlayerModel() { updateModel(playerCharacter, simulation); }

    public void configurePlayer()
    {
        playerCharacter = mainMenu.character;
        raceHUD.playerName = playerCharacter.displayName();
        raceHUD.playerColor = playerCharacter.tint();
        raceHUD.racerNames = lineup.Skip(1).Select(c => c.displayName()).ToArray();
        raceHUD.racerColors = lineup.Skip(1).Select(c => c.tint()).ToArray();
    }

    public void updateRaceWorld(double dt)
    {
        dirtWorld.sky.updateShadowCenter(raceCameraLocked ? Double3.zero : new Double3(simulation.x, simulation.groundY, simulation.z));
        // Effects stay in model order so tires/tracks and emitter counts match
        // their geometry, independent of who occupies the player slot.
        var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
        var lineupOrder = lineup;
        var canonical = RacePerformance.CharacterAllCases.Select(c => states[System.Array.IndexOf(lineupOrder, c)]).ToArray();
        dirtWorld.update(canonical[0], opponent: canonical[1], dt: dt,
                         modelScale: robot.modelScale, additional: new[] { canonical[2], canonical[3] });
    }
}
