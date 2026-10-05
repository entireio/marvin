// metal-labels.m: DYLD_INSERT_LIBRARIES probe that labels every Metal command encoder of a process with what it draws
// into or dispatches, so a Metal System Trace (xctrace, which records encoder labels) attributes GPU time to render
// passes. Godot's Metal driver sets no encoder labels and no debug groups, and its RenderingDevice timestamp queries
// return zeros on Metal, so neither Godot's visual profiler nor the trace's default "Render Command N" names say which
// pass is which. Measurement only: the probe changes labels, never commands.
//
// Labels (set at endEncoding, when the encoder's work is known):
//   render:  "R#k <color0> [+N] | D <depth> <load|clear> | <draws> draws"  e.g. "R#3 RGBA16F 1920x1080x4 +1 | D D32F 1920x1080x4 clear | 812 draws"
//   compute: "C#k <pipeline labels or #index> x<dispatches> <last grid>"  pipeline index = creation order in the process
//   blit:    "B#k"
// k is the encoder's ordinal in its command buffer.
// Formats: RGBA16F, RGBA8, BGRA8, RG16F, R32F, D32F, D32FS8, R16F, R8, ...; sizes as W x H (x samples when > 1).
//
// Environment: MARVIN_METAL_LABELS=1 enables it (with DYLD_INSERT_LIBRARIES=metal-labels.dylib); MARVIN_METAL_LABELS_LOG=1
// prints the first 200 labels to stderr.
// Build: clang -O2 -fobjc-arc -dynamiclib -framework Metal -framework Foundation metal-labels.m -o metal-labels.dylib
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <objc/runtime.h>

static BOOL logging = NO;
static int logged = 0;
static const void *infoKey = &infoKey;
static NSMapTable *pipelineIndex;  // pipeline object -> creation index
static NSLock *lock;
static NSUInteger nextPipeline = 0;

@interface MLEncoderInfo : NSObject
@property(nonatomic, copy) NSString *prefix;
@property(nonatomic) NSUInteger draws, dispatches;
@property(nonatomic, strong) NSMutableOrderedSet<NSString *> *pipelines;
@property(nonatomic, copy) NSString *grid;
@end
@implementation MLEncoderInfo
@end

static NSString *formatName(MTLPixelFormat f) {
    switch (f) {
        case MTLPixelFormatRGBA16Float: return @"RGBA16F";
        case MTLPixelFormatRGBA8Unorm: return @"RGBA8";
        case MTLPixelFormatRGBA8Unorm_sRGB: return @"RGBA8s";
        case MTLPixelFormatBGRA8Unorm: return @"BGRA8";
        case MTLPixelFormatBGRA8Unorm_sRGB: return @"BGRA8s";
        case MTLPixelFormatRG16Float: return @"RG16F";
        case MTLPixelFormatR16Float: return @"R16F";
        case MTLPixelFormatR32Float: return @"R32F";
        case MTLPixelFormatRG32Float: return @"RG32F";
        case MTLPixelFormatRGBA32Float: return @"RGBA32F";
        case MTLPixelFormatR8Unorm: return @"R8";
        case MTLPixelFormatRG8Unorm: return @"RG8";
        case MTLPixelFormatR32Uint: return @"R32U";
        case MTLPixelFormatRG11B10Float: return @"RG11B10F";
        case MTLPixelFormatRGB10A2Unorm: return @"RGB10A2";
        case MTLPixelFormatDepth32Float: return @"D32F";
        case MTLPixelFormatDepth32Float_Stencil8: return @"D32FS8";
        case MTLPixelFormatDepth16Unorm: return @"D16";
        default: return [NSString stringWithFormat:@"fmt%lu", (unsigned long)f];
    }
}

static NSString *textureName(id<MTLTexture> t) {
    if (!t) return @"-";
    NSString *s = [NSString stringWithFormat:@"%@ %lux%lu", formatName(t.pixelFormat), (unsigned long)t.width, (unsigned long)t.height];
    if (t.sampleCount > 1) s = [s stringByAppendingFormat:@"x%lu", (unsigned long)t.sampleCount];
    if (t.arrayLength > 1) s = [s stringByAppendingFormat:@"[%lu]", (unsigned long)t.arrayLength];
    return s;
}

