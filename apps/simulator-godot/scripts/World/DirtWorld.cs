using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Procedural clay, directional ruts, world-space tread marks and pooled debris.
/// Geometry and textures are generated locally; no network assets are required.
public sealed partial class DirtWorld
{
    public readonly SCNScene scene = new SCNScene();
    public readonly DeformableSand duneSand = new DeformableSand();
    public BinarySky sky { get; private set; }
    private SandstormWorld stormVisual;
    public Sandstorm storm { get; private set; } = new Sandstorm();
    public void configureStorm(Sandstorm value)
    {
        storm = value; stormVisual.root.isHidden = !value.enabled; stormVisual.reset();
        town.setStorm(value.enabled); sky.setStorm(value.enabled);
        if (camera != null) { stormVisual.update(value, camera: camera, dt: 1.0 / 60); }
    }
    public readonly SCNNode cityGateNode = new SCNNode();
    public readonly TownWorld town;
    public readonly Double2[][] escapeRoutes;
    private readonly SCNNode effects = new SCNNode();
    private readonly SCNNode dustBatch = new SCNNode(), clodBatch = new SCNNode();
    // PORT: Swift `weak var camera`; the camera node outlives the world in every caller.
    public SCNNode camera;
    private struct Fleck
    {
        public readonly SCNNode node;
        public Double3 velocity;
        public double life;
        public bool dust;
        public double duration;
        public Float3 tint;
        public float radius;
        public double opacity;
        public Fleck(SCNNode node, bool dust)
        {
            this.node = node; this.dust = dust;
            velocity = Double3.zero; life = 0.0; duration = 1.0; tint = new Float3(1); radius = 0.003f; opacity = 0.0;
        }
    }
    private readonly List<Fleck> flecks = new();
    private readonly DirtTrail[] trails = { new DirtTrail(style: DirtTrail.Style.tracks), new DirtTrail(style: DirtTrail.Style.tires), new DirtTrail(style: DirtTrail.Style.tires), new DirtTrail(style: DirtTrail.Style.tracks) };
    private double[][] emission = { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0 }, new[] { 0.0, 0.0 } };
    public int[] racerEmittedCount { get; private set; } = { 0, 0, 0, 0 };
    public int[] trailCounts => trails.Select(t => t.count).ToArray();
    public Dictionary<string, int>[] trailDiagnostics(Func<SCNNode, bool> visible = null) => trails.Select(t => t.diagnostics(visible: visible)).ToArray();
    public void setBenchmarkTrailsHidden(bool hidden) { foreach (var trail in trails) trail.root.isHidden = hidden; }
    private readonly SCNMaterial dustMaterial = new SCNMaterial();
    private readonly SCNSphere clodGeometry = new SCNSphere(radius: 0.012);
    private readonly int poolSize = 1600;
    private int poolIndex = 0;
    public int emittedCount { get; private set; } = 0;

    // simd bridge for the facade's simdPosition/simdScale/simdWorldTransform (SCNFloat3/SCNFloat4x4 aliases).
    private static Float3 F3(SCNFloat3 v) { var (x, y, z) = SimdBridge.Get(v); return new Float3((float)x, (float)y, (float)z); }
    private static SCNFloat3 S3(Float3 v) => SimdBridge.F3(v.x, v.y, v.z);
    private static SCNVector3 V3(Float3 v) => new SCNVector3(v.x, v.y, v.z);

    private NSImage packedEarthTexture()
    {
        int size = 512;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var bytes = bitmap.bitmapData;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint seed = unchecked((uint)((long)x * 374761393 + (long)y * 668265263));
                seed = unchecked((seed ^ (seed >> 13)) * 1274126177);
                double grain = (double)((seed ^ (seed >> 16)) & 255) / 255;
                double u = (double)x / size, v = (double)y / size;
                double soil = (CityMaterials.surfaceNoise(u, v, cells: 5, seed: 11) - 0.5) * 7 + (CityMaterials.surfaceNoise(u, v, cells: 21, seed: 31) - 0.5) * 5;
                double pebble = grain > 0.984 ? -19.0 : 0.0;
                double value = soil + (grain - 0.5) * 16 + pebble;
                int i = (y * size + x) * 4;
                bytes[i] = (byte)(157 + value); bytes[i + 1] = (byte)(140 + value); bytes[i + 2] = (byte)(115 + value); bytes[i + 3] = 255;
            }
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap); return image;
    }

    public DirtWorld(Action<double, string> progress = null)
    {
        town = new TownWorld(progress: (fraction, label) => progress?.Invoke(0.05 + fraction * 0.43, label));
        progress?.Invoke(0.49, "Planning routes through town");
        escapeRoutes = PostRaceEscape.makeRoutes(city: town.collisionWorld);
        progress?.Invoke(0.495, "Preparing the sand and racecourse");
        sky = new BinarySky(scene: scene);
        stormVisual = new SandstormWorld(texture: packedEarthTexture()); scene.rootNode.addChildNode(stormVisual.root);
        var ground = new SCNPlane(width: 256, height: 256);
        var earth = material(0x827656, roughness: 1);
        earth.diffuse.contents = packedEarthTexture();
        earth.normal.contents = null;
        foreach (var channel in new[] { earth.diffuse, earth.normal })
        {
            channel.wrapS = SCNWrapMode.repeat; channel.wrapT = SCNWrapMode.repeat;
            channel.contentsTransform = SCNMatrix4MakeScale(64, 64, 1);
        }
        ground.materials = new() { earth };
        var terrain = new SCNNode(CommandLine.arguments.Contains("--benchmark-transparent-ground") ? ground : TownGround.coveredTerrain(material: earth)); terrain.name = "Town base terrain"; terrain.eulerAngles.x = -Math.PI / 2; terrain.position.y = -0.025;
        scene.rootNode.addChildNode(terrain);
        addDesertTerrain(earth: earth, progress: fraction => progress?.Invoke(0.50 + fraction * 0.34, "Building the dunes"));
        var clay = material(0x986441, roughness: 0.94);
        clay.diffuse.contents = soilTexture(track: true, normal: false);
        clay.normal.contents = soilTexture(track: true, normal: true); clay.normal.intensity = 0.65;
        {
            // PORT: Bundle.main.resourceURL/Dirt is res://assets/Dirt (tools/sync-assets.py).
            var @base = "res://assets/Dirt/";
            clay.diffuse.contents = NSImage.contentsOf(@base + "diffuse.jpg");
            clay.normal.contents = NSImage.contentsOf(@base + "normal.jpg");
            clay.roughness.contents = NSImage.contentsOf(@base + "roughness.jpg");
            foreach (var channel in new[] { clay.diffuse, clay.normal, clay.roughness })
            {
                channel.wrapS = SCNWrapMode.repeat; channel.wrapT = SCNWrapMode.repeat;
                channel.contentsTransform = SCNMatrix4MakeScale(48, 1, 1);
            }
            // Broad compacted lanes modulate the scanned microdetail independently.
            clay.multiply.contents = soilTexture(track: true, normal: false);
            clay.multiply.intensity = 0.35;
        }
        // Sun-dried rose clay: retain the scan's ruts and grain while lifting
        // its dark brown albedo away from the city paving's pale sandy palette.
        // One shared material keeps lanes, berms and shoulders consistent.
        clay.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.surface] = @"
