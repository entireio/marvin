using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;
using static System.FormattableString;

namespace Marvin;

// PORT: the whole of TownWorld.swift: TownWorld, TownMesh, TownCollisionBuilder and TownPainter.
// Swift arrays are List<T>, dictionaries Dictionary, sets HashSet. Swift `defer` is try/finally.

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
    public bool shadowCullingEnabled = CommandLine.arguments.Contains("--benchmark-shadow-culling");
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
            // PORT (Godot-only, performance): radius lies between 102 and 158 m, so within 83 m of the centre edge < -18:
            // exposure and the outer fade are exactly 0, remaining is exactly 1 and the wind tongues are not needed (the same
            // alpha); beyond it the Swift code runs unchanged.
            [SCNShaderModifierEntryPoint.fragment] = TownGround.pigmentFunctions + "\n" + @"
#pragma transparent
#pragma body
vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float alpha = streetTint.a;
if (length(p) > 83.0) {
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
    alpha = streetTint.a * remaining;
}
ALPHA = alpha;
",
        };
        TownGround.useNoiseTable(material);
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

    private void buildNeighborhoodUtilities()
    {
        var courts = new[] { new Double2(-84.0, -20.0), new Double2(55, -67), new Double2(-20, 86), new Double2(-80, 37) };
        foreach (var (index, center) in enumerated(courts))
        {
            var city = new CityCollisionWorld(collisionBuilder.bodies);
            var candidates = Enumerable.Range(0, 24).Select(k =>
            {
                var angle = (double)k * 2 * Math.PI / 24;
                return center + new Double2(cos(angle), sin(angle)) * 3.8;
            }).ToList();
            // PORT: `.first(where:)` over the 24 candidates.
            Double2? found = null;
            foreach (var candidate in candidates)
            {
                if (!(streetDistance(candidate.x, candidate.y) > 1.5 && !blocksEntrance(candidate))) { continue; }
                var body = new RobotCollisions.Body(position: new Double3(candidate.x, 0, candidate.y), profile: new RobotCollisions.Profile(mass: 1, halfWidth: 1.5, halfDepth: 1.5, height: 3.8, round: true));
                if (city.nearby(body).All(other => RobotCollisions.contact(body, other) == null)) { found = candidate; break; }
            }
            if (found is not Double2 p) { continue; }
            var q = paint(p.x, p.y, yaw: (double)index * 0.8);
            if (index % 2 == 0)
            {
                // Communal water condenser: tank, fin stack, pipe, stone seat.
                q.cylinder(0, 0.45, 0, 0.68, 0.57, 0.9, 0xafa68c, sides: 16);
                q.cylinder(0, 1.7, 0, 0.14, 0.12, 2.5, 0x8b9183, sides: 12);
                for (var k = 0; k < 6; k++) { q.cylinder(0, 1.20 + (double)k * 0.28, 0, 0.38 - (double)k * 0.025, 0.38 - (double)k * 0.025, 0.08, 0xaca990, sides: 12); }
                q.beam(new Double3(0.25, 0.68, 0), new Double3(0.9, 0.68, 0), 0.065, 0x696b5b);
                q.adobe(0.9, 0.23, 0.75, 0.42, 0.46, 1.2, 0xb8a68a);
            } else
            {
                // Freight handcart and accumulated deliveries at a shared court.
                q.box(0, 0.38, 0, 1.55, 0.16, 0.95, 0x717a6c);
                foreach (var x in new[] { -0.65, 0.65 }) { foreach (var z in new[] { -0.38, 0.38 }) { q.beam(new Double3(x - 0.07, 0.17, z), new Double3(x + 0.07, 0.17, z), 0.17, 0x484d44, sides: 12); } }
                q.crate(-0.3, 0.47, 0, 0.62, 0x947c5a); q.crate(0.37, 0.47, 0.09, 0.47, 0xa38c65);
                q.beam(new Double3(0.72, 0.45, -0.4), new Double3(1.3, 0.95, -0.4), 0.04, 0x6a7161);
                q.beam(new Double3(0.72, 0.45, 0.4), new Double3(1.3, 0.95, 0.4), 0.04, 0x6a7161);
            }
            conversation(p + new Double2(2, 0), axis: new Double2(0, 1), index: 25000 + index * 10, count: 2);
        }
    }

    private void buildDoorstepLife()
    {
        var city = new CityCollisionWorld(collisionBuilder.bodies);
        foreach (var (i, e) in enumerated(entrances))
        {
            if (!(i % 3 == 1 && max(abs(e.center.x), abs(e.center.y)) > 40)) { continue; }
            Double2 @out = new Double2(sin(e.yaw), cos(e.yaw)), side = new Double2(@out.y, -@out.x);
            var sign = i % 2 == 0 ? 1.0 : -1.0;
            var p = e.center + side * sign * (e.width / 2 + 0.85) + @out * 0.46;
            var body = new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), heading: e.yaw, profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.48, halfDepth: 0.35, height: 1.1));
            if (!(!blocksEntrance(p) && streetDistance(p.x, p.y) > 0.8 &&
                  city.nearby(body).All(other => RobotCollisions.contact(body, other) == null) &&
                  pedestrianAccess.All(access => access.route.All(point => Simd.distance(point, p) > 0.82)))) { continue; }
            var q = paint(p.x, p.y, yaw: e.yaw);
            switch ((i / 3) % 5)
            {
            case 0:
                q.vessel(-0.19, 0, 0, 0.48, 0xa98a65); q.vessel(0.23, 0.0, 0.08, 0.29, 0xb9a17e);
                break;
            case 1:
                q.crate(-0.17, 0, 0, 0.47, 0x897356); q.crate(0.15, 0.47, -0.03, 0.28, 0x9f8b69);
                break;
            case 2:
                q.box(0, 0.30, 0, 0.86, 0.10, 0.42, 0x8b765b);
                foreach (var x in new[] { -0.33, 0.33 }) { q.adobe(x, 0.13, 0, 0.15, 0.26, 0.34, 0xbca98c); }
                q.vessel(0.23, 0.36, 0, 0.16, 0xc2b199);
                break;
            case 3:
                q.cylinder(0, 0.42, 0, 0.28, 0.25, 0.84, 0x92978a, sides: 16);
                foreach (var y in new[] { 0.1, 0.68 }) { q.cylinder(0, y, 0, 0.295, 0.295, 0.05, 0x585e50, sides: 16); }
                q.beam(new Double3(0, 0.88, 0), new Double3(0.27, 0.88, 0), 0.025, 0x646a5c);
                break;
            default:
                q.box(0, 0.14, 0, 0.72, 0.28, 0.47, 0x655a4b);
                for (var k = 0; k < 3; k++) { q.cylinder(-0.23 + (double)k * 0.23, 0.37, 0, 0.09, 0.07, 0.20, 0x8c9484, sides: 10); }
                break;
            }
        }
    }

    private void buildHouseholdYards()
    {
        // Activity belongs against a facade, with the door-to-street route clear.
        // Check the entire furnished footprint, not only each prop's centre.
        foreach (var (i, e) in sorted(enumerated(entrances), (a, b) => (a.offset * 73) % 503 < (b.offset * 73) % 503))
        {
            if (!(max(abs(e.center.x), abs(e.center.y)) > 36)) { continue; }
            if (householdYards.Count >= 130) { break; }
            Double2 @out = new Double2(sin(e.yaw), cos(e.yaw)), side = new Double2(@out.y, -@out.x);
            var city = new CityCollisionWorld(collisionBuilder.bodies);
            var nearby = pedestrianAccess.Where(access => Simd.distance(access.door, e.center) < 8).SelectMany(access => access.route).ToList();
            Double2? chosen = null;
            foreach (var sign in new[] { i % 2 == 0 ? 1.0 : -1.0, i % 2 == 0 ? -1.0 : 1.0 })
            {
                foreach (var along in new[] { 1.8, 2.7 })
                {
                    var p = e.center + side * sign * along + @out * 0.88;
                    var footprint = new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), heading: e.yaw, profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.98, halfDepth: 0.70, height: 2.3));
                    if (!(streetDistance(p.x, p.y) > 1.2 &&
                          city.nearby(footprint).All(other => RobotCollisions.contact(footprint, other) == null) &&
                          nearby.All(point =>
                          {
                              var walker = new RobotCollisions.Body(position: new Double3(point.x, 0, point.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.26, halfDepth: 0.26, height: 1.45, round: true));
                              return RobotCollisions.contact(footprint, walker) == null;
                          }))) { continue; }
                    chosen = p; break;
                }
                if (chosen != null) { break; }
            }
            if (chosen is not Double2 yard) { continue; }
            var q = paint(yard.x, yard.y, yaw: e.yaw); var kind = i % 6;
            householdYards.Add(yard);
            switch (kind)
            {
            case 0: // Shaded household workbench, drawers and loose tools.
                q.canopy(0, 0, 1.8, 1.25, 1.9, 0.12, new uint[] { 0x8e7756, 0x677c70, 0x936b50 }[(i / 6) % 3]);
                q.box(0, 0.60, -0.15, 1.4, 0.10, 0.65, 0x7e6c52);
                foreach (var x in new[] { -0.57, 0.57 }) { q.box(x, 0.28, -0.15, 0.09, 0.56, 0.56, 0x6f6550); }
                q.crate(-0.35, 0.04, -0.1, 0.38, 0x8d775a);
                for (var j = 0; j < 3; j++)
                {
                    double x = -0.35 + (double)j * 0.22, z = -0.34 + (double)((i / 6 + j) % 3) * 0.045;
                    q.beam(new Double3(x, 0.68, z), new Double3(x + 0.07, 0.68, 0.05), 0.016, 0x737b71, sides: 6);
                    q.ring(x, 0.68, z, 0.038, 0.021, 0.025, 0x919785, sides: 6);
                }
                break;
            case 1: // Water storage and plumbing rather than a decorative barrel.
                q.cylinder(-0.25, 0.55, -0.1, 0.34, 0.30, 1.1, 0x9a9b87, sides: 16);
                foreach (var y in new[] { 0.14, 0.92 }) { q.cylinder(-0.25, y, -0.1, 0.355, 0.355, 0.05, 0x666d5c, sides: 16); }
                q.beam(new Double3(-0.25, 0.28, 0.25), new Double3(0.24, 0.28, 0.25), 0.035, 0x777461);
                q.vessel(0.48, 0, 0.24, 0.5, 0xab8257);
                break;
            case 2: // Uneven stacks of deliveries on a low pallet.
                for (var k = 0; k < 5; k++) { q.box(0, 0.07, -0.43 + (double)k * 0.20, 1.48, 0.14, 0.14, 0x83704f); }
                q.crate(-0.35, 0.14, 0, 0.55, 0x9c825d); q.crate(0.29, 0.14, -0.15, 0.43, 0x7b785f);
                q.crate(-0.30, 0.69, -0.04, 0.36, 0x8b7556);
                break;
            case 3: // Seat, storage niche and a rolled shade mat.
                q.adobe(0, 0.20, -0.17, 1.6, 0.40, 0.54, 0xb2a084);
                q.box(0, 0.43, -0.17, 1.42, 0.06, 0.48, 0x806854);
                q.vessel(0.49, 0.47, -0.18, 0.20, 0xbfa583);
                q.beam(new Double3(-0.65, 0.15, 0.35), new Double3(0.35, 0.15, 0.35), 0.14, 0x9c7a54, sides: 12);
                break;
            case 4: // Repair work: open toolbox, gearing and a spare wheel.
                q.box(-0.30, 0.16, 0, 0.65, 0.32, 0.48, 0x76634f);
                for (var j = 0; j < 4; j++) { q.box(-0.53 + (double)j * 0.15, 0.34, 0, 0.075, 0.05, 0.36, 0x91947f); }
                q.ring(0.43, 0.15, -0.11, 0.30, 0.16, 0.30, 0x695747, sides: 16);
                q.cylinder(0.45, 0.35, -0.12, 0.12, 0.12, 0.1, 0x94826a, sides: 12);
                break;
            default: // Clay storage vessels with different profiles and sizes.
                q.vessel(-0.4, 0, -0.12, 0.72, 0x9c7954);
                q.vessel(0.13, 0, 0.14, 0.46, 0xbba17a);
                q.crate(0.49, 0, -0.22, 0.34, 0x8f7c5c);
                break;
            }
        }
        Godot.GD.Print($"Household outdoor work/storage areas: {householdYards.Count}");
    }

    private void buildDomesticCourts()
    {
        var made = 0;
        foreach (var (i, e) in enumerated(entrances))
        {
            if (!(i % 3 == 0 && max(abs(e.center.x), abs(e.center.y)) > 58)) { continue; }
            if (made >= 24) { break; }
            Double2 @out = new Double2(sin(e.yaw), cos(e.yaw)), side = new Double2(@out.y, -@out.x);
            var origin = e.center + @out * 0.18;
            var pieces = new List<(double, double, double, double)> { (-1.35, 1.25, 0.18, 2.5), (1.35, 1.25, 0.18, 2.5), (-1.04, 2.43, 0.8, 0.18), (1.04, 2.43, 0.8, 0.18) };
            var city = new CityCollisionWorld(collisionBuilder.bodies);
            var bodies = pieces.Select(piece =>
            {
                var (x, z, w, d) = piece;
                var p = origin + side * x + @out * z;
                return new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), heading: e.yaw, profile: new RobotCollisions.Profile(mass: 1, halfWidth: w / 2 + 0.05, halfDepth: d / 2 + 0.05, height: 0.80));
            }).ToList();
            if (!bodies.All(body =>
                streetDistance(body.position.x, body.position.z) > 0.7 && city.nearby(body).All(other => RobotCollisions.contact(body, other) == null))) { continue; }
            var nearbyRoutes = pedestrianAccess.Where(access => Simd.distance(access.door, origin) < 9).SelectMany(access => access.route).ToList();
            if (!nearbyRoutes.All(p =>
            {
                var person = new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.26, halfDepth: 0.26, height: 1.4, round: true));
                return bodies.All(body => RobotCollisions.contact(person, body) == null);
            })) { continue; }
            var q = paint(origin.x, origin.y, yaw: e.yaw); uint ink = i % 2 == 0 ? 0xb8a58au : 0xc5b697u;
            foreach (var (x, z, w, d) in pieces) { q.adobe(x, 0.40, z, w, 0.80, d, ink); }
            // The open central gate is wider than the audited pedestrian body.
            foreach (var x in new[] { -0.59, 0.59 }) { q.adobe(x, 0.53, 2.43, 0.16, 1.06, 0.23, ink); }
            made += 1;
        }
        Godot.GD.Print($"Domestic outdoor courts: {made}");
    }

    private void refreshPedestrianAccess()
    {
        // Replan using all final architecture, stalls, steps and street props,
        // so the audit cannot pass a path blocked by later set dressing.
        var obstacles = collisionBuilder.bodies.Where(body => body.profile.mass != 70 && body.position.y < 1.45 && body.position.y + body.profile.height > 0.10).Select(body =>
            new TownAccessMap.Footprint(center: new Double2(body.position.x, body.position.z), width: body.profile.halfWidth * 2, depth: body.profile.halfDepth * 2, yaw: body.heading)
        ).ToList();
        var access = new TownAccessMap(footprints: obstacles, streets: streets.Select(street => street.path).ToList());
        var refreshed = new List<PedestrianAccess>();
        foreach (var old in pedestrianAccess)
        {
            // PORT: compactMap; a missing entrance or route drops the record.
            var found = entrances.FindIndex(entrance => Simd.distance(entrance.center, old.door) < 0.01);
            if (found < 0) { continue; }
            var e = entrances[found];
            var @out = new Double2(sin(e.yaw), cos(e.yaw));
            if (access.routeToStreet(from: e.center + @out * 0.65) is not List<Double2> route)
            {
                inaccessibleBuildings.Add(old.building); continue;
            }
            refreshed.Add(new PedestrianAccess(building: old.building, door: e.center, route: new List<Double2> { e.center + @out * 0.42, e.center + @out * 0.6 }.Concat(route).ToList()));
        }
        pedestrianAccess = refreshed;
        foreach (var (i, site) in enumerated(venueSites))
        {
            var local = i == 0 ? new Double2(0, -4.1) : (i == 1 ? new Double2(1.4, -0.5) : new Double2(0, -4.3));
            var door = site.center + new Double2(local.x * cos(site.yaw) + local.y * sin(site.yaw), -local.x * sin(site.yaw) + local.y * cos(site.yaw));
            if (access.routeToStreet(from: door) is List<Double2> route) { venueAccess.Add(new PedestrianAccess(building: site.center, door: door, route: route)); }
        }
        Godot.GD.Print($"Final pedestrian access: {pedestrianAccess.Count}, inaccessible [{string.Join(", ", inaccessibleBuildings)}]");
    }

    private void buildStreetLife()
    {
        // Wait beside a real entrance, looking at its door, without occupying
        // the approach used by visitors. Conversations occupy small forecourts.
        foreach (var (i, e) in enumerated(entrances))
        {
            if (!(i % 5 == 0 && max(abs(e.center.x), abs(e.center.y)) < 47)) { continue; }
            Double2 @out = new Double2(sin(e.yaw), cos(e.yaw)), side = new Double2(@out.y, -@out.x);
            if (i % 10 == 0)
            {
                pendingActivities.Add(new List<StreetActivity> { new StreetActivity(position: e.center + @out * 0.75 + side * 0.95,
                    target: e.center, role: "waiting at door", group: 10000 + i, index: 10000 + i) });
            } else
            {
                conversation(e.center + @out * 1.2 + side * 1.6, axis: @out, index: 11000 + i, count: 2);
            }
        }
        for (var group = 0; group < 12; group++)
        {
            conversation(new Double2(-8.3 + (double)(group % 6) * 3.2, finishZ - 8.0 - (double)(group / 6) * 1.25),
                         axis: new Double2(cos((double)group * 0.6), sin((double)group * 0.6)), index: 12000 + group * 3, count: 3);
        }
        // Side terrace: civic spectators overlooking the northern sweeping turn.
        var p = paint(3, 22);
        p.box(0, 0.62, 0, 8, 1.24, 2.4, sand);
        for (var i = 0; i < 18; i++)
        {
            double x = -0.7 + (double)(i % 9) * 0.65, z = 21.7 + (double)(i / 9) * 0.65;
            citizen(x, z, y: 1.26, yaw: Math.PI, index: i + 500, seated: false);
        }
        foreach (var x in new[] { -3.8, 3.8 }) { p.box(x, 1.7, 0, 0.08, 1, 2.3, cream, detail: true); }
    }

    private void buildMarketDetails()
    {
        foreach (var (i, x) in enumerated(new[] { -7.5, -3.7, 3.7, 7.5 }))
        {
            var z = finishZ - 10.0; var p = paint(x, z);
            p.canopy(0, 0, 2.8, 1.45, 1.65, 0.22, i % 2 == 0 ? 0x8b5c44u : 0x617875u);
            p.box(0, 0.62, -0.1, 2.35, 0.16, 0.65, 0x766048);
            foreach (var side in new[] { -1.0, 1 }) { p.box(side * 0.95, 0.3, -0.1, 0.14, 0.6, 0.5, 0x54483b, detail: true); }
            for (var k = 0; k < 6; k++)
            {
                var xx = -0.9 + (double)k * 0.36;
                if (i % 2 == 0) { p.vessel(xx, 0.73, -0.08, 0.38 + (double)(k % 3) * 0.05, k % 2 == 0 ? 0x97816au : 0xa8795cu); }
                else { p.box(xx, 0.81, -0.08, 0.25, 0.22, 0.34, k % 2 == 0 ? 0x667771u : 0xad8f65u, detail: true); }
            }
            p.beam(new Double3(-1.4, 1.64, -0.77), new Double3(1.4, 1.64, -0.77), 0.022, dark, sides: 6);
            foreach (var sx in new[] { -0.75, 0.75 }) { p.beam(new Double3(sx, 1.57, -0.77), new Double3(sx, 1.64, -0.77), 0.012, dark, sides: 5); }
            signs.plate(new[] { "CERAMICS", "DROID EXCHANGE", "SPICE MERCHANT", "POWER CELLS" }[i], eyebrow: "ASTER BAZAAR", footer: "TRADE  /  REPAIR  /  SUPPLIES", badge: $"0{i + 1}",
                        at: new SCNVector3(x, 1.37, z - 0.77), width: 2.15, height: 0.40, yaw: Math.PI, accent: i % 2 == 0 ? rust : teal, into: root);
            pendingActivities.Add(new List<StreetActivity> {
                new StreetActivity(position: new Double2(x, z + 0.65), target: new Double2(x, z - 0.85), role: "market vendor", group: 13000 + i, index: 1701 + i),
                new StreetActivity(position: new Double2(x, z - 0.85), target: new Double2(x, z + 0.65), role: "market customer", group: 13000 + i, index: 1801 + i) });
            p.box(1.12, 0.24, 0.45, 0.43, 0.48, 0.44, 0x665846, detail: true);
        }
        // Pedestrian groups follow roads and cluster at shops, never the course.
        foreach (var (roadIndex, street) in enumerated(streets.Take(6)))
        {
            for (var i = 1; i < street.points.Count; i++)
            {
                Double2 a = street.points[i - 1], delta = street.points[i] - a; var length = Simd.length(delta);
                var count = (int)(length / 3.5);
                for (var k = 0; k < count; k++)
                {
                    var t = ((double)k + 0.5) / (double)max(1, count);
                    var routePoint = a + delta * t;
                    minBy(Enumerable.Range(0, street.path.Count), (j0, j1) => Simd.length_squared(street.path[j0] - routePoint) < Simd.length_squared(street.path[j1] - routePoint), out var nearest);
                    var curvedTangent = Simd.normalize(street.path[min(nearest + 1, street.path.Count - 1)] - street.path[max(0, nearest - 1)]);
                    var curvedNormal = new Double2(-curvedTangent.y, curvedTangent.x);
                    var center = street.path[nearest] + curvedNormal * ((k % 2 == 0 ? 1.0 : -1.0) * (street.width / 2 - 0.32));
                    if (!(max(abs(center.x), abs(center.y)) < 52)) { continue; }
                    var index = 2000 + roadIndex * 100 + i * 13 + k;
                    if (k % 2 == 0)
                    {
                        // Keep the actual street placement instead of discarding it
                        // and spawning every pedestrian at the same few houses.
                        var side = k % 4 == 0 ? 1.0 : -1.0;
                        var route = new List<Double2>();
                        for (var j = max(0, nearest - 24); j <= min(street.path.Count - 1, nearest + 24); j++)
                        {
                            var d = street.path[min(j + 1, street.path.Count - 1)] - street.path[max(0, j - 1)];
                            var n = new Double2(-d.y, d.x) / max(0.001, Simd.length(d));
                            route.Add(street.path[j] + n * (side * (street.width / 2 - 0.55)));
                        }
                        walkingStreets.Add(route);
                    } else
                    {
                        // Outside the walking lane, leave a complete pair or nobody.
                        conversation(center - curvedNormal * 0.65, axis: curvedTangent, index: 14000 + index * 3, count: 2);
                    }
                }
            }
        }
        foreach (var (x, z, yaw, ink) in new[] { (31.0, -16.0, 0.40, 0x996551u), (-24.0, -27.7, 1.4, 0x7c8880u), (7.0, 34.0, -1.4, 0x9a855fu) })
        {
            var p = paint(x, z, yaw: yaw);
            // Original low-slung utility speeders: rounded nose, cockpit, side pods.
            p.box(0, 0.19, 0, 1.3, 0.04, 2.5, 0x726957);
            p.adobe(0, 0.52, 0, 1.22, 0.36, 2.5, ink);
            p.dome(0, 0.64, 0.82, 0.55, 0.23, 0.75, ink, sides: 12);
            p.box(0, 0.77, -0.33, 0.86, 0.26, 0.85, 0x414b4b);
            p.box(0, 0.80, -0.91, 0.88, 0.26, 0.14, ink);
            foreach (var side in new[] { -1.0, 1 })
            {
                p.beam(new Double3(side * 0.75, 0.50, -0.95), new Double3(side * 0.75, 0.50, 0.95), 0.19, 0x605c51, sides: 8);
                p.box(side * 0.4, 0.51, 1.26, 0.16, 0.12, 0.035, 0xd7c393, detail: true);
            }
        }
        // Visible cables and hardware turn the repair pockets into a paddock.
        foreach (var (i, origin) in enumerated(InfieldLayout.tentOrigins))
        {
            var z = origin.y; var center = InfieldLayout.tentPoint(i, new Double2(i == 0 ? -0.1 : 0, i == 0 ? 0.35 : 0)); var p = paint(center.x, center.y, yaw: InfieldLayout.tentYaws[i]);
            for (var k = 0; k < 5; k++)
            {
                p.beam(new Double3(-1.25, 0.08, -0.35 + (double)k * 0.22), new Double3(-0.5, 0.08, -0.55 + (double)k * 0.2), 0.023, 0x554a3d, sides: 5);
            }
            p.box(z < 0 ? -1.5 : 1.5, 0.47, z < 0 ? 0.5 : 0.95, 0.32, 0.94, 0.40, 0x77766a);
            p.box(z < 0 ? -1.5 : 1.5, 0.80, z < 0 ? 0.28 : 0.73, 0.22, 0.18, 0.025, 0x45656b);
            p.box(z < 0 ? -1.5 : 1.5, 0.55, z < 0 ? 0.28 : 0.73, 0.13, 0.05, 0.03, 0xc5a56c);
        }
    }

    private void conversation(Double2 center, Double2 axis, int index, int count)
    {
        var angle = atan2(axis.y, axis.x);
        pendingActivities.Add(Enumerable.Range(0, count).Select(member =>
        {
            var a = angle + (double)member * 2 * Math.PI / (double)count;
            return new StreetActivity(position: center + new Double2(cos(a), sin(a)) * 0.48,
                                      target: center, role: "conversation", group: index, index: index + member);
        }).ToList());
    }

    private bool blocksEntrance(Double2 point)
    {
        return entrances.Any(e =>
        {
            Double2 delta = point - e.center, @out = new Double2(sin(e.yaw), cos(e.yaw));
            double forward = Simd.dot(delta, @out), side = Simd.dot(delta, new Double2(@out.y, -@out.x));
            return Simd.length(delta) < 0.65 || (forward > -0.3 && forward < 1.7 && abs(side) < 0.58);
        });
    }

    private void placeStreetActivities()
    {
        // Validate against finished scenery, before building navigation and the
        // permanent collision cache. Admit groups atomically; no orphan talkers.
        var scenery = new CityCollisionWorld(collisionBuilder.bodies);
        foreach (var group in pendingActivities)
        {
            if (!group.All(person =>
            {
                var p = person.position; var course = DirtCourse.projection(x: p.x, z: p.y);
                if (!(!blocksEntrance(p) && course.offset > 0 && course.distance > DirtCourse.fenceOffset + 0.3)) { return false; }
                var body = new RobotCollisions.Body(position: new Double3(p.x, 0.02, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.24, halfDepth: 0.24, height: 1.45, round: true));
                return !scenery.nearby(body).Any(other => RobotCollisions.contact(body, other) != null)
                    && !streetActivities.Any(activity => Simd.distance(activity.position, p) < 0.65);
            })) { continue; }
            foreach (var person in group)
            {
                citizen(person.position.x, person.position.y, y: 0.02, yaw: person.yaw, index: person.index, seated: false, shelter: true, activity: person.role == "conversation" ? TownCrowd.Activity.conversation : (person.role == "waiting at door" ? TownCrowd.Activity.waiting : TownCrowd.Activity.trading));
                streetActivities.Add(person);
            }
        }
        pendingActivities.Clear();
    }

    private void citizen(double x, double z, double y, double yaw, int index, bool seated, bool walking = false, bool shelter = false, TownCrowd.Activity activity = TownCrowd.Activity.ordinary)
    {
        if (!seated && blocksEntrance(new Double2(x, z))) { return; }
        population += 1;
        if (seated)
        {
            int ix = (int)floor(x / 8), iz = (int)floor(z / 8); var key = Invariant($"{ix},{iz}");
            var zone = spectatorZones.TryGetValue(key, out var existing) ? existing : new SpectatorSoundZone(position: Double2.zero, people: 0, stormPeople: 0);
            zone.position = (zone.position * (double)zone.people + new Double2(x, z)) / (double)(zone.people + 1);
            zone.people += 1;
            if (TownCrowd.staysOutside(x: x, z: z, index: index)) { zone.stormPeople += 1; }
            spectatorZones[key] = zone;
        }
        if (walking && walkingCount < 18) { walkingCount += 1; return; }
        var projection = DirtCourse.projection(x: x, z: z);
        if (projection.offset > 0 && projection.distance > DirtCourse.fenceOffset)
        {
            if (shelter || !TownCrowd.staysOutside(x: x, z: z, index: index)) { absentPeople.Add(collisionBuilder.bodies.Count); }
            collisionBuilder.bodies.Add(new RobotCollisions.Body(position: new Double3(x, y, z), heading: yaw, profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.20, halfDepth: 0.20, height: seated ? 0.8 : 1.45, round: true)));
        }
        _ = crowd.add(x: x, y: y, z: z, yaw: yaw, index: index, seated: seated, animated: false, shelter: shelter, activity: activity);
    }
    /// Authored reference block: construction, repairs and usable objects have
    /// specific placements rather than scattering decoration over the whole city.
    private void buildReferenceDetails()
    {
        var z = finishZ - 4.9; var p = paint(0, z);
        foreach (var side in new[] { -1.0, 1 })
        {
            // Handrails follow the flights, with vertical posts and socket plates.
            var x = side * 7.29;
            p.beam(new Double3(x, 0.86, 1.30), new Double3(x, 3.31, -2.10), 0.026, 0x6d6555);
            for (var i = 0; i < 5; i++)
            {
                double zz = 1.3 - (double)i * 0.85, y = 0.08 + (double)i * 0.60;
                p.beam(new Double3(x, y, zz), new Double3(x, y + 0.81, zz), 0.02, 0x6d6555, sides: 6);
            }
            var q = paint(side * 8.5, finishZ - 5.5);
            // Plaster repairs collect at the plinth, pipes and sill edges.
            for (var k = 0; k < 7; k++)
            {
                q.plasterPatch(-0.94 + (double)k * 0.30, 0.18 + (double)(k % 3) * 0.12, 1.802, 0.23, 0.24, 0xa38e71, seed: k);
            }
            q.box(0, 3.28, 1.83, 2.24, 0.11, 0.20, 0xaa9676, detail: true);
            q.box(0, 3.46, 1.84, 2.33, 0.12, 0.23, 0xd0ba95, detail: true);
            // Copper service riser, elbows, straps and junction housing.
            q.beam(new Double3(0.93, 0.18, 1.86), new Double3(0.93, 2.76, 1.86), 0.036, 0x897157);
            q.beam(new Double3(0.93, 2.76, 1.86), new Double3(0.38, 2.76, 1.86), 0.036, 0x897157);
            foreach (var y in new[] { 0.48, 1.35, 2.28 }) { q.box(0.93, y, 1.88, 0.14, 0.05, 0.09, 0x554c40, detail: true); }
            q.box(0.65, 1.10, 1.94, 0.33, 0.48, 0.19, 0x6d7970, detail: true);
            q.box(0.65, 1.10, 2.042, 0.26, 0.36, 0.012, 0x8d998a, detail: true);
            for (var k = 0; k < 4; k++) { q.box(0.65, 1.20 - (double)k * 0.055, 2.052, 0.18, 0.014, 0.009, 0x3e4842, detail: true); }
            q.cable(new Double3(-0.98, 2.91, 1.88), new Double3(0.9, 2.82, 1.88), 0.18, 0x594b3c);
            q.vessel(-0.91, 0.02, 2.02, 0.44, 0x9f7154);
            q.crate(0.5, 0.02, 2.12, 0.48, 0x8d7656);
        }
        // Riveted fascia and support brackets make the race sign a built object.
        foreach (var x in new[] { -2.7, -1.35, 0, 1.35, 2.7 })
        {
            p.box(x, 1.54, 2.04, 0.06, 0.77, 0.08, 0x655d50, detail: true);
            foreach (var y in new[] { 1.26, 1.83 }) { p.dome(x, y, 2.125, 0.023, 0.023, 0.014, 0x968b70, sides: 6, detail: true); }
        }
        // Market work surfaces: plank joints, stacked produce and hanging stock.
        foreach (var (i, x) in enumerated(new[] { -7.5, -3.7, 3.7, 7.5 }))
        {
            var q = paint(x, finishZ - 10.0);
            q.valance(0, -0.73, 2.8, 1.65, 0.16, 0xa58d67, rise: 0.22);
            for (var k = 0; k < 8; k++) { q.box(-1.0 + (double)k * 0.29, 0.718, -0.1, 0.25, 0.022, 0.67, 0x9c8765, detail: true); }
            q.crate(-1.05, 0.025, 0.77, 0.51, 0x817057);
            q.crate(-1.01, 0.54, 0.76, 0.43, 0x9b835e);
            q.vessel(0.7, 0.72, -0.12, 0.33, 0xba9470);
            q.vessel(1.02, 0.02, 0.64, 0.48, 0x876653);
            q.cable(new Double3(-1.4, 1.64, -0.75), new Double3(1.4, 1.64, -0.75), 0.10, 0x6c5c47);
            for (var k = 0; k < 3; k++)
            {
                var xx = -0.86 + (double)k * 0.44;
                q.beam(new Double3(xx, 1.60, -0.74), new Double3(xx, 1.28, -0.74), 0.008, 0x615a4c, sides: 5);
                if (i % 2 == 0) { q.vessel(xx, 1.02, -0.74, 0.26, 0x987559); }
                else { q.ring(xx, 1.24, -0.74, 0.10, 0.068, 0.10, 0x6b7167, sides: 12); }
            }
        }
        // The hero pit is a functioning workshop: a workboard, drawers, hoist
        // hardware, engine fins and a hose resting on the packed-earth apron.
        {
            var center = InfieldLayout.tentPoint(0, new Double2(-0.1, 0.35)); var q = paint(center.x, center.y, yaw: InfieldLayout.tentYaws[0]);
            q.box(-1.54, 1.05, 0.15, 0.08, 0.77, 1.75, 0x586157, detail: true);
            for (var j = 0; j < 9; j++)
            {
                var zz = -0.55 + (double)j * 0.17;
                q.beam(new Double3(-1.48, 0.82, zz), new Double3(-1.48, 1.23 - (double)(j % 3) * 0.08, zz), 0.014, 0xb0a891, sides: 6);
                q.box(-1.46, 1.22 - (double)(j % 3) * 0.08, zz, 0.025, 0.045, 0.07, 0x8a8d80, detail: true);
            }
            for (var row = 0; row < 3; row++)
            {
                var y = 0.18 + (double)row * 0.15;
                q.box(-0.93, y, 0.93, 0.57, 0.13, 0.13, 0x6e8279, detail: true);
                q.box(-0.93, y, 1.006, 0.19, 0.025, 0.02, 0xb3ad94, detail: true);
            }
            for (var k = 0; k < 7; k++) { q.cylinder(0.51, 0.80 + (double)k * 0.041, 0.91, 0.235, 0.235, 0.014, 0x7e8174, sides: 14, detail: true); }
            q.cable(new Double3(1.4, 0.14, -1.1), new Double3(0.7, 0.10, 0.40), -0.035, 0x4c5148);
            q.crate(-1.30, 0.08, -0.85, 0.50, 0x8a795a);
            q.vessel(-1.42, 0.08, -1.43, 0.39, 0x988b69);
        }
    }

    private void buildWayfinding()
    {
        // Signs face the approach and stand at street edges, not in the roadway.
        var routes = new List<(double, double, double, string, string, string)> {
            (-12.5, finishZ - 10.2, Math.PI, "←  BAZAAR", "GRANDSTAND  /  GATES 1–2", "M"),
            (20.5, -29.0, Math.PI, "←  SPACEPORT", "DOCK 07  /  LANDING BAYS", "D"),
            (27.0, -12.0, -Math.PI / 2, "GATES 1–2  ←", "GRANDSTAND  /  BAZAAR", "R") };
        foreach (var (x, z, yaw, title, footer, badge) in routes)
        {
            var p = paint(x, z, yaw: yaw);
            p.beam(new Double3(0, 0, 0), new Double3(0, 2.5, 0), 0.045, 0x55574f, sides: 8);
            signs.plate(title, eyebrow: "MOS ASTER WAYFINDING", footer: footer, badge: badge,
                        at: new SCNVector3(x, 2.14, z), width: 2.45, height: 0.55, yaw: (CGFloat)yaw, accent: teal, into: root);
        }
        signs.plate("NORTH CURVE", eyebrow: "MOS ASTER GRAND PRIX", footer: "SPECTATOR TERRACE", badge: "N",
                    at: new SCNVector3(3, 0.82, 20.76), width: 3.6, height: 0.55, yaw: Math.PI, into: root);
    }

    public void update(double dt, SCNVector3 camera, Double2 player, List<RobotCollisions.Body> robots = null, Func<SCNNode, bool> visible = null, SCNNode shadowCamera = null, double viewportAspect = 1)
    {
        robots ??= new List<RobotCollisions.Body>();
        var current = shadowCullingEnabled ? ShadowFrustum.cameras(shadowCamera, aspect: viewportAspect) : new List<ShadowFrustum>();
        // Querying SceneKit presentation nodes synchronizes with rendering.
        // Retain our own preceding camera poses instead, plus the current pose.
        updateExplorationDetail(camera: camera, player: player, frusta: current.Concat(priorShadowFrusta).ToList());
        priorShadowFrusta = current.Concat(priorShadowFrusta).Take(2).ToList();
        if (!(dt > 0)) { return; }
        clock += dt;
        crowd.update(time: clock); residents?.update(dt: dt, robots: robots, pedestrians: streetResidents?.bodies ?? new List<RobotCollisions.Body>(), visible: visible);
        streetResidents?.update(dt: dt, obstacles: robots.Concat((IEnumerable<RobotCollisions.Body>)residents?.bodies ?? Array.Empty<RobotCollisions.Body>()).ToList(), visible: visible);
    }
    public void reset()
    {
        clock = 0; crowd.update(time: 0); residents?.reset(); streetResidents?.reset();
        foreach (var node in explorationNodes) { node.isHidden = true; }
    }
    /// Clip the chase/orbit boom against simple scenery bounds, with a small
    /// near-plane margin, including buildings beyond the race-side district.
    public SCNVector3 cameraPivot(Double3 position, double chassisHeight)
    {
        var head = new SCNVector3(position.x, position.y + chassisHeight + 0.18, position.z);
        // The distance field anticipates walls and lintels before their edge
        // crosses an upward ray; it also covers low beams intersecting head Y.
        var rise = min(0.72, cameraRoom(at: head, range: 0.9));
        var raised = new SCNVector3(position.x, (double)head.y + rise, position.z);
        return clearCamera(from: head, to: raised);
    }
    /// Nearby wall clearance anticipates corner occlusion before the boom ray
    /// suddenly crosses a facade. It varies continuously with player position.
    public double cameraRoom(SCNVector3 at, double range = 9)
    {
        var point = at;
        var a = new Double3((double)point.x, (double)point.y, (double)point.z);
        var radius = max(0.1, (range + 0.2) / sqrt(2.0));
        var query = new RobotCollisions.Body(position: a, profile: new RobotCollisions.Profile(mass: 1, halfWidth: radius, halfDepth: radius, height: 1));
        var room = double.PositiveInfinity;
        foreach (var body in collisionWorld.nearby(query).Concat(InfieldLayout.obstacles))
        {
            if (!(body.profile.mass != 70)) { continue; }
            Double3 d = a - body.position; double c = cos(body.heading), s = sin(body.heading); var p = body.profile;
            var q = new Double3(abs(c * d.x - s * d.z) - p.halfWidth, max(-d.y, d.y - p.height), abs(s * d.x + c * d.z) - p.halfDepth);
            room = min(room, Simd.length(Simd.max(q, Double3.zero)));
        }
        foreach (var (low, high) in cameraBounds) { room = min(room, Simd.length(Simd.max(Simd.max(low - a, a - high), Double3.zero))); }
        return max(0, room - 0.16);
    }
    /// Lift a long dune boom over ridges continuously, rather than collapsing
    /// it several metres when a shallow ray first becomes tangent to a crest.
    public SCNVector3 terrainCamera(SCNVector3 from, SCNVector3 to)
    {
        SCNVector3 pivot = from, desired = to;
        if (!(max(abs((double)pivot.x), abs((double)pivot.z)) > DesertTerrain.townEdge)) { return desired; }
        var result = desired;
        for (var i = 1; i <= 64; i++)
        {
            double t = (double)i / 64, x = (double)pivot.x + ((double)desired.x - (double)pivot.x) * t, z = (double)pivot.z + ((double)desired.z - (double)pivot.z) * t;
            var needed = (DirtCourse.height(x: x, z: z) + 0.25 - (double)pivot.y * (1 - t)) / t;
            result.y = max(result.y, (CGFloat)needed);
        }
        return result;
    }
    public SCNVector3 clearCamera(SCNVector3 from, SCNVector3 to)
    {
        SCNVector3 target = from, desired = to;
        var a = new Double3((double)target.x, (double)target.y, (double)target.z);
        Double3 b = new Double3((double)desired.x, (double)desired.y, (double)desired.z), delta = b - a;
        var limit = 1.0;
        void clip(Double3 origin, Double3 direction, Double3 low, Double3 high)
        {
            Double3 lo = low - new Double3(0.14), hi = high + new Double3(0.14);
            double enter = 0.0, leave = 1.0;
            for (var axis = 0; axis < 3; axis++)
            {
                if (abs(direction[axis]) < 1e-8)
                {
                    if (origin[axis] < lo[axis] || origin[axis] > hi[axis]) { return; }
                } else
                {
                    double t0 = (lo[axis] - origin[axis]) / direction[axis], t1 = (hi[axis] - origin[axis]) / direction[axis];
                    enter = max(enter, min(t0, t1)); leave = min(leave, max(t0, t1));
                    if (enter > leave) { return; }
                }
            }
            limit = min(limit, max(0, enter - 0.01 / max(0.01, Simd.length(delta))));
        }
        // Use the actual individual, rotated walls. Compound envelopes cover
        // empty courtyards and squeeze a camera even in a clear passage.
        Double3 middle = (a + b) / 2; var radius = max(0.1, Simd.length(delta) / 2 + 0.2);
        var query = new RobotCollisions.Body(position: middle, profile: new RobotCollisions.Profile(mass: 1, halfWidth: radius, halfDepth: radius, height: 1));
        foreach (var body in collisionWorld.nearby(query).Concat(InfieldLayout.obstacles))
        {
            if (!(body.profile.mass != 70)) { continue; }
            double c = cos(body.heading), s = sin(body.heading);
            Double3 local(Double3 p) => new Double3(c * p.x - s * p.z, p.y, s * p.x + c * p.z);
            var p = body.profile;
            clip(local(a - body.position), local(delta), new Double3(-p.halfWidth, 0, -p.halfDepth), new Double3(p.halfWidth, p.height, p.halfDepth));
        }
        foreach (var (low, high) in cameraBounds) { clip(a, delta, low, high); }
        // A camera boom can intersect a dune even if both endpoints are above
        // ground. Stop at the first obstruction, leaving near-plane clearance.
        var steps = max(1, (int)ceil(Simd.length(delta) * limit / 0.20));
        for (var i = 1; i <= steps; i++)
        {
            var t = limit * (double)i / (double)steps; var p = a + delta * t;
            if (p.y < DirtCourse.height(x: p.x, z: p.z) + 0.18)
            {
                limit = limit * (double)(i - 1) / (double)steps; break;
            }
        }
        var result = a + delta * limit;
        return new SCNVector3(result.x, result.y, result.z);
    }
    public Dictionary<string, int> statistics => new()
    {
        ["householdYards"] = householdYards.Count, ["accessibleCompounds"] = pedestrianAccess.Count, ["inaccessibleCompounds"] = inaccessibleBuildings.Count, ["explorationCells"] = explorationNodes.Count, ["explorationTriangles"] = explorationTriangles, ["streetRoutes"] = streets.Count, ["doorConnections"] = residents?.connections ?? 0, ["buildings"] = buildings, ["repairTents"] = repairLots.Count, ["infieldHouses"] = lots.Count(lot => infield(lot.x, lot.z)), ["people"] = population, ["animatedPeople"] = population,
        ["signs"] = signs.count, ["signTextFits"] = signs.valid ? 1 : 0, ["walkingPeople"] = (residents?.walkers.Count ?? 0) + (streetResidents?.walkers.Count ?? 0), ["crowdCells"] = crowd.cellCount, ["crowdNearTriangles"] = crowd.triangles, ["crowdFarTriangles"] = crowd.farTriangles, ["cells"] = cells.Count, ["nearTriangles"] = triangleCount, ["farTriangles"] = coarseTriangles,
    };
    public bool cityCoveragePassed => Enumerable.Range(0, 8).All(sector =>
        lots.Count(lot =>
        {
            var radius = hypot(lot.x, lot.z);
            var angle = atan2(lot.z, lot.x) + Math.PI;
            return radius > 55 && radius < 120 && min(7, (int)(angle / (2 * Math.PI) * 8)) == sector;
        }) > 16);
    public bool streetNetworkPassed
    {
        get
        {
            var reached = new HashSet<int> { 0 };
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < streets.Count; i++)
                {
                    if (reached.Contains(i)) { continue; }
                    if (reached.Any(j => streets[i].points.Any(point => streets[j].points.Contains(point)))) { reached.Add(i); changed = true; }
                }
            }
            return reached.Count == streets.Count;
        }
    }
    public bool validate()
    {
        return buildings > 350 && population > 120 && (residents?.walkers.Count ?? 0) <= 18 && (residents?.connections ?? 0) >= 4 && triangleCount < 440_000 && coarseTriangles < 320_000 && cells.Count < 150
            && inaccessibleBuildings.Count == 0 && pedestrianAccess.Count == lots.Count && venueAccess.Count == venueSites.Count
            && signs.valid && signs.count >= 20 && crowd.valid && cityCoveragePassed && streetNetworkPassed
            && repairLots.Count == 2
            && lots.All(lot => !infield(lot.x, lot.z) && clearLot(lot.x, lot.z, lot.width, lot.depth))
            && Enumerable.Range(0, InfieldLayout.tentOrigins.Length).All(i => repairFootprintClear(i));
    }

}

