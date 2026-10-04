// STUBS for the audio stream: minimal stand-ins for types owned by other streams (App shell, World,
// DirtWorld, TownWorld). Only the members RaceAudio.cs / RaceAudioSmoke.cs use, with the macOS behaviour
// those members have for audio. The orchestrator deletes this file when merging.
using System.Linq;
using Marvin.Core;

namespace Marvin;

/// App.swift AppController (app-shell stream). Lifecycle members used by updateRaceAudio and the audio smoke test.
public partial class AppController : Godot.Node
{
    public RaceAudio raceAudio;
    public bool raceSoundMuted = false;
    public bool inSandbox = false;
    public bool isDirtTrack = false;
    public double? dirtIntro;
    public readonly World world = new World();
    public Simulation simulation = new Simulation();
    public DirtRace race = new DirtRace();
    public DirtRacePhysics racePhysics = new DirtRacePhysics();
    public DirtOpponent opponent = new DirtOpponent();
    public DirtOpponent bb8Opponent = new DirtOpponent(laneOffset: 0);
    public DirtOpponent wallEOpponent = new DirtOpponent(laneOffset: -0.65);
    public DirtOpponent[] opponents => new[] { opponent, bb8Opponent, wallEOpponent };
    public RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    /// PlayerCharacter.swift: the chosen robot swaps places with Marvin.
    public RacePerformance.Character[] lineup
    {
        get
        {
            var order = RacePerformance.CharacterAllCases;
            (order[0], order[(int)playerCharacter]) = (order[(int)playerCharacter], order[0]);
            return order;
        }
    }
    private DirtWorld cachedDirtWorld;
    public DirtWorld dirtWorld { get => cachedDirtWorld ??= new DirtWorld(); set => cachedDirtWorld = value; }
    public bool? weatherOverride = false;

    public void startDirtTrack()
    {
        isDirtTrack = true; inSandbox = true;
        simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.playerGrid.offset, dirtStartPhase: DirtCourse.playerGrid.phase);
        reset(null); dirtIntro = 0;
    }
    public void reset(object sender)
    {
        raceAudio?.resetConversation();
        if (!inSandbox) return;
        dirtIntro = null;
        simulation.reset();
        if (isDirtTrack)
        {
            var slots = DirtCourse.startingGrid;
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase, character: playerCharacter);
            opponent = new DirtOpponent(slot: slots[1]);
            bb8Opponent = new DirtOpponent(slot: slots[2], laneOffset: 0);
            wallEOpponent = new DirtOpponent(slot: slots[3], laneOffset: -0.65);
            race = new DirtRace(startPhase: slots[0].phase); racePhysics = new DirtRacePhysics(characters: lineup);
            racePhysics.storm = new Sandstorm(enabled: weatherOverride ?? Sandstorm.drawForRace());
        }
    }
    public void showMainMenu(object sender)
    {
        raceAudio?.resetConversation();
        inSandbox = false; dirtIntro = null;
    }
}

/// World.swift (sandbox world stream): the shared camera node.
public class World
{
    public readonly SCNNode camera = new SCNNode();
}

/// DirtWorld.swift (track stream).
public class DirtWorld
{
    public readonly TownWorld town = new TownWorld();
}

/// TownWorld.swift (town stream). The real soundZones also contain the venue sites and street-activity
/// groups; the infield repair tents come from SimulationCore exactly as in TownWorld.swift.
public class TownWorld
{
    public SpectatorSoundZone[] spectatorSoundZones => System.Array.Empty<SpectatorSoundZone>();
    public TownSoundZone[] soundZones => InfieldLayout.tentOrigins
        .Select(origin => new TownSoundZone(position: origin, kind: TownSoundZone.Kind.workshop, activity: 0.65, infieldRepair: true)).ToArray();
}