#pragma body
float clayDetail = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = vec3(0.34, 0.205, 0.145)
       + clayDetail * vec3(0.58, 0.44, 0.33);
",
        };
        // Lane, berms, shoulders and exit ramps share one 3.9 m cross-course
        // texture scale. Restarting V on each strip made the 15 cm shoulders
        // look like a pale band and carried that compressed detail into ramps.
        var ring = courseSurface(inner: -DirtCourse.width, outer: DirtCourse.width, y: 0);
        ring.materials = new() { clay };
        var lane = new SCNNode(ring); lane.name = "Compacted race surface";
        scene.rootNode.addChildNode(lane);
        // Raised loose-soil berms stay outside the driveable surface.
        var berm = courseSurface(inner: DirtCourse.width, outer: DirtCourse.width + DirtCourse.bermWidth, y: 0.10);
        berm.materials = new() { clay };
        scene.rootNode.addChildNode(new SCNNode(berm));
        var inner = courseSurface(inner: -DirtCourse.width - DirtCourse.bermWidth, outer: -DirtCourse.width, y: 0.055);
        inner.materials = new() { clay };
        scene.rootNode.addChildNode(new SCNNode(inner));
        foreach (var (innerEdge, outerEdge) in new[] { (-DirtCourse.fenceOffset, -DirtCourse.width - DirtCourse.bermWidth), (DirtCourse.width + DirtCourse.bermWidth, DirtCourse.fenceOffset) })
        {
            var shoulder = courseSurface(inner: innerEdge, outer: outerEdge, y: -1);
            shoulder.materials = new() { clay }; scene.rootNode.addChildNode(new SCNNode(shoulder));
        }
        addInfieldDirt(earth: earth);
        addServiceEmbankment(clay: clay, earth: earth);
        progress?.Invoke(0.86, "Building track walls and gate");
        addTrackWalls();
        addCityExit(clay: clay, earth: earth);
        // Start / finish checker paint, across the full lane at phase zero.
        var start = DirtCourse.point(0);
        for (int row = 0; row < 2; row++)
            for (int cell = 0; cell < 10; cell++)
            {
                var tile = box(start.x + (double)row * 0.15 - 0.15, 0.003, start.z - DirtCourse.width + ((double)cell + 0.5) * (DirtCourse.width * 2 / 10),
                               0.15, 0.004, DirtCourse.width * 2 / 10, material((row + cell) % 2 == 0 ? 0xddd2b9u : 0x393a32u));
                tile.castsShadow = false;
            }
        // Staggered two-column starting boxes, open at the rear.
        foreach (var slot in DirtCourse.startingGrid)
        {
            var p = DirtCourse.point(slot.phase, offset: slot.offset); double heading = DirtCourse.heading(slot.phase);
            var vertices = new List<SCNVector3>(); var indices = new List<int>();
            foreach (var (cx, cz, w, l) in new[] { (-0.43, 0.0, 0.025, 0.95), (0.43, 0.0, 0.025, 0.95), (0.0, 0.475, 0.86, 0.025) })
            {
                int @base = vertices.Count;
                foreach (var (sx, sz) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                {
                    double x = p.x + cos(heading) * (cx + sx * w / 2) + sin(heading) * (cz + sz * l / 2);
                    double z = p.z - sin(heading) * (cx + sx * w / 2) + cos(heading) * (cz + sz * l / 2);
                    vertices.Add(new SCNVector3(x, DirtCourse.height(x, z) + 0.009, z));
                }
                indices.AddRange(new[] { @base, @base + 2, @base + 1, @base, @base + 3, @base + 2 });
            }
            var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices) }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
            geometry.materials = new() { material(0xe7dcc4, roughness: 1) };
            var node = new SCNNode(geometry); node.name = "Starting grid box"; node.castsShadow = false;
            scene.rootNode.addChildNode(node);
        }
        foreach (var t in new[] { 0.5, 1.8, 3.5, 5.0 })
        {
            var p = DirtCourse.point(t);
            var path = new NSBezierPath(); path.move(new NSPoint(-0.15, -0.12));
            path.line(new NSPoint(0, 0.15)); path.line(new NSPoint(0.15, -0.12));
            path.line(new NSPoint(0, -0.02)); path.close();
            var geometry = new SCNShape(path, extrusionDepth: 0); geometry.materials = new() { material(0xd6ba85) };
            var arrow = new SCNNode(geometry); arrow.position = new SCNVector3(p.x, DirtCourse.elevation(t) + 0.006, p.z);
            arrow.eulerAngles = new SCNVector3(-Math.PI / 2, DirtCourse.heading(t) + Math.PI, 0);
            arrow.castsShadow = false; scene.rootNode.addChildNode(arrow);
        }
        scene.rootNode.addChildNode(town.root);
        scene.rootNode.addChildNode(effects);
        clodGeometry.segmentCount = 5; clodGeometry.materials = new() { material(0xffffff, roughness: 1) };
        WindblownDust.configure(clodGeometry.materials[0]);
        dustMaterial.lightingModel = SCNMaterial.LightingModel.lambert; dustMaterial.diffuse.contents = dustTexture();
        WindblownDust.configure(dustMaterial);
        dustMaterial.writesToDepthBuffer = false; dustMaterial.isDoubleSided = true;
        for (int i = 0; i < poolSize; i++)
        {
            bool dust = i % 3 != 0;
            var node = new SCNNode(dust ? new SCNPlane(width: 0.18, height: 0.18) : clodGeometry);
            if (dust) { node.geometry.materials = new() { dustMaterial }; node.constraints = new() { new SCNBillboardConstraint() }; }
            node.castsShadow = false; node.isHidden = true;
            // Simulation slots are not individual render submissions.
            flecks.Add(new Fleck(node: node, dust: dust));
        }
        foreach (var (batch, mat) in new[] { (dustBatch, dustMaterial), (clodBatch, clodGeometry.materials[0]) })
        {
            var placeholder = new SCNPlane(width: 0, height: 0); placeholder.materials = new() { mat };
            batch.geometry = placeholder; batch.castsShadow = false; effects.addChildNode(batch);
        }
        foreach (var trail in trails) effects.addChildNode(trail.root);
        progress?.Invoke(0.90, "Preparing robots and race");
    }

    /// Level brick courses rise from a common foundation, batched per boundary. The track-facing
    /// surface stays on the existing collision line; thickness extends away from racing.
    private void addTrackWalls()
    {
        uint[] palette = { 0xa28a70, 0x998068, 0xb0997c, 0x927963, 0xa78c70, 0xb29b80 };
        foreach (var side in new[] { -1.0, 1.0 })
        {
            var boundary = DirtCourse.surfacePoints(offset: side * (DirtCourse.fenceOffset + DirtCourse.boundaryWallThickness / 2));
            var distances = new List<double> { 0.0 };
            for (int i = 1; i < boundary.Length; i++) distances.Add(distances[^1] + Simd.length(boundary[i] - boundary[i - 1]));
            double length = distances[^1]; int count = (int)ceil(length / 0.43); double step = length / (double)count;
            Double2 sample(double distance)
            {
                double d = (distance % length + length) % length;
                int lo = 0, hi = distances.Count - 1;
                while (lo + 1 < hi) { int m = (lo + hi) / 2; if (distances[m] <= d) { lo = m; } else { hi = m; } }
                double fraction = (d - distances[lo]) / max(0.000001, distances[hi] - distances[lo]);
                return boundary[lo] + (boundary[hi] - boundary[lo]) * fraction;
            }
            var mesh = new TownMesh();
            void quad(Float3 a, Float3 b, Float3 c, Float3 d, uint ink)
            {
                mesh.triangle(a, c, b, ink); mesh.triangle(a, d, c, ink);
            }
            double wallGround(Double2 p)
            {
                // The exit apron slopes away below this retaining wall; its
                // tapered fill must not carve a notch into adjacent masonry.
                return side > 0 ? DirtCourse.elevation(DirtCourse.phase(p.x, p.y), offset: DirtCourse.width) : DirtCourse.height(p.x, p.y);
            }
            double courseHeight = 0.13, foundation = -0.04;
            double maxHeight = boundary.Select(q => DirtCourse.height(q.x, q.y)).Max() + DirtCourse.postHeight;
            int rows = (int)ceil((maxHeight - foundation) / courseHeight);
            for (int row = 0; row < rows; row++)
                for (int i = 0; i < count; i++)
                {
                    double distance = ((double)i + (row % 2 == 0 ? 0 : 0.5)) * step;
                    Double2 a = sample(distance + 0.004), b = sample(distance + step - 0.004), center = (a + b) * 0.5;
                    Double2 direction = Simd.normalize(b - a), normal = new Double2(-direction.y, direction.x) * (DirtCourse.boundaryWallThickness / 2);
                    int seed = i * 73 + row * 193 + (side > 0 ? 31 : 0);
                    // Global horizontal bed joints: hills add courses from the same
                    // foundation instead of tilting the individual bricks uphill.
                    double target = side > 0 ? max(CityExit.wallTop(a), CityExit.wallTop(b)) : max(wallGround(a), wallGround(center), wallGround(b)) + DirtCourse.postHeight;
                    int localRows = max(3, (int)ceil((target - foundation) / courseHeight));
                    if (!(row < localRows)) continue;
                    double @base = foundation + (double)row * courseHeight;
                    double rise = courseHeight - 0.006;
                    uint ink = palette[(seed ^ (seed >> 3)) % palette.Length];
                    var corners = new List<Double2> { a - normal, b - normal, b + normal, a + normal };
                    // Cut crossing bricks at a fixed vertical jamb plane instead of
                    // dropping a whole stretcher. Alternate courses retain their bond;
                    // the exposed cut faces close the wall right up to the gate posts.
                    var exitLocal = CityExit.local(center);
                    bool atExit = side > 0 && exitLocal.y > -1.1 && exitLocal.y < CityExit.run + 1;
                    bool atService = side < 0 && center.y > -15.4 && center.y < -5.3;
                    if (atExit || atService)
                    {
                        Func<Double2, double> along = atExit
                            ? q => CityExit.local(q).x : q => q.x - DirtCourse.serviceEntryX;
                        double halfWidth = atExit ? CityExit.width / 2 + 0.08 : DirtCourse.serviceEntryHalfWidth;
                        double jambSide = along(center) < 0 ? -1.0 : 1.0;
                        double signedDistance(Double2 q) => jambSide * along(q) - halfWidth;
                        var clipped = new List<Double2>();
                        for (int j = 0; j < corners.Count; j++)
                        {
                            Double2 p = corners[j], q = corners[(j + 1) % corners.Count];
                            double dp = signedDistance(p), dq = signedDistance(q);
                            if (dp >= 0) { clipped.Add(p); }
                            if ((dp >= 0) != (dq >= 0)) { clipped.Add(p + (q - p) * (dp / (dp - dq))); }
                        }
                        corners = clipped;
                        if (!(corners.Count >= 3)) continue;
                    }
                    var brickCenter = corners.Aggregate(Double2.zero, (s, q) => s + q) / (double)corners.Count;
                    var bottom = corners.Select(p => new Float3((float)p.x, (float)@base, (float)p.y)).ToArray();
                    var lip = corners.Select(p => new Float3((float)p.x, (float)(@base + rise - 0.012), (float)p.y)).ToArray();
                    var top = corners.Select((p, j) =>
                    {
                        var q = p + (brickCenter - p) * 0.055;
                        double chip = row == localRows - 1 ? (double)((seed + j * 7) % 7) * 0.001 : 0;
                        return new Float3((float)q.x, (float)(@base + rise - chip), (float)q.y);
                    }).ToArray();
                    for (int j = 0; j < corners.Count; j++)
                    {
                        int k = (j + 1) % corners.Count;
                        quad(bottom[j], bottom[k], lip[k], lip[j], ink);
                        quad(lip[j], lip[k], top[k], top[j], ink);
                    }
                    for (int j = 1; j < top.Length - 1; j++) { mesh.triangle(top[0], top[j + 1], top[j], ink); }
                }
            var node = new SCNNode(mesh.geometry(material: CityMaterials.plaster));
            node.name = side < 0 ? "Inner irregular brick track wall" : "Outer irregular brick track wall";
            scene.rootNode.addChildNode(node);
        }
    }

    /// A closed heightfield across the entire opening, including its side slopes.
    /// No cropped offset ribbons or exposed underside; physics samples this height.
    private void addServiceEmbankment(SCNMaterial clay, SCNMaterial earth)
    {
        int nz = 62; var boundary = DirtCourse.surfacePoints(offset: -DirtCourse.fenceOffset);
        double phase = DirtCourse.projection(DirtCourse.serviceEntryX, -15).phase;
        int center = (int)(phase / (2 * Math.PI) * (double)DirtCourse.sampleCount);
        Double2 boundaryPoint(int i) => boundary[(i + DirtCourse.sampleCount) % DirtCourse.sampleCount];
        bool inApron(int i)
        {
            var p = boundaryPoint(i);
            return abs(p.x - DirtCourse.serviceEntryX) < 3.9 && p.y > -15.4 && p.y < -10;
        }
        int start = center, end = center;
        while (center - start < 40 && inApron(start - 1)) { start -= 1; }
        while (end - center < 40 && inApron(end + 1)) { end += 1; }
        int nx = end - start;
        // Preserve this local edge correspondence. Independently trimming
        // inward offset curves collapses whole rows at the infield hairpin.
        // A monotone extrusion toward the service yard cannot fold onto itself.
        var vertices = new List<SCNVector3>(); var normals = new List<SCNVector3>(); var uv = new List<CGPoint>(); var clayUV = new List<CGPoint>(); var colors = new List<float>(); var indices = new List<int>();
        double renderedHeight(double x, double z)
        {
            double support = DirtCourse.height(x, z), t = max(0, min(1, (support + 0.025) / 0.035));
            return support - 0.0001 * (1 - t * t * (3 - 2 * t));
        }
        void add(double x, double z, double? y = null)
        {
            double h = y ?? renderedHeight(x, z);
            vertices.Add(new SCNVector3(x, h, z));
            double epsilon = 0.025;
            var n = new Double3(renderedHeight(x - epsilon, z) - renderedHeight(x + epsilon, z), 2 * epsilon, renderedHeight(x, z - epsilon) - renderedHeight(x, z + epsilon));
            n /= Simd.length(n); normals.Add(new SCNVector3(n.x, n.y, n.z));
            uv.Add(new CGPoint(x / 4, -z / 4));
            var projection = DirtCourse.projection(x, z);
            clayUV.Add(new CGPoint(projection.phase / (2 * Math.PI), (projection.offset + DirtCourse.width) / (2 * DirtCourse.width)));
            double run = max(0, min(1, (projection.distance - DirtCourse.fenceOffset) / 3.6));
            double side = max(0, min(1, (abs(x - DirtCourse.serviceEntryX) - 1.0) / 2.0));
            double noise = CityMaterials.surfaceNoise(x / 12, z / 12, cells: 7, seed: 197);
            double shoulder = min(1, run * 6), blend = shoulder * shoulder * (3 - 2 * shoulder);
            double red = max(0, min(1, (1 - run) * (1 - side * blend) + run * (1 - run) * (noise - 0.5) * 0.85));
            double foot = max(0, min(1, (h + 0.025) / 0.06));
            colors.AddRange(new[] { (float)(red * foot * foot * (3 - 2 * foot)), 0, 0, 1 });
        }
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                Double2 p;
                if (j == 0) { p = boundaryPoint(start + i); }
                else
                {
                    // Put the retaining drop inside its masonry, not halfway
                    // across an arbitrary apron triangle.
                    double offset = -DirtCourse.fenceOffset - DirtCourse.boundaryWallThickness + (j == 1 ? 0.001 : -0.001);
                    var edge = DirtCourse.point((double)(start + i) / (double)DirtCourse.sampleCount * 2 * Math.PI, offset: offset);
                    p = new Double2(edge.x, edge.z + (double)max(0, j - 2) * 0.08);
                }
                add(p.x, p.y);
                clayUV[^1] = new CGPoint((double)(start + i) / (double)DirtCourse.sampleCount, (DirtCourse.width - DirtCourse.fenceOffset - (p.y - boundaryPoint(start + i).y)) / (2 * DirtCourse.width));
            }
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + (nx + 1), d = c + 1;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
        // Fill every perimeter edge down below the ground plane, including the
        // concealed track-side seam. The underside can never be seen through.
        var border = Enumerable.Range(0, nx + 1).Concat(Enumerable.Range(1, nz).Select(k => k * (nx + 1) + nx)).Concat(Enumerable.Range(0, nx).Reverse().Select(k => nz * (nx + 1) + k)).Concat(Enumerable.Range(1, nz - 1).Reverse().Select(k => k * (nx + 1))).ToArray();
        int bottom = vertices.Count;
        foreach (var index in border) { var v = vertices[index]; add(v.x, v.z, -0.08); }
        for (int i = 0; i < border.Length; i++)
        {
            int j = (i + 1) % border.Length, a = border[i], b = border[j], c = bottom + i, d = bottom + j;
            indices.AddRange(new[] { a, b, c, b, d, c });
        }
        for (int i = 1; i < border.Length - 1; i++) { indices.AddRange(new[] { bottom, bottom + i + 1, bottom + i }); }
        var colorSource = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
        var sources = new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv) };
        var elements = new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) };
        var sandGeometry = new SCNGeometry(sources, elements);
        // Use the actual surrounding ground material, not an approximate tint.
        var sandMaterial = earth.copy();
        sandMaterial.diffuse.contentsTransform = SCNMatrix4Identity;
        sandGeometry.materials = new() { sandMaterial };
        var @base = new SCNNode(sandGeometry); @base.name = "Filled service embankment";
        @base.castsShadow = false; scene.rootNode.addChildNode(@base);
        var geometry = new SCNGeometry(new[] { sources[0], sources[1], SCNGeometrySource.textureCoordinates(clayUV), colorSource }, new[] { new SCNGeometryElement(indices.Take(nx * nz * 6).ToList(), SCNGeometryPrimitiveType.triangles) });
        var material = clay.copy();
        var modifiers = material.shaderModifiers ?? new();
        modifiers[SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
float redSoil;
#pragma body
redSoil = COLOR.r;
";
        // `_output.color.rgb *= redSoil; _output.color.a = redSoil;` (premultiplied, #pragma transparent)
        // is the `ALPHA = a;` idiom (PORTING.md).
        modifiers[SCNShaderModifierEntryPoint.fragment] = @"
#pragma transparent
#pragma body
ALPHA = redSoil;
";
        material.shaderModifiers = modifiers;
        material.transparencyMode = SCNTransparencyMode.aOne; material.writesToDepthBuffer = false;
        geometry.materials = new() { material };
        var pigment = new SCNNode(geometry); pigment.name = "Track clay mixed into service sand";
        pigment.position.y = 0.0002; pigment.castsShadow = false;
        scene.rootNode.addChildNode(pigment);
    }

    /// Shared world-space pigment follows the ground and both embankments.
    private void addInfieldDirt(SCNMaterial earth)
    {
        int size = 768; double span = 60.0;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var bytes = bitmap.bitmapData;
        Array.Clear(bytes, 0, size * size * 4);
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                double x = ((double)column + 0.5) / (double)size * span - span / 2;
                double z = ((double)row + 0.5) / (double)size * span - span / 2;
                var p = DirtCourse.projection(x, z);
                if (!(p.distance > DirtCourse.terrainEdge - 0.10)) continue;
                uint seed = unchecked((uint)((long)column * 374761393 + (long)row * 668265263));
                seed = unchecked((seed ^ (seed >> 13)) * 1274126177);
                double grain = (double)((seed ^ (seed >> 16)) & 255) / 255;
                double broad = sin(x * 2.1 + sin(z * 1.7)) * sin(z * 2.7 + x * 0.8);
                double reach = 1.05 + 0.50 * sin(p.phase * 19) + 0.22 * sin(p.phase * 47);
                double edge = max(0, 1 - (p.distance - DirtCourse.terrainEdge) / max(0.35, reach + broad * 0.23));
                // Traffic fans out after the gate and thins toward the service bench.
                double progress = max(0, min(1, (z + 12.0) / 3.0));
                double lateral = abs(x - DirtCourse.serviceEntryX);
                double route = max(0, 1 - lateral / (0.95 + progress * 0.60 + broad * 0.18))
                    * max(0, min(1, (z + 15.0) / 1.5)) * max(0, min(1, (-5.1 - z) / 2.0));
                double wheel = exp(-pow((lateral - 0.46) / 0.13, 2)) * route;
                double coverage;
                if (p.offset < 0)
                {
                    // Keep the existing infield deposit and service traffic mask verbatim.
                    coverage = max(edge * 0.78, route * 0.73 + wheel * 0.20);
                }
                else
                {
                    // Soil thrown over the wall settles in irregular fans, strongest near
                    // the boundary, with broader sparse patches out toward the town.
                    double macro = CityMaterials.surfaceNoise(x / 60 + 0.5, z / 60 + 0.5, cells: 23, seed: 83);
                    double detail = CityMaterials.surfaceNoise(x / 60 + 0.5, z / 60 + 0.5, cells: 79, seed: 137);
                    double spread = 1.5 + macro * 2.1;
                    double falloff = max(0, 1 - (p.distance - DirtCourse.terrainEdge) / spread);
                    coverage = pow(falloff, 1.65) * (0.36 + macro * 0.49) * (0.58 + detail * 0.42);
                }
                double alpha = max(0, min(0.94, coverage * (0.72 + grain * 0.33) + broad * 0.045 * coverage));
                int index = (row * size + column) * 4;
                double value = grain * 12 - 6 - (p.offset < 0 ? wheel * 15 : 0);
                bytes[index] = (byte)max(0, min(255, (184 + value) * alpha));
                bytes[index + 1] = (byte)max(0, min(255, (144 + value) * alpha));
                bytes[index + 2] = (byte)max(0, min(255, (118 + value) * alpha));
                bytes[index + 3] = (byte)(alpha * 255);
            }
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap);
        var deposit = new SCNMaterialProperty(contents: image);
        deposit.wrapS = SCNWrapMode.clampToBorder; deposit.wrapT = SCNWrapMode.clampToBorder;
        deposit.mipFilter = SCNFilterMode.linear;
        earth.setValue(deposit, "infieldDeposit");
        var modifiers = earth.shaderModifiers ?? new();
        var @base = modifiers.TryGetValue(SCNShaderModifierEntryPoint.surface, out var existing) ? existing : "#pragma body\n";
        // MSL: constexpr sampler(coord::normalized, address::clamp_to_zero, filter::linear) has no mip filter,
        // so level 0 is sampled; clamp_to_zero is the inside-[0,1] test. SceneKit samples the premultiplied
        // bitmap as premultiplied linear colour; the facade's texture holds straight alpha (PORTING.md), so
        // the sample is premultiplied here.
        modifiers[SCNShaderModifierEntryPoint.surface] = "#pragma arguments\nsampler2D infieldDeposit : source_color, filter_linear, repeat_disable;\n#pragma declaration\n" + @base + "\n" + @"