/// Offline-style mesh batching performed once at scene preparation. No SceneKit
/// primitive nodes survive for each window, brick or spectator body part.
public sealed class TownMesh
{
    public static int inputVertices = 0, outputVertices = 0;
    public static double maximumMergedBasisRadians = 0.0; public static int mergedBasisComparisons = 0;
    public static List<(SCNGeometry, SCNGeometry)> validationPairs = new();
    public static List<(SCNGeometry, SCNGeometry)> tangentProbePairs = new();
    public int materialSlot = 0;
    private List<int>[] groups = { new(), new(), new(), new() };
    public List<CGPoint> wearUV = new();
    public Func<Float3, CGPoint> wearProjector;
    public List<SCNVector3> positions = new(), normals = new(); public List<CGPoint> uv = new(); public List<float> colors = new(); public List<int> indices = new();
    public void triangle(Float3 a, Float3 b, Float3 c, uint color, IReadOnlyList<Float3> smooth = null)
    {
        var cross = Simd.cross(b - a, c - a);
        if (!(Simd.length_squared(cross) > 1e-12f)) { return; }
        var n = Simd.normalize(cross); var @base = positions.Count;
        // Baked face tone supplies cheap architectural depth even outside sun shadows.
        var vertices = new[] { a, b, c };
        for (var i = 0; i < 3; i++)
        {
            var v = vertices[i];
            var normal = smooth?[i] ?? n;
            float contact = 0.83f + 0.17f * min(1f, max(0f, v.y) / 1.2f);
            float shade = (0.94f + 0.06f * max(0f, normal.y)) * contact;
            positions.Add(new SCNVector3(v.x, v.y, v.z)); normals.Add(new SCNVector3(normal.x, normal.y, normal.z));
            var axis = Simd.abs(n);
            Float2 tex = axis.y > max(axis.x, axis.z) ? new Float2(v.x, v.z) : (axis.x > axis.z ? new Float2(v.z, v.y) : new Float2(v.x, v.y));
            uv.Add(new CGPoint((double)tex.x * 0.48, (double)tex.y * 0.48));
            wearUV.Add(wearProjector?.Invoke(v) ?? new CGPoint(0.0625, 0.0625));
            colors.Add((float)((color >> 16) & 255) / 255 * shade); colors.Add((float)((color >> 8) & 255) / 255 * shade); colors.Add((float)(color & 255) / 255 * shade); colors.Add(1);
        }
        indices.Add(@base); indices.Add(@base + 1); indices.Add(@base + 2);
        groups[materialSlot].Add(@base); groups[materialSlot].Add(@base + 1); groups[materialSlot].Add(@base + 2);
    }
    // PORT: Swift's synthesized Hashable compares every lane with == (NaN is never equal,
    // -0 equals +0); C# Equals would treat NaN as equal, so equality is written out.
    private readonly struct VertexKey : IEquatable<VertexKey>
    {
        public readonly bool quantizedBasis;
        public readonly Float3 position, normal, tangent, bitangent;
        public readonly Double2 uv, wear; public readonly Float4 color;
        public VertexKey(bool quantizedBasis, Float3 position, Float3 normal, Float3 tangent, Float3 bitangent, Double2 uv, Double2 wear, Float4 color)
        {
            this.quantizedBasis = quantizedBasis; this.position = position; this.normal = normal; this.tangent = tangent; this.bitangent = bitangent;
            this.uv = uv; this.wear = wear; this.color = color;
        }
        public bool Equals(VertexKey o) => quantizedBasis == o.quantizedBasis && position == o.position && normal == o.normal && tangent == o.tangent
            && bitangent == o.bitangent && uv == o.uv && wear == o.wear && color == o.color;
        public override bool Equals(object obj) => obj is VertexKey o && Equals(o);
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(quantizedBasis);
            foreach (var v in new[] { position, normal, tangent, bitangent }) { h.Add(v.x + 0f); h.Add(v.y + 0f); h.Add(v.z + 0f); }
            h.Add(uv.x + 0.0); h.Add(uv.y + 0.0); h.Add(wear.x + 0.0); h.Add(wear.y + 0.0);
            h.Add(color.x + 0f); h.Add(color.y + 0f); h.Add(color.z + 0f); h.Add(color.w + 0f);
            return h.ToHashCode();
        }
    }
    public SCNGeometry geometry(SCNMaterial material, Float3 relativeTo = default)
    {
        var origin = relativeTo;
        // Reuse only identical complete vertex attributes. Keep every original
        // triangle, material slot and index order, including normal/UV seams.
        // Guard projected tangent directions too: unrestricted merging would
        // change SceneKit-generated tangent averages for normal maps.
        // This reduces repeated vertex work in both sun maps and the color pass.
        var arguments = CommandLine.arguments;
        var collectBasisComparisons = arguments.Contains("--mesh-reuse-test") || arguments.Contains("--benchmark-tangent-reuse");
        SCNGeometry build(bool reuseNearbyTangents)
        {
            var lookup = new Dictionary<VertexKey, int>(positions.Count / 2); var remap = new List<int>(positions.Count);
            List<SCNVector3> local = new(), outNormals = new(); List<CGPoint> outUV = new(), outWear = new(); var outColors = new List<float>();
            var basisRepresentatives = new List<(Float3, Float3)>();
            Float3 tangent = Float3.zero, bitangent = Float3.zero;
            for (var i = 0; i < positions.Count; i++)
            {
                if (i % 3 == 0)
                {
                    Float3 point(int j) => new Float3((float)positions[j].x - origin.x, (float)positions[j].y - origin.y, (float)positions[j].z - origin.z);
                    Float3 e1 = point(i + 1) - point(i), e2 = point(i + 2) - point(i);
                    var u1 = new Float2((float)uv[i + 1].x - (float)uv[i].x, (float)uv[i + 1].y - (float)uv[i].y);
                    var u2 = new Float2((float)uv[i + 2].x - (float)uv[i].x, (float)uv[i + 2].y - (float)uv[i].y);
                    var determinant = u1.x * u2.y - u1.y * u2.x;
                    tangent = Float3.zero; bitangent = Float3.zero;
                    if (abs(determinant) > 1e-8f) { tangent = (e1 * u2.y - e2 * u1.y) / determinant; bitangent = (e2 * u1.x - e1 * u2.x) / determinant; }
                }
                var p = new Float3((float)positions[i].x - origin.x, (float)positions[i].y - origin.y, (float)positions[i].z - origin.z);
                var n = new Float3((float)normals[i].x, (float)normals[i].y, (float)normals[i].z);
                var c = new Float4(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2], colors[i * 4 + 3]);
                // Compare the basis after normal projection. Raw gradients can
                // differ greatly in length yet generate the same tangent frame.
                // Ill-conditioned or degenerate frames retain the exact key.
                var unitNormal = Simd.normalize(n);
                var projectedT = tangent - unitNormal * Simd.dot(unitNormal, tangent);
                var projectedB = bitangent - unitNormal * Simd.dot(unitNormal, bitangent);
                float tLength = Simd.length(projectedT), bLength = Simd.length(projectedB);
                var stable = float.IsFinite(tLength) && float.IsFinite(bLength) && tLength > 0.1f && bLength > 0.1f
                    && tLength >= 0.25f * Simd.length(tangent) && bLength >= 0.25f * Simd.length(bitangent)
                    && abs(Simd.dot(Simd.cross(unitNormal, projectedT), projectedB)) > 0.25f * tLength * bLength;
                Float3 basisKey(Float3 exact, Float3 projected, float length)
                {
                    if (!(reuseNearbyTangents && stable)) { return exact; }
                    var scaled = projected / length * 65536;
                    return new Float3(rounded(scaled.x), rounded(scaled.y), rounded(scaled.z)) / 65536;
                }
                var key = new VertexKey(quantizedBasis: reuseNearbyTangents && stable, position: p, normal: n, tangent: basisKey(tangent, projectedT, tLength), bitangent: basisKey(bitangent, projectedB, bLength), uv: new Double2(uv[i].x, uv[i].y), wear: new Double2(wearUV[i].x, wearUV[i].y), color: c);
                if (lookup.TryGetValue(key, out var existing))
                {
                    if (reuseNearbyTangents && stable && collectBasisComparisons)
                    {
                        var previous = basisRepresentatives[existing];
                        foreach (var (a, b) in new[] { (previous.Item1, projectedT), (previous.Item2, projectedB) })
                        {
                            Double3 x = Simd.normalize(new Double3(a.x, a.y, a.z)), y = Simd.normalize(new Double3(b.x, b.y, b.z));
                            var angle = atan2(Simd.length(Simd.cross(x, y)), Simd.dot(x, y));
                            TownMesh.maximumMergedBasisRadians = max(TownMesh.maximumMergedBasisRadians, angle);
                            TownMesh.mergedBasisComparisons += 1;
                        }
                    }
                    remap.Add(existing); continue;
                }
                if (reuseNearbyTangents && collectBasisComparisons) { basisRepresentatives.Add((projectedT, projectedB)); }
                var index = local.Count; lookup[key] = index; remap.Add(index);
                local.Add(new SCNVector3(p.x, p.y, p.z)); outNormals.Add(normals[i]); outUV.Add(uv[i]); outWear.Add(wearUV[i]); outColors.Add(c.x); outColors.Add(c.y); outColors.Add(c.z); outColors.Add(c.w);
            }
            var source = new SCNGeometrySource(SCNGeometrySource.Bytes(outColors), SCNGeometrySourceSemantic.color, local.Count, true, 4, 4, 0, 16);
            var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(local), SCNGeometrySource.normals(outNormals), SCNGeometrySource.textureCoordinates(outUV), SCNGeometrySource.textureCoordinates(outWear), source },
                groups.Where(group => group.Count != 0).Select(group => new SCNGeometryElement(group.Select(old => remap[old]).ToList(), SCNGeometryPrimitiveType.triangles)).ToList());
            var materials = new[] { material, CityMaterials.cloth, CityMaterials.metal, CityMaterials.adobe };
            g.materials = Enumerable.Range(0, groups.Length).Where(slot => groups[slot].Count != 0).Select(slot => materials[slot]).ToList();
            return g;
        }
        var reuseNearbyTangents = !arguments.Contains("--benchmark-exact-tangents");
        var g = build(reuseNearbyTangents: reuseNearbyTangents);
        if (arguments.Contains("--benchmark-tangent-reuse") && arguments.Contains("--benchmark-gpu-probe"))
        {
            TownMesh.tangentProbePairs.Add((build(reuseNearbyTangents: false), g));
        }
        TownMesh.inputVertices += positions.Count; TownMesh.outputVertices += g.sourcesFor(SCNGeometrySourceSemantic.vertex).First().vectorCount;
        if (arguments.Contains("--mesh-reuse-test") || arguments.Contains("--benchmark-original-vertices"))
        {
            var rawColor = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, positions.Count, true, 4, 4, 0, 16);
            var rawLocal = positions.Select(v => new SCNVector3((float)v.x - origin.x, (float)v.y - origin.y, (float)v.z - origin.z)).ToList();
            var original = new SCNGeometry(new[] { SCNGeometrySource.vertices(rawLocal), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv), SCNGeometrySource.textureCoordinates(wearUV), rawColor },
                groups.Where(group => group.Count != 0).Select(group => new SCNGeometryElement(group, SCNGeometryPrimitiveType.triangles)).ToList());
            original.materials = Enumerable.Range(0, groups.Length).Where(slot => groups[slot].Count != 0).Select(slot => new[] { material, CityMaterials.cloth, CityMaterials.metal, CityMaterials.adobe }[slot]).ToList();
            if (arguments.Contains("--benchmark-original-vertices")) { return original; }
            TownMesh.validationPairs.Add((original, g));
        }
        return g;
    }
}
public sealed class TownCollisionBuilder { public List<RobotCollisions.Body> bodies = new(); }

