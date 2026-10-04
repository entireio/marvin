// SceneKit facade: names that Swift code uses unqualified.
// Every file of the Godot assembly sees the facade types (SCNNode, NSColor, ...)
// and the SceneKit free functions (SCNMatrix4MakeTranslation, SCNVector3Zero, ...).
global using Marvin.SceneKit;
global using static Marvin.SceneKit.SCNGlobals;
// AppKit aliases of the CoreGraphics geometry types (identical on macOS).
global using NSPoint = Marvin.SceneKit.CGPoint;
global using NSSize = Marvin.SceneKit.CGSize;
global using NSRect = Marvin.SceneKit.CGRect;
// SCNQuaternion is a typealias of SCNVector4 in SceneKit.
global using SCNQuaternion = Marvin.SceneKit.SCNVector4;
// CGFloat is Double on 64-bit macOS. `(CGFloat)x` compiles to `(double)x`.
global using CGFloat = System.Double;