vec2 depositWorld = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
vec2 depositUV = (depositWorld + 30.0) / 60.0;
vec4 deposit = texture(infieldDeposit, depositUV) * scn_inside(depositUV);
deposit.rgb *= deposit.a;
ALBEDO = ALBEDO * (1.0 - deposit.a) + deposit.rgb;
";
        earth.shaderModifiers = modifiers;
    }

    private SCNGeometry courseSurface(double inner, double outer, double y, bool serviceOnly = false)
    {
        var points = new List<SCNVector3>(); var uv = new List<CGPoint>(); var indices = new List<int>();
        int segments = DirtCourse.sampleCount, strips = 32;
        var outlines = Enumerable.Range(0, strips + 1).Select(j => DirtCourse.surfacePoints(offset: inner + (outer - inner) * (double)j / (double)strips)).ToArray();
        for (int i = 0; i <= segments; i++)
        {
            double phase = (double)i / (double)segments * 2 * Math.PI;
            for (int j = 0; j <= strips; j++)
            {
                double across = (double)j / (double)strips;
                var p = outlines[j][i];
                double rut = y < 0 ? 0 : y == 0 ? 0.0015 * sin(across * 180 + sin(phase * 9) * 0.8) * sin(across * Math.PI) : sin(across * Math.PI) * y;
                double height = DirtCourse.height(p.x, p.y) + (y == 0 ? rut : 0);
                points.Add(new SCNVector3(p.x, height, p.y)); uv.Add(new CGPoint((double)i / (double)segments, (inner + (outer - inner) * across + DirtCourse.width) / (2 * DirtCourse.width)));
                if (i < segments && j < strips)
                {
                    int a = i * (strips + 1) + j, b = a + (strips + 1);
                    var midpoint = (outlines[j][i] + outlines[j][i + 1]) * 0.5;
                    if (!serviceOnly || DirtCourse.serviceAccess(midpoint.x, midpoint.y))
                    {
                        indices.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                    }
                }
            }
        }
        var normals = new List<SCNVector3>();
        for (int i = 0; i <= segments; i++)
            for (int j = 0; j <= strips; j++)
            {
                SCNVector3 a = points[min(segments, i + 1) * (strips + 1) + j], b = points[max(0, i - 1) * (strips + 1) + j];
                SCNVector3 c = points[i * (strips + 1) + min(strips, j + 1)], d = points[i * (strips + 1) + max(0, j - 1)];
                var along = new Double3(a.x - b.x, a.y - b.y, a.z - b.z);
                var acrossVector = new Double3(c.x - d.x, c.y - d.y, c.z - d.z);
                var n = new Double3(along.y * acrossVector.z - along.z * acrossVector.y, along.z * acrossVector.x - along.x * acrossVector.z, along.x * acrossVector.y - along.y * acrossVector.x);
                if (n.y < 0) { n = -n; }
                double length = sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                n = length > 1e-12 ? n / length : new Double3(0, 1, 0);
                var point = points[i * (strips + 1) + j]; double x = point.x, z = point.z;
                var gate = CityExit.local(new Double2(x, z));
                if ((abs(x - DirtCourse.serviceEntryX) < 4 && z > -16 && z < -9) || (abs(gate.x) < 6 && abs(gate.y) < 2))
                {
                    double e = 0.025;
                    n = Simd.normalize(new Double3(DirtCourse.height(x - e, z) - DirtCourse.height(x + e, z), 2 * e, DirtCourse.height(x, z - e) - DirtCourse.height(x, z + e)));
                }
                normals.Add(new SCNVector3(n.x, n.y, n.z));
            }
        var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(points), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv) }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
        return geometry;
    }
    private NSImage soilTexture(bool track, bool normal)
    {
        int w = 1024, h = 256;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        var data = bitmap.bitmapData;
        double noise(long x, long y)
        {
            uint n = unchecked((uint)(x * 374761393 + y * 668265263));
            n = unchecked((n ^ (n >> 13)) * 1274126177);
            return (double)(n ^ (n >> 16)) / (double)uint.MaxValue;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = (double)y / (double)h, u = (double)x / (double)w;
                double grain = noise(x, y), blotch = noise(x / 16, y / 12);
                double groove = sin(v * 190 + sin(u * Math.PI * 12) * 0.7);
                double lane = exp(-pow((v - 0.52) / 0.27, 2));
                double value = (grain - 0.5) * 0.20 + (blotch - 0.5) * 0.08 + (track ? groove * 0.025 - lane * 0.13 : 0);
                double[] rgb;
                if (normal)
                {
                    rgb = new[] { 0.5 + (grain - noise(x + 1, y)) * 0.22, 0.5 + (grain - noise(x, y + 1)) * 0.22 + (track ? cos(v * 190) * 0.11 : 0), 0.97 };
                }
                else
                {
                    rgb = track ? new[] { 0.57 + value, 0.37 + value * 0.8, 0.23 + value * 0.55 } : new[] { 0.66 + value * 0.45, 0.53 + value * 0.4, 0.37 + value * 0.3 };
                }
                int i = (y * w + x) * 4;
                for (int c = 0; c < 3; c++) { data[i + c] = (byte)max(0, min(255, rgb[c] * 255)); }
                data[i + 3] = 255;
            }
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }
    private NSImage dustTexture() => WindblownDust.texture(plume: true);
    private SCNNode box(double x, double y, double z, double w, double h, double d, SCNMaterial mat)
    {
        var shape = new SCNBox(width: w, height: h, length: d, chamferRadius: 0.008); shape.materials = new() { mat };
        var node = new SCNNode(shape); node.position = new SCNVector3(x, y, z); scene.rootNode.addChildNode(node); return node;
    }
    public void reset()
    {
        town.reset(); duneSand.reset();
        dustBatch.isHidden = true; clodBatch.isHidden = true;
        for (int i = 0; i < flecks.Count; i++) { var f = flecks[i]; f.life = 0; f.node.isHidden = true; flecks[i] = f; }
        foreach (var trail in trails) trail.reset(); emission = new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0 }, new[] { 0.0, 0.0 } };
        racerEmittedCount = new[] { 0, 0, 0, 0 }; emittedCount = 0; poolIndex = 0;
    }
    public (double x, double z, double width)[][] additionalContacts = Array.Empty<(double x, double z, double width)[]>();
    public void update(Simulation state, Simulation opponent, double dt, double modelScale, IReadOnlyList<Simulation> additional)
    {
        if (!(dt > 0)) return;
        storm = state.storm;
        town.shadowDirections = new List<Double3>(sky.daylight.directions);
        var allStates = new[] { state, opponent }.Concat(additional).ToArray();
        var allContacts = new[] { new[] { (x: 0.262225 * modelScale, z: -0.23 * modelScale, width: 0.155 * modelScale), (x: -0.262225 * modelScale, z: -0.23 * modelScale, width: 0.155 * modelScale) }, R2D2.groundContacts }.Concat(additionalContacts).ToArray();
        duneSand.update(states: allStates, contacts: allContacts.Select((feet, index) =>
        {
            bool tracked = index == 0 || index == 3;
            return feet.Select(c => new SandDeformation.Contact(x: c.x, z: tracked ? 0 : c.z, width: max(0.10, c.width), length: tracked ? max(abs(c.z) * 2, RobotCollisions.profiles[index].halfDepth * 1.8) : 0.12)).ToArray();
        }).ToArray(), dt: dt);
        if (camera != null) { stormVisual.update(storm, camera: camera, dt: dt); }
        for (int i = 0; i < flecks.Count; i++)
        {
            if (!(flecks[i].life > 0)) continue;
            var f = flecks[i];
            f.life -= dt;
            // Fine dust loses its launch momentum quickly; grains fall and settle.
            f.velocity *= exp(-dt * (f.dust ? 1.4 : 0.7));
            f.velocity.y += dt * (f.dust ? 0.045 : -9.8);
            if (storm.enabled)
            {
                var wind = storm.wind(f.node.position.x, f.node.position.z);
                f.velocity += (wind - f.velocity) * min(1, dt * (f.dust ? 1.8 : 0.10));
            }
            f.node.simdPosition += S3(new Float3((float)(f.velocity.x * dt), (float)(f.velocity.y * dt), (float)(f.velocity.z * dt)));
            var ground = (CGFloat)(storm.height(f.node.position.x, f.node.position.z) + duneSand.field.offset(f.node.position.x, f.node.position.z));
            if (f.node.position.y < ground + (CGFloat)f.radius)
            {
                f.node.position.y = ground + (CGFloat)f.radius;
                if (f.dust)
                {
                    // Floor correction must not cancel the initial upward kick.
                    f.velocity.y = max(0, f.velocity.y);
                }
                else
                {
                    f.velocity = Double3.zero; f.life = min(f.life, 0.09);
                }
            }
            double age = f.duration - f.life;
            double fadeIn = min(1, age / (f.dust ? 0.12 : 0.025));
            double fadeOut = min(1, max(0, f.life) / (f.dust ? f.duration * 0.65 : 0.12));
            f.node.opacity = (CGFloat)(f.opacity * fadeIn * fadeOut);
            if (f.dust)
            {
                var size = (CGFloat)(1 + age * 0.9);
                f.node.scale = new SCNVector3(size, size, size);
            }
            f.node.isHidden = f.life <= 0; flecks[i] = f;
        }
        emit(state, racer: 0, dt: dt, contacts: new[] {
            (0.262225 * modelScale, -0.23 * modelScale, 0.155 * modelScale),
            (-0.262225 * modelScale, -0.23 * modelScale, 0.155 * modelScale) });
        emit(opponent, racer: 1, dt: dt, contacts: R2D2.groundContacts);
        for (int i = 0; i < additional.Count; i++)
        {
            emit(additional[i], racer: i + 2, dt: dt, contacts: additionalContacts[i]);
        }
        rebuildDebrisBatches();
    }
    /// Two draw submissions replace up to 1,600 individual particle nodes.
    /// Per-vertex tints keep the source soil color without per-particle materials.
    private void rebuildDebrisBatches()
    {
        var transform = camera?.simdWorldTransform ?? SimdBridge.M(SCNMatrix4Identity);
        var t = SimdBridge.M(transform);
        var right = new Float3((float)t.m11, (float)t.m12, (float)t.m13);
        var up = new Float3((float)t.m21, (float)t.m22, (float)t.m23);
        var normal = Simd.normalize(Simd.cross(right, up));
        foreach (var dust in new[] { false, true })
        {
            var vertices = new List<SCNVector3>(); var normals = new List<SCNVector3>(); var uv = new List<CGPoint>(); var rgba = new List<float>(); var indices = new List<int>();
            foreach (var f in flecks)
            {
                if (!(f.life > 0 && f.dust == dust)) continue;
                var center = F3(f.node.simdPosition); int @base = vertices.Count;
                if (dust)
                {
                    float radius = f.radius * F3(f.node.simdScale).x;
                    var w = storm.wind(center.x, center.z);
                    var wind = new Float3((float)w.x, 0, (float)w.z);
                    var projected = wind - normal * Simd.dot(wind, normal);
                    var along = storm.enabled && Simd.length(projected) > 0.01f ? Simd.normalize(projected) : right;
                    var across = Simd.normalize(Simd.cross(normal, along));
                    float stretch = storm.enabled ? 3.8f : 1.6f;
                    foreach (var (sx, sy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                    {
                        vertices.Add(V3(center + along * (float)sx * radius * stretch + across * (float)sy * radius * 0.65f));
                        normals.Add(V3(normal)); uv.Add(new CGPoint((sx + 1) / 2, (sy + 1) / 2));
                    }
                    indices.AddRange(new[] { @base, @base + 1, @base + 2, @base, @base + 2, @base + 3 });
                }
                else
                {
                    foreach (var v in new[] { new Float3(0, 1, 0), new Float3(-0.87f, -0.5f, -0.5f), new Float3(0.87f, -0.5f, -0.5f), new Float3(0, -0.5f, 1) })
                    {
                        vertices.Add(V3(center + v * f.radius)); normals.Add(V3(Simd.normalize(v))); uv.Add(CGPoint.zero);
                    }
                    indices.AddRange(new[] { @base, @base + 2, @base + 1, @base, @base + 3, @base + 2, @base, @base + 1, @base + 3, @base + 1, @base + 2, @base + 3 });
                }
                for (int k = 0; k < 4; k++) { rgba.AddRange(new[] { f.tint.x, f.tint.y, f.tint.z, (float)f.node.opacity }); }
            }
            var batch = dust ? dustBatch : clodBatch;
            batch.isHidden = indices.Count == 0;
            if (indices.Count == 0) continue;
            var colors = new SCNGeometrySource(SCNGeometrySource.Bytes(rgba), SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
            var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv), colors }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
            geometry.materials = new() { dust ? dustMaterial : clodGeometry.materials[0] };
            batch.geometry = geometry;
        }
    }
    private void emit(Simulation state, int racer, double dt, IReadOnlyList<(double x, double z, double width)> contacts)
    {
        trails[racer].update(state, contacts: contacts);
        var forward = new Double3(sin(state.heading), 0, cos(state.heading));
        var lateral = new Double3(cos(state.heading), 0, -sin(state.heading));
        var origin = new Double3(state.x, state.groundY + 0.035, state.z);
        for (int side = 0; side < contacts.Count; side++)
        {
            var contact = contacts[side];
            double speed = contacts.Count == 1 ? state.speed : side == 0 ? state.leftSpeed : side == 1 ? state.rightSpeed : state.speed;
            double magnitude = abs(speed);
            if (!(magnitude > 0.18 && !state.contacting && state.hasDirtContact)) continue;
            double sign = speed > 0 ? 1.0 : -1.0;
            double contactZ = (racer == 0 || racer == 3) ? -max(abs(contact.z), RobotCollisions.profiles[racer].halfDepth * 0.85) * sign : contact.z;
            bool inDunes = max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge + 8;
            double contactLoad = inDunes ? duneSand.field.contactWeight(state, new SandDeformation.Contact(x: contact.x, z: contactZ, width: contact.width, length: 0.1)) : 1;
            if (!(contactLoad > 0)) continue;
            emission[racer][side] += dt * min(2.5, magnitude) * 22 * contactLoad;
            while (emission[racer][side] >= 1)
            {
                emission[racer][side] -= 1;
                int i = poolIndex; poolIndex = (poolIndex + 1) % poolSize;
                // Tracks shed from their trailing end. Fixed wheel contacts stay
                // in place when reversing (including R2's front wheel).
                var position = origin + lateral * (contact.x + SwiftRandom.doubleClosed(-contact.width * 0.35, contact.width * 0.35)) + forward * contactZ;
                position.y = state.terrainHeight(position.x, position.z) + 0.018;
                // Match the dune shader's feather at the town edge. Track clay
                // transitions to town soil across the same sandy shoulder.
                double edge = max(abs(position.x), abs(position.z));
                double t = max(0, min(1, (edge - 156) / 36));
                double dune = t * t * (3 - 2 * t);
                double cover = min(1, state.storm.depth(position.x, position.z) / 0.025);
                double clay = edge < 80 ? max(0, min(1, (DirtCourse.width + 0.7 - DirtCourse.projection(position.x, position.z).distance) / 0.9)) : 0;
                var town = new Float3(0.62f, 0.55f, 0.45f);
                var track = new Float3(0.64f, 0.43f, 0.31f);
                var sand = new Float3(0.78f, 0.57f, 0.34f);
                var soil = ((town + (track - town) * (float)clay) * (1 - (float)dune) + sand * (float)dune) * (1 - (float)cover) + sand * (float)cover;
                var f = flecks[i];
                double clayWeight = clay * (1 - dune) * (1 - cover);
                // Clay throws cohesive clods; dry dune sand mostly lofts fines.
                f.dust = SwiftRandom.doubleClosed(0, 1) > (0.25 + 0.42 * clayWeight);
                f.tint = soil * SwiftRandom.floatClosed(0.94f, 1.06f);
                // Suspended fines scatter light; grains retain the soil albedo.
                if (f.dust) { f.tint = f.tint * 0.90f + new Float3(0.07f, 0.055f, 0.035f); }
                double kick = min(1.6, magnitude);
                f.velocity = -forward * sign * kick * SwiftRandom.doubleClosed(0.08, 0.22)
                    + lateral * SwiftRandom.doubleClosed(-0.09, 0.09)
                    + new Double3(0, (f.dust ? SwiftRandom.doubleClosed(0.30, 0.55) : SwiftRandom.doubleClosed(0.16, 0.38)) * sqrt(kick), 0);
                if (!f.dust)
                {
                    f.velocity += -forward * sign * kick * (0.35 * clayWeight)
                        + new Double3(0, SwiftRandom.doubleClosed(0.25, 0.65) * sqrt(kick) * clayWeight, 0);
                }
                f.duration = f.dust ? SwiftRandom.doubleClosed(1.2, 1.8) : SwiftRandom.doubleClosed(0.22, 0.38) + clayWeight * 0.5;
                f.life = f.duration;
                f.radius = f.dust ? SwiftRandom.floatClosed(0.12f, 0.20f) : SwiftRandom.floatClosed(0.0015f, 0.0035f) + (float)clayWeight * SwiftRandom.floatClosed(0.006f, 0.012f);
                f.opacity = f.dust ? (state.storm.enabled ? 0.36 : 0.48) : 0.70;
                f.node.position = new SCNVector3(position.x, position.y, position.z);
                f.node.scale = new SCNVector3(1, 1, 1); f.node.opacity = 0; f.node.isHidden = false;
                flecks[i] = f;
                emittedCount += 1; racerEmittedCount[racer] += 1;
            }
        }
    }
}