// PORT: a Swift struct of immutable fields (the meshes and collision builder are shared
// references in Swift too). A sealed class, because C# local functions and lambdas inside a
// struct cannot use `this`; no painter is ever mutated after construction, so the
// semantics are identical.
internal sealed class TownPainter
{
    public readonly TownMesh near, far; public readonly Float3 origin; public readonly float yaw;
    public readonly TownCollisionBuilder collisions = null;
    public TownPainter(TownMesh near, TownMesh far, Float3 origin, float yaw, TownCollisionBuilder collisions = null)
    {
        this.near = near; this.far = far; this.origin = origin; this.yaw = yaw; this.collisions = collisions;
    }
    private void solid(double x, double y, double z, double w, double h, double d, bool round = false, double turn = 0)
    {
        if (!(h > 0.08 && w > 0.04 && d > 0.04 && y + h / 2 > 0.10)) { return; }
        var p = point(x, y - h / 2, z);
        // Infield fixtures already have their detailed shared collision layout.
        var projection = DirtCourse.projection(x: (double)p.x, z: (double)p.z);
        if (!(projection.offset > 0 && projection.distance > DirtCourse.fenceOffset)) { return; }
        collisions?.bodies.Add(new RobotCollisions.Body(position: new Double3((double)p.x, (double)p.y, (double)p.z), heading: (double)yaw + turn, profile: new RobotCollisions.Profile(mass: 1, halfWidth: w / 2, halfDepth: d / 2, height: h, round: round)));
    }
    private Float3 point(double x, double y, double z)
    {
        float c = cos(yaw), s = sin(yaw);
        return origin + new Float3((float)x * c + (float)z * s, (float)y, -(float)x * s + (float)z * c);
    }
    private void tri(Float3 a, Float3 b, Float3 c, uint ink, bool detail)
    {
        near.triangle(a, b, c, ink); if (!detail) { far.triangle(a, b, c, ink); }
    }
    private void quad(Float3 a, Float3 b, Float3 c, Float3 d, uint ink, bool detail)
    {
        tri(a, b, c, ink, detail); tri(a, c, d, ink, detail);
    }
    public void beam(Double3 from, Double3 to, double radius, uint ink, int sides = 8)
    {
        Double3 middle = (from + to) / 2, delta = to - from;
        solid(middle.x, middle.y, middle.z, radius * 2, abs(delta.y) + radius * 2, hypot(delta.x, delta.z) + radius * 2, turn: atan2(delta.x, delta.z));
        near.materialSlot = 2; far.materialSlot = 2;
        try
        {
            var axis = Simd.normalize(to - from);
            var seed = abs(axis.y) < 0.9 ? new Double3(0, 1, 0) : new Double3(1, 0, 0);
            Double3 u = Simd.normalize(Simd.cross(axis, seed)) * radius, v = Simd.cross(axis, u);
            Float3 world(Double3 p) => point(p.x, p.y, p.z);
            for (var i = 0; i < sides; i++)
            {
                double a = (double)i * 2 * Math.PI / (double)sides, b = (double)(i + 1) * 2 * Math.PI / (double)sides;
                Double3 aa = u * cos(a) + v * sin(a), bb = u * cos(b) + v * sin(b);
                quad(world(from + aa), world(to + aa), world(to + bb), world(from + bb), ink, true);
                tri(world(from), world(from + bb), world(from + aa), ink, true);
                tri(world(to), world(to + aa), world(to + bb), ink, true);
            }
        }
        finally { near.materialSlot = 0; far.materialSlot = 0; }
    }
    /// Replace the wall face itself with a jagged opening, inset reveals and a dark cavity.
    private void damagedWall(Float3 v0, Float3 v1, Float3 v2, Float3 v3, uint ink, int seed)
    {
        var normal = Simd.normalize(Simd.cross(v3 - v0, v1 - v0));
        Float3 sample(float u, float v) => (v0 * (1 - u) + v1 * u) * (1 - v) + (v3 * (1 - u) + v2 * u) * v;
        var boundary = new[] { new Float2(0, 0), new Float2(0.5f, 0), new Float2(1, 0), new Float2(1, 0.5f), new Float2(1, 1), new Float2(0.5f, 1), new Float2(0, 1), new Float2(0, 0.5f) };
        var center = new Float2(0.28f + (float)(seed % 41) * 0.01f, 0.30f + (float)((seed / 41) % 34) * 0.01f);
        var holes = enumerated(boundary).Select(entry =>
        {
            var i = entry.offset;
            float jitter = 0.73f + (float)((seed + i * 7) % 5) * 0.12f;
            var angle = (float)i * floatPi / 4 - floatPi * 0.75f; // Swift Float.pi (rounded toward zero)
            var uv = center + new Float2(cos(angle) * (0.10f + (float)(seed % 7) * 0.01f), sin(angle) * (0.11f + (float)((seed / 7) % 5) * 0.012f)) * jitter;
            return sample(uv.x, uv.y);
        }).ToArray();
        float depth = 0.13f + (float)(seed % 4) * 0.025f;
        var backing = sample(center.x, center.y) - normal * (depth + 0.035f);
        for (var i = 0; i < 8; i++)
        {
            int j = (i + 1) % 8; Float2 a = boundary[i], b = boundary[j];
            quad(sample(a.x, a.y), holes[i], holes[j], sample(b.x, b.y), ink, true);
            Float3 innerA = holes[i] - normal * depth, innerB = holes[j] - normal * depth;
            quad(holes[i], innerA, innerB, holes[j], TownPainter.tone(ink, i % 3 == 0 ? 0.58 : 0.76), true);
            tri(backing, innerB, innerA, TownPainter.tone(ink, 0.48), true);
        }
        // The tiny cavity disappears at the existing architecture LOD distance.
        far.triangle(v0, v3, v2, ink); far.triangle(v0, v2, v1, ink);
    }

