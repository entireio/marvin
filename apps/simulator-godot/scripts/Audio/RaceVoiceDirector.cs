using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Speech follows nearby events. Steady driving never schedules periodic chatter.
/// PORT: Swift arrays/dictionaries are values; use <see cref="Clone"/> to copy this struct.
public struct RaceVoiceDirector
{
    public static readonly string[] moods = { "acknowledge", "effort", "startle", "overtake", "passed" };
    public const int variantCount = 6;
    public struct Observation
    {
        public readonly Double2 position; public readonly double speed; public readonly bool contact;
        public double? progress = null;
        public Observation(Double2 position, double speed, bool contact, double? progress = null)
        {
            this.position = position; this.speed = speed; this.contact = contact; this.progress = progress;
        }
    }
    public readonly struct Event
    {
        public readonly int robot; public readonly string mood; public readonly int variant;
        public Event(int robot, string mood, int variant) { this.robot = robot; this.mood = mood; this.variant = variant; }
    }
    private readonly struct Pending
    {
        public readonly int robot; public readonly string mood; public readonly int priority; public readonly double expires;
        public Pending(int robot, string mood, int priority, double expires) { this.robot = robot; this.mood = mood; this.priority = priority; this.expires = expires; }
    }
    private double elapsed = 0.0, globalReady = 0.4;
    private double[] ready = new double[4], passReady = new double[4];
    private bool[] previousContact = new bool[4], accelerationArmed = { true, true, true, true };
    private bool[] greeted = new bool[4]; private int[] side = new int[4];
    private List<Pending> pending = new();
    private Dictionary<string, List<int>> bags = new();
    private Dictionary<string, int> last = new();
    private ulong random;

    /// Swift `init(seed: UInt64 = UInt64.random(in: 1...UInt64.max))`.
    public RaceVoiceDirector() : this(randomSeed()) { }
    public RaceVoiceDirector(ulong seed) { random = seed; }
    private static ulong randomSeed()
    {
        ulong value;
        do { value = (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.NextInt64() << 1); } while (value == 0);
        return value;
    }

    /// Deep copy (Swift value semantics).
    public readonly RaceVoiceDirector Clone()
    {
        var copy = this;
        copy.ready = (double[])ready.Clone(); copy.passReady = (double[])passReady.Clone();
        copy.previousContact = (bool[])previousContact.Clone(); copy.accelerationArmed = (bool[])accelerationArmed.Clone();
        copy.greeted = (bool[])greeted.Clone(); copy.side = (int[])side.Clone();
        copy.pending = new List<Pending>(pending);
        copy.bags = bags.ToDictionary(e => e.Key, e => new List<int>(e.Value));
        copy.last = new Dictionary<string, int>(last);
        return copy;
    }

    private int next(int n)
    {
        random = unchecked(random * 6364136223846793005UL + 1442695040888963407UL);
        return (int)((random >> 32) % (ulong)n);
    }
    private int variant(int robot, string mood)
    {
        var key = robot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + mood;
        if (!bags.TryGetValue(key, out var existing) || existing.Count == 0)
        {
            var bag = Enumerable.Range(0, variantCount).ToList();
            for (int i = bag.Count - 1; i >= 1; i--) { int j = next(i + 1); (bag[i], bag[j]) = (bag[j], bag[i]); }
            if (last.TryGetValue(key, out var previous) && bag[^1] == previous) (bag[0], bag[^1]) = (bag[^1], bag[0]);
            bags[key] = bag;
        }
        var values = bags[key];
        var value = values[^1]; values.RemoveAt(values.Count - 1);
        last[key] = value; return value;
    }

    public Event? advance(IReadOnlyList<Simulation> states, IReadOnlyList<double> progress = null, double dt = 0, bool canSpeak = true)
    {
        progress ??= Array.Empty<double>();
        var observations = states.Select((s, i) =>
            new Observation(position: new Double2(s.x, s.z), speed: s.groundSpeed, contact: s.contacting, progress: progress.Count == 4 ? progress[i] : null)).ToArray();
        return advance(observations: observations, dt: dt, canSpeak: canSpeak);
    }

    public Event? advance(IReadOnlyList<Observation> observations, double dt, bool canSpeak = true)
    {
        if (!(observations.Count == 4 && dt > 0)) return null;
        elapsed += min(dt, 0.1);
        var listener = observations[0].position;
        var now = elapsed;
        pending.RemoveAll(p => p.expires < now);
        for (int i = 0; i < 4; i++)
        {
            var s = observations[i];
            bool near = Simd.distance(s.position, listener) < 12;
            string mood = null; int priority = 0;
            if (i > 0 && s.progress is double a && observations[0].progress is double b)
            {
                // Continuous lap progress avoids false overtakes at the start/finish seam.
                // Hysteresis rejects repeated side changes while running side by side.
                var delta = a - b;
                int newSide = delta > 0.035 ? 1 : delta < -0.035 ? -1 : side[i];
                if (side[i] != 0 && newSide != side[i] && near && elapsed >= passReady[i] && s.speed > 1 && observations[0].speed > 1)
                {
                    mood = newSide > 0 ? "overtake" : "passed"; priority = 3; passReady[i] = elapsed + 18;
                }
                side[i] = newSide;
            }
            if (s.contact && !previousContact[i] && s.speed > 3 && mood == null) { mood = "startle"; priority = 2; }
            if (s.speed < 2) accelerationArmed[i] = true;
            if (s.speed > 5 && accelerationArmed[i])
            {
                accelerationArmed[i] = false;
                // Acceleration is expressed by the drivetrain, not a recurring squeak.
            }
            if (i == 0 && !greeted[i] && s.speed > 0.5) { greeted[i] = true; if (mood == null) mood = "acknowledge"; }
            previousContact[i] = s.contact;
            if (mood != null && near && (priority == 3 || elapsed >= ready[i]))
            {
                int robot = i, level = priority;
                if (!pending.Any(p => p.robot == robot && p.priority > level))
                {
                    pending.RemoveAll(p => p.robot == robot);
                    pending.Add(new Pending(robot: i, mood: mood, priority: priority, expires: elapsed + 2.0));
                }
            }
        }
        if (!(canSpeak && elapsed >= globalReady)) return null;
        // PORT: Swift's sort is stable for the observed inputs; Swift.sorted keeps that order.
        pending = sorted(pending, (x, y) => x.priority == y.priority ? x.expires < y.expires : x.priority > y.priority).ToList();
        var observed = observations; var readyAt = ready; var time = elapsed;
        int index = pending.FindIndex(p => Simd.distance(observed[p.robot].position, listener) < 12 && (p.priority == 3 || time >= readyAt[p.robot]));
        if (index < 0) return null;
        var @event = pending[index]; pending.RemoveAt(index);
        ready[@event.robot] = elapsed + 22 + (double)next(9); globalReady = elapsed + 8;
        return new Event(robot: @event.robot, mood: @event.mood, variant: variant(robot: @event.robot, mood: @event.mood));
    }
}