public sealed partial class DirtWorld
{
    /// Exercise actual emission and pooled geometry on clay, town soil and dunes.
    public bool checkDebris()
    {
        bool passed = checkClodRendering();
        var grainSizes = new List<double>();
        var grainColors = new List<Float3>();
        foreach (var (name, point) in new[] { ("clay", new Double2(0, -15)), ("town", new Double2(90, 40)), ("dunes", new Double2(210, 65)) })
        {
            reset();
            var p = DirtCourse.projection(point.x, point.y);
            var state = new Simulation(dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase);
            if (name == "clay") { state = new Simulation(dirtTrack: true); }
            var input = new DriveInput(); input.throttle = 0.7;
            var physics = new DirtRacePhysics(); var race = new DirtRace();
            race.countDown(dt: 3);
            var opponents = new[] { new DirtOpponent(), new DirtOpponent(slot: DirtCourse.startingGrid[2]), new DirtOpponent(slot: DirtCourse.startingGrid[3]) };
            for (int k = 0; k < 30; k++) { physics.advance(input, ref state, ref race, opponents, dt: 1.0 / 60, raceDT: 1.0 / 60, robotCollisionsEnabled: false); }
            for (int k = 0; k < 90; k++) { emit(state, racer: 0, dt: 1.0 / 60, contacts: new[] { (0.26, -0.23, 0.15), (-0.26, -0.23, 0.15) }); }
            var live = flecks.Where(f => f.life > 0).ToList(); var grains = live.Where(f => !f.dust).ToList();
            double average = grains.Select(f => (double)f.radius).Sum() / (double)max(1, grains.Count);
            grainSizes.Add(average);
            grainColors.Add(grains.Select(f => f.tint).Aggregate(Float3.zero, (s, x) => s + x) / (float)max(1, grains.Count));
            bool grounded = live.All(f => abs(f.node.position.y - DirtCourse.height(f.node.position.x, f.node.position.z) - 0.018) < 0.0001);
            bool finite = live.All(f => float.IsFinite(f.tint.x) && double.IsFinite(f.velocity.y));
            passed = passed && live.Count > 20 && grains.Count > 0 && grounded && finite;
            if (name == "dunes") { passed = passed && grains.All(f => f.radius <= 0.0035f); }
            rebuildDebrisBatches();
            var colors = clodBatch.geometry?.sourcesFor(SCNGeometrySourceSemantic.color).FirstOrDefault();
            passed = passed && colors != null && !(clodBatch.isHidden || dustBatch.isHidden);
            int count = emittedCount;
            Float3? tint = live.Count > 0 ? live[0].tint : null;
            update(state, opponent: state, dt: 0, modelScale: 1, additional: Array.Empty<Simulation>());
            Float3? firstLive = flecks.Any(f => f.life > 0) ? flecks.First(f => f.life > 0).tint : null;
            passed = passed && count == emittedCount && tint == firstLive;
            state.stop();
            for (int k = 0; k < 120; k++) { update(state, opponent: state, dt: 1.0 / 60, modelScale: 1, additional: Array.Empty<Simulation>()); }
            passed = passed && emittedCount == count && flecks.All(f => f.life <= 0) && dustBatch.isHidden && clodBatch.isHidden;
            Godot.GD.Print($"Debris {name}: {live.Count} particles, average grain radius {Swift.description(average * 1000)} mm, contact heights {(grounded ? "true" : "false")}");
        }
        passed = passed && grainSizes[0] > grainSizes[2] * 3
            && grainColors[2].x > grainColors[0].x + 0.08f
            && grainColors[2].y > grainColors[0].y + 0.08f;
        reset();
        passed = passed && emittedCount == 0 && racerEmittedCount.SequenceEqual(new[] { 0, 0, 0, 0 });
        Godot.GD.Print($"Ground-dependent debris: {(passed ? "PASS" : "FAIL")}");
        return passed;
    }