// Works for MTLRenderPassDescriptor and MTL4RenderPassDescriptor (same attachment properties).
static NSString *describePass(id desc) {
    NSMutableString *s = [NSMutableString stringWithString:@"R "];
    id attachments = [desc valueForKey:@"colorAttachments"];
    NSString *first = nil; int more = 0;
    for (NSUInteger i = 0; i < 8; i++) {
        id a = [attachments objectAtIndexedSubscript:i];
        id<MTLTexture> t = [a valueForKey:@"texture"];
        if (!t) continue;
        if (!first) first = textureName(t); else more++;
    }
    [s appendString:first ?: @"-"];
    if (more) [s appendFormat:@" +%d", more];
    id depth = [desc valueForKey:@"depthAttachment"];
    id<MTLTexture> dt = [depth valueForKey:@"texture"];
    if (dt) {
        MTLLoadAction load = [[depth valueForKey:@"loadAction"] unsignedIntegerValue];
        [s appendFormat:@" | D %@ %@", textureName(dt), load == MTLLoadActionClear ? @"clear" : (load == MTLLoadActionLoad ? @"load" : @"dontcare")];
    }
    return s;
}

static MLEncoderInfo *infoFor(id encoder, NSString *prefix) {
    MLEncoderInfo *info = objc_getAssociatedObject(encoder, infoKey);
    if (!info) {
        info = [MLEncoderInfo new];
        info.prefix = prefix;
        info.pipelines = [NSMutableOrderedSet new];
        objc_setAssociatedObject(encoder, infoKey, info, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    }
    return info;
}

static NSString *pipelineName(id pipeline) {
    if (!pipeline) return @"nil";
    NSString *label = [pipeline respondsToSelector:@selector(label)] ? [pipeline label] : nil;
    if (label.length) return label;
    [lock lock];
    NSNumber *index = [pipelineIndex objectForKey:pipeline];
    if (!index) { index = @(nextPipeline++); [pipelineIndex setObject:index forKey:pipeline]; }
    [lock unlock];
    return [NSString stringWithFormat:@"#%@", index];
}

// ---- Swizzling helpers: replace a method on every class that implements it itself. A hooked class's original may call
// super's implementation, which can be hooked too; a small per-thread stack of (object, selector, class level) makes such
// a nested call continue with the original of the next hooked class up the chain instead of recursing.
typedef struct { Class cls; SEL sel; IMP imp; } Original;
static Original originals[8192];
static int originalCount = 0;
typedef struct { const void *obj; SEL sel; Class level; } Frame;
static __thread Frame frames[32];
static __thread int depth = 0;

static IMP findOriginal(Class c, SEL s) {
    for (int i = 0; i < originalCount; i++) if (originals[i].cls == c && originals[i].sel == s) return originals[i].imp;
    return NULL;
}
static IMP enter(id self, SEL s) {
    const void *obj = (__bridge const void *)self;
    Class start = object_getClass(self);
    for (int i = depth - 1; i >= 0; i--)
        if (frames[i].obj == obj && frames[i].sel == s) { start = class_getSuperclass(frames[i].level); break; }
    for (Class c = start; c; c = class_getSuperclass(c)) {
        IMP imp = findOriginal(c, s);
        if (imp) {
            if (depth < 32) frames[depth] = (Frame){obj, s, c};
            depth++;
            return imp;
        }
    }
    depth++;
    if (depth <= 32) frames[depth - 1] = (Frame){obj, s, Nil};
    return NULL;  // cannot happen: we were called through a hooked class
}
static void leave(void) { depth--; }
static BOOL definesMethod(Class c, SEL s) {
    unsigned n = 0; BOOL found = NO;
    Method *list = class_copyMethodList(c, &n);
    for (unsigned i = 0; i < n; i++) if (method_getName(list[i]) == s) { found = YES; break; }
    free(list);
    return found;
}
static void hook(Class c, SEL s, IMP replacement) {
    if (originalCount >= 8192 || !definesMethod(c, s) || findOriginal(c, s)) return;
    Method m = class_getInstanceMethod(c, s);
    IMP old = method_setImplementation(m, replacement);
    originals[originalCount++] = (Original){c, s, old};
}
// CALL(type, args...): call the original for this level and leave.
#define CALL(T, ...) ({ IMP imp_ = enter(self, _cmd); __typeof__(((T)0)(self, _cmd, ##__VA_ARGS__)) r_ = ((T)imp_)(self, _cmd, ##__VA_ARGS__); leave(); r_; })
#define CALLV(T, ...) do { IMP imp_ = enter(self, _cmd); ((T)imp_)(self, _cmd, ##__VA_ARGS__); leave(); } while (0)

// Encoder ordinal within its command buffer (k in "R#k", "C#k", "B#k"): tells apart passes with the same attachments
// (Godot's opaque and transparent passes draw into the same targets).
static const void *ordinalKey = &ordinalKey;
static NSString *ordinal(id commandBuffer, NSString *kind) {
    NSNumber *n = objc_getAssociatedObject(commandBuffer, ordinalKey);
    NSUInteger k = n ? n.unsignedIntegerValue : 0;
    objc_setAssociatedObject(commandBuffer, ordinalKey, @(k + 1), OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    return [NSString stringWithFormat:@"%@#%lu", kind, (unsigned long)k];
}
static NSString *renderPrefix(id commandBuffer, id desc) {
    return [ordinal(commandBuffer, @"R") stringByAppendingString:[describePass(desc) substringFromIndex:1]];
}

// Command buffer: encoder factories.
static id hookRenderEncoder(id self, SEL _cmd, id desc) {
    id encoder = CALL(id (*)(id, SEL, id), desc);
    if (encoder && desc) infoFor(encoder, renderPrefix(self, desc));
    return encoder;
}
static id hookRenderEncoderOptions(id self, SEL _cmd, id desc, NSUInteger options) {
    id encoder = CALL(id (*)(id, SEL, id, NSUInteger), desc, options);
    if (encoder && desc) infoFor(encoder, renderPrefix(self, desc));
    return encoder;
}
static id hookCompute0(id self, SEL _cmd) {
    id encoder = CALL(id (*)(id, SEL));
    if (encoder) infoFor(encoder, ordinal(self, @"C"));
    return encoder;
}
static id hookCompute1(id self, SEL _cmd, id arg) {
    id encoder = CALL(id (*)(id, SEL, id), arg);
    if (encoder) infoFor(encoder, ordinal(self, @"C"));
    return encoder;
}
static id hookComputeDispatchType(id self, SEL _cmd, NSUInteger type) {
    id encoder = CALL(id (*)(id, SEL, NSUInteger), type);
    if (encoder) infoFor(encoder, ordinal(self, @"C"));
    return encoder;
}
static id hookBlit0(id self, SEL _cmd) {
    id encoder = CALL(id (*)(id, SEL));
    if (encoder) infoFor(encoder, ordinal(self, @"B"));
    return encoder;
}
static id hookBlit1(id self, SEL _cmd, id arg) {
    id encoder = CALL(id (*)(id, SEL, id), arg);
    if (encoder) infoFor(encoder, ordinal(self, @"B"));
    return encoder;
}

// Encoders: count work, label at the end.
static void countDraw(id self) { MLEncoderInfo *i = objc_getAssociatedObject(self, infoKey); if (i) i.draws++; }
static void hookDrawPrimitives4(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, NSUInteger d) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, NSUInteger), a, b, c, d);
}
static void hookDrawPrimitives3(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger), a, b, c);
}
static void hookDrawIndexed6(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, id d, NSUInteger e, NSUInteger f) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, id, NSUInteger, NSUInteger), a, b, c, d, e, f);
}
static void hookDrawIndexed8(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, id d, NSUInteger e, NSUInteger f, NSInteger g, NSUInteger h) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, id, NSUInteger, NSUInteger, NSInteger, NSUInteger), a, b, c, d, e, f, g, h);
}
static void hookDrawIndexed5(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, id d, NSUInteger e) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, id, NSUInteger), a, b, c, d, e);
}
// MTL4 render encoder: index buffers are GPU addresses (uint64).
static void hookDrawIndexed4Addr(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, uint64_t d, NSUInteger e) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, uint64_t, NSUInteger), a, b, c, d, e);
}
static void hookDrawIndexed4AddrInst(id self, SEL _cmd, NSUInteger a, NSUInteger b, NSUInteger c, uint64_t d, NSUInteger e, NSUInteger f, NSInteger g, NSUInteger h) {
    countDraw(self); CALLV(void (*)(id, SEL, NSUInteger, NSUInteger, NSUInteger, uint64_t, NSUInteger, NSUInteger, NSInteger, NSUInteger), a, b, c, d, e, f, g, h);
}
static void hookSetComputePipeline(id self, SEL _cmd, id pipeline) {
    MLEncoderInfo *i = objc_getAssociatedObject(self, infoKey);
    if (i) [i.pipelines addObject:pipelineName(pipeline)];
    CALLV(void (*)(id, SEL, id), pipeline);
}
static void hookDispatch(id self, SEL _cmd, MTLSize a, MTLSize b) {
    MLEncoderInfo *i = objc_getAssociatedObject(self, infoKey);
    if (i) { i.dispatches++; i.grid = [NSString stringWithFormat:@"%lux%lu", (unsigned long)a.width, (unsigned long)a.height]; }
    CALLV(void (*)(id, SEL, MTLSize, MTLSize), a, b);
}
static void hookEndEncoding(id self, SEL _cmd) {
    MLEncoderInfo *i = objc_getAssociatedObject(self, infoKey);
    if (i) {
        NSString *label;
        if ([i.prefix hasPrefix:@"R"]) label = [NSString stringWithFormat:@"%@ | %lu draws", i.prefix, (unsigned long)i.draws];
        else if ([i.prefix hasPrefix:@"C"]) {
            NSArray *p = i.pipelines.array;
            NSString *names = p.count > 4 ? [[[p subarrayWithRange:NSMakeRange(0, 4)] componentsJoinedByString:@","] stringByAppendingFormat:@",+%lu", (unsigned long)(p.count - 4)] : [p componentsJoinedByString:@","];
            label = [NSString stringWithFormat:@"%@ %@ x%lu %@", i.prefix, names, (unsigned long)i.dispatches, i.grid ?: @""];
        } else label = i.prefix;
        [self setLabel:label];
        if (logging && logged < 200) { logged++; fprintf(stderr, "metal-labels: %s\n", label.UTF8String); }
    }
    CALLV(void (*)(id, SEL));
}

