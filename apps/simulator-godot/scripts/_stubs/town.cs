// STUBS (stream "town"): minimal stand-ins for types owned by other streams or later stages, with
// only the members that TownWorld.cs / TownGround.cs / TownShadowBatch.cs / TownSigns.cs /
// TownAccessMap.cs call. The orchestrator deletes this file when merging; the real ports replace
// every type here.
using System;
using System.Collections.Generic;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// TownResidents.swift (next stage of this stream)
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
    public int connections => 0;
    public TownResidents(List<TownDoorway> doors, CityCollisionWorld city, TownCrowd crowd, SCNNode root, int count) { }
    public void setStorm(bool value) { }
    public void reset() { }
    public void update(double dt, List<RobotCollisions.Body> robots, List<RobotCollisions.Body> pedestrians = null, Func<SCNNode, bool> visible = null) { }
}

// TownStreetResidents.swift (next stage of this stream)
public sealed class TownStreetResidents
{
    public sealed class Walker { }
    public List<Walker> walkers { get; private set; } = new();
    public List<RobotCollisions.Body> bodies => new();
    public int visible => 0;
    public TownStreetResidents(List<List<Double2>> paths, CityCollisionWorld city, TownCrowd crowd, SCNNode root) { }
    public void setStorm(bool value) { }
    public void reset() { }
    public void update(double dt, List<RobotCollisions.Body> obstacles, Func<SCNNode, bool> visible = null) { }
}

// TownCrowd.swift (next stage of this stream): CityMaterials and TownCrowd.
public static class CityMaterials
{
    public static string asset(string name) => "res://assets/City/" + name;
    public static readonly SCNMaterial plaster = new SCNMaterial(), adobe = new SCNMaterial(), metal = new SCNMaterial();
    // Double-sided like the real CityMaterials.cloth (TownShadowBatch groups by it; canopies are seen from below).
    public static readonly SCNMaterial cloth = new SCNMaterial { isDoubleSided = true };
}
public sealed class TownCrowd
{
    public enum Activity { ordinary = 0, conversation = -1, waiting = -2, trading = -3 }
    public int stormPopulation { get; private set; } = 0;
    public int triangles { get; private set; } = 0;
    public int farTriangles { get; private set; } = 0;
    public int cellCount { get; private set; } = 0;
    public bool valid => false;
    // Verbatim copy of the Swift one-liner (it decides spectator sound zones and storm absences).
    public static bool staysOutside(double x, double z, int index) => abs((long)index * 17 + (long)(x * 13) + (long)(z * 7)) % 31 == 0;
    public void update(double time) { }
    public void setStorm(bool active) { }
    public SCNNode add(double x, double y, double z, double yaw, int index, bool seated, bool animated, bool shelter = false, Activity activity = Activity.ordinary) => null;
    public void finish(SCNNode into) { }
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
    public static List<ShadowFrustum> cameras(SCNNode node, double aspect) => new();
    public bool intersects(ShadowBounds bounds) => true;
}

// RaceAudio.swift
public struct SpectatorSoundZone
{
    public Double2 position;
    public int people;
    public int stormPeople;
    public SpectatorSoundZone(Double2 position, int people, int stormPeople) { this.position = position; this.people = people; this.stormPeople = stormPeople; }
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