    private bool checkClodRendering()
    {
        SCNScene testScene = new SCNScene(); SCNNode camera = new SCNNode(), light = new SCNNode(), sample = new SCNNode();
        testScene.background.contents = NSColor.black;
        camera.camera = new SCNCamera(); camera.camera.usesOrthographicProjection = true; camera.camera.orthographicScale = 1;
        camera.position = new SCNVector3(0, 0, 2);
        light.light = new SCNLight(); light.light.type = SCNLight.LightType.ambient; light.light.color = NSColor.white; light.light.intensity = 1000;
        foreach (var node in new[] { camera, light, sample }) testScene.rootNode.addChildNode(node);
        var renderer = new SCNRenderer(device: null, options: null); renderer.scene = testScene; renderer.pointOfView = camera;
        var pixels = new List<Double3>();
        foreach (var tint in new[] { new Float3(0.64f, 0.43f, 0.31f), new Float3(0.78f, 0.57f, 0.34f) })
        {
            var rgba = Enumerable.Range(0, 4).SelectMany(_ => new[] { tint.x, tint.y, tint.z, 1f }).ToArray();
            var colors = new SCNGeometrySource(SCNGeometrySource.Bytes(rgba), SCNGeometrySourceSemantic.color, 4, true, 4, 4, 0, 16);
            var mesh = new SCNGeometry(new[] { SCNGeometrySource.vertices(new[] { new SCNVector3(-1, -1, 0), new SCNVector3(1, -1, 0), new SCNVector3(1, 1, 0), new SCNVector3(-1, 1, 0) }), SCNGeometrySource.normals(Enumerable.Repeat(new SCNVector3(0, 0, 1), 4).ToArray()), colors }, new[] { new SCNGeometryElement(new[] { 0, 1, 2, 0, 2, 3 }, SCNGeometryPrimitiveType.triangles) });
            mesh.materials = clodGeometry.materials; sample.geometry = mesh;
            var image = renderer.snapshot(atTime: 0, with: new CGSize(64, 64), antialiasingMode: SCNAntialiasingMode.none);
            var bitmap = image.tiffRepresentation is byte[] tiff ? NSBitmapImageRep.data(tiff) : null;
            var c = bitmap?.colorAt(32, 32)?.usingColorSpace(NSColorSpace.deviceRGB);
            if (c == null) return false;
            pixels.Add(new Double3(c.redComponent, c.greenComponent, c.blueComponent));
        }
        Double3 clay = pixels[0], sand = pixels[1];
        bool passed = clay.x > clay.y * 1.15 && clay.y > clay.z * 1.1 && sand.x > clay.x + 0.03 && sand.y > clay.y + 0.03;
        Godot.GD.Print($"Rendered clod colors: clay {clay}, sand {sand}: {(passed ? "PASS" : "FAIL")}");
        return passed;
    }
}

