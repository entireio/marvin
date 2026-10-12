// main-stalls.m: DYLD_INSERT_LIBRARIES probe that records how long the main thread's run loop goes without turning, in any
// Cocoa app (the macOS game, whose sources must not change, and the Godot port, whose main loop pumps the run loop once
// per frame). A 240 Hz CFRunLoop timer in the common modes fires whenever the main thread gets back to its run loop; a
// gap between two firings is a main-thread stall (or, in Godot, a long frame). Measurement only.
//
// Environment:
//   MARVIN_STALL_LOG=PATH   write one line per gap of at least MARVIN_STALL_MS (default 50) ms:
//                           "<seconds since launch> <gap ms>", and at exit a summary line "# timer firings N, ..."
// Build: clang -O2 -fobjc-arc -dynamiclib -framework Foundation main-stalls.m -o main-stalls.dylib
#import <Foundation/Foundation.h>
#include <mach/mach_time.h>

static FILE *stallLog;
static double threshold = 50, start, last, longest;
static unsigned long firings, stalls;
static mach_timebase_info_data_t timebase;

static double nowSeconds(void) {
    return (double)mach_absolute_time() * timebase.numer / timebase.denom / 1e9;
}

static void fired(CFRunLoopTimerRef timer, void *info) {
    double t = nowSeconds();
    double gap = (t - last) * 1000;
    if (gap >= threshold) {
        stalls++;
        fprintf(stallLog, "%.3f %.1f\n", last - start, gap);
        fflush(stallLog);
    }
    if (gap > longest) longest = gap;
    last = t;
    firings++;
}

static void finish(void) {
    if (!stallLog) return;
    fprintf(stallLog, "# timer firings %lu, stalls >= %.0f ms: %lu, longest %.1f ms, run %.1f s\n", firings, threshold, stalls, longest, nowSeconds() - start);
    fclose(stallLog);
    stallLog = NULL;
}

__attribute__((constructor)) static void mainStallsInit(void) {
    const char *path = getenv("MARVIN_STALL_LOG");
    if (!path) return;
    stallLog = fopen(path, "w");
    if (!stallLog) return;
    const char *ms = getenv("MARVIN_STALL_MS");
    if (ms) threshold = atof(ms);
    mach_timebase_info(&timebase);
    start = last = nowSeconds();
    atexit(finish);
    dispatch_async(dispatch_get_main_queue(), ^{
        last = nowSeconds();
        CFRunLoopTimerRef timer = CFRunLoopTimerCreate(NULL, CFAbsoluteTimeGetCurrent(), 1.0 / 240, 0, 0, fired, NULL);
        CFRunLoopAddTimer(CFRunLoopGetMain(), timer, kCFRunLoopCommonModes);
    });
}
