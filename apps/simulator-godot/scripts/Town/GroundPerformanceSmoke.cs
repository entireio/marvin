using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of GroundPerformanceSmoke.swift (an AppController extension): `--ground-performance-test`, `--mesh-reuse-test`
// and `--shadow-culling-test` all run checkGroundPerformance (App.swift), which reads the flags itself. The
// comparisons render the same frozen scene twice per view through an SCNRenderer at 1920 x 1080 and compare the
// packed 8-bit pixels; their lighting is deterministic (midday 0.5, low sun 0.12, storm 0.5, phase 1.2).
public partial class AppController
{
    /// Compare the same frozen native scene with the old transparent ground and
    /// the covered-base partition. Keep HDR, SSAO, shadows and MSAA enabled.
    public bool checkGroundPerformance(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            var tangentFixturesPassed = CommandLine.arguments.Contains("--benchmark-exact-tangents") || checkTangentReuseFixtures();
            weatherOverride = false;
            TownShadowBatch shadowBatch = null;
            var originalFOV = 0.0; var fieldOfViewSaved = false;
            try
            {
                startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
                if (!(dirtWorld.scene.rootNode.childNode(withName: "Town base terrain", recursively: true) is SCNNode terrain && terrain.geometry?.firstMaterial is SCNMaterial earth)) { return false; }
                var reference = new SCNPlane(256, 256); reference.materials = new() { earth };
                // Validate the primitive's actual generated UV mapping rather than
                // assuming SceneKit uses a particular vertical texture convention.
                if (!(reference.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault() is SCNGeometrySource positions && reference.sourcesFor(SCNGeometrySourceSemantic.texcoord).FirstOrDefault() is SCNGeometrySource coordinates)) { return false; }
                static double component(SCNGeometrySource source, int vertex, int axis)
                {
                    var offset = source.dataOffset + vertex * source.dataStride + axis * source.bytesPerComponent;
                    if (source.bytesPerComponent == 4) { return (double)BitConverter.ToSingle(source.data, offset); }
                    return BitConverter.ToDouble(source.data, offset);
                }
                var xs = Enumerable.Range(0, positions.vectorCount).Select(i => component(positions, i, 0)).ToArray();
                var ys = Enumerable.Range(0, positions.vectorCount).Select(i => component(positions, i, 1)).ToArray();
                // SCNPlane exposes a unit primitive source; width/height are applied
                // by SceneKit separately. Compare its normalized affine mapping.
                // PORT: the facade's SCNPlane source holds the real 256 m tessellation; the normalized mapping is the same.
                double xMin = minElement(xs).Value, xMax = maxElement(xs).Value, yMin = minElement(ys).Value, yMax = maxElement(ys).Value;
                var uvMatches = Enumerable.Range(0, positions.vectorCount).All(i =>
                    abs(component(coordinates, i, 0) - (xs[i] - xMin) / (xMax - xMin)) < 1e-6
                        && abs(component(coordinates, i, 1) - (yMax - ys[i]) / (yMax - yMin)) < 1e-6);
                if (!uvMatches) { print("SCNPlane UV mapping differs from covered terrain"); return false; }
                var optimized = TownGround.coveredTerrain(material: earth);
                var renderer = new SCNRenderer(view.device, null);
                renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                var rows = new List<Dictionary<string, object>>();
                var meshComparison = CommandLine.arguments.Contains("--mesh-reuse-test");
                var pairs = TownMesh.validationPairs;
                var meshDataPassed = !meshComparison || (pairs.Count != 0 && pairs.All(pair => MeshReuseValidation.validateMeshReuse(pair.Item1, pair.Item2)));
                // Dictionary(uniqueKeysWithValues:) keyed by ObjectIdentifier(optimized geometry).
                var originals = new Dictionary<SCNGeometry, SCNGeometry>(ReferenceEqualityComparer.Instance);
                foreach (var (old, @new) in pairs) { originals.Add(@new, old); }
                var meshNodes = new List<(SCNNode, SCNGeometry, SCNGeometry)>();
                if (meshComparison)
                {
                    foreach (var (old, @new) in pairs)
                    {
                        old.levelsOfDetail = @new.levelsOfDetail?.Select(level =>
                        {
                            var g = level.geometry != null && originals.TryGetValue(level.geometry, out var original) ? original : level.geometry;
                            return level.screenSpaceRadius > 0 ? SCNLevelOfDetail.withScreenSpaceRadius(g, level.screenSpaceRadius) : new SCNLevelOfDetail(g, worldSpaceDistance: level.worldSpaceDistance);
                        }).ToArray();
                    }
                    dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
                    {
                        if (node.geometry is SCNGeometry g && originals.TryGetValue(g, out var old)) { meshNodes.Add((node, old, g)); }
                    });
                }
                var shadowComparison = CommandLine.arguments.Contains("--shadow-culling-test");
                var liveShadowBatch = CommandLine.arguments.Contains("--benchmark-shadow-batch-live");
                var shadowBatchComparison = CommandLine.arguments.Contains("--benchmark-shadow-batch") || liveShadowBatch;
                var shadowBatchSyncPassed = true;
                shadowBatch = shadowBatchComparison && !liveShadowBatch ? new TownShadowBatch(root: dirtWorld.scene.rootNode, camera: world.camera.camera) : null;
                var frustumPassed = true;
                var views = new (string, SCNVector3, SCNVector3)[] {
                    ("street", new SCNVector3(35, 1.3, 14), new SCNVector3(40, 0.1, 22)),
                    ("inner-boundary", new SCNVector3(29, 1.1, -13), new SCNVector3(39, -0.02, -16)),
                    ("outer-boundary", new SCNVector3(92, 1.2, 8), new SCNVector3(101, 0, 10)),
                    ("aerial", new SCNVector3(42, 34, 15), new SCNVector3(42, 0, 15)),
                    ("uncovered-infield", new SCNVector3(0, 6, -4), new SCNVector3(-4, 0, -8)),
                    ("inner-fade", new SCNVector3(27, 0.65, 0), new SCNVector3(36, -0.02, 3)),
                    ("grazing", new SCNVector3(40, 0.64, 18), new SCNVector3(90, 0, 20)),
                    ("reverse-turn", new SCNVector3(35, 1.3, 14), new SCNVector3(29, 0.1, 3)),
                    ("lod-boundary", new SCNVector3(30, 1.3, 20), new SCNVector3(110, 0.8, 10)),
                    ("closeup-street", new SCNVector3(35, 1.3, 14), new SCNVector3(40, 0.8, 22)),
                    ("closeup-reverse", new SCNVector3(35, 1.3, 14), new SCNVector3(29, 0.8, 3)) };
                originalFOV = world.camera.camera.fieldOfView; fieldOfViewSaved = true;
                foreach (var (light, fraction, stormEnabled) in new[] { ("midday", 0.5, false), ("low-sun", 0.12, false), ("storm", 0.5, true) })
                {
                    var storm = new Sandstorm(enabled: stormEnabled); storm.advance(90);
                    dirtWorld.configureStorm(storm);
                    dirtWorld.sky.apply(new BinaryDaylight(fraction: fraction, phase: 1.2));
                    foreach (var (name, eye, target) in views)
                    {
                        world.camera.camera.fieldOfView = name.StartsWith("closeup-") ? 20 : originalFOV;
                        world.camera.position = eye; world.camera.look(at: target, up: eye.x == target.x && eye.z == target.z ? new SCNVector3(0, 0, -1) : new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        dirtWorld.sky.updateShadowCenter(new Double3((double)target.x, 0, (double)target.z));
                        var images = new List<NSBitmapImageRep>(); var casters = new List<int>();
                        var optimizedFirst = shadowComparison && name == "reverse-turn";
                        var variants = optimizedFirst ? new[] { ("optimized", true), ("reference", false) } : new[] { ("reference", false), ("optimized", true) };
                        foreach (var (label, useOptimized) in variants)
                        {
                            shadowBatch?.setEnabled(false);
                            if (liveShadowBatch) { dirtWorld.town.setShadowBatchEnabled(false); }
                            terrain.geometry = shadowComparison || shadowBatchComparison || meshComparison || useOptimized ? optimized : reference;
                            foreach (var (node, old, @new) in meshNodes) { node.geometry = useOptimized ? @new : old; }
                            dirtWorld.town.shadowCullingEnabled = shadowComparison && useOptimized;
                            dirtWorld.town.shadowDirections = dirtWorld.sky.daylight.directions.ToList();
                            dirtWorld.town.update(dt: 0, camera: eye, player: new Double2((double)target.x, (double)target.z), shadowCamera: world.camera, viewportAspect: 1920.0 / 1080);
                            shadowBatch?.setEnabled(useOptimized);
                            if (liveShadowBatch)
                            {
                                dirtWorld.town.setShadowBatchEnabled(useOptimized);
                                // Change the caster footprint while already enabled,
                                // then return. A frozen-only toggle would leave stale
                                // proxies or re-enable duplicate original casters.
                                foreach (var position in new[] { new Double2(-140, -140), new Double2((double)target.x, (double)target.z) })
                                {
                                    dirtWorld.town.update(dt: 0, camera: new SCNVector3(position.x, eye.y, position.y), player: position, shadowCamera: world.camera, viewportAspect: 1920.0 / 1080);
                                    var state = dirtWorld.town.shadowBatchDiagnostics; var expected = dirtWorld.town.shadowCasterCount;
                                    int? value(string key) => state.TryGetValue(key, out var v) ? v : null;
                                    shadowBatchSyncPassed = shadowBatchSyncPassed && value("enabled") == (useOptimized ? 1 : 0)
                                        && value("activeProxies") == (useOptimized ? expected : 0)
                                        && value("originalCasters") == (useOptimized ? 0 : expected);
                                }
                                dirtWorld.town.update(dt: 0, camera: eye, player: new Double2((double)target.x, (double)target.z), shadowCamera: world.camera, viewportAspect: 1920.0 / 1080);
                            }
                            casters.Add(dirtWorld.town.shadowCasterCount);
                            _ = renderer.prepare(dirtWorld.scene, shouldAbortBlock: null);
                            var image = renderer.snapshot(0, new CGSize(1920, 1080), view.antialiasingMode);
                            var bitmap = NSBitmapImageRep.data(image.tiffRepresentation); images.Add(bitmap);
                            File.WriteAllBytes(Path.Combine(directory, $"{light}-{name}-{label}.png"), bitmap.representation(NSBitmapImageFileType.png));
                            if (light == "low-sun" && name == "uncovered-infield")
                            {
                                // Same-state controls distinguish snapshot instability
                                // from differences introduced by the optimization.
                                var repeatImage = renderer.snapshot(0, new CGSize(1920, 1080), view.antialiasingMode);
                                var repeatBitmap = NSBitmapImageRep.data(repeatImage.tiffRepresentation);
                                File.WriteAllBytes(Path.Combine(directory, $"{light}-{name}-{label}-repeat.png"), repeatBitmap.representation(NSBitmapImageFileType.png));
                            }
                        }
                        if (optimizedFirst) { images.Reverse(); casters.Reverse(); }
                        if (shadowComparison)
                        {
                            var frusta = ShadowFrustum.cameras(world.camera, aspect: 1920.0 / 1080);
                            foreach (var (depth, expected) in new[] { (-8.0, true), (8.0, false) })
                            {
                                var p = world.camera.convertPosition(new SCNVector3(0, 0, depth), to: null);
                                var proxy = new SCNNode(new SCNBox(0.5, 0.5, 0.5, 0)); proxy.position = p;
                                var center = new Double3((double)p.x, (double)p.y, (double)p.z);
                                var bounds = new ShadowBounds(low: center - new Double3(0.25), high: center + new Double3(0.25));
                                frustumPassed = frustumPassed && renderer.isNode(proxy, insideFrustumOf: world.camera) == expected;
                                if (expected) { frustumPassed = frustumPassed && frusta.Any(f => f.intersects(bounds)); }
                            }
                            // A native-visible box must never be rejected by the
                            // conservative mathematical side-plane classifier.
                            foreach (var x in stride(-90.0, 90.0, 30))
                            {
                                foreach (var z in stride(-90.0, 90.0, 30))
                                {
                                    var proxy = new SCNNode(new SCNBox(4, 10, 4, 0));
                                    proxy.position = new SCNVector3(x, 5, z);
                                    var bounds = new ShadowBounds(low: new Double3(x - 2, 0, z - 2), high: new Double3(x + 2, 10, z + 2));
                                    if (renderer.isNode(proxy, insideFrustumOf: world.camera) && frusta.Count != 0)
                                    {
                                        frustumPassed = frustumPassed && frusta.Any(f => f.intersects(bounds));
                                    }
                                }
                            }
                        }
                        int changed = 0, total = 0; double error = 0.0, maximumError = 0.0;
                        NSBitmapImageRep a = images[0], b = images[1];
                        var pixelEncoding = "device RGB components";
                        // Compare every stored channel directly when both native
                        // images have the same packed 8-bit representation. Avoid
                        // millions of AppKit color conversions. Retain the general
                        // conversion path for other bitmap formats/color spaces.
                        if (!a.isPlanar && !b.isPlanar && a.bitsPerSample == 8 && b.bitsPerSample == 8 &&
                            a.samplesPerPixel == b.samplesPerPixel && new[] { 3, 4 }.Contains(a.samplesPerPixel) &&
                            a.bitsPerPixel == a.samplesPerPixel * 8 && b.bitsPerPixel == b.samplesPerPixel * 8 &&
                            a.bitmapFormat == b.bitmapFormat && a.colorSpace == b.colorSpace &&
                            a.bitmapData is byte[] aa && b.bitmapData is byte[] bb)
                        {
                            pixelEncoding = "packed 8-bit bitmap channels";
                            var channels = a.samplesPerPixel;
                            for (var y = 0; y < 1080; y++)
                            {
                                for (var x = 0; x < 1920; x++)
                                {
                                    int ai = y * a.bytesPerRow + x * channels, bi = y * b.bytesPerRow + x * channels;
                                    var difference = 0;
                                    for (var c = 0; c < channels; c++) { difference = Math.Max(difference, Math.Abs((int)aa[ai + c] - (int)bb[bi + c])); }
                                    var delta = (double)difference / 255;
                                    if (difference > 5) { changed += 1; }
                                    error += delta; maximumError = max(maximumError, delta); total += 1;
                                }
                            }
                        }
                        else
                        {
                            for (var y = 0; y < 1080; y++)
                            {
                                for (var x = 0; x < 1920; x++)
                                {
                                    var ca = a.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                    var cb = b.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                    var delta = max(abs(ca.redComponent - cb.redComponent), max(abs(ca.greenComponent - cb.greenComponent), abs(ca.blueComponent - cb.blueComponent)));
                                    if (delta > 5.0 / 255) { changed += 1; }
                                    error += delta; maximumError = max(maximumError, delta); total += 1;
                                }
                            }
                        }
                        rows.Add(new Dictionary<string, object>
                        {
                            ["view"] = $"{light}-{name}", ["changedFraction"] = (double)changed / (double)total, ["meanMaxChannelError"] = error / (double)total, ["maximumChannelError"] = maximumError,
                            ["comparedPixels"] = total, ["pixelEncoding"] = pixelEncoding, ["referenceCasters"] = casters[0], ["optimizedCasters"] = casters[1],
                        });
                    }
                }
                var reduced = rows.Any(row => (int)row["optimizedCasters"] < (int)row["referenceCasters"]);
                var passed = shadowBatchSyncPassed && tangentFixturesPassed && TownMesh.maximumMergedBasisRadians < 0.005 * Math.PI / 180 && meshDataPassed
                    && (!meshComparison || (meshNodes.Count != 0 && TownMesh.outputVertices < TownMesh.inputVertices)) && frustumPassed && (!shadowComparison || reduced)
                    && rows.All(row => (double)row["changedFraction"] < (shadowComparison || shadowBatchComparison || meshComparison ? 0.00001 : 0.001));
                var report = new Dictionary<string, object>
                {
                    ["passed"] = passed, ["tangentFixturesPassed"] = tangentFixturesPassed, ["meshDataPassed"] = meshDataPassed, ["meshInputVertices"] = TownMesh.inputVertices, ["meshOutputVertices"] = TownMesh.outputVertices,
                    ["maximumMergedBasisDegrees"] = TownMesh.maximumMergedBasisRadians * 180 / Math.PI, ["mergedBasisComparisons"] = TownMesh.mergedBasisComparisons, ["comparisons"] = rows, ["uvMappingPassed"] = uvMatches,
                    ["frustumPassed"] = frustumPassed, ["shadowComparison"] = shadowComparison, ["shadowBatch"] = shadowBatch?.statistics ?? dirtWorld.town.shadowBatchDiagnostics, ["shadowBatchSyncPassed"] = shadowBatchSyncPassed,
                    ["casterReductionObserved"] = reduced, ["note"] = "Original receiver depth, transparent surface and material retained; small rasterization differences require manual review.",
                };
                File.WriteAllText(Path.Combine(directory, "comparison.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                print(JSONSerialization.prettyPrintedSortedKeys(report)); return passed;
            }
            finally
            {
                if (fieldOfViewSaved) { world.camera.camera.fieldOfView = originalFOV; }
                shadowBatch?.setEnabled(false);
                weatherOverride = null;
            }
        }
        catch (Exception error) { print(error.ToString()); return false; }
    }

    private bool checkTangentReuseFixtures()
    {
        static int count((Float3, Float3)[] frames)
        {
            var mesh = new TownMesh(); var n = new Float3(0, 0, 1);
            foreach (var (t, b) in frames) { mesh.triangle(Float3.zero, t, b, 0xffffff, smooth: new[] { n, n, n }); }
            mesh.uv = frames.SelectMany(_ => new[] { CGPoint.zero, new CGPoint(1, 0), new CGPoint(0, 1) }).ToList();
            return mesh.geometry(material: new SCNMaterial()).sourcesFor(SCNGeometrySourceSemantic.vertex).First().vectorCount;
        }
        var epsilon = 0.000002f;
        var unstable = count(new[] { (new Float3(epsilon, epsilon, 1), new Float3(-1, 0, 0)), (new Float3(-epsilon, epsilon, 1), new Float3(-1, 0, 0)) }) == 6;
        var mirrored = count(new[] { (new Float3(1, 0, 0), new Float3(0, 1, 0)), (new Float3(1, 0, 0), new Float3(0, -1, 0)) }) == 6;
        var stable = count(new[] { (new Float3(1, 0, 0), new Float3(0, 1, 0)), (new Float3(2, 0, 0), new Float3(0, 2, 0)) }) == 5;
        var y = 0.250001f;
        var isolatedFallback = count(new[] { (new Float3(1, 0, 0), new Float3(MathF.Sqrt(1 - y * y), y, 0)), (new Float3(1, 0, 0), new Float3(0.96824646f, 0.25f, 0)) }) == 6;
        print($"Tangent reuse fixtures: unstable={(unstable ? "true" : "false")}, mirrored={(mirrored ? "true" : "false")}, stable={(stable ? "true" : "false")}, fallback={(isolatedFallback ? "true" : "false")}");
        return unstable && mirrored && stable && isolatedFallback;
    }

    [GameMode("--ground-performance-test")]
    [GameMode("--mesh-reuse-test")]
    [GameMode("--shadow-culling-test")]
    public static async System.Threading.Tasks.Task RunGroundPerformanceTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkGroundPerformance(at: dir);
        exit(passed ? 0 : 1);
    }
}
