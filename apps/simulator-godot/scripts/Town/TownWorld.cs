using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;
using static System.FormattableString;

namespace Marvin;

// PORT: TownWorld.swift is split. This file holds lines 1-1258 of the Swift file: the class
// declarations, init, exploration/shadow detail, streets, grandstand, settlement planning and
// compounds, repair pit, landmarks and district places (through buildDistrictPlaces). The rest of the
// class (buildNeighborhoodUtilities ... validate), TownMesh, TownCollisionBuilder and TownPainter
// are ported in the second part. Swift arrays are List<T>, dictionaries Dictionary, sets HashSet.

/// A compact, deterministic desert port. Static geometry is baked into spatial
/// cells with two detail levels; matching collision proxies use a spatial index.
public sealed partial class TownWorld
{
    public readonly SCNNode root = new SCNNode();
    internal readonly TownCollisionBuilder collisionBuilder = new TownCollisionBuilder();
    private bool stormActive = false;
    private HashSet<int> absentPeople = new();
    // PORT: Swift `lazy var`s.
    private CityCollisionWorld? _clearCollisions, _stormCollisions;
    private CityCollisionWorld clearCollisions => _clearCollisions ??= new CityCollisionWorld(collisionBuilder.bodies);
    private CityCollisionWorld stormCollisions => _stormCollisions ??= new CityCollisionWorld(collisionBuilder.bodies.Where((_, offset) => !absentPeople.Contains(offset)).ToList());
    public CityCollisionWorld collisionWorld => (stormActive ? stormCollisions : clearCollisions).withDynamicBodies(
        ((IEnumerable<RobotCollisions.Body>)residents?.bodies ?? Array.Empty<RobotCollisions.Body>()).Concat((IEnumerable<RobotCollisions.Body>)streetResidents?.bodies ?? Array.Empty<RobotCollisions.Body>()));
    public List<TownDoorway> doorways { get; private set; } = new();
    public TownResidents residents { get; private set; }
    public readonly struct StreetActivity
    {
        public readonly Double2 position, target; public readonly string role; public readonly int group, index;
        public StreetActivity(Double2 position, Double2 target, string role, int group, int index)
        {
            this.position = position; this.target = target; this.role = role; this.group = group; this.index = index;
        }
        public double yaw => atan2(target.x - position.x, target.y - position.y);
    }
    private List<List<StreetActivity>> pendingActivities = new();
    public List<StreetActivity> streetActivities { get; private set; } = new();
    private int walkingCount = 0;
    private List<List<Double2>> walkingStreets = new();
    public TownStreetResidents streetResidents { get; private set; }
    public int visiblePopulation => (stormActive ? crowd.stormPopulation : population - walkingCount) + (residents?.visible ?? 0) + (streetResidents?.visible ?? 0);
    public void setStorm(bool active) { stormActive = active; crowd.setStorm(active); residents?.setStorm(active); streetResidents?.setStorm(active); }
    private readonly SCNMaterial surface = CityMaterials.plaster;
    private readonly TownCrowd crowd = new TownCrowd();
    private readonly TownSigns signs = new TownSigns();
    private int storefrontSigns = 0;
    private Dictionary<string, TownCell> cells = new();
    private Dictionary<string, TownCell> explorationCells = new();
    private List<SCNNode> explorationNodes = new();
    private List<SCNNode> architectureNodes = new();
    private TownShadowBatch shadowBatch;
    public void prepareShadowBatch(SCNCamera camera)
    {
        if (shadowBatch == null)
        {
            if (root.parent is not SCNNode sceneRoot) { throw TownShadowBatch.Failure.unsupportedGeometry; }
            shadowBatch = new TownShadowBatch(root: sceneRoot, camera: camera);
        }
        shadowBatch?.setEnabled(true);
    }
    public void setShadowBatchEnabled(bool enabled) { shadowBatch?.setEnabled(enabled); }
    public Dictionary<string, int> shadowBatchTelemetry => shadowBatch?.telemetryStatistics ?? new();
    public Dictionary<string, int> shadowBatchDiagnostics => shadowBatch?.statistics ?? new();
    public List<Double3> shadowDirections = new();
    // PORT: CommandLine.arguments -> the process arguments (Godot passes game arguments after "--").
    public bool shadowCullingEnabled = Environment.GetCommandLineArgs().Contains("--benchmark-shadow-culling");
    private List<Double3> shadowProxyDirections = new();
    private List<ShadowFrustum> priorShadowFrusta = new();
    private Dictionary<SCNNode, List<ShadowBounds>> shadowVolumes = new(ReferenceEqualityComparer.Instance);
    public int shadowCasterCount { get; private set; } = 0;
    private void prepareShadowVolumes()
    {
        if (shadowDirections.Count == shadowProxyDirections.Count && shadowDirections.Zip(shadowProxyDirections).All(pair => pair.First == pair.Second)) { return; }
        shadowProxyDirections = new List<Double3>(shadowDirections); shadowVolumes.Clear();
        if (!(shadowDirections.Count == 2 && shadowDirections.All(d => double.IsFinite(d.x) && double.IsFinite(d.y) && double.IsFinite(d.z) && d.y > 0.01))) { return; }
        foreach (var node in architectureNodes)
        {
            var allBounds = new List<(SCNVector3 min, SCNVector3 max)> { node.boundingBox };
            foreach (var level in node.geometry?.levelsOfDetail ?? Array.Empty<SCNLevelOfDetail>()) { if (level.geometry?.boundingBox is { } levelBounds) { allBounds.Add(levelBounds); } }
            Double3 low = new Double3(double.PositiveInfinity), high = new Double3(double.NegativeInfinity);
            foreach (var bounds in allBounds) { foreach (var x in new[] { bounds.min.x, bounds.max.x }) { foreach (var y in new[] { bounds.min.y, bounds.max.y }) { foreach (var z in new[] { bounds.min.z, bounds.max.z }) {
                var p = node.convertPosition(new SCNVector3(x, y, z), to: null);
                var point = new Double3((double)p.x, (double)p.y, (double)p.z);
                low = Simd.min(low, point); high = Simd.max(high, point);
            }}}}
            if (!Enumerable.Range(0, 3).All(i => double.IsFinite(low[i]) && double.IsFinite(high[i]) && high[i] >= low[i])) { continue; }
            // One metre around the caster exceeds the current 116m/2048 map's
            // 2–3 texel filter footprint. Expand BEFORE low-sun projection.
            low -= new Double3(1); high += new Double3(1);
            var volumes = new List<ShadowBounds>();
            foreach (var sun in shadowDirections)
            {
                Double3 shadowLow = low, shadowHigh = high;
                foreach (var point in new ShadowBounds(low: low, high: high).corners)
                {
                    var end = point - sun * (max(0, point.y + 2) / sun.y);
                    shadowLow = Simd.min(shadowLow, end); shadowHigh = Simd.max(shadowHigh, end);
                }
                volumes.Add(new ShadowBounds(low: shadowLow, high: shadowHigh));
            }
            shadowVolumes[node] = volumes;
        }
    }
    public int explorationTriangles { get; private set; } = 0;
    public bool explorationDetailEnabled = true; // native benchmark comparison only
    public int activeExplorationCells => explorationNodes.Count(n => !n.isHidden);
    public void updateExplorationDetail(SCNVector3 camera, Double2 player, List<ShadowFrustum> frusta = null)
    {
        frusta ??= new List<ShadowFrustum>();
        if (shadowCullingEnabled) { prepareShadowVolumes(); }
        shadowCasterCount = 0;
        // A street-level view outside the circuit gets a moving detail window.
        // Neither the racing cameras nor the locked finish overview needs it.
        // Shadow casters follow the player into town. Keeping only the original
        // race-side cells enabled left every outer courtyard unshadowed.
        var focus = camera.y >= 12 ? new Double2((double)camera.x, (double)camera.z + 33) : player;
        var outside = max(abs(focus.x), abs(focus.y)) > 28;
        foreach (var node in architectureNodes)
        {
            var p = node.position;
            var enabled = outside ? hypot((double)p.x - focus.x, (double)p.z - focus.y) < 75 : (abs(p.x) < 48 && abs(p.z) < 48);
            if (enabled && shadowCullingEnabled && frusta.Count != 0 && shadowVolumes.TryGetValue(node, out var volumes)) { enabled = volumes.Any(volume => frusta.Any(frustum => frustum.intersects(volume))); }
            if (enabled) { shadowCasterCount += 1; }
            if (shadowBatch?.setCaster(node, enabled: enabled) != true && node.castsShadow != enabled) { node.castsShadow = enabled; }
        }
        var exploring = explorationDetailEnabled && max(abs(focus.x), abs(focus.y)) > 28 && camera.y < 60;
        foreach (var node in explorationNodes)
        {
            var distance = hypot((double)(camera.x - node.position.x), (double)(camera.z - node.position.z));
            var amount = exploring ? max(0, min(1, (58 - distance) / 16)) : 0;
            node.isHidden = amount == 0;
            if (amount > 0) { node.opacity = (CGFloat)(amount * amount * (3 - 2 * amount)); }
        }
    }
    public int buildings { get; private set; } = 0;
    public int population { get; private set; } = 0;
    public List<Double2> householdYards { get; private set; } = new();
    private Dictionary<string, SpectatorSoundZone> spectatorZones = new();
    public List<SpectatorSoundZone> spectatorSoundZones => spectatorZones.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => spectatorZones[k]).ToList();
    /// Acoustic emitters come from the same authored venues and visible activities.
    private List<TownSoundZone> _soundZones;
    public List<TownSoundZone> soundZones
    {
        get => _soundZones ??= new Func<List<TownSoundZone>>(() =>
        {
            var zones = enumerated(venueSites).Select(entry =>
                new TownSoundZone(position: entry.element.center, kind: entry.offset == 0 ? TownSoundZone.Kind.workshop : entry.offset == 1 ? TownSoundZone.Kind.cantina : TownSoundZone.Kind.market)).ToList();
            zones.AddRange(InfieldLayout.tentOrigins.Select(origin => new TownSoundZone(position: origin, kind: TownSoundZone.Kind.workshop, activity: 0.65, infieldRepair: true)));
            var groups = streetActivities.GroupBy(activity => activity.group).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var key in sorted(groups.Keys))
            {
                var group = groups[key];
                if (!(group.Count > 0 && group.Count > 1)) { continue; }
                var first = group[0];
                if (first.role.Contains("market") || first.role.Contains("conversation"))
                {
                    zones.Add(new TownSoundZone(position: first.position, kind: TownSoundZone.Kind.market, activity: 0.22));
                }
            }
            return zones;
        })();
        set => _soundZones = value;
    }
    public int triangleCount { get; private set; } = 0;
    public int coarseTriangles { get; private set; } = 0;
    public List<TownLot> lots { get; private set; } = new();
    public List<List<Double2>> mapBuildings { get; private set; } = new();
    private List<(Double3, Double3)> cameraBounds = new();
    private double clock = 0.0;
    private readonly uint sand = 0xc5a174, cream = 0xe6c89c;
    private readonly uint rust = 0xa25a40, teal = 0x427c80;
    private readonly uint dark = 0x3e4545, trim = 0x9d805e;
    private readonly double finishZ = DirtCourse.point(0).z;

    public readonly struct TownLot
    {
        public readonly double x, z, width, depth;
        public TownLot(double x, double z, double width, double depth) { this.x = x; this.z = z; this.width = width; this.depth = depth; }
    }
    private sealed class TownCell
    {
        public readonly TownMesh near = new TownMesh(), far = new TownMesh();
        public readonly Float3 origin;
        public TownCell(float x, float z) { origin = new Float3(x, 0, z); }
    }

    public TownWorld(Action<double, string> progress = null)
    {
        root.name = "Mos Aster desert spaceport";
        progress?.Invoke(0.02, "Laying out town streets");
        buildRoads();
        progress?.Invoke(0.08, "Building spectator stands");
        buildGrandstand();
        progress?.Invoke(0.12, "Building landmarks");
        buildLandmarks();
        buildDistrictPlaces();
        progress?.Invoke(0.14, "Building town districts");
        buildSettlement();
        progress?.Invoke(0.38, "Preparing the repair yard");
        buildRepairPit();
        progress?.Invoke(0.48, "Adding spectators");
        buildStreetLife();
        progress?.Invoke(0.54, "Adding town details");
        buildMarketDetails();
        progress?.Invoke(0.59, "Weathering the town");
        buildReferenceDetails();
        progress?.Invoke(0.64, "Placing signs");
        buildWayfinding();
        buildNeighborhoodUtilities();
        buildDoorstepLife();
        buildDomesticCourts();
        buildHouseholdYards();
        refreshPedestrianAccess();
        root.addChildNode(TownGround.build(access: pedestrianAccess.Concat(venueAccess).ToList(), yards: householdYards));
        placeStreetActivities();
        progress?.Invoke(0.66, "Connecting residents to their homes");
        residents = new TownResidents(doors: doorways, city: clearCollisions, crowd: crowd, root: root, count: 10);
        population -= walkingCount - (residents?.walkers.Count ?? 0);
        walkingCount = residents?.walkers.Count ?? 0;
        streetResidents = new TownStreetResidents(paths: walkingStreets, city: clearCollisions, crowd: crowd, root: root);
        population += streetResidents?.walkers.Count ?? 0;
        walkingCount += streetResidents?.walkers.Count ?? 0;
        crowd.finish(into: root);
        var keys = cells.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var (index, key) in enumerated(keys))
        {
            progress?.Invoke(0.70 + 0.30 * (double)index / (double)keys.Count, "Preparing the town");
            var cell = cells[key];
            var near = cell.near.geometry(surface, cell.origin);
            var far = cell.far.geometry(surface, cell.origin);
            near.levelsOfDetail = new[] { new SCNLevelOfDetail(geometry: far, worldSpaceDistance: 82) };
            var node = new SCNNode(near);
            // PORT: node.simdPosition = cell.origin (SIMD3<Float>).
            node.position = new SCNVector3(cell.origin.x, cell.origin.y, cell.origin.z); node.name = $"Town cell {key}";
            // Initial racing footprint; exploration moves the bounded caster set.
            node.castsShadow = abs(cell.origin.x) < 48 && abs(cell.origin.z) < 48;
            architectureNodes.Add(node); root.addChildNode(node);
            triangleCount += cell.near.indices.Count / 3;
            coarseTriangles += cell.far.indices.Count / 3;
        }
        foreach (var key in explorationCells.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var cell = explorationCells[key];
            var node = new SCNNode(cell.near.geometry(surface, cell.origin));
            node.position = new SCNVector3(cell.origin.x, cell.origin.y, cell.origin.z); node.name = $"Exploration detail {key}";
            node.castsShadow = false; node.isHidden = true;
            explorationTriangles += cell.near.indices.Count / 3;
            explorationNodes.Add(node); root.addChildNode(node);
        }
        // CPU mesh builders are no longer needed once the GPU buffers exist.
        explorationCells.Clear();

    }

    /// Swift `.enumerated()`.
    private static IEnumerable<(int offset, T element)> enumerated<T>(IEnumerable<T> source)
    {
        var offset = 0;
        foreach (var element in source) { yield return (offset, element); offset += 1; }
    }

    private TownPainter explorationPainter(double x, double z, double yaw)
    {
        int ix = (int)floor(x / 16), iz = (int)floor(z / 16); var key = Invariant($"{ix},{iz}");
        var cell = explorationCells.TryGetValue(key, out var existing) ? existing : new TownCell((float)(ix * 16 + 8), (float)(iz * 16 + 8));
        explorationCells[key] = cell;
        return new TownPainter(near: cell.near, far: cell.far, origin: new Float3((float)x, 0, (float)z), yaw: (float)yaw);
    }
    private TownCell cell(double x, double z)
    {
        var span = max(abs(x), abs(z)) < 48 ? 16.0 : 32.0;
        int ix = (int)floor(x / span), iz = (int)floor(z / span); var key = Invariant($"{(int)span}:{ix},{iz}");
        if (cells.TryGetValue(key, out var existing)) { return existing; }
        var value = new TownCell((float)((double)ix * span + span / 2), (float)((double)iz * span + span / 2));
        cells[key] = value; return value;
    }
    private TownPainter paint(double x, double z, double yaw = 0)
    {
        var c = cell(x, z);
        return new TownPainter(near: c.near, far: c.far, origin: new Float3((float)x, 0, (float)z), yaw: (float)yaw, collisions: collisionBuilder);
    }
    /// Entire footprint plus a margin must clear every part of the spline.
    /// Dense edge/interior samples also prevent a lot spanning another branch.
    private bool clearLot(double x, double z, double w, double d)
    {
        if (abs(x) - w / 2 > 22 || abs(z) - d / 2 > 22) { return true; }
        int nx = (int)ceil(w / 0.35), nz = (int)ceil(d / 0.35);
        for (var i = 0; i <= nx; i++) { for (var j = 0; j <= nz; j++) {
            double px = x - w / 2 + w * (double)i / (double)nx, pz = z - d / 2 + d * (double)j / (double)nz;
            if (DirtCourse.projection(x: px, z: pz).distance < DirtCourse.terrainEdge + 0.6) { return false; }
        }}
        return true;
    }

    private readonly struct Street
    {
        public readonly List<Double2> points;
        public readonly double width;
        public readonly int kind;
        public readonly List<Double2> path;
        public Street(List<Double2> points, double width, int kind = 0)
        {
            this.points = points; this.width = width; this.kind = kind;
            // Interpolating Hermite curves retain junctions while easing changes
            // of direction. Tangents are limited by the shorter adjacent block.
            var tangents = Enumerable.Range(0, points.Count).Select(i =>
            {
                if (i == 0) { return points[1] - points[0]; }
                if (i == points.Count - 1) { return points[i] - points[i - 1]; }
                Double2 incoming = points[i] - points[i - 1], outgoing = points[i + 1] - points[i];
                return Simd.normalize(Simd.normalize(incoming) + Simd.normalize(outgoing)) * min(Simd.length(incoming), Simd.length(outgoing));
            }).ToList();
            var samples = new List<Double2>();
            for (var i = 0; i < points.Count - 1; i++)
            {
                var count = max(8, (int)ceil(Simd.length(points[i + 1] - points[i]) / 0.9));
                for (var j = 0; j < count; j++)
                {
                    double t = (double)j / (double)count, t2 = t * t, t3 = t2 * t;
                    samples.Add(points[i] * (2 * t3 - 3 * t2 + 1) + tangents[i] * (t3 - 2 * t2 + t) + points[i + 1] * (-2 * t3 + 3 * t2) + tangents[i + 1] * (t3 - t2));
                }
            }
            path = samples.Concat(new[] { points[points.Count - 1] }).ToList();
        }
    }
    // Destination-led arteries: market approach, dock access and northern trade
    // route. None forms a closed perimeter around the race.
    public List<List<Double2>> mapStreets => streets.Select(street => street.path).ToList();
    private readonly List<Street> streets = new()
    {
        // Broad shared sandy streets; the gaps between compounds form local
        // passages. Do not clear residential loops or individual driveways.
        new Street(points: new() { new(-150, -76), new(-64, -39), new(-35, -29), new(-15, -27), new(0, -26), new(18, -31), new(45, -45), new(145, -68) }, width: 3.8),
        new Street(points: new() { new(45, -45), new(35, -25), new(28, -10), new(33, 3), new(35, 14) }, width: 4.2),
        new Street(points: new() { new(35, 14), new(36, 30), new(18, 36), new(-8, 32), new(-30, 39), new(-64, 58), new(-145, 80) }, width: 3.6),
        new Street(points: new() { new(-145, -5), new(-62, -8), new(-39, -15), new(-35, -29) }, width: 2.4, kind: 1),
        new Street(points: new() { new(36, 30), new(60, 48), new(105, 42), new(150, 52) }, width: 4.0),
        new Street(points: new() { new(-64, 58), new(-48, 92), new(-65, 150) }, width: 2.0, kind: 1),
        // Secondary streets connect districts, not individual driveways. Keep
        // the established circuit-side fabric and all six original arteries.
        new Street(points: new() { new(-64, -39), new(-84, -20), new(-92, 10), new(-80, 37), new(-64, 58) }, width: 2.3, kind: 1),
        new Street(points: new() { new(-64, -39), new(-67, -57), new(-42, -64), new(-12, -78), new(20, -72), new(55, -67), new(81, -42), new(78, -14), new(65, 17), new(60, 48) }, width: 2.8, kind: 1),
        new Street(points: new() { new(-48, 92), new(-20, 86), new(11, 69), new(40, 80), new(69, 69), new(60, 48) }, width: 2.5, kind: 1),
        new Street(points: new() { new(-84, -20), new(-111, -40), new(-102, -68), new(-72, -87), new(-42, -64) }, width: 2.0, kind: 1),
        new Street(points: new() { new(20, -72), new(18, -102), new(51, -112), new(79, -93), new(81, -42) }, width: 2.1, kind: 1),
        new Street(points: new() { new(105, 42), new(113, 17), new(104, -10), new(78, -14) }, width: 2.2, kind: 1),
        new Street(points: new() { new(-80, 37), new(-111, 55), new(-100, 78), new(-48, 92) }, width: 1.8, kind: 1),
    };


    // Native visual/performance survey follows the actual eastbound street.
    public Double2 explorationSurveyPoint(double fraction)
    {
        var path = streets[4].path; var t = max(0, min(1, fraction)) * (double)(path.Count - 1);
        var i = min(path.Count - 2, (int)t); var blend = t - (double)i;
        return path[i] * (1 - blend) + path[i + 1] * blend;
    }
    private bool preservingTrackside = false;
    private double streetDistance(double x, double z)
    {
        var p = new Double2(x, z);
        var distance = double.MaxValue;
        foreach (var street in (preservingTrackside ? streets.Take(6).ToList() : streets)) { for (var i = 1; i < street.path.Count; i++) {
            Double2 a = street.path[i - 1], d = street.path[i] - a;
            var t = max(0, min(1, Simd.dot(p - a, d) / Simd.length_squared(d)));
            distance = min(distance, Simd.length(p - a - d * t) - street.width / 2);
        }}
        return distance;
    }
    private void drawStreets(List<Street> routes, string name)
    {
        var mesh = new TownMesh();
        foreach (var street in routes)
        {
            // One continuous ground ribbon, with soft shoulders. No overlapping
            // rectangular slabs or ruler-straight parallel cart-track markings.
            var shoulder = 0.6;
            var widths = new List<double> { -street.width / 2 - shoulder, -street.width * 0.30, street.width * 0.30, street.width / 2 + shoulder };
            uint ink = street.kind == 0 ? 0x9d8d75u : 0xa99a81u;
            float opacity = street.kind == 0 ? 0.44f : 0.23f;
            var normals = Enumerable.Range(0, street.path.Count).Select(i =>
            {
                var tangent = Simd.normalize(street.path[min(i + 1, street.path.Count - 1)] - street.path[max(0, i - 1)]);
                return new Double2(-tangent.y, tangent.x);
            }).ToList();
            for (var i = 1; i < street.path.Count; i++)
            {
                for (var band = 0; band < widths.Count - 1; band++)
                {
                    Float3 vertex(int j, int edge)
                    {
                        var center = street.path[j];
                        var irregular = 1 + 0.07 * sin(center.x * 0.63 + center.y * 0.31) + 0.04 * sin(center.y * 1.43);
                        var p = center + normals[j] * widths[edge] * irregular;
                        return new Float3((float)p.x, (float)(-0.016 + (double)street.kind * 0.001), (float)p.y);
                    }
                    Float3 a = vertex(i - 1, band), b = vertex(i, band), c = vertex(i, band + 1), d = vertex(i - 1, band + 1);
                    var first = mesh.colors.Count / 4;
                    mesh.triangle(a, c, b, ink); mesh.triangle(a, d, c, ink);
                    float left = band == 0 ? 0 : opacity;
                    float right = band == widths.Count - 2 ? 0 : opacity;
                    foreach (var (j, alpha) in enumerated(new[] { left, right, left, left, right, right }))
                    {
                        Float3 v = new[] { a, c, b, a, d, c }[j]; var p = new Double2((double)v.x, (double)v.z);
                        var fade = 1.0;
                        foreach (var end in new[] { street.path[0], street.path[street.path.Count - 1] })
                        {
                            if (!(TownFootprint.edgeDistance(end) > 0)) { continue; }
                            var t = min(1, Simd.distance(p, end) / 14);
                            fade *= t * t * (3 - 2 * t);
                        }
                        mesh.colors[(first + j) * 4 + 3] = alpha * (float)fade;
                    }
                }
            }
        }
        var material = new SCNMaterial();
        material.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        material.diffuse.contents = NSColor.white; material.roughness.contents = 1.0;
        material.transparencyMode = SCNTransparencyMode.aOne;
        material.writesToDepthBuffer = false;
        material.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
vec4 streetTint;
#pragma body
streetTint = vec4(pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2)), COLOR.a);
",
            [SCNShaderModifierEntryPoint.surface] = "ALBEDO = streetTint.rgb;",
            [SCNShaderModifierEntryPoint.fragment] = TownGround.pigmentFunctions + "\n" + @"
