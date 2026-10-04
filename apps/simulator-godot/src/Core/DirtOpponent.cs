using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// A deterministic look-ahead driver using the same drive, terrain and fence
/// physics as Marvin. No teleports, scripted lap awards or catch-up speed boost.
public struct DirtOpponent
{
    // PORT: Swift `public internal(set) var`; public fields so callers (and DirtRacePhysics)
    // can mutate them in place through `opponents[i].simulation`.
    public Simulation simulation = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: DirtCourse.opponentGrid.offset, dirtStartPhase: DirtCourse.opponentGrid.phase);
    public DirtRace race = new DirtRace(startPhase: DirtCourse.opponentGrid.phase);
    private double pendingTime = 0.0, pendingRaceTime = 0.0;
    private readonly double laneOffset;
    public DirtOpponent() : this(DirtCourse.opponentGrid) { }
    /// PORT: Swift's default slot is DirtCourse.opponentGrid; pass it explicitly when also passing laneOffset.
    public DirtOpponent((double phase, double offset) slot, double laneOffset = 0.65)
    {
        simulation = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase);
        race = new DirtRace(startPhase: slot.phase);
        this.laneOffset = laneOffset;
        race.countDown(3);
    }
    /// Swift <c>DirtOpponent(laneOffset:)</c> with the default slot.
    public DirtOpponent(double laneOffset) : this(DirtCourse.opponentGrid, laneOffset) { }

    /// Caller gates this on the shared countdown, pause and focus. Finishers keep circulating at cooldown speed.
    public void advance(double dt, double raceDT)
    {
        if (!(double.IsFinite(dt) && dt > 0 && double.IsFinite(raceDT) && raceDT > 0)) return;
        // Evaluate steering at a fixed rate too, so frame rate does not alter
        // cornering decisions. Preserve wall-clock timing when frames stall.
        pendingTime += min(dt, 0.1);
        pendingRaceTime += raceDT;
        var h = 1.0 / 120;
        while (pendingTime >= h - 1e-10)
        {
            var clockStep = pendingRaceTime * min(1, h / pendingTime);
            pendingTime = max(0, pendingTime - h);
            pendingRaceTime = max(0, pendingRaceTime - clockStep);
            step(h, clockStep);
        }
    }

    /// PORT: Swift `var driveInput: DriveInput`. C# cannot have a property and a static method
    /// with the same name, so this is the instance method <c>driveInput()</c>.
    public readonly DriveInput driveInput() => driveInput(simulation, laneOffset, race.finished);

    public static DriveInput driveInput(Simulation simulation, double laneOffset = 0, bool cruising = false)
    {
        var projection = DirtCourse.projection(simulation.x, simulation.z);
        var phase = projection.phase;
        // A rival nudged through the open exit returns via the doorway instead
        // of steering into the retaining wall from the city side.
        var outside = projection.offset > 0 && projection.distance > DirtCourse.fenceOffset;
        var q = CityExit.local(new Double2(simulation.x, simulation.z));
        var reentry = abs(q.x) > 0.45 ? CityExit.point(q.y < 3 ? q.x : 0, 3.5) : CityExit.point(0, -1.25);
        var target = outside ? (x: reentry.x, z: reentry.y) : DirtCourse.point(phase + 0.05, laneOffset);
        var desired = atan2(target.x - simulation.x, target.z - simulation.z);
        var error = atan2(sin(desired - simulation.heading), cos(desired - simulation.heading));
        var input = new DriveInput();
        input.throttle = outside ? (abs(error) < 0.25 ? 0.55 : 0) : max(0.15, 1 - abs(error) * 1.5);
        input.boost = !outside && !cruising && abs(error) < 0.08;
        if (cruising) { input.throttle *= 0.65; }
        if (simulation.storm.enabled && !outside)
        {
            // Preview deposits on the actual lane before reaching their crest.
            // Airborne robots cannot steer; blindly boosting here can launch a
            // light chassis over the closed exit gate.
            var depths = new double[7];
            for (var sample = 0; sample <= 6; sample++)
            {
                var p = DirtCourse.point(phase + (double)sample * 0.018, laneOffset);
                depths[sample] = simulation.storm.depth(p.x, p.z);
            }
            var depth = maxElement(depths) ?? 0;
            var pace = max(3.2, 5.2 - depth * 18);
            input.boost = false;
            input.throttle = min(input.throttle, pace / 6);
            input.assistedBrakePressure = max(0, min(1, (simulation.groundSpeed - pace) * 0.65));
            input.assistedBraking = input.assistedBrakePressure > 0;
        }
        input.turn = -error * 3;
        return input;
    }

    private void step(double dt, double raceDT)
    {
        simulation.advance(driveInput(), dt);
        race.advance(simulation.x, simulation.z, raceDT);
    }

    public readonly int playerPosition(DirtRace player)
    {
        if (player.finished && race.finished) return player.elapsed <= race.elapsed ? 1 : 2;
        if (player.finished) return 1;
        if (race.finished) return 2;
        return player.progress >= race.progress ? 1 : 2;
    }
}
