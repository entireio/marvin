using System.Collections.Generic;
using static Marvin.Core.Swift;

namespace Marvin;

/// Physical, bounded sign plates. Text is rasterized once, centered using actual
/// font metrics, and mipmapped; no per-frame text layout or floating billboards.
public sealed class TownSigns
{
    public int count { get; private set; } = 0;
    public bool valid { get; private set; } = true;
    private Dictionary<string, NSImage> textures = new();
    // PORT: Swift's `into root:` is required but follows defaulted parameters; C# requires optional
    // parameters last, so `into` defaults to null. Callers pass `at:` and `into:` by name, as in Swift.
    public void plate(string title, string eyebrow, string footer, string badge,
                      SCNVector3 at, CGFloat width, CGFloat height, CGFloat yaw = 0,
                      uint accent = 0xb98450, SCNNode into = null)
    {
        SCNVector3 position = at; SCNNode root = into;
        var node = new SCNNode(); node.name = $"Sign · {title}"; node.position = position; node.eulerAngles.y = yaw;
        var frame = new SCNBox(width + 0.045, height + 0.045, 0.055, 0.012);
        var metal = new SCNMaterial(); metal.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        metal.diffuse.contents = NSColor.calibratedWhite(0.17, 1); metal.metalness.contents = 0.55; metal.roughness.contents = 0.65;
        frame.materials = new() { metal }; var backing = new SCNNode(frame); backing.castsShadow = false; node.addChildNode(backing);
        var plane = new SCNPlane(width, height);
        var material = new SCNMaterial(); material.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        var key = $"{title}|{eyebrow}|{footer}|{badge}|{accent}|{description(width / height)}";
        if (!textures.ContainsKey(key)) { textures[key] = image(title, eyebrow: eyebrow, footer: footer, badge: badge, aspect: width / height, accent: accent); }
        material.diffuse.contents = textures[key]; material.diffuse.mipFilter = SCNFilterMode.linear; material.diffuse.maxAnisotropy = 8;
        material.roughness.contents = 0.82;
        // A small self-lit contribution keeps signs legible under market awnings.
        material.emission.contents = textures[key]; material.emission.intensity = 0.12;
        plane.materials = new() { material }; var face = new SCNNode(plane); face.position.z = 0.029; face.castsShadow = false;
        node.addChildNode(face); root.addChildNode(node); count += 1;
        valid = valid && width > 0 && height > 0 && width / height > 2 && width / height < 9;
    }
    private NSImage image(string title, string eyebrow, string footer, string badge, CGFloat aspect, uint accent)
    {
        int w = 1024, h = (int)rounded(1024 / aspect);
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext.bitmapImageRep(bitmap);
        CGFloat W = (CGFloat)w, H = (CGFloat)h;
        NSColor color(uint rgb) => NSColor.calibratedRed((CGFloat)((rgb >> 16) & 255) / 255, (CGFloat)((rgb >> 8) & 255) / 255, (CGFloat)(rgb & 255) / 255, 1);
        color(0x263c3c).setFill(); new NSRect(0, 0, W, H).fill();
        color(accent).setFill(); new NSRect(0, H - 10, W, 10).fill();
        color(0xb8aa8c).withAlphaComponent(0.45).setStroke();
        var outline = new NSBezierPath(new NSRect(12, 12, W - 24, H - 31)); outline.lineWidth = 2; outline.stroke();
        CGFloat badgeW = H * 0.55, left = badgeW + H * 0.15, right = W - H * 0.10;
        color(accent).setFill(); new NSRect(H * 0.10, H * 0.25, badgeW, H * 0.49).fill();
        void text(string value, NSRect @in, CGFloat size, uint ink, NSFont.Weight weight, CGFloat tracking = 0)
        {
            var rect = @in;
            var paragraph = new NSMutableParagraphStyle(); paragraph.alignment = NSTextAlignment.center; paragraph.lineBreakMode = NSLineBreakMode.byClipping;
            Dictionary<NSAttributedString.Key, object> attributes() => new()
            {
                [NSAttributedString.Key.font] = NSFont.named(weight == NSFont.Weight.bold ? "AvenirNextCondensed-DemiBold" : "AvenirNextCondensed-Medium", size) ?? NSFont.systemFont(size, weight),
                [NSAttributedString.Key.foregroundColor] = color(ink), [NSAttributedString.Key.paragraphStyle] = paragraph, [NSAttributedString.Key.kern] = tracking,
            };
            while (size > 8)
            {
                var measured_ = new NSAttributedString(value, attributes()).size();
                if (measured_.width <= rect.width && measured_.height <= rect.height) { break; }
                size -= 1;
            }
            NSAttributedString @string = new NSAttributedString(value, attributes()); CGSize measured = @string.size();
            if (measured.width > rect.width + 1 || measured.height > rect.height + 2)
            {
                Godot.GD.Print($"Sign text overflow: {value}, measured ({description(measured.width)}, {description(measured.height)}), available ({description(rect.size.width)}, {description(rect.size.height)})");
                valid = false;
            }
            @string.draw(new NSRect(rect.minX, rect.midY - measured.height / 2, rect.width, measured.height + 1));
        }
        text(badge, new NSRect(H * 0.10, H * 0.25, badgeW, H * 0.49), size: H * 0.30, ink: 0x263c3c, weight: NSFont.Weight.bold);
        CGFloat x = left + H * 0.08, tw = right - x;
        text(eyebrow, new NSRect(x, H * 0.73, tw, H * 0.16), size: H * 0.105, ink: 0xc2b69c, weight: NSFont.Weight.medium, tracking: 2.6);
        text(title, new NSRect(x, H * 0.29, tw, H * 0.43), size: H * 0.34, ink: 0xf1e1bc, weight: NSFont.Weight.bold, tracking: 1.5);
        color(accent).withAlphaComponent(0.70).setFill(); new NSRect(x + tw * 0.1, H * 0.28, tw * 0.8, 1.5).fill();
        text(footer, new NSRect(x, H * 0.09, tw, H * 0.17), size: H * 0.105, ink: 0xc2b69c, weight: NSFont.Weight.medium, tracking: 1.8);
        // Restrained enamel wear; lettering remains readable.
        color(0xd9ccb0).withAlphaComponent(0.08).setFill();
        for (var i = 0; i < 65; i++)
        {
            new NSRect((CGFloat)((i * 179 + 23) % w), (CGFloat)((i * 43 + 17) % h), (CGFloat)(1 + i % 6), 1).fill();
        }
        NSGraphicsContext.restoreGraphicsState();
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }
}
