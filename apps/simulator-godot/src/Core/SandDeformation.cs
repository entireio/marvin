using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Per-race, bounded sand heightfield. Mutated only on the serial simulation
/// clock; rendering consumes immutable snapshots, never these mutable arrays.
/// PORT: Swift Dictionary/Set iteration order is unspecified (per-process hash seed); here it is
/// .NET Dictionary/HashSet order, which is deterministic. That order decides which equally
/// old tile is evicted (ensure) and the Float summation order of settle() across tile seams.
public sealed class SandDeformation
{
    public const double step = 0.0625, size = 4.0;
    public const int resolution = 64, capacity = 64;
    public readonly struct Key : IEquatable<Key>
    {
        public readonly int x, z;
        public Key(int x, int z) { this.x = x; this.z = z; }
        public bool Equals(Key other) => x == other.x && z == other.z;
        public override bool Equals(object obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, z);
        public static bool operator ==(Key a, Key b) => a.Equals(b);
        public static bool operator !=(Key a, Key b) => !a.Equals(b);
        // Invariant culture: Swift always prints an ASCII minus (sv-SE etc. would print U+2212).
        public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Key(x: {x}, z: {z})");
    }
    public struct Contact
    {
        public readonly double x, z, width, length;
        public Contact(double x, double z, double width, double length) { this.x = x; this.z = z; this.width = width; this.length = length; }
    }
    public sealed class Tile
    {
        public readonly Key key;
        // PORT: Swift `public fileprivate(set) var delta` is a copy-on-write value: a caller holding it keeps
        // a snapshot. Here it is the live array, edited in place by stamp/settle; copy it (or use grid())
        // before keeping it across simulation steps.
        public float[] delta { get; internal set; } = new float[4096];
        internal float[] @base = new float[4096];
        internal double touched = 0.0;
        internal int minI = 64, maxI = 0, minJ = 64, maxJ = 0;
        internal Tile(Key key)
        {
            this.key = key;
            for (var j = 0; j < 64; j++)
            {
                for (var i = 0; i < 64; i++)
                {
                    @base[j * 64 + i] = (float)DesertTerrain.height((double)key.x * 4 + (double)i * SandDeformation.step, (double)key.z * 4 + (double)j * SandDeformation.step);
                }
            }
        }
    }
    public Dictionary<int, Contact[]> contactLayouts = new Dictionary<int, Contact[]>();
    public Dictionary<Key, Tile> tiles { get; private set; } = new Dictionary<Key, Tile>();
    public HashSet<Key> dirty { get; private set; } = new HashSet<Key>();
    public int topologyVersion { get; private set; } = 0;
    public double clock { get; private set; } = 0.0;
    public double displacedVolume { get; private set; } = 0.0;
    private Double2[] @protected = Array.Empty<Double2>();
    public SandDeformation() { }
    public void reset() { tiles.Clear(); dirty.Clear(); topologyVersion += 1; clock = 0; displacedVolume = 0; }
    public void begin(double dt, IEnumerable<Double2> positions) { clock += dt; @protected = positions.ToArray(); dirty.Clear(); }
    public static Key key(double x, double z) => new Key((int)floor(x / 4), (int)floor(z / 4));
    private Tile ensure(Key key)
    {
        if (tiles.TryGetValue(key, out var existing)) return existing;
        var center = new Double2((double)key.x * 4 + 2, (double)key.z * 4 + 2);
        if (!(max(abs(center.x), abs(center.y)) > DesertTerrain.townEdge + 4 &&
              max(abs(center.x), abs(center.y)) < DesertTerrain.extent - 4)) return null;
        if (tiles.Count == capacity)
        {
            var guarded = @protected;
            var evictable = tiles.Values.Where(t =>
            {
                var p = new Double2((double)t.key.x * 4 + 2, (double)t.key.z * 4 + 2);
                return guarded.All(q => Simd.distance(p, q) > 8);
            });
            if (!minBy(evictable, (a, b) => a.touched < b.touched, out var old)) return null;
            tiles.Remove(old.key);
            for (var z = -1; z <= 1; z++) { for (var x = -1; x <= 1; x++) { dirty.Add(new Key(old.key.x + x, old.key.z + z)); } }
        }
        var t = new Tile(key); tiles[key] = t; topologyVersion += 1; dirty.Add(key); return t;
    }
    public float vertex(int ix, int iz)
    {
        int tx = (int)floor((double)ix / 64), tz = (int)floor((double)iz / 64);
        if (!tiles.TryGetValue(new Key(tx, tz), out var t)) return 0;
        return t.delta[(iz - tz * 64) * 64 + ix - tx * 64];
    }
    /// One immutable snapshot including the normal halo. Resolve at most nine
    /// tile references rather than doing a dictionary lookup for every vertex.
    public float[] grid(Key key)
    {
        var values = new float[67 * 67];
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!tiles.TryGetValue(new Key(key.x + dx, key.z + dz), out var tile)) continue;
                int loX = max(-1, dx * 64), hiX = min(65, dx * 64 + 63);
                int loZ = max(-1, dz * 64), hiZ = min(65, dz * 64 + 63);
                for (var j = loZ; j <= hiZ; j++)
                {
                    for (var i = loX; i <= hiX; i++)
                    {
                        values[(j + 1) * 67 + i + 1] = tile.delta[(j - dz * 64) * 64 + i - dx * 64];
                    }
                }
            }
        }
        return values;
    }
    /// Same diagonal/barycentric interpolation as the detailed surface mesh.
    public double offset(double x, double z)
    {
        if (!(tiles.Count != 0 && max(abs(x), abs(z)) > DesertTerrain.townEdge)) return 0;
        double gx = x / step, gz = z / step;
        int ix = (int)floor(gx), iz = (int)floor(gz);
        float u = (float)(gx - (double)ix), v = (float)(gz - (double)iz);
        float a = vertex(ix, iz), b = vertex(ix + 1, iz), c = vertex(ix, iz + 1), d = vertex(ix + 1, iz + 1);
        return (double)(u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v));
    }
    public double supportOffset(double x, double z, double heading, RobotCollisions.Profile profile)
    {
        if (!(tiles.Count != 0 && max(abs(x), abs(z)) > DesertTerrain.townEdge)) return offset(x, z);
        var index = Array.FindIndex(RobotCollisions.profiles, candidate => candidate.mass == profile.mass);
        if (index < 0 || !contactLayouts.TryGetValue(index, out var contacts)) return offset(x, z);
        double c = cos(heading), s = sin(heading);
        var sum = 0.0;
        var count = 0;
        foreach (var contact in contacts)
        {
            foreach (var f in new[] { -0.3, 0.0, 0.3 })
            {
                var zz = contact.z + contact.length * f;
                sum += offset(x + c * contact.x + s * zz, z - s * contact.x + c * zz); count += 1;
            }
        }
        return sum / (double)max(1, count);
    }
    public double soleHeight(Simulation state, double x, double z) =>
        state.groundY + state.duneOrientation.act(new Double3(x, 0, z)).y;
    public double contactWeight(Simulation state, Contact contact)
    {
        var x = state.x + cos(state.heading) * contact.x + sin(state.heading) * contact.z;
        var z = state.z - sin(state.heading) * contact.x + cos(state.heading) * contact.z;
        var penetration = DesertTerrain.height(x, z) + offset(x, z) - soleHeight(state, contact.x, contact.z);
        return max(0, min(1, (penetration + 0.025) / 0.05));
    }
    public void stamp(Simulation state, IEnumerable<Contact> contacts, double dt)
    {
        if (!(state.hasDirtContact && max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge + 8)) return;
        double c = cos(state.heading), s = sin(state.heading);
        var orientation = state.duneOrientation;
        double syX = orientation.act(new Double3(1, 0, 0)).y, syZ = orientation.act(new Double3(0, 0, 1)).y;
        foreach (var contact in contacts)
        {
            double cx = state.x + c * contact.x + s * contact.z, cz = state.z - s * contact.x + c * contact.z;
            var radius = hypot(contact.width / 2 + 0.20, contact.length / 2 + 0.12);
            int minX = (int)floor((cx - radius) / step), maxX = (int)ceil((cx + radius) / step);
            int minZ = (int)floor((cz - radius) / step), maxZ = (int)ceil((cz + radius) / step);
            // Allocate the footprint plus a one-cell normal/settling halo.
            for (var tz = (int)floor((double)(minZ - 2) / 64); tz <= (int)floor((double)(maxZ + 2) / 64); tz++)
            {
                for (var tx = (int)floor((double)(minX - 2) / 64); tx <= (int)floor((double)(maxX + 2) / 64); tx++) { _ = ensure(new Key(tx, tz)); }
            }
            var banks = new List<(Tile tile, int index, float weight)>();
            float removed = 0, total = 0;
            for (var iz = minZ; iz <= maxZ; iz++)
            {
                for (var ix = minX; ix <= maxX; ix++)
                {
                    var key = new Key((int)floor((double)ix / 64), (int)floor((double)iz / 64));
                    if (!tiles.TryGetValue(key, out var tile)) continue;
                    double x = (double)ix * step, z = (double)iz * step, dx = x - cx, dz = z - cz;
                    double lateral = c * dx - s * dz, along = s * dx + c * dz;
                    double edge = abs(lateral) - contact.width / 2, end = abs(along) - contact.length / 2;
                    if (!(end < 0.10 && edge < 0.19)) continue;
                    var index = (iz - key.z * 64) * 64 + ix - key.x * 64;
                    double @base = (double)tile.@base[index], sole = state.groundY + syX * (contact.x + lateral) + syZ * (contact.z + along);
                    // Exposed running gear cannot deform or spray the ground.
                    if (!(@base + (double)tile.delta[index] - sole > -0.025)) continue;
                    var irregular = 0.88 + 0.12 * sin(x * 29 + sin(z * 19)) * cos(z * 23 - x * 11);
                    var longitudinal = max(0, min(1, (0.10 - end) / 0.10));
                    if (edge < 0.015 && end < 0.025)
                    {
                        var pressure = max(0, min(1, (0.015 - edge) / 0.045)) * longitudinal;
                        // Retain some burial: loose sand meets the tread above its
                        // sole. Limit cut depth; repeated passes cannot dig forever.
                        var target = (float)max(-0.12, min(-0.035 * irregular, sole + 0.035 - @base));
                        var cut = max(0f, tile.delta[index] - target) * (float)(pressure * (1 - exp(-dt * 18)));
                        tile.delta[index] -= cut; removed += cut;
                        if (cut > 0.000001f) { changed(key, index); tile.touched = clock; }
                    }
                    else if (edge > 0.015 && edge < 0.19)
                    {
                        var weight = (float)(sin((edge - 0.015) / 0.175 * Math.PI) * longitudinal * irregular);
                        banks.Add((tile, index, weight)); total += weight;
                    }
                }
            }
            if (total > 0)
            {
                foreach (var (tile, index, weight) in banks)
                {
                    if (!(removed > 0.000001f)) continue;
                    tile.delta[index] = min(0.095f, tile.delta[index] + removed * 0.72f * weight / total); changed(tile.key, index); tile.touched = clock;
                }
            }
            displacedVolume += (double)removed * step * step;
        }
    }
    private void changed(Key key, int index)
    {
        dirty.Add(key);
        int i = index % 64, j = index / 64;
        if (tiles.TryGetValue(key, out var tile))
        {
            tile.minI = min(tile.minI, i); tile.maxI = max(tile.maxI, i);
            tile.minJ = min(tile.minJ, j); tile.maxJ = max(tile.maxJ, j);
        }
        var xs = i < 2 ? new[] { -1, 0 } : (i == 63 ? new[] { 0, 1 } : new[] { 0 });
        var zs = j < 2 ? new[] { -1, 0 } : (j == 63 ? new[] { 0, 1 } : new[] { 0 });
        foreach (var z in zs)
        {
            foreach (var x in xs)
            {
                var neighbor = new Key(key.x + x, key.z + z);
                if (tiles.ContainsKey(neighbor)) { dirty.Add(neighbor); }
            }
        }
    }
    public void settle(double dt)
    {
        // Conservative exchange between neighboring cells: loose berms soften
        // and slump downhill, while compacted tracks persist. Stop processing
        // old footprints; no whole-desert simulation or unbounded history.
        var changes = new Dictionary<Key, float[]>();
        var active = tiles.Values.Where(t => clock - t.touched < 2.5).ToArray();
        foreach (var tile in active)
        {
            var key = tile.key;
            if (!(tile.minI <= tile.maxI && tile.minJ <= tile.maxJ)) continue;
            for (var j = max(0, tile.minJ - 1); j <= min(63, tile.maxJ + 1); j++)
            {
                for (var i = max(0, tile.minI - 1); i <= min(63, tile.maxI + 1); i++)
                {
                    var index = j * 64 + i;
                    var a = tile.delta[index];
                    foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                    {
                        int ix = key.x * 64 + i + di, iz = key.z * 64 + j + dj;
                        var otherKey = new Key((int)floor((double)ix / 64), (int)floor((double)iz / 64));
                        if (!tiles.TryGetValue(otherKey, out var other)) continue;
                        var n = (iz - otherKey.z * 64) * 64 + ix - otherKey.x * 64;
                        var b = other.delta[n];
                        if (!(abs(a) + abs(b) > 0.0001f)) continue;
                        var slope = (tile.@base[index] + a) - (other.@base[n] + b);
                        var excess = max(0f, abs(slope) - (float)(step * 0.66));
                        var loose = max(0f, min(1f, (max(a, b) + 0.004f) / 0.045f));
                        var flux = ((a - b) * 0.7f + (slope < 0 ? -excess : excess) * 2.2f) * (float)dt * loose;
                        if (!(abs(flux) > 0.000001f)) continue;
                        if (!changes.ContainsKey(key)) { changes[key] = new float[4096]; }
                        if (!changes.ContainsKey(otherKey)) { changes[otherKey] = new float[4096]; }
                        changes[key][index] -= flux; changes[otherKey][n] += flux;
                    }
                }
            }
        }
        foreach (var (key, change) in changes)
        {
            if (!tiles.TryGetValue(key, out var tile)) continue;
            for (var i = 0; i < change.Length; i++)
            {
                if (!(abs(change[i]) > 0.000001f)) continue;
                tile.delta[i] += change[i]; changed(key, i);
            }
        }
    }
}
