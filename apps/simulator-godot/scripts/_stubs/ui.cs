// STUBS for the ui stream (deleted by the orchestrator when merging): only the members the UI files call.
// Owners: Robot, R2D2, ImportedRacer -> robots stream; TownWorld, DirtWorld -> world/town streams;
// AppController -> app stream (must be `partial`: LevelLoading.swift extends it).
// The robot roots carry simple placeholder primitives so menu portrait captures show framing and shadows.
using System;
using System.Collections.Generic;
using Marvin.Core;

namespace Marvin;

public sealed class Robot
{
    public const uint silverColor = 0xc3c7c9;
    public readonly SCNNode root = new(), yawNode = new(), pitchNode = new();
    public List<SCNNode> eyes = new();
    public Robot(string resources)
    {
        root.addChildNode(yawNode); yawNode.addChildNode(pitchNode);
        var body = new SCNBox(0.62, 0.30, 0.48, 0.06);
        body.firstMaterial.diffuse.contents = NSColor.srgbRed(0.76, 0.78, 0.79, 1);
        root.addChildNode(new SCNNode(body) { position = new SCNVector3(0, 0.15, 0) });
        yawNode.position = new SCNVector3(0, 0.33, 0);
        var head = new SCNBox(0.56, 0.32, 0.36, 0.08);
        head.firstMaterial.diffuse.contents = NSColor.srgbRed(0.76, 0.78, 0.79, 1);
        pitchNode.addChildNode(new SCNNode(head) { position = new SCNVector3(0, 0.16, 0) });
        foreach (var x in new[] { -0.12, 0.12 })
        {
            var eye = new SCNNode(new SCNBox(0.08, 0.05, 0.01, 0)) { position = new SCNVector3(x, 0.18, 0.185) };
            eye.geometry.firstMaterial.diffuse.contents = NSColor.white; eye.geometry.firstMaterial.lightingModel = SCNMaterial.LightingModel.constant;
            pitchNode.addChildNode(eye); eyes.Add(eye);
        }
    }
}

public sealed class R2D2
{
    public readonly SCNNode root = new();
    public R2D2(string resources)
    {
        var body = new SCNCylinder(0.24, 0.62);
        body.firstMaterial.diffuse.contents = NSColor.white;
        root.addChildNode(new SCNNode(body) { position = new SCNVector3(0, 0.31, 0) });
        var dome = new SCNSphere(0.24);
        dome.firstMaterial.diffuse.contents = NSColor.srgbRed(0.85, 0.86, 0.88, 1);
        root.addChildNode(new SCNNode(dome) { position = new SCNVector3(0, 0.62, 0) });
    }
    public void update(Simulation state) { }
    public void applyExpression(RacePerformance.Pose pose) { }
}

public sealed class ImportedRacer
{
    public enum Kind { bb8, wallE }
    public readonly SCNNode root = new();
    public ImportedRacer(Kind kind, string resources)
    {
        SCNGeometry g = kind == Kind.bb8 ? new SCNSphere(0.3) : new SCNBox(0.5, 0.5, 0.45, 0.03);
        g.firstMaterial.diffuse.contents = kind == Kind.bb8 ? NSColor.srgbRed(1, 0.57, 0.29, 1) : NSColor.srgbRed(0.95, 0.79, 0.30, 1);
        root.addChildNode(new SCNNode(g) { position = new SCNVector3(0, kind == Kind.bb8 ? 0.3 : 0.4, 0) });
    }
    public void update(Simulation state) { }
    public void applyExpression(RacePerformance.Pose pose, double heading) { }
}

public sealed class TownWorld
{
    public List<Double2[]> mapBuildings = new();
    public List<Double2[]> mapStreets = new();
}

public sealed class DirtWorld
{
    public DirtWorld(Action<double, string> progress = null) { }
}

public partial class AppController
{
    public bool isLoadingDirt = false;
    public int loadingHeartbeats = 0;
    public readonly SimulatorView view = new();
    public readonly LevelLoadingView loadingView = new();
    public NSWindow window;
    public readonly MainMenuView mainMenu = new(NSRect.zero);
    public DirtWorld cachedDirtWorld;
    public bool inSandbox = false;
    public readonly string smokeDirectory = null;
    public void installContentOverlay(NSView overlay) { view.addSubview(overlay); }
    public void startDirtTrack() { }
    public void showMainMenu(object sender) { }
}
