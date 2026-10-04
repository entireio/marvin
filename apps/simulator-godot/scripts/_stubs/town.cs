// STUBS (stream "town"): minimal stand-ins for types owned by other streams, with only the members
// that TownWorld.cs / TownSigns.cs / TownAccessMap.cs call. The orchestrator deletes this file when
// merging; the real ports replace every type here.
using System;
using System.Collections.Generic;
using Marvin.Core;

namespace Marvin;

// TownResidents.swift
public sealed class TownDoorway
{
    public readonly Double2 center; public readonly double yaw;
    public TownDoorway(Double2 center, double yaw, SCNNode root, int variant = 0) { this.center = center; this.yaw = yaw; }
}
public sealed class TownResidents
{
    public sealed class Walker { }
    public List<Walker> walkers { get; private set; } = new();
    public List<RobotCollisions.Body> bodies => new();
    public int visible => 0;
    public TownResidents(List<TownDoorway> doors, CityCollisionWorld city, TownCrowd crowd, SCNNode root, int count) { }
    public void setStorm(bool value) { }
}

// TownStreetResidents.swift
public sealed class TownStreetResidents
{
    public sealed class Walker { }
    public List<Walker> walkers { get; private set; } = new();
    public List<RobotCollisions.Body> bodies => new();
    public int visible => 0;
    public TownStreetResidents(List<List<Double2>> paths, CityCollisionWorld city, TownCrowd crowd, SCNNode root) { }
    public void setStorm(bool value) { }
}

// TownCrowd.swift
public static class CityMaterials
{
    public static readonly SCNMaterial plaster = new SCNMaterial();
}
public sealed class TownCrowd
{
    public int stormPopulation { get; private set; } = 0;
    public void setStorm(bool active) { }
    public void finish(SCNNode into) { }
}

// TownShadowBatch.swift
public sealed class TownShadowBatch
{
    public sealed class Failure : Exception
    {
        private Failure(string message) : base(message) { }
        public static Failure unsupportedGeometry => new("unsupportedGeometry");
    }
    public TownShadowBatch(SCNNode root, SCNCamera camera) { }
    public void setEnabled(bool value) { }
    public bool setCaster(SCNNode original, bool enabled) => false;
    public Dictionary<string, int> telemetryStatistics => new();
    public Dictionary<string, int> statistics => new();
}

// ShadowVolume.swift
public readonly struct ShadowBounds
{
    public readonly Double3 low, high;
    public readonly List<Double3> corners;
    public ShadowBounds(Double3 low, Double3 high)
    {
        this.low = low; this.high = high;
        var result = new List<Double3>();
        foreach (var x in new[] { low.x, high.x }) foreach (var y in new[] { low.y, high.y }) foreach (var z in new[] { low.z, high.z }) result.Add(new Double3(x, y, z));
        corners = result;
    }
}
public readonly struct ShadowFrustum
{
    public bool intersects(ShadowBounds bounds) => true;
}

// RaceAudio.swift
public struct SpectatorSoundZone
{
    public Double2 position;
    public int people;
    public int stormPeople;
}
public struct TownSoundZone
{
    public enum Kind { market, workshop, cantina }
    public readonly Double2 position; public readonly Kind kind;
    public double activity;
    public bool infieldRepair;
    public TownSoundZone(Double2 position, Kind kind, double activity = 1, bool infieldRepair = false)
    {
        this.position = position; this.kind = kind; this.activity = activity; this.infieldRepair = infieldRepair;
    }
}

// TownGround.swift
public static class TownGround
{
    // Godot translation of TownGround.pigmentFunctions (same text as FacadeTest.PigmentFunctions).
    public const string pigmentFunctions = @"
float townNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    vec4 h = fract(sin(vec4(dot(i, vec2(127.1, 311.7)), dot(i + vec2(1, 0), vec2(127.1, 311.7)), dot(i + vec2(0, 1), vec2(127.1, 311.7)), dot(i + 1.0, vec2(127.1, 311.7)))) * 43758.5453);
    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}
vec3 townPigment(vec2 p) {
    vec2 warp = vec2(townNoise(p / 31.0), townNoise(p / 37.0 + 19.0)) * 9.0;
    float broad = townNoise((p + warp) / 22.0), fine = townNoise((p + warp) / 5.5);
    float pale = smoothstep(0.28, 0.72, broad * 0.50 + fine * 0.50);
    vec3 soil = mix(vec3(0.255, 0.208, 0.145), vec3(0.46, 0.36, 0.235), pale);
    return soil * (0.89 + 0.22 * townNoise(p / 2.1 + 7.0));
}
";
    public static SCNNode build(List<TownWorld.PedestrianAccess> access, List<Double2> yards) => new SCNNode();
}