#pragma transparent
#pragma body
vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float angle = atan(p.y, p.x);
float radius = 130.0 + 13.0 * sin(3.0 * angle + 0.4) + 9.0 * cos(5.0 * angle - 0.7) + 6.0 * sin(2.0 * angle);
float edge = length(p) - radius;
float exposure = smoothstep(-18.0, 22.0, edge);
// Oblique wind-driven tongues eat through the road at different widths;
// exposed remnants become smaller until the underlying sand covers all.
vec2 wind = vec2(p.x * 0.86 + p.y * 0.51, -p.x * 0.51 + p.y * 0.86);
float tongues = townNoise(wind / vec2(2.8, 0.75));
float broken = smoothstep(0.28, 0.72, tongues * 0.65 + exposure * 0.70);
float remaining = (1.0 - smoothstep(-4.0, 22.0, edge)) * (1.0 - exposure * broken);
float alpha = streetTint.a * remaining;
ALPHA = alpha;
",
        };
        var road = new SCNNode(mesh.geometry(material));
        road.name = name; road.castsShadow = false; root.addChildNode(road);
    }
    private void buildRoads()
    {
        drawStreets(streets, name: "Worn shared sandy streets");
        var plaza = paint(0, finishZ - 7.0);
        plaza.box(0, -0.006, 0, 19, 0.018, 7.0, 0xab9679);
    }

    private void buildGrandstand()
    {
        double z = finishZ - 4.9; var p = paint(0, z);
        // Shops form the plinth, with seating rising toward the back of town.
        // Structural arcade: real recessed bays, not dark rectangles on a solid box.
        p.box(0, 0.68, -2.36, 12.8, 1.36, 0.30, 0xb49d7e);
        p.box(0, 1.34, -0.65, 12.8, 0.18, 3.8, 0xc7b18b);
        p.box(0, 0.04, -0.65, 12.8, 0.08, 3.8, 0x8a7964);
        p.box(0, 0.12, 0.9, 13.2, 0.24, 1.0, cream);
        foreach (var (bay, x) in enumerated(stride(from: -5.5, through: 5.5, by: 1.85)))
        {
            p.arcade(x, 0.05, 1.22, 1.44, 1.22, 0.42, 0.17, 0xc5b08b);
            p.box(x, 0.61, 0.32, 1.42, 1.06, 0.08, 0x51493d, detail: true);
            p.box(x, 0.16, 1.36, 1.54, 0.15, 0.47, 0xa9977b);
            p.awning(x, 1.66, 1.64, 1.02, 1.46, bay % 2 == 0 ? 0xad9671u : 0x758680u);
            foreach (var sx in new[] { -0.77, 0.77 })
            {
                p.beam(new Double3(x + sx, 0.12, 2.06), new Double3(x + sx, 1.44, 2.06), 0.025, 0x5e5548, sides: 6);
                p.beam(new Double3(x + sx, 1.42, 2.06), new Double3(x + sx, 1.57, 1.10), 0.017, 0x746554, sides: 6);
            }
            // Joinery, open shelves, pots and supply cases are visible in the alcove.
            foreach (var shelf in new[] { 0.42, 0.85 })
            {
                p.box(x, shelf, 0.73, 1.2, 0.055, 0.46, 0x77634c, detail: true);
                for (var k = 0; k < 4; k++)
                {
                    var xx = x - 0.43 + (double)k * 0.28;
                    if ((k + bay) % 2 == 0) { p.cylinder(xx, shelf + 0.12, 0.74, 0.08, 0.10, 0.22, 0xab8c64, sides: 10, detail: true); }
                    else { p.box(xx, shelf + 0.12, 0.74, 0.19, 0.23, 0.20, 0x64756c, detail: true); }
                }
            }
            p.plasterPatch(x - 0.88, 0.38, 1.448, 0.08, 0.27, 0x927f64, seed: bay);
        }
        for (var row = 0; row < 5; row++)
        {
            double y = 1.4 + (double)row * 0.31, rz = 0.72 - (double)row * 0.56;
            p.box(0, y - 0.12, rz, 12.6, 0.24, 0.58, cream);
            p.box(0, y + 0.06, rz - 0.12, 12.1, 0.12, 0.25, rust, detail: true);
            for (var seat = 0; seat < 22; seat++)
            {
                if (!(seat != 10 && seat != 11 && (seat * 7 + row * 11) % 13 > 1)) { continue; }
                var x = -5.7 + (double)seat * 0.54 + sin((double)(seat * 17 + row)) * 0.035;
                var turn = sin((double)(seat * 7 + row * 13)) * 0.23;
                citizen(x, z + rz + cos((double)(seat * 11)) * 0.035, y: y + 0.12, yaw: turn, index: row * 22 + seat, seated: true);
            }
        }
        // Central aisle and accessible-looking side stairs, not a solid slab.
        foreach (var side in new[] { -1.0, 1 })
        {
            for (var step = 0; step < 12; step++)
            {
                var h = (double)(step + 1) * 0.225;
                p.box(side * 6.95, h / 2, 1.1 - (double)step * 0.30, 0.85, h, 0.31, cream);
            }
            p.box(side * 6.45, 2.05, -0.4, 0.09, 1.45, 3.65, trim);
        }
        // Shade roof with a gap over the central aisle. Opaque geometry avoids
        // layers of alpha over the crowded start and finish.
        foreach (var (i, x) in enumerated(new[] { -3.45, 3.45 }))
        {
            p.canopy(x, -1.12, 5.65, 2.8, 3.83, 0.32, i == 0 ? 0xb6a17eu : 0x9eaa9bu);
            p.valance(x, 0.285, 5.65, 3.83, 0.23, i == 0 ? 0xb6a17eu : 0x9eaa9bu, rise: 0.32);
            foreach (var edge in new[] { -2.8, 2.8 })
            {
                p.cable(new Double3(x + edge, 3.83, 0.28), new Double3(x + edge, 2.85, 0.28), 0.018, 0x766653);
            }
            // Tension seams, reinforced hems and roof battens follow the cloth.
            foreach (var xx in new[] { -1.85, 0, 1.85 })
            {
                p.beam(new Double3(x + xx, 3.79, -2.52), new Double3(x + xx, 3.79, 0.27), 0.025, 0x75634e, sides: 6);
            }
        }
        foreach (var (i, x) in enumerated(new[] { -5.2, -2.4, 2.4, 5.2 }))
        {
            var sector = new[] { "A", "B", "C", "D" }[i];
            signs.plate($"SECTOR {sector}", eyebrow: "GRANDSTAND", footer: "ROWS 01–05", badge: sector,
                        at: new SCNVector3(x, 3.55, z + 0.29), width: 1.05, height: 0.32, accent: teal, into: root);
            p.beam(new Double3(x, 3.73, 0.29), new Double3(x, 3.82, 0.29), 0.018, dark, sides: 5);
        }
        // Two plaster towers bookend the stand and make the civic landmark
        // readable from the track as well as the opening overview.
        foreach (var x in new[] { -8.5, 8.5 })
        {
            buildings += 1;
            var q = paint(x, finishZ - 5.5);
            q.adobe(0, 1.75, 0, 2.4, 3.5, 3.6, sand);
            foreach (var side in new[] { -1.0, 1 }) { q.door(0, side * 1.81, 0.70, 1.35, dark, sand, side: side); }
            q.cylinder(0, 4.45, 0, 0.85, 0.62, 1.8, cream);
            q.dome(0, 5.315, 0, 0.72, 0.55, 0.72, sand);
            q.box(0, 4.55, 0.72, 0.45, 0.65, 0.30, sand, detail: true);
            q.box(0, 4.55, 0.88, 0.27, 0.42, 0.025, dark, detail: true);
            for (var j = 0; j < 4; j++) { q.box(0, 4.41 + (double)j * 0.09, 0.90, 0.30, 0.025, 0.035, trim, detail: true); }
            q.box(0, 6.1, 0, 0.055, 0.85, 0.055, dark, detail: true);
        }
        signs.plate("MOS ASTER GRAND PRIX", eyebrow: "INTERPLANETARY RACING SERIES", footer: "START  /  FINISH", badge: "07",
                    at: new SCNVector3(0, 1.54, z + 2.10), width: 5.7, height: 0.68, into: root);
        foreach (var (i, x) in enumerated(new[] { -8.5, 8.5 }))
        {
            signs.plate($"GATE {i + 1}", eyebrow: "GRANDSTAND", footer: i == 0 ? "SECTORS A / B" : "SECTORS C / D", badge: i == 0 ? "A" : "C",
                        at: new SCNVector3(x, 2.37, finishZ - 3.64), width: 1.78, height: 0.58, accent: teal, into: root);
        }
    }

    public readonly struct Entrance
    {
        public readonly Double2 center; public readonly double yaw, width, height; public readonly int variant; public readonly bool walkable;
        public Entrance(Double2 center, double yaw, double width, double height, int variant, bool walkable)
        {
            this.center = center; this.yaw = yaw; this.width = width; this.height = height; this.variant = variant; this.walkable = walkable;
        }
    }
    public List<Entrance> entrances { get; private set; } = new();
    public List<Double3> windowSupports { get; private set; } = new();
    public readonly struct PedestrianAccess
    {
        public readonly Double2 building, door; public readonly List<Double2> route;
        public PedestrianAccess(Double2 building, Double2 door, List<Double2> route) { this.building = building; this.door = door; this.route = route; }
    }
    public List<PedestrianAccess> pedestrianAccess { get; private set; } = new();
    public List<Double2> inaccessibleBuildings { get; private set; } = new();
    public List<PedestrianAccess> venueAccess { get; private set; } = new();
    private Dictionary<int, DoorPlan> plannedDoors = new();
    private TownAccessMap accessMap;
    private readonly struct Facade
    {
        public readonly double x, z, turn, span, height;
        public Facade(double x, double z, double turn, double span, double height) { this.x = x; this.z = z; this.turn = turn; this.span = span; this.height = height; }
    }
    // PORT: a class (immutable) so a missing plan is null, like Swift's optional.
    private sealed class DoorPlan
    {
        public readonly Facade facade; public readonly double offset, width, height; public readonly int variant;
        public DoorPlan(Facade facade, double offset, double width, double height, int variant) { this.facade = facade; this.offset = offset; this.width = width; this.height = height; this.variant = variant; }
    }
    private List<Facade> compoundFacades(double w, double d, double h, int style)
    {
        var faces = new List<Facade>();
        void block(double cx, double cz, double width, double depth, double height, int[] sides)
        {
            foreach (var side in sides)
            {
                var turn = (double)side * Math.PI / 2;
                var half = side % 2 == 0 ? depth / 2 : width / 2;
                faces.Add(new Facade(x: cx + sin(turn) * half, z: cz + cos(turn) * half, turn: turn, span: side % 2 == 0 ? width : depth, height: height));
            }
        }
        if (style < 3)
        {
            block(-w * 0.17, 0, w * 0.66, d, h * 0.86, sides: new[] { 0, 2, 3 });
            block(w * 0.25, d * 0.10, w * 0.50, d * 0.79, h * 0.64, sides: new[] { 0, 1 });
        }
        else if (style == 6)
        {
            block(-w * 0.34, 0, w * 0.32, d, h * 0.84, sides: new[] { 0, 2, 3 });
            block(w * 0.40, d * 0.07, w * 0.20, d * 0.69, h * 0.50, sides: new[] { 1 });
        }
        else if (style == 8)
        {
            block(-w * 0.23, -d * 0.05, w * 0.54, d * 0.90, h, sides: new[] { 0, 2, 3 });
            block(w * 0.26, 0, w * 0.47, d, h * 0.58, sides: new[] { 0, 1, 2 });
        }
        else { block(0, 0, w, d, style == 5 ? max(1.6, h * 0.44) : h * 0.60, sides: new[] { 0, 1, 2, 3 }); }
        return faces;
    }
    private DoorPlan entrancePlan(double x, double z, double w, double d, double h, int index, double yaw, int style)
    {
        var faces = compoundFacades(w: w, d: d, h: h, style: style);
        (double, DoorPlan)? best = null;
        var seed = unchecked((uint)((long)index * 747796405L + 2891336453L));
        var hash = (int)((seed ^ (seed >> 16)) & 0x7fffffff);
        foreach (var (f, face) in enumerated(faces))
        {
            var variant = (hash / 31 + f) % 5;
            var width = min(face.span - 0.44, new[] { 0.78, 1.03, 0.88, 1.32, 0.96 }[variant]);
            var height = min(face.height - 0.18, new[] { 1.45, 1.38, 1.62, 1.48, 1.36 }[variant]);
            if (!(width >= 0.65 && height >= 1.12)) { continue; }
            for (var slot = 0; slot < 5; slot++)
            {
                var offset = ((double)slot - 2) / 2 * max(0, (face.span - width) / 2 - 0.55);
                double lx = face.x + cos(face.turn) * offset, lz = face.z - sin(face.turn) * offset;
                var center = new Double2(x + cos(yaw) * lx + sin(yaw) * lz, z - sin(yaw) * lx + cos(yaw) * lz);
                var @out = new Double2(sin(yaw + face.turn), cos(yaw + face.turn));
                if (!(accessMap?.streetDistance(center + @out * 0.55) is double distance &&
                      accessMap?.streetDistance(center + @out * 0.65) != null)) { continue; }
                // Prefer a short public approach, but stagger neighbouring entries.
                var score = distance + (double)((slot + hash) % 5) * 0.32;
                if (best == null || score < best.Value.Item1) { best = (score, new DoorPlan(facade: face, offset: offset, width: width, height: height, variant: variant)); }
            }
        }
        return best?.Item2;
    }
    private ulong citySeed = 0xA57E2026;
    private double random()
    {
        citySeed = unchecked(citySeed * 6364136223846793005UL + 1442695040888963407UL);
        return (double)(citySeed >> 32) / (double)uint.MaxValue;
    }
    private bool reserved(double x, double z, double w, double d, bool checkStreet = true)
    {
        if (venueSites.Any(site => abs(x - site.center.x) < site.halfWidth + w / 2 + 1.1 && abs(z - site.center.y) < site.halfDepth + d / 2 + 1.1)) { return true; }
        if (infield(x, z) || !clearLot(x, z, w, d) || CityExit.reserved(new Double2(x, z), radius: hypot(w, d) / 2)) { return true; }
        if (checkStreet && streetDistance(x, z) < hypot(w, d) * 0.44) { return true; }
        if (abs(x) < 11 + w / 2 && abs(z - (finishZ - 6)) < 3.8 + d / 2) { return true; }
        if (abs(x - 26) < 5.2 + w / 2 && abs(z - 17) < 7.0 + d / 2) { return true; }
        if (abs(x - 3) < 4.4 + w / 2 && abs(z - 22) < 1.6 + d / 2) { return true; }
        // Authored civic landmarks have room to breathe without an empty belt.
        foreach (var (cx, cz, r) in new[] { (-27.0, 22.0, 3.0), (10.0, 43.0, 5.0), (-38.0, -5.0, 5.0), (43.0, 7.0, 5.0) })
        {
            if (abs(x - cx) < r + w / 2 && abs(z - cz) < r + d / 2) { return true; }
        }
        return false;
    }
    private void buildSettlement()
    {
        int index = 0, row = 0;
        var compounds = new List<(double, double, double, double, double, int, double)>();
        void reserve(double x, double z, double w, double d, double h, int i, double yaw)
        {
            compounds.Add((x, z, w, d, h, i, yaw));
            lots.Add(new TownLot(x: x, z: z, width: w * abs(cos(yaw)) + d * abs(sin(yaw)) + 0.2, depth: d * abs(cos(yaw)) + w * abs(sin(yaw)) + 0.2));
        }
        preservingTrackside = true;
        {
            var z = -145.0;
            // Reproduce the established core exactly, including its random stream
            // and infill clearance, before replacing only the outer districts.
            while (z < 145)
            {
                var stepZ = 6.4 + random() * 2.4;
                var x = -150.0 + random() * 6;
                while (x < 150)
                {
                    var stepX = 5.8 + random() * 3.6;
                    double bx = x + stepX / 2, bz = z + stepZ / 2 + sin((double)row * 1.9 + (double)index * 0.67) * 0.55;
                    double w = stepX - 0.38, d = stepZ - 0.42;
                    var yaw = (random() - 0.5) * 0.16;
                    double boundW = w * abs(cos(yaw)) + d * abs(sin(yaw)), boundD = d * abs(cos(yaw)) + w * abs(sin(yaw));
                    var h = 2.0 + random() * 3.8;
                    if (!reserved(bx, bz, boundW + 0.2, boundD + 0.2))
                    {
                        reserve(bx, bz, w, d, h, index, yaw);
                    }
                    x += stepX; index += 1;
                }
                z += stepZ; row += 1;
            }
        }
        // Smaller infill hugs the course where full urban compounds cannot fit.
        for (var j = 0; j < 16; j++) { for (var i = 0; i < 16; i++) {
            double x = -29.0 + (double)i * 3.8, z = -28.0 + (double)j * 3.8;
            double w = 2.9, d = 2.8;
            if (reserved(x, z, w + 0.4, d + 0.4)) { continue; }
            if (lots.Any(lot => abs(x - lot.x) < (lot.width + w) / 2 + 0.15 && abs(z - lot.z) < (lot.depth + d) / 2 + 0.15)) { continue; }
            reserve(x, z, w, d, 1.8 + random() * 1.5, 1400 + i + j * 16, 0);
        }}
        compounds = compounds.Where(compound => max(abs(compound.Item1), abs(compound.Item2)) < 56).Select(compound =>
        {
            var (x, z, w, d, h, i, yaw) = compound;
            // Keep trackside positions and silhouettes, but open pedestrian
            // lanes within the formerly impenetrable residential blocks.
            var setback = w > 4.5 && d > 4.5 ? 1.5 : 0.0;
            return (x, z, w - setback, d - setback, max(h, 2.35), i, yaw);
        }).ToList();
        void updateLots()
        {
            lots = compounds.Select(compound =>
            {
                var (x, z, w, d, _, _, yaw) = compound;
                return new TownLot(x: x, z: z, width: w * abs(cos(yaw)) + d * abs(sin(yaw)) + 0.2, depth: d * abs(cos(yaw)) + w * abs(sin(yaw)) + 0.2);
            }).ToList();
        }
        updateLots();
        preservingTrackside = false;
        // Outside the protected trackside core, grow street-facing blocks by
        // deterministic dart throwing. Variable setbacks and usable passages
        // replace nearly touching rectangular rows. Wider civic courts break
        // dense neighborhoods, and density tapers at the irregular urban edge.
        var courts = new List<(Double2, double)> { (new Double2(-84, -20), 6), (new Double2(-42, -64), 5),
            (new Double2(55, -67), 7), (new Double2(60, 48), 7), (new Double2(-20, 86), 5), (new Double2(-80, 37), 6) };
        (double distance, double yaw) frontage(Double2 p)
        {
            double best = double.PositiveInfinity, yaw = 0.0;
            foreach (var street in streets) { for (var i = 1; i < street.path.Count; i += 3) {
                Double2 a = street.path[max(0, i - 3)], d = street.path[i] - a;
                var t = max(0, min(1, Simd.dot(p - a, d) / max(0.0001, Simd.length_squared(d))));
                var distance = Simd.length(p - a - d * t) - street.width / 2;
                if (distance < best) { best = distance; yaw = -atan2(d.y, d.x); }
            }}
            return (best, yaw);
        }
        void place(double x, double z, double w, double d, double h, double yaw, int i, double gap = 1.25)
        {
            var p = new Double2(x, z); double c = abs(cos(yaw)), s = abs(sin(yaw)), bw = w * c + d * s, bd = d * c + w * s;
            if (max(abs(x), abs(z)) < 56 || TownFootprint.edgeDistance(p) > -hypot(w, d) / 2 - 1) { return; }
            if (courts.Any(court => Simd.distance(p, court.Item1) < court.Item2 + hypot(w, d) / 2)) { return; }
            if (reserved(x, z, bw + 0.4, bd + 0.4, checkStreet: false)) { return; }
            Double2 u = new Double2(cos(yaw), -sin(yaw)), v = new Double2(sin(yaw), cos(yaw));
            // Test the real rotated footprint, not an inflated circumscribed
            // rectangle that pushes every street facade several metres away.
            foreach (var (a, b) in new[] { (-1.0, -1.0), (1.0, -1.0), (-1.0, 1.0), (1.0, 1.0), (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
            {
                var q = p + u * (a * w / 2) + v * (b * d / 2);
                if (streetDistance(q.x, q.y) < 0.45) { return; }
            }
            foreach (var (xx, zz, ww, dd, _, _, angle) in compounds)
            {
                Double2 delta = p - new Double2(xx, zz), ou = new Double2(cos(angle), -sin(angle)), ov = new Double2(sin(angle), cos(angle));
                if (abs(delta.x) > (bw + ww * abs(ou.x) + dd * abs(ov.x)) / 2 + gap || abs(delta.y) > (bd + ww * abs(ou.y) + dd * abs(ov.y)) / 2 + gap) { continue; }
                var separated = new[] { u, v, ou, ov }.Any(axis =>
                    abs(Simd.dot(delta, axis)) > (w * abs(Simd.dot(u, axis)) + d * abs(Simd.dot(v, axis)) + ww * abs(Simd.dot(ou, axis)) + dd * abs(Simd.dot(ov, axis))) / 2 + gap);
                if (!separated) { return; }
            }
            reserve(x, z, w, d, h, i, yaw);
        }
        // Establish continuous but varied street frontage first. Small gaps
        // lead into courtyards and rear alleys, rather than isolated cottages.
        var frontageID = 2000;
        foreach (var street in streets)
        {
            double distance = 0.0, next = 3.0;
            for (var j = 1; j < street.path.Count; j++)
            {
                distance += Simd.distance(street.path[j], street.path[j - 1]);
                if (distance < next) { continue; }
                var tangent = Simd.normalize(street.path[j] - street.path[j - 1]); var normal = new Double2(-tangent.y, tangent.x);
                double w = 4.8 + random() * 2.4, d = 5.0 + random() * 2.5;
                foreach (var side in new[] { -1.0, 1.0 })
                {
                    var p = street.path[j] + normal * side * (street.width / 2 + d / 2 + 0.8 + random() * 0.4);
                    place(p.x, p.y, w, d, 2.35 + random() * 3.25, -atan2(tangent.y, tangent.x), frontageID); frontageID += 1;
                }
                next = distance + w + 0.8 + random() * 0.8;
            }
        }
        for (var i = 0; i < 18000; i++)
        {
            double x = (random() - 0.5) * 306, z = (random() - 0.5) * 306; var p = new Double2(x, z);
            if (max(abs(x), abs(z)) < 56 || TownFootprint.edgeDistance(p) > -4) { continue; }
            double w = 3.4 + random() * 4.3, d = 3.8 + random() * 4.0, h = 2.35 + random() * 3.15; var front = frontage(p);
            if (front.distance > 28) { continue; }
            var yaw = front.yaw + (random() - 0.5) * 0.20;
            place(x, z, w, d, h, yaw, 5000 + i);
        }
        // Smaller workshops occupy residual frontage pockets. This adds a
        // second scale of buildings without moving the established compounds
        // or sacrificing the pedestrian clearance between them.
        var beforeInfill = compounds.Count;
        for (var i = 0; i < 9000; i++)
        {
            if (compounds.Count - beforeInfill >= 64) { break; }
            double x = (random() - 0.5) * 280, z = (random() - 0.5) * 280; var p = new Double2(x, z);
            if (max(abs(x), abs(z)) < 58 || TownFootprint.edgeDistance(p) > -9) { continue; }
            var front = frontage(p);
            if (front.distance > 16) { continue; }
            double w = 2.6 + random() * 1.2, d = 3.0 + random() * 1.5;
            place(x, z, w, d, 2.7 + random() * 1.2, front.yaw + (random() - 0.5) * 0.08, 24000 + i, gap: 0.95);
        }
        var landmarks = collisionBuilder.bodies.Where(body => body.position.y < 1.4 && body.profile.mass != 70 && body.profile.height > 0.18).Select(body =>
            new TownAccessMap.Footprint(center: new Double2(body.position.x, body.position.z), width: body.profile.halfWidth * 2, depth: body.profile.halfDepth * 2, yaw: body.heading)
        ).ToList();
        List<TownAccessMap.Footprint> footprints()
        {
            return compounds.SelectMany(compound =>
            {
                var (x, z, w, d, _, i, yaw) = compound;
                var style = (i * 13 + i / 7) % 9;
                List<(double, double, double, double)> shapes;
                if (style < 3) { shapes = new() { (-w * 0.17, 0, w * 0.66, d), (w * 0.25, d * 0.1, w * 0.50, d * 0.79) }; }
                else if (style == 6) { shapes = new() { (-w * 0.34, 0, w * 0.32, d), (w * 0.1, -d * 0.31, w * 0.78, d * 0.38), (w * 0.4, d * 0.07, w * 0.2, d * 0.69) }; }
                else if (style == 8) { shapes = new() { (-w * 0.23, -d * 0.05, w * 0.54, d * 0.90), (w * 0.26, 0, w * 0.47, d) }; }
                else { shapes = new() { (0, 0, w, d) }; }
                return shapes.Select(shape =>
                {
                    var (xx, zz, ww, dd) = shape;
                    return new TownAccessMap.Footprint(center: new Double2(x + xx * cos(yaw) + zz * sin(yaw), z - xx * sin(yaw) + zz * cos(yaw)), width: ww, depth: dd, yaw: yaw);
                });
            }).ToList();
        }
        // Audit every compound before building it. If a block has trapped a
        // doorway, create more alley clearance at that footprint and replan.
        for (var iteration = 0; iteration < 6; iteration++)
        {
            accessMap = new TownAccessMap(footprints: footprints().Concat(landmarks).ToList(), streets: streets.Select(street => street.path).ToList());
            plannedDoors.Clear(); var missing = new List<int>();
            foreach (var (j, compound) in enumerated(compounds))
            {
                var (x, z, w, d, h, i, yaw) = compound;
                if (entrancePlan(x, z, w: w, d: d, h: h, index: i, yaw: yaw, style: (i * 13 + i / 7) % 9) is DoorPlan plan) { plannedDoors[i] = plan; }
                else { missing.Add(j); }
            }
            if (missing.Count == 0 || iteration == 5) { break; }
            foreach (var j in missing)
            {
                var (x, z, w, d, h, i, yaw) = compounds[j];
                compounds[j] = (x, z, max(2.3, w - 0.45), max(2.3, d - 0.45), max(2.6, h), i, yaw);
                if (iteration >= 2)
                {
                    for (var k = 0; k < compounds.Count; k++)
                    {
                        if (k == j) { continue; }
                        var (xx, zz, ww, dd, hh, ii, yy) = compounds[k];
                        if (hypot(xx - x, zz - z) < (hypot(w, d) + hypot(ww, dd)) / 2 + 1.5 && ww > 3 && dd > 3)
                        {
                            compounds[k] = (xx, zz, ww - 0.35, dd - 0.35, hh, ii, yy);
                        }
                    }
                }
            }
        }
        updateLots();
        mapBuildings = compounds.Select(compound =>
        {
            var (x, z, w, d, _, _, yaw) = compound;
            return new List<Double2> { new(-w / 2, -d / 2), new(w / 2, -d / 2), new(w / 2, d / 2), new(-w / 2, d / 2) }.Select(p =>
                new Double2(x + p.x * cos(yaw) + p.y * sin(yaw), z - p.x * sin(yaw) + p.y * cos(yaw))
            ).ToList();
        }).ToList();
        mapBuildings.AddRange(venueSites.Select(site =>
            new List<Double2> { new(-site.halfWidth, -site.halfDepth), new(site.halfWidth, -site.halfDepth), new(site.halfWidth, site.halfDepth), new(-site.halfWidth, site.halfDepth) }.Select(p =>
                site.center + new Double2(p.x * cos(site.yaw) + p.y * sin(site.yaw), -p.x * sin(site.yaw) + p.y * cos(site.yaw))
            ).ToList()));
        foreach (var (x, z, w, d, h, i, yaw) in compounds)
        {
            var plan = plannedDoors.GetValueOrDefault(i);
            if (plan != null)
            {
                var f = plan.facade; double lx = f.x + cos(f.turn) * plan.offset, lz = f.z - sin(f.turn) * plan.offset;
                var center = new Double2(x + lx * cos(yaw) + lz * sin(yaw), z - lx * sin(yaw) + lz * cos(yaw));
                var @out = new Double2(sin(yaw + f.turn), cos(yaw + f.turn));
                if (accessMap?.routeToStreet(from: center + @out * 0.65) is List<Double2> route)
                {
                    pedestrianAccess.Add(new PedestrianAccess(building: new Double2(x, z), door: center, route: new List<Double2> { center + @out * 0.42, center + @out * 0.6 }.Concat(route).ToList()));
                } else { inaccessibleBuildings.Add(new Double2(x, z)); }
            } else { inaccessibleBuildings.Add(new Double2(x, z)); }
            cityCompound(x, z, w: w, d: d, h: h, index: i, yaw: yaw);
        }
        Godot.GD.Print($"Pedestrian access: {pedestrianAccess.Count}/{compounds.Count}, inaccessible [{string.Join(", ", inaccessibleBuildings)}]");
        accessMap = null; // Planning data never participates in the render loop.
    }

    private void cityCompound(double x, double z, double w, double d, double h, int index, double yaw)
    {
        var near = max(abs(x), abs(z)) < 52;
        var p = paint(x, z, yaw: yaw);
        buildings += 1;
        var colors = new uint[] { 0xb5a084, 0xbdaa8c, 0xc4ad8c, 0xa58c70, 0xc9b89b, 0x9e8872, 0xb7a890, 0xc0a786 };
        var chalk = new uint[] { 0xc9bca5, 0xd9ceba, 0xb9aa95, 0xd2c4a9, 0xc5bcb0, 0xae987e, 0xded3bb, 0xb8a58f };
        var palette = near ? colors : chalk;
        var ink = palette[(index * 7 + index / 11) % palette.Length];
        uint roof = 0x9e8970;
        var style = (index * 13 + index / 7) % 9;
        var plan = plannedDoors.GetValueOrDefault(index);
        var entrance = plan != null && near && max(abs(x), abs(z)) < 43 && (style == 3 || style == 4 || style == 7) && h * 0.6 > 1.50 && doorways.Count < 18;
        // Sun/contact shading seats the actual walls in sand; no rectangular
        // dark slab is stamped beneath an irregular compound.
        if (style < 3)
        {
            // Joined adobe dwelling + offset domed chamber; no individual plinth.
            p.adobe(-w * 0.17, h * 0.43, 0, w * 0.66, h * 0.86, d, ink, simple: false);
            p.adobe(w * 0.25, h * 0.32, d * 0.10, w * 0.50, h * 0.64, d * 0.79, ink, simple: false);
            var r = min(w * 0.30, d * 0.43);
            if (style == 2)
            {
                // Roof terrace with an asymmetric shade sail, not another dome.
                p.box(-w * 0.17, h * 0.86 + 0.13, -d * 0.46, w * 0.66, 0.26, 0.12, ink);
                p.box(-w * 0.47, h * 0.86 + 0.13, 0, 0.12, 0.26, d * 0.88, ink);
                p.awning(-w * 0.17, -d * 0.08, w * 0.48, d * 0.53, h * 0.86 + 1.25, 0x706f58);
                foreach (var xx in new[] { -w * 0.39, w * 0.05 }) { p.box(xx, h * 0.86 + 0.62, -d * 0.33, 0.07, 1.24, 0.07, 0x695b48); }
            } else { p.dome(-w * 0.17, h * 0.86, 0, r, r * 0.57, r, ink, sides: near ? 20 : 16); }
            if (style == 1) { p.dome(w * 0.25, h * 0.64, d * 0.1, w * 0.22, w * 0.15, w * 0.22, ink, sides: 16); }
        } else if (style < 5)
        {
            // Connected stepped roofscape with recessed terraces and parapets.
            if (entrance && plan != null)
            {
                var turn = plan.facade.turn;
                var q = paint(x, z, yaw: yaw + turn);
                q.residentHouse(abs(sin(turn)) > 0.5 ? d : w, h * 0.60, abs(sin(turn)) > 0.5 ? w : d, ink, doorX: plan.offset);
            }
            else { p.adobe(0, h * 0.30, 0, w, h * 0.60, d, ink, simple: false); }
            p.adobe(-w * 0.18, h * 0.78, -d * 0.16, w * 0.61, h * 0.36, d * 0.67, ink, simple: false);
            p.box(w * 0.23, h * 0.606, d * 0.16, w * 0.47, 0.025, d * 0.58, roof);
            p.box(w * 0.47, h * 0.65, 0, w * 0.05, 0.26, d, ink);
            p.box(0, h * 0.65, d * 0.47, w, 0.26, d * 0.05, ink);
            if (style == 4)
            {
                // Ventilated windcatcher rises above a stepped flat roof.
                p.adobe(-w * 0.18, h * 0.96 + 0.8, -d * 0.16, w * 0.26, 1.6, d * 0.26, ink, simple: false);
                p.box(-w * 0.18, h * 0.96 + 1.63, -d * 0.16, w * 0.34, 0.12, d * 0.34, roof);
                foreach (var side in new[] { -1.0, 1 }) { p.box(-w * 0.18, h * 0.96 + 1.18, -d * 0.16 + side * d * 0.131, w * 0.15, 0.5, 0.025, 0x4a493e); }
            }
        } else if (style == 5)
        {
            // Buttressed rotunda integrated into a long low workshop.
            p.adobe(0, h * 0.22, 0, w, h * 0.44, d, ink, simple: false);
            var r = min(w, d) * 0.40;
            p.cylinder(-w * 0.08, h * 0.54, -d * 0.04, r, r * 0.91, h * 1.08, ink, sides: near ? 20 : 16);
            p.dome(-w * 0.08, h * 1.08, -d * 0.04, r * 0.91, r * 0.5, r * 0.91, ink, sides: near ? 20 : 16);
            p.cylinder(-w * 0.08, h * 0.8, -d * 0.04, r * 0.955, r * 0.955, 0.16, roof, sides: near ? 20 : 16);
        } else if (style == 6)
        {
            // Open courtyard enclosed on three sides, shaded workshop frontage.
            p.adobe(-w * 0.34, h * 0.42, 0, w * 0.32, h * 0.84, d, ink, simple: false);
            p.adobe(w * 0.10, h * 0.35, -d * 0.31, w * 0.78, h * 0.70, d * 0.38, ink, simple: false);
            p.adobe(w * 0.40, h * 0.25, d * 0.07, w * 0.20, h * 0.50, d * 0.69, ink, simple: false);
            p.awning(-w * 0.08, -d * 0.04, w * 0.34, d * 0.36, h * 0.65, 0x87725c);
            // An open-to-sky domestic patio, with a low bench along its wall.
            p.box(-w * 0.10, 0.22, -d * 0.065, w * 0.32, 0.44, 0.28, 0x9d8467);
            p.vessel(w * 0.23, 0.02, -d * 0.065, 0.55, 0xa27b54);
        } else if (style == 7)
        {
            // Industrial block: vaulted hall, sunken rooftop machinery enclosure.
            if (entrance && plan != null)
            {
                var turn = plan.facade.turn;
                var q = paint(x, z, yaw: yaw + turn);
                q.residentHouse(abs(sin(turn)) > 0.5 ? d : w, h * 0.60, abs(sin(turn)) > 0.5 ? w : d, ink, doorX: plan.offset);
            }
            else { p.adobe(0, h * 0.30, 0, w, h * 0.60, d, ink, simple: false); }
            p.dome(-w * 0.2, h * 0.60, 0, w * 0.28, w * 0.27, d * 0.45, ink, sides: 16);
            p.box(w * 0.25, h * 0.605, 0, w * 0.40, 0.025, d * 0.7, 0x665c50);
            for (var k = 0; k < 3; k++) { p.box(w * 0.25, h * 0.71, -d * 0.23 + (double)k * d * 0.23, w * 0.29, 0.35, 0.17, roof, detail: near); }
        } else
        {
            // Two unequal flat-roof dwellings linked by a utility room.
            p.adobe(-w * 0.23, h * 0.5, -d * 0.05, w * 0.54, h, d * 0.9, ink, simple: false);
            p.adobe(w * 0.26, h * 0.29, 0, w * 0.47, h * 0.58, d, ink, simple: false);
            p.box(-w * 0.23, h + 0.07, -d * 0.05, w * 0.48, 0.15, d * 0.83, roof);
        }
        if (!near && index % 4 == 0)
        {
            var roofY = style == 8 ? h * 0.58 : (style == 6 ? h * 0.70 : h * 0.60);
            if (new[] { 3, 4, 8 }.Contains(style))
            {
                var steps = max(7, (int)ceil(h * (style == 8 ? 0.42 : 0.36) / 0.19));
                double run = w * 0.40, tread = run / (double)steps, edge = style == 8 ? w * 0.04 : w * 0.125;
                for (var k = 0; k < steps; k++)
                {
                    var rise = h * (style == 8 ? 0.42 : 0.36) * (double)(k + 1) / (double)steps;
                    p.box(edge + run - tread * ((double)k + 0.5), roofY + rise / 2, d * 0.19, tread + 0.005, rise, 0.56, ink);
                }
            }
        }
        if (!near) { p = explorationPainter(x, z, yaw: yaw); }
        // Equipment on usable flat roofs, with cables/pipes at the near tier.
        if (style == 3 || style == 4 || style == 8)
        {
            var roofY = style == 8 ? h * 0.58 : h * 0.60;
            p.box(w * 0.25, roofY + 0.19, d * 0.08, w * 0.25, 0.38, d * 0.28, 0x7f7867);
            for (var k = 0; k < 4; k++) { p.box(w * 0.25, roofY + 0.385, d * 0.01 + (double)k * 0.11, w * 0.22, 0.018, 0.045, 0x49483f, detail: true); }
            p.beam(new Double3(w * 0.15, roofY + 0.12, -d * 0.18), new Double3(w * 0.41, roofY + 0.12, -d * 0.18), 0.095, 0x918773, sides: 8);
            p.beam(new Double3(w * 0.41, roofY + 0.12, -d * 0.18), new Double3(w * 0.41, roofY + 0.12, d * 0.29), 0.095, 0x918773, sides: 8);
        }
        // Dress only a usable entrance; there is no mirrored rear-door stamp.
        if (plan != null)
        {
            var f = plan.facade; double lx = f.x + cos(f.turn) * plan.offset, lz = f.z - sin(f.turn) * plan.offset;
            var center = new Double2(x + lx * cos(yaw) + lz * sin(yaw), z - lx * sin(yaw) + lz * cos(yaw));
            var direction = yaw + f.turn;
            // Entrances define the building even from an elevated town camera.
            var q = paint(center.x, center.y, yaw: direction);
            double dw = entrance ? 0.92 : plan.width, dh = entrance ? 1.30 : plan.height;
            if (style == 5 && h * 0.44 < dh + 0.18) { q.adobe(0, (dh + 0.18) / 2, -0.18, dw + 0.44, dh + 0.18, 0.40, ink); }
            q.cityDoor(dw, dh, ink, variant: plan.variant, open: entrance);
            if (!near && index % 4 == 0 && f.span > 3)
            {
                var utility = explorationPainter(center.x, center.y, yaw: direction); var side = plan.offset > 0 ? -1.0 : 1.0;
                var xx = side * min(f.span * 0.30, dw / 2 + 0.52);
                utility.box(xx, 1.35, 0.16, 0.36, 0.58, 0.25, 0x7d8276);
                utility.beam(new Double3(xx, 0.20, 0.18), new Double3(xx, dh + 0.15, 0.18), 0.045, 0x91826b);
                utility.cable(new Double3(xx, dh + 0.15, 0.16), new Double3(0, dh + 0.18, 0.16), 0.10, 0x554b3e);
            }
            if (entrance) { doorways.Add(new TownDoorway(center: center, yaw: direction, root: root, variant: plan.variant)); }
            entrances.Add(new Entrance(center: center, yaw: direction, width: dw, height: dh, variant: plan.variant, walkable: entrance));
            if (index % 3 == 0) { q.awning(0, 0.40, min(f.span - 0.12, dw + 0.55), 0.85, dh + 0.28, index % 2 == 0 ? 0x7e6a50u : 0x8f5140u); }
            if (near && max(abs(x), abs(z)) < 36 && index % 7 == 0 && storefrontSigns < 8 && f.height > dh + 0.55)
            {
                var title = new[] { "MACHINE WORKS", "CANTINA", "OFFWORLD GOODS", "REACTOR SUPPLY" }[storefrontSigns % 4];
                var sy = min(f.height - 0.12, dh + 0.45);
                signs.plate(title, eyebrow: "MOS ASTER", footer: "MARKET DISTRICT", badge: format("%02d", storefrontSigns + 11),
                            at: new SCNVector3(center.x + sin(direction) * 0.12, sy, center.y + cos(direction) * 0.12), width: min(2.0, (CGFloat)f.span * 0.62), height: 0.34, yaw: (CGFloat)direction,
                            accent: storefrontSigns % 2 == 0 ? rust : teal, into: root);
                storefrontSigns += 1;
            }
        }
        // Openings use the same real facade catalog as door planning. A
        // bounding-box face can be empty space in a joined or U-shaped house.
        foreach (var (faceIndex, f) in enumerated(compoundFacades(w: w, d: d, h: h, style: style)))
        {
            if (!((faceIndex + index) % 2 == 0 && f.span > 1.0)) { continue; }
            var offset = f.span * (index % 2 == 0 ? 0.21 : -0.21);
            double lx = f.x + cos(f.turn) * offset, lz = f.z - sin(f.turn) * offset;
            if (plan != null)
            {
                var pf = plan.facade; double dx = pf.x + cos(pf.turn) * plan.offset, dz = pf.z - sin(pf.turn) * plan.offset;
                if (hypot(lx - dx, lz - dz) < plan.width / 2 + 0.42) { continue; }
            }
            var center = new Double2(x + lx * cos(yaw) + lz * sin(yaw), z - lx * sin(yaw) + lz * cos(yaw));
            double direction = yaw + f.turn, wallHeight = style == 5 ? h * 0.44 : f.height;
            var wy = min(1.75, min(wallHeight * 0.64, wallHeight - 0.30));
            var q = near ? paint(center.x, center.y, yaw: direction) : explorationPainter(center.x, center.y, yaw: direction);
            q.box(0, wy, -0.035, 0.23, 0.46, 0.12, 0x39332c, detail: true);
            q.box(0, wy - 0.245, 0.0, 0.32, 0.075, 0.18, roof, detail: true);
            foreach (var yy in new[] { wy - 0.22, wy + 0.22 }) { windowSupports.Add(new Double3(center.x - sin(direction) * 0.20, yy, center.y - cos(direction) * 0.20)); }
        }
        if (near && (style < 5 || style == 8))
        {
            foreach (var side in new[] { -1.0, 1 })
            {
                p.adobe(side * w * 0.43, h * 0.26, d * 0.41, 0.28, h * 0.52, 0.35, ink);
            }
        }
        // Roof equipment is useful silhouette at medium distance, tiny wires LOD out.
        var ry = near ? (style == 5 ? h * 1.10 : h * 0.65) : h * (style == 8 ? 0.58 : 0.60);
        if (index % 3 == 0 && (near || style == 3 || style == 4 || style == 7 || style == 8))
        {
            p.cylinder(w * 0.28, ry + 0.3, -d * 0.21, 0.32, 0.29, 0.60, roof, sides: 8, detail: true);
            p.box(w * 0.28, ry + 0.64, -d * 0.21, 0.72, 0.10, 0.72, 0x756654, detail: true);
        }
        if (index % 5 == 0 && (near || style == 8))
        {
            p.cylinder(-w * 0.31, h + 0.30, -d * 0.1, 0.08, 0.065, 0.90, 0x5c5449, sides: 6, detail: true);
            p.box(-w * 0.31, h + 0.62, -d * 0.1, 0.66, 0.045, 0.045, 0x65594b, detail: true);
        }
        for (var k = 0; k < (near ? index % 3 : 0); k++)
        {
            p.box(w * 0.31, 0.19 + (double)k * 0.29, d * 0.33, 0.51, 0.36, 0.43, k % 2 == 0 ? 0x715642u : 0x6a7870u, detail: true);
        }
    }

    /// Ray crossing against the closed racing spline. Clearance alone does not
    /// distinguish a safe infield pocket from land outside the circuit.
    private bool infield(double x, double z)
    {
        if (abs(x) > 22 || abs(z) > 22) { return false; }
        var inside = false;
        var a = DirtCourse.point(0);
        for (var i = 1; i <= DirtCourse.sampleCount; i++)
        {
            var b = DirtCourse.point((double)i / (double)DirtCourse.sampleCount * 2 * Math.PI);
            if ((a.z > z) != (b.z > z) && x < (b.x - a.x) * (z - a.z) / (b.z - a.z) + a.x) { inside = !inside; }
            a = b;
        }
        return inside;
    }

    private bool repairFootprintClear(int index)
    {
        var points = InfieldLayout.canopies[index].points;
        for (var i = 1; i < points.Length - 1; i++)
        {
            for (var u = 0; u <= 24; u++) { for (var v = 0; v <= 24 - u; v++) {
                var p = points[0] + (points[i] - points[0]) * (double)u / 24 + (points[i + 1] - points[0]) * (double)v / 24;
                if (!infield(p.x, p.y) || DirtCourse.projection(x: p.x, z: p.y).distance < DirtCourse.terrainEdge + 0.6) { return false; }
            }}
        }
        return true;
    }

    private List<TownLot> repairLots = new();
    private void buildRepairPit()
    {
        var entry = paint(DirtCourse.serviceEntryX, -12.2);
        foreach (var side in new[] { -1.0, 1.0 })
        {
            var ground = DirtCourse.height(x: DirtCourse.serviceEntryX + side * 1.4, z: -12.2);
            entry.cylinder(side * 1.40, ground + 0.28, 0, 0.045, 0.045, 0.56, 0xb59b62, sides: 8, detail: true);
            entry.cylinder(side * 1.40, ground + 0.40, 0, 0.046, 0.046, 0.075, 0x554c40, sides: 8, detail: true);
        }
        var signGround = DirtCourse.height(x: DirtCourse.serviceEntryX - 1.85, z: -12.21);
        signs.plate("SERVICE ACCESS", eyebrow: "REPAIR BAY", footer: "KEEP CLEAR", badge: "S",
                    at: new SCNVector3(DirtCourse.serviceEntryX - 1.85, signGround + 0.68, -12.2), width: 1.02, height: 0.34, yaw: Math.PI, accent: rust, into: root);
        entry.beam(new Double3(-1.85, signGround, -0.01), new Double3(-1.85, signGround + 0.67, -0.01), 0.025, trim, sides: 6);
        // Two pockets on the west side of the infield, clear of the dirt shoulder.
        foreach (var (i, location) in enumerated(InfieldLayout.tentOrigins))
        {
            double x = location.x, z = location.y, w = i == 0 ? 3.2 : 4.0, d = i == 0 ? 3.2 : 4.0;
            if (!repairFootprintClear(i)) { continue; }
            repairLots.Add(new TownLot(x: x, z: z, width: w + 0.3, depth: d + 0.3));
            var p = paint(x, z, yaw: InfieldLayout.tentYaws[i]);
            p.repairRug(w - 0.2, d - 0.2, i);
            if (i == 0) { p.triangularRepairCanopy(rust); }
            else { p.canopy(0, 0, w, d, 2.0, 0.65, teal); }
            if (i == 0) { var center = InfieldLayout.tentPoint(0, new Double2(-0.1, 0.35)); p = paint(center.x, center.y, yaw: InfieldLayout.tentYaws[0]); }
            var roof = InfieldLayout.canopies[i].points;
            cameraBounds.Add((new Double3(roof.Select(q => q.x).Min(), 2.0, roof.Select(q => q.y).Min()), new Double3(roof.Select(q => q.x).Max(), 2.65, roof.Select(q => q.y).Max())));
            // Open sides reveal benches, parts racks and a robot on a lift.
            p.box(-0.92, 0.69, 0.15, 0.64, 0.12, 1.7, trim);
            foreach (var zz in new[] { -0.55, 0.85 }) { p.box(-0.92, 0.33, zz, 0.48, 0.66, 0.09, dark, detail: true); }
            for (var k = 0; k < 4; k++)
            {
                var zz = -0.44 + (double)k * 0.35;
                p.cylinder(-0.92, 0.84, zz, 0.12, 0.12, 0.18, k % 2 == 0 ? teal : cream, sides: 8, detail: true);
                p.box(-0.69, 0.775, zz, 0.08, 0.035, 0.25, dark, detail: true); // spanners
                p.box(-0.69, 0.775, zz + 0.11, 0.16, 0.035, 0.07, cream, detail: true);
            }
            p.box(0.24, 0.14, 0.20, 0.95, 0.28, 1.3, dark);
            // Tapered service droid with separate shell, collar and articulated limbs.
            p.cylinder(0.24, 0.62, 0.20, 0.28, 0.23, 0.63, cream, sides: 24);
            p.cylinder(0.24, 0.92, 0.20, 0.245, 0.245, 0.065, dark, sides: 24, detail: true);
            p.dome(0.24, 0.965, 0.20, 0.26, 0.23, 0.26, teal, sides: 24);
            p.box(0.24, 1.07, 0.448, 0.18, 0.065, 0.035, dark, detail: true);
            p.box(0.30, 1.07, 0.47, 0.035, 0.035, 0.016, 0xbb9567, detail: true);
            p.box(0.24, 0.66, 0.465, 0.25, 0.32, 0.035, dark, detail: true);
            for (var k = 0; k < 5; k++)
            {
                p.box(0.24, 0.55 + (double)k * 0.047, 0.487, 0.20, 0.018, 0.012, trim, detail: true);
            }
            foreach (var side in new[] { -1.0, 1.0 })
            {
                var xx = 0.24 + side * 0.34;
                p.cylinder(xx, 0.77, 0.20, 0.10, 0.10, 0.12, trim, sides: 16, detail: true);
                p.beam(new Double3(xx, 0.74, 0.20), new Double3(xx + side * 0.09, 0.40, 0.32), 0.065, cream, sides: 12);
                p.beam(new Double3(xx + side * 0.09, 0.40, 0.32), new Double3(xx, 0.33, 0.46), 0.045, dark, sides: 12);
                p.box(xx, 0.29, 0.34, 0.22, 0.15, 0.46, trim, detail: true);
                p.cylinder(xx, 0.375, 0.43, 0.068, 0.068, 0.035, cream, sides: 16, detail: true);
            }
            // Engine hoist and hanging spare motor.
            p.box(1.17, 0.8, 0.91, 0.09, 1.6, 0.09, dark);
            p.box(0.83, 1.60, 0.91, 0.76, 0.10, 0.10, rust);
            p.box(0.51, 1.29, 0.91, 0.03, 0.54, 0.03, dark, detail: true);
            p.cylinder(0.51, 0.94, 0.91, 0.18, 0.22, 0.36, trim, sides: 8);
            for (var k = 0; k < 3; k++)
            {
                p.cylinder(i == 0 ? -0.1 : 1.03, 0.12 + (double)k * 0.17, i == 0 ? 0.85 : -0.86, 0.27, 0.27, 0.15, dark, sides: 10, detail: true);
                p.cylinder(i == 0 ? -0.1 : 1.03, 0.20 + (double)k * 0.17, i == 0 ? 0.85 : -0.86, 0.14, 0.14, 0.015, cream, sides: 10, detail: true);
            }
            var mechanic = InfieldLayout.tentPoint(i, new Double2(i == 0 ? 0.65 : 0.73, i == 0 ? -0.25 : -0.35));
            citizen(mechanic.x, mechanic.y, y: 0.055, yaw: InfieldLayout.tentYaws[i] - 1.2, index: 707 + i, seated: false);
            p.box(-0.96, 0.19, -1.06, 0.60, 0.36, 0.40, teal);
            p.box(-0.96, 0.40, -1.06, 0.21, 0.06, 0.08, dark, detail: true);
            if (i == 1) { foreach (var sx in new[] { -0.65, 0.65 }) { p.beam(new Double3(sx, 1.98, -2.015), new Double3(sx, 2.44, -2.015), 0.012, dark, sides: 5); } }
            var sign = InfieldLayout.tentPoint(i, new Double2(i == 0 ? -1.625 : 0, i == 0 ? -0.65 : -2.035));
            signs.plate(i == 0 ? "DROID REPAIR" : "PARTS & SALVAGE", eyebrow: "RACE SERVICE", footer: "CREW ACCESS ONLY", badge: i == 0 ? "01" : "02",
                        at: new SCNVector3(sign.x, 1.74, sign.y), width: 1.85, height: 0.46, yaw: InfieldLayout.tentYaws[i] + (i == 0 ? -Math.PI / 2 : Math.PI), accent: rust, into: root);
        }
        foreach (var (index, part) in enumerated(InfieldLayout.parts))
        {
            var p = paint(part.x, part.z, yaw: part.yaw);
            p.salvage(part.width, part.depth, part.height, part.kind, index);
        }

    }

    private void buildLandmarks()
    {
        // Spaceport hangar and landing circle, outside the circuit.
        var p = paint(23, 14);
        p.box(3, 0.035, 0, 9, 0.07, 12, 0x9f947b);
        p.cylinder(3, 0.08, 1.0, 3.7, 3.7, 0.06, trim);
        p.cylinder(3, 0.115, 1.0, 3.4, 3.4, 0.015, 0xb7a98d);
        for (var i = 0; i < 12; i++)
        {
            var a = (double)i * Math.PI / 6;
            p.box(3 + cos(a) * 3.55, 0.14, 1 + sin(a) * 3.55, 0.25, 0.04, 0.25, cream, detail: true);
        }
        p.box(3, 2.3, 7.0, 9, 4.6, 4.2, sand);
        p.box(3, 1.85, 4.87, 7.5, 3.7, 0.055, dark);
        p.box(3, 4.8, 7, 9.4, 0.40, 4.5, cream);
        foreach (var x in stride(from: -0.5, through: 6.5, by: 0.7)) { p.box(x, 1.9, 4.82, 0.065, 3.6, 0.06, trim, detail: true); }
        signs.plate("DOCK 07", eyebrow: "MOS ASTER SPACEPORT", footer: "ARRIVALS  /  CARGO", badge: "07",
                    at: new SCNVector3(26, 4.13, 18.77), width: 5.6, height: 0.65, yaw: Math.PI, accent: teal, into: root);
        // A small parked original utility shuttle: low hull, wings and engines.
        p.box(3, 0.7, 0.6, 1.35, 0.65, 3.2, cream);
        p.box(3, 1.13, 0.2, 0.75, 0.36, 1.1, teal);
        p.box(3, 0.62, 1.2, 4.2, 0.16, 1.6, trim);
        foreach (var x in new[] { 1.25, 4.75 })
        {
            p.cylinder(x, 0.7, 1.2, 0.29, 0.24, 0.75, dark, detail: true);
            p.box(x, 0.28, 1.2, 0.11, 0.5, 0.5, dark, detail: true);
        }
        // Comms tower on the far skyline, constructed from a few opaque forms.
        var t = paint(-27, 22);
        t.cylinder(0, 2.0, 0, 1.7, 1.3, 4, sand);
        t.cylinder(0, 4.5, 0, 1.4, 1.1, 1, cream);
        t.cylinder(0, 6.8, 0, 0.23, 0.16, 4.2, dark);
        t.cylinder(0, 7.3, 0, 1.9, 0.25, 0.9, cream);
        t.box(0, 9.4, 0, 0.075, 2.4, 0.075, dark);
        foreach (var x in new[] { -1.0, 1 }) { t.box(x, 8.0, 0, 0.035, 1.6, 0.035, trim, detail: true); }
        buildCityLandmarks();
    }

    private void buildCityLandmarks()
    {
        // A layered navigation tower and large civic dome break the roofscape.
        var p = paint(10, 43);
        p.adobe(0, 2.3, 0, 9, 4.6, 8, 0xad9678);
        foreach (var side in new[] { -1.0, 1 })
        {
            p.door(0, side * 4.015, 1.5, 2.1, 0x393129, 0xad9678, side: side);
            foreach (var x in new[] { -3.8, 3.8 }) { p.adobe(x, 2.2, side * 3.75, 0.48, 4.4, 0.75, 0x9b8367); }
            foreach (var x in new[] { -2.2, 2.2 }) { p.box(x, 3.4, side * 4.01, 0.33, 0.75, 0.025, 0x594b3c); }
        }
        p.cylinder(0, 6.4, 0, 3.0, 2.4, 4.0, 0xbca88a, sides: 16);
        foreach (var y in new[] { 5.0, 8.3 }) { p.cylinder(0, y, 0, 3.2, 3.05, 0.45, 0x88785f, sides: 16); }
        p.dome(0, 8.5, 0, 2.6, 1.5, 2.6, 0xbba58a, sides: 20);
        p.cylinder(0, 10.7, 0, 0.30, 0.20, 1.7, dark, sides: 8);
        for (var i = 0; i < 12; i++)
        {
            var a = (double)i * Math.PI / 6; var q = paint(10 + cos(a) * 2.72, 43 + sin(a) * 2.72, yaw: Math.PI / 2 - a);
            q.box(0, 6.7, 0, 0.36, 1.25, 0.07, 0x4b4338);
            q.box(0, 3.0, 1.15, 0.42, 4.0, 0.5, 0x927d61);
        }
        // Circular docking courts inspired by the film's excavated landing bays.
        foreach (var (i, pt) in enumerated(new[] { (-38.0, -5.0), (43.0, 7.0) }))
        {
            var q = paint(pt.Item1, pt.Item2);
            q.cylinder(0, 0.025, 0, 4.8, 4.8, 0.05, 0x514a42, sides: 32);
            q.ring(0, 0.90, 0, 4.8, 4.1, 1.8, 0xaf9878, sides: 32, entry: true);
            q.ring(0, 1.83, 0, 4.93, 4.02, 0.14, 0xc0ad8d, sides: 32, entry: true);
            q.box(0, 0.11, 0, 4.5, 0.05, 0.12, 0x9a8668);
            q.box(0, 0.6, 0, 1.2, 0.5, 2.8, 0x8b9187);
            q.box(0, 0.72, 0.3, 3.4, 0.13, 1.5, 0xa69780);
            q.box(0, 1.0, -0.4, 0.65, 0.3, 0.9, 0x465e60);
            q.adobe(-2.0, 1.0, 4.0, 3.2, 2.0, 2.5, 0xaf9878);
            q.dome(-2.0, 2.0, 4.0, 1.5, 0.9, 1.15, 0xaf9878, sides: 16);
            for (var k = 0; k < 6; k++) { q.box(2.7, 0.15 + (double)k * 0.27, 3.8 - (double)k * 0.36, 1.0, 0.3, 0.38, 0x9c886b); }
            signs.plate("LANDING BAY", eyebrow: "SPACEPORT AUTHORITY", footer: "KEEP APRON CLEAR", badge: $"0{i + 8}",
                        at: new SCNVector3(pt.Item1, 1.15, pt.Item2 - 4.94), width: 2.9, height: 0.59, yaw: Math.PI, accent: teal, into: root);
        }
        // Distinct civic silhouettes, within the established reserved footprints.
        // Utility towers should read as different functions, not four copies.
        foreach (var (index, position) in enumerated(new[] { (-53.0, 48.0), (49.0, 53.0), (-70.0, -41.0), (22.0, 71.0) }))
        {
            var (x, z) = position; var q = paint(x, z);
            switch (index)
            {
            case 0: // Narrow stepped communications mast with a receiver crown.
                for (var k = 0; k < 4; k++)
                {
                    double width = 2.55 - (double)k * 0.37, @base = (double)k * 2.35;
                    q.adobe(0, @base + 1.175, 0, width, 2.35, width * 0.88, 0xaa9271);
                    q.box(0, @base + 2.26, 0, width + 0.16, 0.12, width * 0.88 + 0.16, 0x89785f);
                }
                q.cylinder(0, 9.85, 0, 0.19, 0.13, 0.9, 0x616459, sides: 16);
                q.cylinder(0, 10.45, 0, 0.30, 1.45, 0.45, 0xaba486, sides: 24);
                q.cylinder(0, 10.70, 0, 1.48, 1.48, 0.10, 0x716e5b, sides: 24);
                q.beam(new Double3(0, 10.7, 0), new Double3(0, 12.1, 0), 0.045, 0x6f7164);
                break;
            case 1: // Ventilated observation drum with a broad domed cap.
                q.cylinder(0, 4.6, 0, 1.32, 0.97, 9.2, 0xa18e74, sides: 24);
                q.cylinder(0, 9.3, 0, 1.45, 1.45, 0.28, 0x7d7562, sides: 24);
                q.cylinder(0, 9.87, 0, 1.17, 1.17, 0.9, 0x414b43, sides: 24);
                for (var k = 0; k < 12; k++)
                {
                    var a = (double)k * Math.PI / 6;
                    q.box(cos(a) * 1.18, 9.88, sin(a) * 1.18, 0.10, 0.94, 0.10, 0x9b8d72);
                }
                q.cylinder(0, 10.39, 0, 1.49, 1.49, 0.15, 0xb5a181, sides: 24);
                q.dome(0, 10.46, 0, 1.49, 0.85, 1.49, 0xb5a181, sides: 24);
                break;
            case 2: // Thick-walled windcatcher: open dark louvres under a flat cap.
                q.adobe(0, 4.0, 0, 2.65, 8, 2.35, 0xb6a084);
                q.box(0, 8.9, 0, 2.25, 1.8, 1.97, 0x4e5248);
                foreach (var side in new[] { -1.0, 1 })
                {
                    foreach (var zz in new[] { -1.08, 1.08 }) { q.adobe(side * 1.21, 8.9, zz, 0.23, 1.8, 0.23, 0xb6a084); }
                    for (var k = 0; k < 5; k++) { q.box(0, 8.25 + (double)k * 0.28, side * 1.11, 2.40, 0.09, 0.14, 0x8a8068); }
                }
                q.adobe(0, 9.94, 0, 2.95, 0.28, 2.66, 0xc1af8f);
                q.adobe(-0.7, 10.39, 0.46, 0.58, 0.62, 0.6, 0xb6a084);
                break;
            default: // Condenser column, exposed pipework and unequal fin bands.
                q.cylinder(0, 5.8, 0, 1.30, 0.85, 11.6, 0x9c886c, sides: 24);
                foreach (var y in new[] { 3.2, 3.65, 7.6, 10.0 }) { q.cylinder(0, y, 0, 1.55, 1.40, 0.20, 0xb29d7e, sides: 24); }
                q.dome(0, 11.6, 0, 0.87, 0.5, 0.87, 0xbba88b, sides: 24);
                foreach (var a in new[] { 0.4, 2.5, 4.6 })
                {
                    q.beam(new Double3(cos(a) * 1.25, 0.2, sin(a) * 1.25), new Double3(cos(a) * 1.03, 8.4, sin(a) * 1.03), 0.045, 0x717869);
                }
                break;
            }
        }
    }

    public readonly struct VenueSite
    {
        public readonly string name; public readonly Double2 center; public readonly double halfWidth, halfDepth, yaw;
        public VenueSite(string name, Double2 center, double halfWidth, double halfDepth, double yaw)
        {
            this.name = name; this.center = center; this.halfWidth = halfWidth; this.halfDepth = halfDepth; this.yaw = yaw;
        }
    }
    public readonly List<VenueSite> venueSites = new() { new VenueSite(name: "DROID EXCHANGE", center: new Double2(64, 64), halfWidth: 9, halfDepth: 10, yaw: Math.PI),
        new VenueSite(name: "THE TWIN SUNS", center: new Double2(-44, -53), halfWidth: 9, halfDepth: 9, yaw: Math.PI),
        new VenueSite(name: "MOS ASTER MARKET", center: new Double2(-108, 11), halfWidth: 9, halfDepth: 10, yaw: Math.PI / 2) };
    private void buildDistrictPlaces()
    {
        foreach (var (i, site) in enumerated(venueSites))
        {
            var q = paint(site.center.x, site.center.y, yaw: site.yaw);
            Double2 point(double x, double z)
            {
                return site.center + new Double2(x * cos(site.yaw) + z * sin(site.yaw), -x * sin(site.yaw) + z * cos(site.yaw));
            }
            if (i == 0)
            {
                // A U-shaped industrial compound with an open repair/salvage
                // yard. The crane, racks and stacked running gear identify it.
                q.adobe(0, 2.1, -6.8, 16, 4.2, 4.0, 0x9c8668);
                q.adobe(-7, 1.55, -0.8, 3, 3.1, 8.0, 0xb29a78);
                q.adobe(7, 1.7, -2.5, 3, 3.4, 6.0, 0xa38c6c);
                for (var k = 0; k < 4; k++)
                {
                    var x = -5.7 + (double)k * 3.8;
                    q.box(x, 4.23, -6.8, 3.5, 0.16, 4.3, 0x697574);
                    q.box(x, 4.55, -7.8, 2.5, 0.64, 0.12, 0x5e6764);
                }
                foreach (var x in new[] { -4.4, 4.4 }) { q.box(x, 2.65, -0.5, 0.22, 5.3, 0.25, 0x76523c); }
                q.box(0, 5.28, -0.5, 9.3, 0.30, 0.10, 0x806249);
                foreach (var y in new[] { 5.10, 5.46 }) { q.box(0, y, -0.5, 9.5, 0.08, 0.42, 0x76523c); }
                foreach (var x in new[] { -4.4, 4.4 })
                {
                    q.box(x, 0.10, -0.5, 0.75, 0.20, 0.85, 0x6a5140);
                    q.beam(new Double3(x, 4.3, -0.5), new Double3(x + (x < 0 ? 0.8 : -0.8), 5.1, -0.5), 0.05, 0x856447);
                }
                q.beam(new Double3(-2.4, 5.1, -0.5), new Double3(-2.4, 1.6, -0.5), 0.025, 0x454944, sides: 6);
                q.engineAssembly(-2.4, 1.0, -0.5, scale: 1.0, variant: 0);
                foreach (var z in new[] { -0.78, -0.22 }) { q.cylinder(-2.4, 1.69, z, 0.22, 0.19, 0.18, 0x8b7055, sides: 12); }
                foreach (var x in new[] { -6.4, -4.6 }) { foreach (var z in new[] { 2.85, 3.55 }) { q.box(x, 0.34, z, 0.12, 0.68, 0.12, 0x555e56); } }
                q.box(-5.5, 0.76, 3.2, 2.4, 0.16, 1.0, 0x69706a);
                q.box(-5.9, 1.02, 3.2, 0.7, 0.36, 0.52, 0x80624b);
                for (var j = 0; j < 3; j++) { q.beam(new Double3(-5.3 + (double)j * 0.17, 0.87, 3), new Double3(-5.3 + (double)j * 0.17, 0.87, 3.45), 0.025, 0x4c5752, sides: 6); }
                for (var k = 0; k < 5; k++)
                {
                    var z = -3.2 + (double)k * 1.65;
                    if (k % 2 == 0)
                    {
                        q.engineAssembly(4.6, 0, z, scale: 0.85 + (double)k * 0.07, variant: k);
                    } else
                    {
                        q.box(4.6, 0.23, z, 0.85, 0.46, 1.1, 0x4b524c);
                        for (var j = 0; j < 6; j++) { q.box(4.6, 0.48, z - 0.45 + (double)j * 0.18, 0.93, 0.08, 0.08, 0x927758); }
                    }
                    if (k < 3)
                    {
                        q.droidSalvage(-4.7, 0, z, variant: k);
                    }
                }
                q.box(0, 1.3, -4.78, 3.3, 2.6, 0.06, 0x343e3b);
                for (var k = 0; k < 9; k++) { q.box(0, 0.18 + (double)k * 0.27, -4.72, 3.2, 0.025, 0.05, 0x68716a); }
                q.cable(new Double3(-4.4, 4.8, -0.48), new Double3(4.4, 4.8, -0.48), 0.35, 0x45483e);
                for (var k = 0; k < 3; k++) { q.crate(3.4 + (double)(k % 2) * 0.7, 0.02 + (double)(k / 2) * 0.62, -3.3, 0.62, 0x80735a); }
                var sign = point(0, -4.68);
                signs.plate("DROID EXCHANGE", eyebrow: "REPAIRS  /  REBUILT MOTORS", footer: "PARTS · TRACKS · POWER CELLS", badge: "08", at: new SCNVector3(sign.x, 3.22, sign.y), width: 6.8, height: 0.95, yaw: (CGFloat)site.yaw, accent: teal, into: root);
                conversation(point(-2.8, 3.7), axis: new Double2(cos(site.yaw), -sin(site.yaw)), index: 22000, count: 2);
            } else if (i == 1)
            {
                // Low rotunda + unequal annex; the shaded seating terrace is
                // visibly a cantina, with a clear central approach to its door.
                q.cylinder(-3.2, 1.7, -3.5, 3.8, 3.6, 3.4, 0xb79b78, sides: 24);
                q.dome(-3.2, 3.4, -3.5, 3.6, 1.2, 3.6, 0xc0a480, sides: 24);
                q.adobe(3.4, 1.45, -4.2, 6.5, 2.9, 6, 0xa89070);
                q.box(3.4, 3.02, -4.2, 6.7, 0.24, 6.2, 0x948369);
                q.box(1.4, 1.06, -1.18, 1.5, 2.12, 0.06, 0x363c32);
                q.awning(0.7, 3.1, 12.0, 5.5, 3.15, 0x955d42);
                foreach (var x in new[] { -5.1, 6.5 }) { foreach (var z in new[] { 0.5, 5.6 }) { q.box(x, 1.55, z, 0.10, 3.1, 0.10, 0x71644e); } }
                foreach (var (k, p) in enumerated(new[] { new Double2(-3.7, 1.6), new Double2(-3.7, 4.4), new Double2(4.7, 1.6), new Double2(4.7, 4.4) }))
                {
                    q.cylinder(p.x, 0.58, p.y, 0.62, 0.62, 0.06, 0x6d5944, sides: 20);
                    q.cylinder(p.x, 0.28, p.y, 0.06, 0.06, 0.56, 0x494c42, sides: 12);
                    for (var a = 0; a < 3; a++)
                    {
                        var angle = (double)a * 2 * Math.PI / 3;
                        q.beam(new Double3(p.x, 0.16, p.y), new Double3(p.x + cos(angle) * 0.38, 0.04, p.y + sin(angle) * 0.38), 0.028, 0x555246);
                        q.vessel(p.x + cos(angle) * 0.24, 0.62, p.y + sin(angle) * 0.24, 0.14, 0xc5b399);
                    }
                    foreach (var side in new[] { -1.0, 1 }) { q.cylinder(p.x + side * 0.95, 0.24, p.y, 0.3, 0.3, 0.48, 0x8c7154, sides: 12); }
                    var group = point(p.x, p.y + 0.95);
                    conversation(group, axis: new Double2(cos(site.yaw), -sin(site.yaw)), index: 23000 + k * 3, count: 2);
                }
                q.box(0.7, 3.08, 5.8, 12, 0.18, 0.18, 0x71644e);
                var sign = point(0.7, 5.95);
                signs.plate("THE TWIN SUNS", eyebrow: "MOS ASTER CANTINA", footer: "DRINKS · MUSIC · SHADE", badge: "09", at: new SCNVector3(sign.x, 3.15, sign.y), width: 5.4, height: 0.70, yaw: (CGFloat)site.yaw, accent: 0xa66e44, into: root);
            } else
            {
                // Market arcade and independent fabric stalls frame a shared
                // pedestrian court. Unequal awnings create a broken roofline.
                q.adobe(0, 1.9, -7.0, 17, 3.8, 3.8, 0xbfa780);
                foreach (var (j, x) in enumerated(new[] { -6.0, 0, 6 })) { q.dome(x, 3.8, -7, 1.6 + (double)j * 0.24, 0.6 + (double)(j % 2) * 0.3, 1.7, 0xc9b48f, sides: 16); }
                foreach (var x in new[] { -5.0, 0, 5 })
                {
                    q.box(x, 0.74, -5.08, 1.14, 1.48, 0.08, 0x414a3d);
                    q.arcade(x, 0, -4.90, 1.14, 1.58, 0.32, 0.22, 0xc2ae8d);
                    q.box(x, 1.85, -5.02, 1.85, 0.12, 0.28, 0xa68d69);
                    foreach (var side in new[] { -1.0, 1 }) { q.adobe(x + side * 0.84, 0.96, -5.02, 0.22, 1.92, 0.28, 0xc2ae8d); }
                }
                // Deep, traversable masonry reveals layer the shopfront behind
                // the stalls. The piers stay outside the central pedestrian aisle.
                foreach (var (j, x) in enumerated(new[] { -5.0, 0, 5 }))
                {
                    q.arcade(x, 0, -4.45, 3.45, 3.10, 1.25, 0.34, 0xbba582, wallTop: 3.48);
                    q.adobe(x, 3.64, -4.45, 4.34, 0.44 + (double)(j % 2) * 0.12, 1.25, 0xbba582);
                    foreach (var side in new[] { -1.0, 1 })
                    {
                        q.adobe(x + side * 2.02, 1.43, -4.45, 0.55, 2.86, 1.25, 0xbba582);
                    }
                }
                foreach (var side in new[] { -1.0, 1 }) { for (var k = 0; k < 3; k++) {
                    double x = side * (5.3 + (double)(k % 2) * 0.65), z = -3.4 + (double)k * 3.6 + (side > 0 ? 0.55 : 0);
                    q.canopy(x, z, 3.6, 2.9, 2.55 + (double)(k % 2) * 0.23, 0.30, new uint[] { 0x846849, 0x5d7d76, 0x9a6747 }[k]);
                    q.box(x, 0.58, z, 2.8, 0.10, 0.8, 0x74624a);
                    foreach (var dx in new[] { -1.15, 1.15 }) { foreach (var dz in new[] { -0.28, 0.28 }) { q.box(x + dx, 0.265, z + dz, 0.07, 0.53, 0.07, 0x64583f); } }
                    for (var j = 0; j < 5; j++)
                    {
                        var px = x - 1.0 + (double)j * 0.49;
                        if (k == 0) { q.vessel(px, 0.64, z, 0.22 + (double)(j % 3) * 0.08, j % 2 == 0 ? 0xa8865fu : 0x8e7354u); }
                        else if (k == 1)
                        {
                            q.produceTray(px, 0.64, z, variant: j + (side > 0 ? 5 : 0));
                        } else
                        {
                            q.cylinder(px, 0.84, z, 0.13, 0.13, 0.40, side < 0 ? 0x608681u : 0xad7954u, sides: 12);
                            q.cylinder(px, 1.07, z, 0.16, 0.16, 0.06, 0xb59b73, sides: 12);
                        }
                    }
                    q.valance(x, z + 1.45, 3.6, 2.40 + (double)(k % 2) * 0.23, 0.18, new uint[] { 0x846849, 0x5d7d76, 0x9a6747 }[k], rise: 0.0);
                    q.crate(x + side * 1.1, 0.02, z - 0.9, 0.48, 0x837052);
                    q.cable(new Double3(x - 1.65, 2.27, z - 1.2), new Double3(x + 1.65, 2.27, z - 1.2), 0.12, 0x51483c);
                    for (var item = 0; item < 4; item++)
                    {
                        double xx = x - 1.15 + (double)item * 0.69, yy = 1.7 + (double)((item + k) % 3) * 0.08;
                        q.beam(new Double3(xx, 2.15, z - 1.2), new Double3(xx, yy + 0.17, z - 1.2), 0.009, 0x675b48, sides: 6);
                        if (k == 0) { q.vessel(xx, yy, z - 1.2, 0.22, 0xae8f65); }
                        else
                        {
                            q.cylinder(xx, yy, z - 1.2, 0.07, 0.075, 0.24, 0x72857c, sides: 16);
                            foreach (var band in new[] { -0.10, 0.06 }) { q.cylinder(xx, yy + band, z - 1.2, 0.082, 0.082, 0.025, 0xb29871, sides: 16); }
                            q.cylinder(xx, yy + 0.15, z - 1.2, 0.045, 0.025, 0.06, 0x726951, sides: 12);
                        }
                    }
                    Double2 seller = point(x, z - 0.9), customer = point(x - side * 2.0, z + 0.3);
                    pendingActivities.Add(new List<StreetActivity> { new StreetActivity(position: seller, target: customer, role: "market vendor", group: 24000 + k + (side > 0 ? 10 : 0), index: 24000 + k + (side > 0 ? 10 : 0)), new StreetActivity(position: customer, target: seller, role: "market customer", group: 24000 + k + (side > 0 ? 10 : 0), index: 24100 + k + (side > 0 ? 10 : 0)) });
                }}
                var sign = point(0, -3.79);
                signs.plate("MOS ASTER MARKET", eyebrow: "FOOD  /  WATER  /  OFFWORLD GOODS", footer: "TRADERS COURT", badge: "10", at: new SCNVector3(sign.x, 3.65, sign.y), width: 4.1, height: 0.55, yaw: (CGFloat)site.yaw, accent: teal, into: root);
            }
            buildings += 1;
        }
    }
}
