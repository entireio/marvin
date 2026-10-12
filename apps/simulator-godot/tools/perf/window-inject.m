// window-inject.m: DYLD_INSERT_LIBRARIES helper that gives a benchmark window an exact frame and level, so the macOS
// game (which must not be modified) and the Godot port render the same drawable size on this 1x display.
//
// The macOS town benchmark sets its window to 960 x 540 points (TownSmoke.swift: a 1080p drawable on a Retina screen);
// on a 1x screen that is a 960 x 540 drawable, and AppKit keeps a titled window below the menu bar and inside the
// visible frame, so not even a 1920 x 1080-point window fits. This library:
//   - replaces a request for a 960 x 540-point window (the benchmark's -[NSWindow setContentSize:] in the macOS game;
//     -[NSWindow setFrame:display:] in Godot, whose facade turns setContentSize into DisplayServer.WindowSetSize) by
//     the injected frame, at a backing scale of 1 in both games, so their HUDs have the same point size too;
//   - keeps a window of the injected size at the injected origin (setFrame:display:, setFrameOrigin:,
//     setFrameTopLeftPoint:) and gives it the injected level;
//   - turns off -[NSWindow constrainFrameRect:toScreen:] for windows of the injected size, so a 1920 x 1080 window can
//     cover the whole screen (the menu bar draws over its top 30 points; the drawable is still rendered in full).
//
// Environment:
//   MARVIN_INJECT_FRAME="x,y,w,h"   Cocoa screen points, origin at the bottom left of the main screen (required)
//   MARVIN_INJECT_LEVEL=n            window level for that window (3 = NSFloatingWindowLevel, as Godot's always-on-top)
//   MARVIN_INJECT_BACKDROP=1         also show an opaque black borderless window over the whole screen one level below,
//                                    so other apps' windows are occluded in every run (a Metal app left running, such
//                                    as a MarvinSimulator, stops drawing when covered; Godot's window alone did not
//                                    occlude it while the macOS game's did)
//   MARVIN_INJECT_LOG=1              log every adjustment to stderr
//
// Build: tools/perf/build-window-inject.sh OUT.dylib (clang -dynamiclib -framework AppKit). Use with an ad-hoc signed
// or hardened binary that allows DYLD variables (the Godot editor has com.apple.security.cs.allow-dyld-environment-variables).
#import <AppKit/AppKit.h>
#import <objc/runtime.h>

static NSRect injectedFrame;
static BOOL enabled = NO, logging = NO, backdropWanted = NO;
static NSWindow *backdrop = nil;   // the black backdrop (MARVIN_INJECT_BACKDROP)
static __weak NSWindow *target = nil;  // the benchmark window, once it has the injected frame
static BOOL creatingBackdrop = NO;
static NSInteger injectedLevel = NSIntegerMin;

static IMP originalSetContentSize, originalSetFrameDisplay, originalSetFrameOrigin, originalSetFrameTopLeft, originalConstrain, originalSetLevel;

static BOOL isInjectedSize(NSSize size) {
    return fabs(size.width - injectedFrame.size.width) < 0.5 && fabs(size.height - injectedFrame.size.height) < 0.5;
}