    public void adobe(double x, double y, double z, double w, double h, double d, uint ink, bool simple = false)
    {
        int oldNear = near.materialSlot, oldFar = far.materialSlot;
        // Two scanned plaster traditions, selected per compound, not a repeating stain.
        if (max(abs(origin.x), abs(origin.z)) > 30 && abs((long)(origin.x * 17 + origin.z * 31)) % 5 != 0)
        {
            near.materialSlot = 3; far.materialSlot = 3;
        }
        try
        {
            if (simple) { box(x, y, z, w, h, d, ink); return; }
            solid(x, y, z, w, h, d);
            var bevel = min(0.18, min(w, d) * 0.12);
            var outline = new List<(double, double, double)>();
            foreach (var (cx, cz, start) in new[] { (w / 2 - bevel, -d / 2 + bevel, -Math.PI / 2), (w / 2 - bevel, d / 2 - bevel, 0), (-w / 2 + bevel, d / 2 - bevel, Math.PI / 2), (-w / 2 + bevel, -d / 2 + bevel, Math.PI) })
            {
                for (var k = 0; k <= 2; k++)
                {
                    var angle = start + (double)k * Math.PI / 4;
                    outline.Add((cx + cos(angle) * bevel, cz + sin(angle) * bevel, angle));
                }
            }
            Float3 normal(double angle, float rise)
            {
                var a = (float)angle - yaw;
                return Simd.normalize(new Float3(cos(a), rise, sin(a)));
            }
            void smoothQuad(Float3[] v, Float3[] n)
            {
                foreach (var ids in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
                {
                    var normals = ids.Select(id => n[id]).ToArray();
                    near.triangle(v[ids[0]], v[ids[1]], v[ids[2]], ink, smooth: normals);
                    far.triangle(v[ids[0]], v[ids[1]], v[ids[2]], ink, smooth: normals);
                }
            }
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i]; var b = outline[(i + 1) % outline.Count];
                Float3 v0 = point(x + a.Item1, y - h / 2, z + a.Item2), v1 = point(x + b.Item1, y - h / 2, z + b.Item2);
                Float3 v2 = point(x + b.Item1 * 0.97, y + h / 2 - bevel, z + b.Item2 * 0.97), v3 = point(x + a.Item1 * 0.97, y + h / 2 - bevel, z + a.Item2 * 0.97);
                Float3 v4 = point(x + b.Item1 * 0.92, y + h / 2, z + b.Item2 * 0.92), v5 = point(x + a.Item1 * 0.92, y + h / 2, z + a.Item2 * 0.92);
                static long hash(long input)
                {
                    var n = unchecked((uint)input);
                    n = unchecked((n ^ (n >> 16)) * 0x7feb352dU); n = unchecked((n ^ (n >> 15)) * 0x846ca68bU);
                    return (long)((n ^ (n >> 16)) & 0x7fffffff);
                }
                var buildingSeed = hash((long)(origin.x * 17) * 73856093 ^ (long)(origin.z * 17) * 19349663);
                var wearSeed = hash(buildingSeed ^ (long)(x * 53 + y * 101 + w * 71 + d * 97) ^ (long)i * 379);
                var condition = buildingSeed % 100;
                var history = condition < 30 ? 0 : (condition < 70 ? 1 : (condition < 92 ? 2 : 3));
                var tile = history * 16 + (int)(wearSeed % 16);
                Float3 along = v1 - v0, up = v3 - v0;
                Func<Float3, CGPoint> mapper = vertex =>
                {
                    var relative = vertex - v0;
                    var u = max(0f, min(1f, Simd.dot(relative, along) / Simd.length_squared(along)));
                    var v = max(0f, min(1f, Simd.dot(relative, up) / Simd.length_squared(up)));
                    return new CGPoint(((double)(tile % 8) + 0.05 + (double)u * 0.90) / 8,
                                       ((double)(tile / 8) + 0.05 + (double)(1 - v) * 0.90) / 8);
                };
                // Broad wear belongs to walls; roofs, pavement and equipment retain their own surfaces.
                near.wearProjector = mapper; far.wearProjector = mapper;
                try
                {
                    var worn = w > 1.5 && h > 1.5 && history == 3 && wearSeed % 3 != 0;
                    if (worn && i == (wearSeed % 3 == 0 ? 11 : 5))
                    {
                        damagedWall(v0, v1, v2, v3, ink, (int)wearSeed);
                    } else
                    {
                        smoothQuad(new[] { v0, v3, v2, v1 }, new[] { normal(a.Item3, 0), normal(a.Item3, 0.18f), normal(b.Item3, 0.18f), normal(b.Item3, 0) });
                    }
                    near.wearProjector = null; far.wearProjector = null;
                    smoothQuad(new[] { v3, v5, v4, v2 }, new[] { normal(a.Item3, 0.18f), normal(a.Item3, 1.3f), normal(b.Item3, 1.3f), normal(b.Item3, 0.18f) });
                    tri(point(x, y + h / 2, z), v4, v5, ink, false);
                }
                finally { near.wearProjector = null; far.wearProjector = null; }
            }
        }
        finally { near.materialSlot = oldNear; far.materialSlot = oldFar; }
    }

    public void ring(double x, double y, double z, double outer, double inner, double h, uint ink, int sides = 24, bool entry = false)
    {
        for (var i = 0; i < sides; i++)
        {
            double a = (double)i * 2 * Math.PI / (double)sides, b = (double)(i + 1) * 2 * Math.PI / (double)sides;
            if (entry && abs((a + b) / 2 - Math.PI / 2) < Math.PI / 8) { continue; }
            double mid = (a + b) / 2, r = (outer + inner) / 2;
            solid(x + cos(mid) * r, y, z + sin(mid) * r, (b - a) * r + 0.02, h, outer - inner, turn: Math.PI / 2 - mid);
            double lo = y - h / 2, hi = y + h / 2;
            Float3 a0 = point(x + cos(a) * outer, lo, z + sin(a) * outer), b0 = point(x + cos(b) * outer, lo, z + sin(b) * outer);
            Float3 a1 = point(x + cos(a) * outer, hi, z + sin(a) * outer), b1 = point(x + cos(b) * outer, hi, z + sin(b) * outer);
            Float3 a2 = point(x + cos(a) * inner, hi, z + sin(a) * inner), b2 = point(x + cos(b) * inner, hi, z + sin(b) * inner);
            Float3 a3 = point(x + cos(a) * inner, lo, z + sin(a) * inner), b3 = point(x + cos(b) * inner, lo, z + sin(b) * inner);
            quad(a0, a1, b1, b0, ink, false); quad(a1, a2, b2, b1, ink, false); quad(a2, a3, b3, b2, ink, false);
            if (entry && abs(a - Math.PI * 5 / 8) < 0.001) { quad(a0, a3, a2, a1, ink, false); }
            if (entry && abs(b - Math.PI * 3 / 8) < 0.001) { quad(b0, b1, b2, b3, ink, false); }
        }
    }
    /// Three solid wall pieces leave an actual walkable vestibule in the facade.
    public void residentHouse(double w, double h, double d, uint ink, double doorX)
    {
        double x = doorX, opening = 0.92, depth = min(1.2, d * 0.45);
        double left = x - opening / 2 + w / 2, right = w / 2 - x - opening / 2;
        adobe(-w / 2 + left / 2, h / 2, 0, left, h, d, ink);
        adobe(w / 2 - right / 2, h / 2, 0, right, h, d, ink);
        adobe(x, (h + 1.3) / 2, d / 2 - depth / 2, opening, h - 1.3, depth, ink);
        adobe(x, h / 2, -depth / 2, opening, h, d - depth, ink);
        box(x, 0.005, d / 2 - depth / 2, opening, 0.01, depth, 0x71634e);
    }
    /// Five related building traditions: rounded adobe arch, clipped lintel,
    /// pointed arch, broad workshop arch, and a plain metal service entrance.
    public void cityDoor(double w, double h, uint ink, int variant, bool open)
    {
        var thickness = new[] { 0.11, 0.16, 0.09, 0.14, 0.085 }[variant];
        var frameInk = TownPainter.tone(ink, new[] { 1.04, 0.90, 0.98, 1.08, 0.83 }[variant]);
        if (open)
        {
            foreach (var side in new[] { -1.0, 1 }) { box(side * (w / 2 + thickness / 2), h / 2, 0.045, thickness, h, 0.18, frameInk); }
            box(0, h + thickness / 2, 0.045, w + 2 * thickness, thickness, 0.18, frameInk);
            if (variant == 0 || variant == 2) { box(0, h + 0.16, 0.025, w + 0.34, 0.07, 0.24, ink); }
        } else
        {
            List<Double2> outline(double width, double height)
            {
                var r = width / 2;
                var result = new List<Double2> { new Double2(-r, 0), new Double2(r, 0) };
                if (variant == 1)
                {
                    result.AddRange(new[] { new Double2(r, height - 0.23), new Double2(r - 0.17, height), new Double2(-r + 0.17, height), new Double2(-r, height - 0.23) });
                } else if (variant == 2)
                {
                    result.AddRange(new[] { new Double2(r, height * 0.66), new Double2(r * 0.65, height * 0.86), new Double2(0, height), new Double2(-r * 0.65, height * 0.86), new Double2(-r, height * 0.66) });
                } else if (variant == 4) { result.AddRange(new[] { new Double2(r, height), new Double2(-r, height) }); }
                else
                {
                    var rise = variant == 3 ? width * 0.22 : r;
                    for (var i = 0; i <= 12; i++) { var a = (double)i * Math.PI / 12; result.Add(new Double2(cos(a) * r, height - rise + sin(a) * rise)); }
                }
                return result;
            }
            List<Double2> inside = outline(w, h), outside = outline(w + 2 * thickness, h + thickness);
            var center = point(0, h * 0.4, 0.038);
            for (var i = 0; i < inside.Count; i++)
            {
                var j = (i + 1) % inside.Count; Double2 a = inside[i], b = inside[j], c = outside[i], d = outside[j];
                tri(center, point(a.x, a.y, 0.038), point(b.x, b.y, 0.038), 0x302b26, false);
                if (i == 0) { continue; } // Sand meets the threshold without a step.
                quad(point(a.x, a.y, 0.07), point(c.x, c.y, 0.15), point(d.x, d.y, 0.15), point(b.x, b.y, 0.07), frameInk, false);
                quad(point(c.x, c.y, -0.035), point(d.x, d.y, -0.035), point(d.x, d.y, 0.15), point(c.x, c.y, 0.15), ink, false);
            }
            var colors = new uint[] { 0x72634f, 0x65716b, 0x846654, 0x574b40, 0x74716a };
            near.materialSlot = 2; far.materialSlot = 2;
            List<Double2> leaf = outline(w * 0.91, h - 0.045); var middle = point(0, h * 0.4, 0.052);
            for (var i = 0; i < leaf.Count; i++)
            {
                Double2 a = leaf[i], b = leaf[(i + 1) % leaf.Count];
                tri(middle, point(a.x, a.y, 0.052), point(b.x, b.y, 0.052), colors[variant], false);
            }
            double top(double x)
            {
                var result = 0.0;
                for (var k = 0; k < leaf.Count; k++)
                {
                    Double2 a = leaf[k], b = leaf[(k + 1) % leaf.Count];
                    if (!(abs(b.x - a.x) > 0.001)) { continue; }
                    var t = (x - a.x) / (b.x - a.x);
                    if (t >= 0 && t <= 1) { result = max(result, a.y + (b.y - a.y) * t); }
                }
                return result;
            }
            var divisions = variant == 3 ? 5 : (variant == 4 ? 3 : 2);
            for (var k = 1; k < divisions; k++)
            {
                var x = -w * 0.44 + w * 0.88 * (double)k / (double)divisions;
                var height = max(0.1, top(x) - 0.04);
                box(x, height / 2, 0.061, 0.016, height, 0.012, 0x3d3730, detail: true);
            }
            if (variant == 1 || variant == 4)
            {
                foreach (var y in new[] { 0.20, 0.47 }) { box(0, h * y, 0.064, w * 0.81, 0.027, 0.018, 0x99917c, detail: true); }
            }
            box(w * 0.27, h * 0.42, 0.077, 0.04, 0.13, 0.038, 0xb3a18a, detail: true);
            near.materialSlot = 0; far.materialSlot = 0;
        }
        // Wall-mounted access control varies sides and height with the family.
        if (variant == 1 || variant == 4)
        {
            box(w / 2 + thickness + 0.10, h * 0.64, 0.045, 0.12, 0.20, 0.07, 0x545b57, detail: true);
            box(w / 2 + thickness + 0.10, h * 0.68, 0.083, 0.055, 0.035, 0.01, 0x9fa990, detail: true);
        }
    }
    public void door(double x, double z, double w, double h, uint ink, uint trim, double side)
    {
        box(x, h * 0.43, z, w, h * 0.86, 0.05, ink);
        dome(x, h * 0.86, z, w / 2, w * 0.52, 0.055, ink, sides: 12);
        foreach (var sx in new[] { -1.0, 1 }) { adobe(x + sx * (w / 2 + 0.07), h * 0.42, z - side * 0.015, 0.13, h * 0.84, 0.13, trim); }
        for (var i = 0; i < 16; i++)
        {
            double a = (double)i * Math.PI / 16, b = (double)(i + 1) * Math.PI / 16;
            double r = w / 2, outer = r + 0.12, cy = h * 0.86;
            var a0 = point(x + cos(a) * r, cy + sin(a) * r, z + side * 0.06);
            var a1 = point(x + cos(a) * outer, cy + sin(a) * outer, z + side * 0.10);
            var b0 = point(x + cos(b) * r, cy + sin(b) * r, z + side * 0.06);
            var b1 = point(x + cos(b) * outer, cy + sin(b) * outer, z + side * 0.10);
            if (side > 0) { quad(a0, a1, b1, b0, trim, true); } else { quad(b0, b1, a1, a0, trim, true); }
        }
        near.materialSlot = 2;
        box(x, h * 0.40, z + side * 0.035, w * 0.79, h * 0.72, 0.012, 0x86715b, detail: true);
        foreach (var sx in new[] { -0.22, 0.22 }) { box(x + sx * w, h * 0.4, z + side * 0.047, 0.022, h * 0.70, 0.014, 0x493e34, detail: true); }
        box(x + w * 0.22, h * 0.4, z + side * 0.068, 0.055, 0.14, 0.035, 0xb7a184, detail: true);
        near.materialSlot = 0;
        box(x, 0.04, z + side * 0.13, w + 0.18, 0.08, 0.31, trim, detail: true);
    }
    public void awning(double x, double z, double w, double d, double y, uint ink)
    {
        cloth(x, z, w, d, y, 0.12, ink, ridge: false);
    }
    private void cloth(double x, double z, double w, double d, double y, double rise, uint ink, bool ridge)
    {
        near.materialSlot = 1; far.materialSlot = 1;
        try
        {
            int nx = w > 5 ? 24 : 12, nz = d > 3 ? 10 : 6;
            Float3 v(int i, int j)
            {
                double u = (double)i / (double)nx, t = (double)j / (double)nz;
                var roof = ridge ? rise * (1 - abs(u * 2 - 1)) : -t * 0.15;
                var sag = min(0.38, d * 0.065) * sin(t * Math.PI) * (0.65 + 0.35 * sin(u * Math.PI)) + 0.045 * sin(u * 8 * Math.PI) * sin(t * Math.PI);
                return point(x + (u - 0.5) * w, y + roof - sag, z + (t - 0.5) * d);
            }
            for (var i = 0; i < nx; i++) { for (var j = 0; j < nz; j++) {
                Float3 a = v(i, j), b = v(i + 1, j), c = v(i + 1, j + 1), e = v(i, j + 1);
                uint panelInk = (i + (int)abs(origin.x + origin.z)) % 5 == 0 ? TownPainter.tone(ink, 0.88) : ink;
                quad(a, e, c, b, panelInk, true);
            }}
            // Distant cloth keeps the silhouette without folds.
            {
                Float3 a = v(0, 0), b = v(nx, 0), c = v(nx, nz), e = v(0, nz);
                far.triangle(a, e, c, ink); far.triangle(a, c, b, ink);
            }
        }
        finally { near.materialSlot = 0; far.materialSlot = 0; }
    }
    public void repairRug(double w, double d, int variation)
    {
        if (variation == 0) { triangularRug(); return; }
        near.materialSlot = 1; far.materialSlot = 1;
        try
        {
            uint @base = variation == 0 ? 0x8a7960u : 0x827568u;
            void patch(double x, double z, double width, double depth, uint ink, double lift = 0)
            {
                var y = 0.004 + lift;
                quad(point(x - width / 2, y, z - depth / 2), point(x - width / 2, y, z + depth / 2), point(x + width / 2, y, z + depth / 2), point(x + width / 2, y, z - depth / 2), ink, false);
            }
            patch(0, 0, w, d, @base);
            foreach (var side in new[] { -1.0, 1.0 })
            {
                patch(side * (w / 2 - 0.16), 0, 0.19, d - 0.13, 0x545849, 0.002);
                patch(0, side * (d / 2 - 0.17), w - 0.14, 0.21, 0x545849, 0.002);
                patch(0, side * (d / 2 - 0.33), w - 0.31, 0.035, 0xb4a17d, 0.003);
                for (var j = 0; j < 26; j++)
                {
                    var x = -w / 2 + 0.10 + (double)j * (w - 0.20) / 25;
                    var length = 0.09 + (double)((j * 7 + variation) % 5) * 0.013;
                    quad(point(x, 0.003, side * d / 2), point(x + 0.024, 0.003, side * d / 2), point(x + 0.02, 0.001, side * (d / 2 + length)), point(x - 0.005, 0.001, side * (d / 2 + length)), 0xa69574, true);
                }
            }
            // Muted woven checks remain legible as a single textile, with no raised tile seams.
            for (var row = 0; row < 5; row++) { for (var col = 0; col < 5; col++) {
                double x = -1.20 + (double)col * 0.60, z = -1.20 + (double)row * 0.60;
                if ((row + col + variation) % 2 == 0) { patch(x, z, 0.58, 0.58, 0x958b70, 0.001); }
            }}
            foreach (var side in new[] { -1.0, 1.0 }) { for (var j = 0; j < 7; j++) {
                double x = -1.2 + (double)j * 0.4, z = side * (d / 2 - 0.17), y = 0.008;
                quad(point(x - 0.085, y, z), point(x, y, z + 0.068), point(x + 0.085, y, z), point(x, y, z - 0.068), 0xb7a783, true);
            }}
        }
        finally { near.materialSlot = 0; far.materialSlot = 0; }
    }

    private void triangularRug()
    {
        near.materialSlot = 1; far.materialSlot = 1;
        try
        {
            var outline = InfieldLayout.orangeCorners.Select(corner => corner * 0.94).ToArray();
            void patch(Double2[] polygon, uint ink, double y)
            {
                var clipped = polygon.ToList();
                for (var i = 0; i < 3; i++)
                {
                    Double2 a = outline[i], b = outline[(i + 1) % 3], d = b - a;
                    double distance(Double2 p) => d.x * (p.y - a.y) - d.y * (p.x - a.x);
                    var output = new List<Double2>();
                    if (clipped.Count == 0) { return; }
                    for (var j = 0; j < clipped.Count; j++)
                    {
                        Double2 p = clipped[j], q = clipped[(j + 1) % clipped.Count]; double dp = distance(p), dq = distance(q);
                        if (dp <= 0) { output.Add(p); }
                        if ((dp <= 0) != (dq <= 0)) { output.Add(p + (q - p) * (dp / (dp - dq))); }
                    }
                    clipped = output;
                }
                if (!(clipped.Count >= 3)) { return; }
                for (var i = 1; i < clipped.Count - 1; i++)
                {
                    Double2 a = clipped[0], b = clipped[i], c = clipped[i + 1];
                    tri(point(a.x, y, a.y), point(b.x, y, b.y), point(c.x, y, c.y), ink, false);
                }
            }
            patch(outline, 0x8a7960, 0.004);
            for (var x = -3; x <= 2; x++) { for (var z = -3; z <= 2; z++) {
                if (!((x + z) % 2 == 0)) { continue; }
                double a = (double)x * 0.5, b = (double)z * 0.5;
                patch(new[] { new Double2(a, b), new Double2(a, b + 0.49), new Double2(a + 0.49, b + 0.49), new Double2(a + 0.49, b) }, 0x958b70, 0.006);
            }}
            var center = outline.Aggregate(Double2.zero, (sum, corner) => sum + corner) / 3;
            for (var i = 0; i < 3; i++)
            {
                Double2 a = outline[i], b = outline[(i + 1) % 3], ia = a + (center - a) * 0.14, ib = b + (center - b) * 0.14;
                patch(new[] { a, b, ib, ia }, 0x545849, 0.009);
                Double2 direction = b - a, n = Simd.normalize(new Double2(-direction.y, direction.x));
                for (var j = 1; j < 17; j++)
                {
                    Double2 p = a + direction * (double)j / 17, q = p + Simd.normalize(direction) * 0.022;
                    quad(point(p.x, 0.006, p.y), point(q.x, 0.006, q.y), point(q.x + n.x * 0.08, 0.003, q.y + n.y * 0.08), point(p.x + n.x * 0.08, 0.003, p.y + n.y * 0.08), 0xa69574, true);
                }
            }
        }
        finally { near.materialSlot = 0; far.materialSlot = 0; }
    }

    public void triangularRepairCanopy(uint ink)
    {
        var corners = InfieldLayout.orangeCorners.Select(corner => new Double3(corner.x, 2, corner.y)).ToArray();
        near.materialSlot = 1; far.materialSlot = 1;
        var center = point(-0.6, 2.3, 0.53);
        for (var i = 0; i < 3; i++)
        {
            Double3 a = corners[i], b = corners[(i + 1) % 3];
            tri(center, point(a.x, a.y, a.z), point(b.x, b.y, b.z), ink, false);
        }
        near.materialSlot = 0; far.materialSlot = 0;
        foreach (var a in corners) { beam(new Double3(a.x, 0, a.z), a, 0.035, 0x625b50); }
        for (var i = 0; i < 3; i++) { cable(corners[i], corners[(i + 1) % 3], 0.025, 0x625b50); }
    }
    public void engineAssembly(double x, double y, double z, double scale, int variant)
    {
        int oldNear = near.materialSlot, oldFar = far.materialSlot;
        near.materialSlot = 2; far.materialSlot = 2;
        try
        {
            uint ink = variant % 2 == 0 ? 0x7e725eu : 0x7b5540u;
            box(x, y + 0.17 * scale, z, 0.88 * scale, 0.34 * scale, 0.64 * scale, ink);
            foreach (var side in new[] { -1.0, 1 })
            {
                cylinder(x + side * 0.28 * scale, y + 0.45 * scale, z, 0.18 * scale, 0.16 * scale, 0.34 * scale, ink, sides: 20);
                for (var fin = 0; fin < 5; fin++) { cylinder(x + side * 0.28 * scale, y + (0.31 + (double)fin * 0.065) * scale, z, 0.205 * scale, 0.205 * scale, 0.025 * scale, 0x9b9682, sides: 16); }
                foreach (var dz in new[] { -0.20, 0.20 }) { box(x + side * 0.42 * scale, y + 0.355 * scale, z + dz * scale, 0.045 * scale, 0.035 * scale, 0.045 * scale, 0x403d32, detail: true); }
            }
            box(x, y + 0.21 * scale, z + 0.335 * scale, 0.44 * scale, 0.20 * scale, 0.055 * scale, 0x393f39);
            for (var k = 0; k < 7; k++) { box(x + (double)(k - 3) * 0.05 * scale, y + 0.21 * scale, z + 0.37 * scale, 0.014 * scale, 0.15 * scale, 0.015 * scale, 0x96957f, detail: true); }
        }
        finally { near.materialSlot = oldNear; far.materialSlot = oldFar; }
    }
    public void droidSalvage(double x, double y, double z, int variant)
    {
        int oldNear = near.materialSlot, oldFar = far.materialSlot;
        near.materialSlot = 2; far.materialSlot = 2;
        try
        {
            uint ink = variant % 2 == 0 ? 0xaaa58eu : 0x8f7763u;
            cylinder(x, y + 0.29, z, 0.26, 0.25, 0.58, ink, sides: 24);
            for (var k = 0; k < 3; k++) { box(x, y + 0.13 + (double)k * 0.16, z + 0.247, 0.25, 0.10, 0.04, 0x485e5b); }
            dome(x, y + 0.59, z, 0.29, 0.24, 0.29, 0xa8ada1, sides: 24);
            cylinder(x, y + 0.585, z, 0.295, 0.295, 0.035, 0x4f5f59, sides: 24);
            box(x - 0.08, y + 0.72, z + 0.25, 0.12, 0.10, 0.065, 0x283a37);
            foreach (var side in new[] { -1.0, 1 })
            {
                box(x + side * 0.31, y + 0.21, z, 0.10, 0.42, 0.13, ink);
                box(x + side * 0.31, y + 0.055, z + 0.065, 0.17, 0.11, 0.28, 0x8b8b76);
            }
        }
        finally { near.materialSlot = oldNear; far.materialSlot = oldFar; }
    }

    public void salvage(double w, double d, double h, int kind, int seed)
    {
        near.materialSlot = 2; far.materialSlot = 2;
        try
        {
            var rust = new uint[] { 0x80533b, 0x98603e, 0x694c39, 0x9c704a }[seed % 4];
            if (kind == 0)
            {
                ring(0, h / 2, 0, w / 2, w * 0.29, h, rust, sides: 16);
                for (var i = 0; i < 6; i++)
                {
                    var a = (double)i * Math.PI / 3;
                    box(cos(a) * w * 0.39, h + 0.01, sin(a) * w * 0.39, 0.045, 0.025, 0.045, 0x51483d, detail: true);
                }
            } else if (kind == 1)
            {
                box(0, h * 0.46, 0, w * 0.78, h * 0.92, d * 0.80, rust);
                for (var k = 0; k < 5; k++) { box(0, h * 0.95, -d * 0.32 + (double)k * d * 0.16, w, 0.035, 0.035, 0x564b3e, detail: true); }
                cylinder(0, h, 0, w * 0.16, w * 0.16, 0.04, 0x6b6350, sides: 10, detail: true);
            } else
            {
                box(0, h / 2, 0, w, h, d, rust);
                for (var k = 0; k < 4; k++) { box(-w * 0.38 + (double)k * w * 0.25, h + 0.005, 0, 0.025, 0.015, d * 0.85, 0x514a3d, detail: true); }
            }
            for (var k = 0; k < 7; k++)
            {
                double xx = sin((double)(seed * 23 + k * 17)) * w * 0.32, zz = cos((double)(seed * 11 + k * 29)) * d * 0.29;
                box(xx, h + 0.02, zz, 0.035 + (double)(k % 3) * 0.012, 0.005, 0.025, 0xb07a45, detail: true);
            }
        }
        finally { near.materialSlot = 0; far.materialSlot = 0; }
    }

    public void canopy(double x, double z, double w, double d, double y, double rise, uint ink)
    {
        cloth(x, z, w, d, y, rise, ink, ridge: true);
        foreach (var side in new[] { -1.0, 1.0 }) { foreach (var zz in new[] { -d / 2, d / 2 }) {
            beam(new Double3(x + side * w / 2, 0, z + zz), new Double3(x + side * w / 2, y, z + zz), 0.035, 0x625b50);
        }}
        beam(new Double3(x, y + rise, z - d / 2), new Double3(x, y + rise, z + d / 2), 0.04, 0x625b50);
    }
    /// Open voussoir arch. Intrados, jambs and wall thickness are actual geometry.
    public void arcade(double x, double y, double z, double w, double h, double depth, double thickness, uint ink, double? wallTop = null)
    {
        double r = w / 2, spring = y + h - r;
        foreach (var side in new[] { -1.0, 1 })
        {
            adobe(x + side * (r + thickness / 2), (y + spring) / 2, z, thickness, spring - y, depth, ink);
            box(x + side * (r + thickness / 2), y + 0.055, z, thickness + 0.06, 0.11, depth + 0.08, TownPainter.tone(ink, 0.85), detail: true);
        }
        for (var i = 0; i < 14; i++)
        {
            var joint = wallTop == null ? 0.005 : 0.0;
            double a = (double)i * Math.PI / 14 + joint, b = (double)(i + 1) * Math.PI / 14 - joint;
            var color = TownPainter.tone(ink, new[] { 0.97, 1.015, 0.94, 1.0 }[i % 4]);
            Float3 v(double angle, double radius, double zz) => point(x + cos(angle) * radius, spring + sin(angle) * radius, zz);
            double f = z + depth / 2, back = z - depth / 2;
            Float3 a0 = v(a, r, f), b0 = v(b, r, f), a1 = v(a, r + thickness, f), b1 = v(b, r + thickness, f);
            Float3 c0 = v(a, r, back), d0 = v(b, r, back), c1 = v(a, r + thickness, back), d1 = v(b, r + thickness, back);
            quad(a0, a1, b1, b0, color, false); quad(d0, d1, c1, c0, color, false);
            quad(a0, b0, d0, c0, TownPainter.tone(color, 0.72), false);
            quad(b1, a1, c1, d1, color, false);
            quad(c0, c1, a1, a0, color, true); quad(b0, b1, d1, d0, color, true);
            if (wallTop is double top)
            {
                // Fill the spandrel to the horizontal roof course. Sharing the
                // exact outer arc avoids slivers of daylight through masonry.
                Float3 af = point(x + cos(a) * (r + thickness), top, f), bf = point(x + cos(b) * (r + thickness), top, f);
                Float3 ab = point(x + cos(a) * (r + thickness), top, back), bb = point(x + cos(b) * (r + thickness), top, back);
                quad(a1, af, bf, b1, ink, false); quad(d1, bb, ab, c1, ink, false);
                quad(af, ab, bb, bf, ink, false);
            }
        }
    }
    /// Thin, irregular patches follow wall surfaces; no extra decal pass or transparency.
    public void plasterPatch(double x, double y, double z, double rx, double ry, uint ink, int seed)
    {
        var n = 11;
        for (var i = 0; i < n; i++)
        {
            Float3 edge(int k)
            {
                var a = (double)k * 2 * Math.PI / (double)n;
                var r = 0.79 + 0.16 * sin((double)(k * 17 + seed * 23));
                return point(x + cos(a) * rx * r, y + sin(a) * ry * r, z);
            }
            tri(point(x, y, z), edge(i), edge(i + 1), ink, true);
        }
    }
    public void cable(Double3 from, Double3 to, double sag, uint ink)
    {
        var previous = from;
        for (var i = 1; i <= 8; i++)
        {
            var t = (double)i / 8;
            var p = from + (to - from) * t - new Double3(0, sin(t * Math.PI) * sag, 0);
            beam(previous, p, 0.009, ink, sides: 5); previous = p;
        }
    }
    public void valance(double x, double z, double width, double y, double drop, uint ink, double rise = 0)
    {
        near.materialSlot = 1;
        try
        {
            for (var i = 0; i < 24; i++)
            {
                double a = (double)i / 24, b = (double)(i + 1) / 24;
                Float3 top(double u) => point(x + (u - 0.5) * width, y + rise * (1 - abs(u * 2 - 1)), z);
                Float3 bottom(double u) => point(x + (u - 0.5) * width, y + rise * (1 - abs(u * 2 - 1)) - drop * (0.78 + 0.22 * sin(u * 6 * Math.PI)), z + 0.025 * sin(u * 12 * Math.PI));
                quad(top(a), bottom(a), bottom(b), top(b), ink, true);
            }
        }
        finally { near.materialSlot = 0; }
    }
    public void crate(double x, double y, double z, double size, uint ink)
    {
        box(x, y + size / 2, z, size, size, size * 0.72, TownPainter.tone(ink, 0.72), detail: true);
        for (var k = 0; k < 4; k++)
        {
            box(x, y + ((double)k + 0.5) * size / 4, z + size * 0.37, size * 0.94, size * 0.19, 0.026, ink, detail: true);
        }
        foreach (var side in new[] { -1.0, 1 }) { box(x + side * size * 0.38, y + size / 2, z + size * 0.39, 0.045, size, 0.025, TownPainter.tone(ink, 0.84), detail: true); }
    }
    /// Open slatted tray with individual produce, not a solid coloured cuboid.
    public void produceTray(double x, double y, double z, int variant)
    {
        box(x, y + 0.015, z, 0.44, 0.03, 0.48, 0x776044, detail: true);
        foreach (var side in new[] { -1.0, 1 })
        {
            box(x + side * 0.22, y + 0.09, z, 0.025, 0.15, 0.50, 0x96734d, detail: true);
            foreach (var level in new[] { 0.035, 0.115 }) { box(x, y + level, z + side * 0.24, 0.44, 0.04, 0.025, 0x96734d, detail: true); }
        }
        var colors = new uint[] { 0x987342, 0x8a5637, 0x7b8152, 0xada069, 0x6a7450 };
        for (var k = 0; k < 9; k++)
        {
            var xx = x + (double)(k % 3 - 1) * 0.135 + sin((double)(k * 7 + variant)) * 0.012;
            var zz = z + (double)(k / 3 - 1) * 0.145 + cos((double)(k * 11 + variant)) * 0.013;
            var r = 0.059 + (double)((k + variant) % 3) * 0.006;
            dome(xx, y + 0.07, zz, r, r * (variant % 2 == 0 ? 1.4 : 0.9), r, colors[(k / 3 + variant) % colors.Length], sides: 12, detail: true);
        }
    }

    public void vessel(double x, double y, double z, double size, uint ink)
    {
        // Lathed clay profile includes the lip and hollow interior, with smooth normals.
        var profile = new[] { new Double2(0.24, 0), new Double2(0.43, 0.24), new Double2(0.40, 0.43), new Double2(0.22, 0.56), new Double2(0.25, 0.66), new Double2(0.17, 0.66), new Double2(0.17, 0.53), new Double2(0.26, 0.13), new Double2(0, 0.12) };
        for (var j = 0; j < profile.Length - 1; j++)
        {
            Double2 lo = profile[j], hi = profile[j + 1], delta = hi - lo;
            Float3 vertex(double a, Double2 v) => point(x + cos(a) * v.x * size, y + v.y * size, z + sin(a) * v.x * size);
            Float3 normal(double a) => Simd.normalize(new Float3((float)(cos(a - (double)yaw) * delta.y), (float)(-delta.x), (float)(sin(a - (double)yaw) * delta.y)));
            for (var k = 0; k < 16; k++)
            {
                double a = (double)k * 2 * Math.PI / 16, b = (double)(k + 1) * 2 * Math.PI / 16;
                Float3 v0 = vertex(a, lo), v1 = vertex(a, hi), v2 = vertex(b, hi), v3 = vertex(b, lo);
                var c = j > 4 ? TownPainter.tone(ink, 0.66) : ink;
                near.triangle(v0, v1, v2, c, smooth: new[] { normal(a), normal(a), normal(b) });
                near.triangle(v0, v2, v3, c, smooth: new[] { normal(a), normal(b), normal(b) });
            }
        }
    }

    public static uint tone(uint ink, double factor)
    {
        uint r = (uint)min(255, (double)((ink >> 16) & 255) * factor), g = (uint)min(255, (double)((ink >> 8) & 255) * factor), b = (uint)min(255, (double)(ink & 255) * factor);
        return r << 16 | g << 8 | b;
    }
    public void box(double x, double y, double z, double w, double h, double d, uint ink, bool detail = false)
    {
        solid(x, y, z, w, h, d);
        var v = new[] { point(x - w / 2, y - h / 2, z - d / 2), point(x + w / 2, y - h / 2, z - d / 2), point(x + w / 2, y + h / 2, z - d / 2), point(x - w / 2, y + h / 2, z - d / 2), point(x - w / 2, y - h / 2, z + d / 2), point(x + w / 2, y - h / 2, z + d / 2), point(x + w / 2, y + h / 2, z + d / 2), point(x - w / 2, y + h / 2, z + d / 2) };
        foreach (var f in boxFaces) { quad(v[f[0]], v[f[1]], v[f[2]], v[f[3]], ink, detail); }
    }
    private static readonly int[][] boxFaces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 }, new[] { 3, 7, 6, 2 }, new[] { 0, 1, 5, 4 } };
    public void cylinder(double x, double y, double z, double bottom, double top, double h, uint ink, int sides = 12, bool detail = false)
    {
        solid(x, y, z, max(bottom, top) * 2, h, max(bottom, top) * 2, round: true);
        for (var i = 0; i < sides; i++)
        {
            double a = (double)i * 2 * Math.PI / (double)sides, b = (double)(i + 1) * 2 * Math.PI / (double)sides;
            Float3 v0 = point(x + cos(a) * bottom, y - h / 2, z + sin(a) * bottom), v1 = point(x + cos(b) * bottom, y - h / 2, z + sin(b) * bottom);
            Float3 v2 = point(x + cos(b) * top, y + h / 2, z + sin(b) * top), v3 = point(x + cos(a) * top, y + h / 2, z + sin(a) * top);
            Float3 normal(double angle)
            {
                var an = (float)angle - yaw;
                return Simd.normalize(new Float3(cos(an), (float)((bottom - top) / max(h, 0.001)), sin(an)));
            }
            foreach (var (v, n) in new[] { (new[] { v0, v3, v2 }, new[] { normal(a), normal(a), normal(b) }), (new[] { v0, v2, v1 }, new[] { normal(a), normal(b), normal(b) }) })
            {
                near.triangle(v[0], v[1], v[2], ink, smooth: n);
                if (!detail) { far.triangle(v[0], v[1], v[2], ink, smooth: n); }
            }
            tri(point(x, y + h / 2, z), v2, v3, ink, detail);
            tri(point(x, y - h / 2, z), v0, v1, ink, detail);
        }
    }
    public void dome(double x, double y, double z, double rx, double ry, double rz, uint ink, int sides = 16, bool detail = false)
    {
        var rings = sides >= 16 ? 8 : 4;
        for (var j = 0; j < rings; j++) { for (var i = 0; i < sides; i++) {
            double a = (double)i * 2 * Math.PI / (double)sides, b = (double)(i + 1) * 2 * Math.PI / (double)sides;
            double p = (double)j / (double)rings * Math.PI / 2, q = (double)(j + 1) / (double)rings * Math.PI / 2;
            var v0 = point(x + cos(a) * cos(p) * rx, y + sin(p) * ry, z + sin(a) * cos(p) * rz);
            var v1 = point(x + cos(b) * cos(p) * rx, y + sin(p) * ry, z + sin(b) * cos(p) * rz);
            var v2 = point(x + cos(b) * cos(q) * rx, y + sin(q) * ry, z + sin(b) * cos(q) * rz);
            var v3 = point(x + cos(a) * cos(q) * rx, y + sin(q) * ry, z + sin(a) * cos(q) * rz);
            var center = point(x, y, z);
            Float3 normal(Float3 v)
            {
                Float3 delta = v - center; float c = cos(yaw), s = sin(yaw);
                var local = new Float3(delta.x * c - delta.z * s, delta.y, delta.x * s + delta.z * c);
                var n = Simd.normalize(local / new Float3((float)(rx * rx), (float)(ry * ry), (float)(rz * rz)));
                return new Float3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c);
            }
            foreach (var vertices in new[] { new[] { v0, v3, v2 }, new[] { v0, v2, v1 } })
            {
                var normals = vertices.Select(vertex => normal(vertex)).ToArray();
                near.triangle(vertices[0], vertices[1], vertices[2], ink, smooth: normals);
                if (!detail) { far.triangle(vertices[0], vertices[1], vertices[2], ink, smooth: normals); }
            }
        }}
    }
    /// Swift `.enumerated()`.
    private static IEnumerable<(int offset, T element)> enumerated<T>(IEnumerable<T> source)
    {
        var offset = 0;
        foreach (var element in source) { yield return (offset, element); offset += 1; }
    }
}