// Diagnostic pairs retain identical simulation state and vary only dust visibility.
public sealed partial class DirtWorld
{
    public void diagnosticDust(bool visible) { rebuildDebrisBatches(); dustBatch.isHidden = !visible; }
    public int diagnosticDustCount => flecks.Count(f => f.life > 0 && f.dust);
}

public sealed partial class DirtWorld
{
    public void printDustOpacity()
    {
        var bitmap = NSBitmapImageRep.data(dustTexture().tiffRepresentation);
        double maximum = 0.0, total = 0.0;
        for (int y = 0; y < bitmap.pixelsHigh; y++)
            for (int x = 0; x < bitmap.pixelsWide; x++)
            {
                double a = bitmap.colorAt(x, y).alphaComponent; maximum = max(maximum, a); total += a;
            }
        var live = flecks.Where(f => f.life > 0 && f.dust).ToList();
        double opacity = live.Count > 0 ? live.Select(f => f.node.opacity).Max() : 0;
        Godot.GD.Print($"Dust opacity: texture peak {Swift.description(maximum)}, texture mean {Swift.description(total / (double)(bitmap.pixelsHigh * bitmap.pixelsWide))}, live peak {Swift.description(opacity)}, effective peak {Swift.description(maximum * opacity)}");
    }
}