static void applyLevel(NSWindow *window) {
    target = window;
    if (!window.opaque) {
        if (logging) fprintf(stderr, "window-inject: window was not opaque; set opaque\n");
        window.opaque = YES;
    }
    if (injectedLevel != NSIntegerMin && window.level != injectedLevel) {
        window.level = injectedLevel;
        if (logging) fprintf(stderr, "window-inject: level %ld\n", (long)injectedLevel);
    }
    if (backdropWanted && !backdrop) {
        creatingBackdrop = YES;
        NSScreen *screen = NSScreen.screens.firstObject;
        backdrop = [[NSWindow alloc] initWithContentRect:screen.frame styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
        backdrop.backgroundColor = NSColor.blackColor;
        backdrop.opaque = YES;
        backdrop.ignoresMouseEvents = YES;
        backdrop.hidesOnDeactivate = NO;
        backdrop.releasedWhenClosed = NO;
        backdrop.level = (injectedLevel != NSIntegerMin ? injectedLevel : window.level) - 1;
        [backdrop orderFront:nil];
        creatingBackdrop = NO;
        [window orderFront:nil];
        if (logging) fprintf(stderr, "window-inject: backdrop level %ld\n", (long)backdrop.level);
    }
}

static BOOL isBenchmarkRequest(NSSize size) {
    return fabs(size.width - 960) < 0.5 && fabs(size.height - 540) < 0.5;
}

static BOOL hooked(NSWindow *window) {
    return enabled && !creatingBackdrop && window != backdrop && (target == nil || window == target);
}

static void injectedSetFrameDisplay(NSWindow *self, SEL _cmd, NSRect frame, BOOL display) {
    if (hooked(self) && (isInjectedSize(frame.size) || isBenchmarkRequest(frame.size))) {
        if (logging) fprintf(stderr, "window-inject: setFrame %.0f,%.0f %.0fx%.0f -> %.0f,%.0f\n", frame.origin.x, frame.origin.y, frame.size.width, frame.size.height, injectedFrame.origin.x, injectedFrame.origin.y);
        frame = injectedFrame;
        ((void (*)(id, SEL, NSRect, BOOL))originalSetFrameDisplay)(self, _cmd, frame, display);
        applyLevel(self);
        return;
    }
    ((void (*)(id, SEL, NSRect, BOOL))originalSetFrameDisplay)(self, _cmd, frame, display);
}

static void injectedSetContentSize(NSWindow *self, SEL _cmd, NSSize size) {
    if (hooked(self) && isBenchmarkRequest(size)) {
        NSRect content = NSMakeRect(0, 0, injectedFrame.size.width, injectedFrame.size.height);
        NSRect frame = [self frameRectForContentRect:content];
        // A full-size content view has frame == content; otherwise keep the content at the injected size.
        frame.origin = injectedFrame.origin;
        if (logging) fprintf(stderr, "window-inject: setContentSize 960x540 -> frame %.0f,%.0f %.0fx%.0f\n", frame.origin.x, frame.origin.y, frame.size.width, frame.size.height);
        injectedFrame.size = frame.size;
        ((void (*)(id, SEL, NSRect, BOOL))originalSetFrameDisplay)(self, @selector(setFrame:display:), frame, YES);
        applyLevel(self);
        return;
    }
    ((void (*)(id, SEL, NSSize))originalSetContentSize)(self, _cmd, size);
}

static void injectedSetFrameOrigin(NSWindow *self, SEL _cmd, NSPoint origin) {
    if (hooked(self) && self == target) origin = injectedFrame.origin;
    ((void (*)(id, SEL, NSPoint))originalSetFrameOrigin)(self, _cmd, origin);
}

static void injectedSetFrameTopLeft(NSWindow *self, SEL _cmd, NSPoint point) {
    if (hooked(self) && self == target) point = NSMakePoint(injectedFrame.origin.x, injectedFrame.origin.y + injectedFrame.size.height);
    ((void (*)(id, SEL, NSPoint))originalSetFrameTopLeft)(self, _cmd, point);
}

static void injectedSetLevel(NSWindow *self, SEL _cmd, NSInteger level) {
    // Godot resets its window level (e.g. when it updates its window flags); keep the injected window above the backdrop.
    if (hooked(self) && self == target && injectedLevel != NSIntegerMin && level < injectedLevel) {
        if (logging) fprintf(stderr, "window-inject: setLevel %ld -> %ld\n", (long)level, (long)injectedLevel);
        level = injectedLevel;
    }
    ((void (*)(id, SEL, NSInteger))originalSetLevel)(self, _cmd, level);
}

static NSRect injectedConstrain(NSWindow *self, SEL _cmd, NSRect frame, NSScreen *screen) {
    if (hooked(self) && (self == target || isInjectedSize(frame.size))) return frame;
    return ((NSRect (*)(id, SEL, NSRect, NSScreen *))originalConstrain)(self, _cmd, frame, screen);
}

static IMP swizzle(Class cls, SEL sel, IMP replacement) {
    Method m = class_getInstanceMethod(cls, sel);
    if (!m) return NULL;
    return method_setImplementation(m, replacement);
}

__attribute__((constructor)) static void windowInjectInit(void) {
    const char *spec = getenv("MARVIN_INJECT_FRAME");
    if (!spec) return;
    double x, y, w, h;
    if (sscanf(spec, "%lf,%lf,%lf,%lf", &x, &y, &w, &h) != 4 || w <= 0 || h <= 0) {
        fprintf(stderr, "window-inject: MARVIN_INJECT_FRAME must be x,y,w,h\n");
        return;
    }
    injectedFrame = NSMakeRect(x, y, w, h);
    const char *level = getenv("MARVIN_INJECT_LEVEL");
    if (level) injectedLevel = atol(level);
    logging = getenv("MARVIN_INJECT_LOG") != NULL;
    backdropWanted = getenv("MARVIN_INJECT_BACKDROP") != NULL && strcmp(getenv("MARVIN_INJECT_BACKDROP"), "0") != 0;
    Class cls = [NSWindow class];
    originalSetContentSize = swizzle(cls, @selector(setContentSize:), (IMP)injectedSetContentSize);
    originalSetFrameDisplay = swizzle(cls, @selector(setFrame:display:), (IMP)injectedSetFrameDisplay);
    originalSetFrameOrigin = swizzle(cls, @selector(setFrameOrigin:), (IMP)injectedSetFrameOrigin);
    originalSetFrameTopLeft = swizzle(cls, @selector(setFrameTopLeftPoint:), (IMP)injectedSetFrameTopLeft);
    originalConstrain = swizzle(cls, @selector(constrainFrameRect:toScreen:), (IMP)injectedConstrain);
    originalSetLevel = swizzle(cls, @selector(setLevel:), (IMP)injectedSetLevel);
    enabled = originalSetContentSize && originalSetFrameDisplay && originalSetFrameOrigin && originalSetFrameTopLeft && originalConstrain && originalSetLevel;
    if (logging) fprintf(stderr, "window-inject: frame %.0f,%.0f %.0fx%.0f level %ld enabled %d\n", x, y, w, h, (long)injectedLevel, enabled);
}