static void hookClasses(Protocol *protocol, void (^each)(Class)) {
    if (!protocol) return;
    unsigned n = 0;
    Class *classes = objc_copyClassList(&n);
    for (unsigned i = 0; i < n; i++) {
        Class c = classes[i];
        // class_conformsToProtocol only checks the class itself; walk up.
        BOOL conforms = NO;
        for (Class k = c; k && !conforms; k = class_getSuperclass(k)) conforms = class_conformsToProtocol(k, protocol);
        if (conforms) each(c);
    }
    free(classes);
}

__attribute__((constructor)) static void metalLabelsInit(void) {
    const char *on = getenv("MARVIN_METAL_LABELS");
    if (!on || strcmp(on, "1") != 0) return;
    logging = getenv("MARVIN_METAL_LABELS_LOG") != NULL;
    pipelineIndex = [NSMapTable weakToStrongObjectsMapTable];
    lock = [NSLock new];
    // Load the GPU driver bundle so its command buffer and encoder classes exist.
    id<MTLDevice> device = MTLCreateSystemDefaultDevice();
    (void)device;
    int hooked = 0;
    __block int *count = &hooked;
    void (^commandBuffer)(Class) = ^(Class c) {
        hook(c, @selector(renderCommandEncoderWithDescriptor:), (IMP)hookRenderEncoder);
        hook(c, @selector(renderCommandEncoderWithDescriptor:options:), (IMP)hookRenderEncoderOptions);
        hook(c, @selector(computeCommandEncoder), (IMP)hookCompute0);
        hook(c, @selector(computeCommandEncoderWithDescriptor:), (IMP)hookCompute1);
        hook(c, @selector(computeCommandEncoderWithDispatchType:), (IMP)hookComputeDispatchType);
        hook(c, @selector(blitCommandEncoder), (IMP)hookBlit0);
        hook(c, @selector(blitCommandEncoderWithDescriptor:), (IMP)hookBlit1);
        (*count)++;
    };
    hookClasses(@protocol(MTLCommandBuffer), commandBuffer);
    hookClasses(objc_getProtocol("MTL4CommandBuffer"), commandBuffer);
    void (^encoder)(Class) = ^(Class c) {
        hook(c, @selector(endEncoding), (IMP)hookEndEncoding);
        hook(c, @selector(drawPrimitives:vertexStart:vertexCount:instanceCount:), (IMP)hookDrawPrimitives4);
        hook(c, @selector(drawPrimitives:vertexStart:vertexCount:), (IMP)hookDrawPrimitives3);
        hook(c, @selector(drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:), (IMP)hookDrawIndexed6);
        hook(c, @selector(drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:), (IMP)hookDrawIndexed8);
        hook(c, @selector(drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:), (IMP)hookDrawIndexed5);
        hook(c, @selector(drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferLength:), (IMP)hookDrawIndexed4Addr);
        hook(c, @selector(drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferLength:instanceCount:baseVertex:baseInstance:), (IMP)hookDrawIndexed4AddrInst);
        hook(c, @selector(setComputePipelineState:), (IMP)hookSetComputePipeline);
        hook(c, @selector(dispatchThreadgroups:threadsPerThreadgroup:), (IMP)hookDispatch);
        hook(c, @selector(dispatchThreads:threadsPerThreadgroup:), (IMP)hookDispatch);
        (*count)++;
    };
    hookClasses(@protocol(MTLCommandEncoder), encoder);
    hookClasses(objc_getProtocol("MTL4CommandEncoder"), encoder);
    fprintf(stderr, "metal-labels: hooked %d classes, %d methods\n", hooked, originalCount);
}
