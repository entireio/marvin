using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Advances every competitor on one 240 Hz clock, then resolves contacts before
/// awarding race progress. The small steps bound relative travel below BB-8's
/// diameter even in opposing boosted impacts, including delayed display frames.
public struct DirtRacePhysics
{
    private double pendingTime = 0.0, pendingRaceTime = 0.0;
    public int contactCount { get; private set; } = 0;
    private CollisionRecovery[] recovery = new[] { new CollisionRecovery(), new CollisionRecovery(), new CollisionRecovery(), new CollisionRecovery() };
    public CityGate gate = new CityGate();
    public Sandstorm storm = new Sandstorm();
    public SandDeformation sand = null;
    private long shelterSteps = 0; // Swift Int: 240 Hz would wrap a 32-bit count (and shift the % 24 cadence) after ~103 days.
    private readonly double exposure(Simulation state, CityCollisionWorld? city)
    {
        if (!(storm.enabled && city is CityCollisionWorld world)) return 1;
        var wind = storm.wind(state.x, state.z);
        var direction = wind / max(1, Simd.length(wind));
        foreach (var distance in new[] { 2.0, 5.0, 9.0 })
        {
            var point = new Double3(state.x, state.groundY + 0.3, state.z) - direction * distance;
            var probe = new RobotCollisions.Body(position: point, profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.3, halfDepth: 0.3, height: 0.3));
            foreach (var obstacle in world.nearby(probe))
            {
                if (!(obstacle.profile.height > 1 && obstacle.profile.halfWidth > 0.4)) continue;
                if (RobotCollisions.contact(probe, obstacle) != null) return 0.25;
            }
        }
        return 1;
    }
    // PORT: Swift `public private(set) var escape`; a public field so its mutating advance works in place.
    public PostRaceEscape escape = new PostRaceEscape();
    public readonly RacePerformance.Character[] characters;
    public DirtRacePhysics() : this(null, null) { }
    /// PORT: null `characters` means RacePerformance.Character.allCases; null `townRoutes` means [].
    public DirtRacePhysics(RacePerformance.Character[] characters = null, Double2[][] townRoutes = null)
    {
        characters ??= RacePerformance.CharacterAllCases;
        precondition(characters.Length == 4 && characters.Distinct().Count() == 4);
        this.characters = characters.ToArray();
        escape = new PostRaceEscape(townRoutes ?? Array.Empty<Double2[]>());
    }

    /// PORT: Swift `inout Simulation`/`inout DirtRace` are ref parameters; `inout [DirtOpponent]` is an
    /// array mutated in place. `assists` defaults to DirtDrivingAssists.off.
    public void advance(DriveInput input, ref Simulation player, ref DirtRace race,
                        DirtOpponent[] opponents, double dt, double raceDT,
                        bool robotCollisionsEnabled = true, DirtDrivingAssists? assists = null, CityCollisionWorld? city = null)
    {
        var assist = assists ?? DirtDrivingAssists.off;
        if (!(opponents.Length == 3 && !player.paused && race.countdown <= 0 &&
              double.IsFinite(dt) && dt > 0 && double.IsFinite(raceDT) && raceDT > 0)) return;
        pendingTime += min(0.1, dt); pendingRaceTime += raceDT;
        var h = 1.0 / 240;
        while (pendingTime >= h - 1e-10)
        {
            var clockStep = pendingRaceTime * min(1, h / pendingTime);
            pendingTime = max(0, pendingTime - h); pendingRaceTime = max(0, pendingRaceTime - clockStep);
            storm.advance(h);
            player.storm = storm;
            player.aerodynamicProfile = RobotCollisions.profiles[(int)characters[0]];
            if (shelterSteps % 24 == 0)
            {
                player.windShelter = exposure(player, city);
                for (var i = 0; i < opponents.Length; i++) { opponents[i].simulation.windShelter = exposure(opponents[i].simulation, city); }
            }
            shelterSteps += 1;
            player.sand = sand;
            player.enableRobotDynamics();
            var projection = DirtCourse.projection(player.x, player.z);
            var exitPosition = CityExit.local(new Double2(player.x, player.z));
            var exploring = (projection.offset > 0 && projection.distance > DirtCourse.fenceOffset) || ((gate.wantsOpen || gate.angle > 0.02) && abs(exitPosition.x) < 3 && exitPosition.y > -5 && exitPosition.y < 8);
            var states = new Simulation[1 + opponents.Length];
            states[0] = player;
            for (var i = 0; i < opponents.Length; i++) states[i + 1] = opponents[i].simulation;
            var initialBodies = states.Select(s => s.collisionBody(RobotCollisions.profiles[(int)s.character])).ToArray();
            var races = new DirtRace[1 + opponents.Length];
            races[0] = race;
            for (var i = 0; i < opponents.Length; i++) races[i + 1] = opponents[i].race;
            escape.advance(races, states, city, gate.angle);
            if (escape.active) { gate.wantsOpen = true; }
            gate.advance(h, initialBodies);
            var playerInput = escape.input(0, states) ?? (exploring ? input : race.finished ? DirtOpponent.driveInput(player, cruising: true) : assist.apply(input, player));
            player.advance(playerInput, h);
            for (var i = 0; i < opponents.Length; i++)
            {
                opponents[i].simulation.sand = sand;
                opponents[i].simulation.storm = storm;
                opponents[i].simulation.aerodynamicProfile = RobotCollisions.profiles[(int)characters[i + 1]];
                opponents[i].simulation.enableRobotDynamics();
                var drive = escape.input(i + 1, states) ?? opponents[i].driveInput();
                opponents[i].simulation.advance(drive, h);
            }
            var bodies = new RobotCollisions.Body[1 + opponents.Length];
            bodies[0] = player.collisionBody(RobotCollisions.profiles[(int)characters[0]]);
            for (var i = 0; i < opponents.Length; i++) bodies[i + 1] = opponents[i].simulation.collisionBody(RobotCollisions.profiles[(int)characters[i + 1]]);
            contactCount += RobotCollisions.resolve(bodies, terrain: true, betweenRobots: robotCollisionsEnabled, gate: gate, city: city, previousPositions: initialBodies.Select(b => b.position).ToArray(), storm: storm, sand: sand);
            for (var i = 0; i < bodies.Length; i++)
            {
                var p = DirtCourse.projection(bodies[i].position.x, bodies[i].position.z);
                if (!escape.active && (i != 0 || !exploring) && !(p.offset > 0 && p.distance > DirtCourse.fenceOffset)) { recovery[i].advance(ref bodies[i], h); }
            }
            player.applyCollisionBody(bodies[0]);
            race.advance(player.x, player.z, clockStep);
            for (var i = 0; i < opponents.Length; i++)
            {
                opponents[i].simulation.applyCollisionBody(bodies[i + 1]);
                opponents[i].race.advance(opponents[i].simulation.x, opponents[i].simulation.z, clockStep);
            }
        }
    }

    /// PORT: Swift copies are deep (arrays are values); C# arrays are shared, so copy explicitly.
    public readonly DirtRacePhysics Clone()
    {
        var copy = this;
        copy.recovery = (CollisionRecovery[])recovery.Clone();
        copy.escape = escape.Clone();
        return copy;
    }
}

/// Deliberately arcade-like yaw assistance after a contact. Leaves the initial
/// impact visible, then eases toward the local forward tangent without teleporting
/// or adding translational speed. The rigid-body solver itself stays physical.
public struct CollisionRecovery
{
    private double remaining = 0.0;
    public CollisionRecovery() { }
    public void advance(ref RobotCollisions.Body body, double dt)
    {
        if (!(double.IsFinite(dt) && dt > 0 && (body.contacted || remaining > 0))) return;
        var phase = DirtCourse.phase(body.position.x, body.position.z);
        var error = atan2(sin(DirtCourse.heading(phase) - body.heading), cos(DirtCourse.heading(phase) - body.heading));
        if (body.contacted && (abs(body.angularVelocity) > 1.2 || abs(error) > 0.6)) { remaining = 3; }
        if (!(remaining > 0)) return;
        remaining = max(0, remaining - dt);
        var target = max(-2.5, min(2.5, error * 3.5));
        body.angularVelocity += (target - body.angularVelocity) * (1 - exp(-10 * dt));
    }
}
