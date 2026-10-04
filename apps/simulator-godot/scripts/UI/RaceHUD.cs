using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class RaceHUD : NSView
{
    public DirtOpponent[] opponents = Array.Empty<DirtOpponent>();
    public string playerName = "Marvin"; public uint playerColor = Robot.silverColor;
    public string[] racerNames = { "R2-D2", "BB-8", "WALL-E" };
    public uint[] racerColors = { 0x58baff, 0xff914b, 0xf2c94c };
    public int position => 1 + opponents.Count(opponent => opponent.playerPosition(race) == 2);
    public DirtRace race = new DirtRace(); public DirtScore[] scores = Array.Empty<DirtScore>();
    public double x = 0.0, z = -10.0, heading = 0.0;
    public RaceMapRegion mapRegion = RaceMapRegion.course;
    public NSImage townMapImage, planetMapImage;
    public bool stormSelected = false;
    public bool stormWarningVisible => stormSelected && race.countdown > 0 && !paused && !escaping && !race.finished;
    public bool introducing = false;
    public bool escaping = false, escapeComplete = false;
    public bool paused = false, helpVisible = true;
    public string saveError;
    public override bool isFlipped => true;
    public override NSView hitTest(NSPoint point) => null;
    // PORT: color(_:alpha:) is a free function in Robot.swift; this private copy keeps the file independent of that port.
    private static NSColor color(uint hex, CGFloat alpha = 1) =>
        NSColor.srgbRed((CGFloat)((hex >> 16) & 255) / 255, (CGFloat)((hex >> 8) & 255) / 255, (CGFloat)(hex & 255) / 255, alpha);
    public static string time(double seconds) =>
        Swift.format("%d:%05.2f", (long)seconds / 60, seconds % 60);
    private Dictionary<NSAttributedString.Key, object> textAttributes(CGFloat size, bool bold = false) =>
        new() { [NSAttributedString.Key.font] = NSFont.monospacedSystemFont(size, bold ? NSFont.Weight.bold : NSFont.Weight.regular), [NSAttributedString.Key.foregroundColor] = color(0xf7eddb) };
    public void text(string @string, CGFloat x, CGFloat y, CGFloat size = 16, bool bold = false)
    {
        @string.draw(new NSPoint(x, y), textAttributes(size, bold));
    }
    private void panel(CGFloat x, CGFloat y, CGFloat w, CGFloat h)
    {
        color(0x263029, alpha: 0.9).setFill(); NSBezierPath.roundedRect(new NSRect(x, y, w, h), 12, 12).fill();
    }
    public override void draw(NSRect rect)
    {
        if (escaping && !paused) { drawTownDeparture(); return; }
        if (race.finished && !paused) { drawResults(); return; }
        if (stormWarningVisible && introducing)
        {
            // PORT: C# forbids reusing the outer local name x in this nested block; sx/sy are Swift's x/y.
            CGFloat w = min((CGFloat)420, bounds.width - 44), h = 88;
            CGFloat sx = (bounds.width - w) / 2, sy = (bounds.height - h) / 2;
            panel(sx, sy, w, h);
            label("A storm is coming...", @in: new NSRect(sx + 20, sy + 28, w - 40, 34), size: 26, weight: NSFont.Weight.semibold, tint: 0xffd78d, alignment: NSTextAlignment.center);
            return;
        }
        var races = new[] { race }.Concat(opponents.Select(opponent => opponent.race)).ToArray();
        CGFloat inset = 18, rowsY = 242, rowSpacing = 22;
        var rowHeight = ceil("Marvin".size(textAttributes(13)).height);
        var panelBottom = rowsY + (CGFloat)(races.Length - 1) * rowSpacing + rowHeight + inset;
        var best = minElement(race.laps) is double fastest ? time(fastest) : "—";
        var lines = new (string value, CGFloat y, CGFloat size, bool bold)[]
        {
            ("DIRT TRACK", 40, 23, true),
            ($"LAP {min(3, race.laps.Length + 1)} / 3", 76, 18, true),
            ($"CURRENT  {time(race.currentLap)}", 111, 16, false),
            ($"TOTAL    {time(race.elapsed)}", 140, 16, false),
            ($"BEST LAP {best}", 169, 16, false),
            ($"POSITION {position} / 4", 202, 18, true),
        };
        var names = new[] { playerName }.Concat(racerNames).ToArray(); var colors = new[] { playerColor }.Concat(racerColors).ToArray();
        var standings = races.Select((competitor, i) =>
        {
            var status = competitor.finished ? "FINISHED" : $"LAP {min(3, competitor.laps.Length + 1)} / 3";
            return $"{names[i].padding(8, " ", 0)}{status}";
        }).ToArray();
        // Measure the actual rendered strings so every edge keeps the same
        // inset, expanding only when longer times or standings need the space.
        var lineWidths = lines.Select(line => line.value.size(textAttributes(line.size, line.bold)).width);
        var rowWidths = standings.Select(standing => 15 + standing.size(textAttributes(13)).width);
        var panelWidth = ceil(lineWidths.Concat(rowWidths).DefaultIfEmpty(0).Max()) + inset * 2;
        panel(22, 22, panelWidth, panelBottom - 22);
        foreach (var line in lines) { text(line.value, 40, line.y, line.size, line.bold); }
        for (int i = 0; i < standings.Length; i++)
        {
            var standing = standings[i];
            color(colors[i]).setFill();
            var rowY = rowsY + (CGFloat)i * rowSpacing;
            NSBezierPath.ovalIn(new NSRect(40, rowY + (rowHeight - 7) / 2, 7, 7)).fill();
            text(standing, 55, rowY, 13);
        }
        var x = bounds.width - 292;
        panel(x, 22, 270, 250);
        text("LOCAL HIGH SCORES", x + 18, 40, 17, true);
        if (scores.Length == 0) { text("Set the first time!", x + 18, 80, 14); }
        for (int i = 0; i < min(5, scores.Length); i++)
        {
            var score = scores[i];
            text($"{i + 1}.  {time(score.total)}", x + 18, 78 + (CGFloat)i * 28, 16);
        }
        text("Fastest 3-lap totals", x + 18, 238, 12);
        CGFloat mapHeight = mapRegion == RaceMapRegion.course ? 150 : 212;
        panel(x, 286, 270, mapHeight + 62);
        text(mapRegion.rawValue, x + 18, 300, 12, true);
        drawNavigationMap(@in: new NSRect(x + 18, 330, 234, mapHeight));
        if (helpVisible)
        {
            // PORT: "⌘R" is spelled with the platform's command key (KeyEquivalent.command: ⌘ on macOS, Ctrl+ elsewhere).
            var help = new[] { "DRIVE W A S D / arrows   BOOST Shift   BRAKE Space", $"CAMERA C / drag / scroll   PAUSE P / Esc   RESTART {KeyEquivalent.command}R" };
            var font = NSFont.monospacedSystemFont(12, NSFont.Weight.regular);
            var width = min(bounds.width - 44, help.Select(line => line.size(new() { [NSAttributedString.Key.font] = font }).width).Max() + 36);
            panel(22, bounds.height - 84, width, 62);
            text("DRIVE W A S D / arrows   BOOST Shift   BRAKE Space", 40, bounds.height - 71, 12);
            text($"CAMERA C / drag / scroll   PAUSE P / Esc   RESTART {KeyEquivalent.command}R", 40, bounds.height - 46, 12);
        }
        if (paused) { drawModal(paused: true); }
        else if (!introducing && race.countdown > 0) { drawModal(paused: false); }
        if (saveError != null) { text("High scores unavailable", x + 18, 214, 12); }
    }

    private void label(string @string, NSRect @in, CGFloat size, NSFont.Weight weight = NSFont.Weight.regular,
                       uint tint = 0xf7eddb, NSTextAlignment alignment = NSTextAlignment.left)
    {
        var style = new NSMutableParagraphStyle(); style.alignment = alignment;
        @string.draw(@in, new() { [NSAttributedString.Key.font] = NSFont.systemFont(size, weight),
            [NSAttributedString.Key.foregroundColor] = color(tint), [NSAttributedString.Key.paragraphStyle] = style });
    }
    private void rule(NSRect rect) { color(0xf7eddb, alpha: 0.15).setFill(); rect.fill(); }
    private void drawModal(bool paused)
    {
        color(0x101a15, alpha: 0.42).setFill(); bounds.fill();
        CGFloat w = 380, h = paused ? 250 : 290;
        CGFloat x = (bounds.width - w) / 2, y = (bounds.height - h) / 2;
        panel(x, y, w, h);
        label(paused ? "TAKE A BREATHER" : "DIRT TRACK · 3 LAPS", @in: new NSRect(x + 24, y + 26, w - 48, 22), size: 12, weight: NSFont.Weight.semibold, tint: 0xffd78d, alignment: NSTextAlignment.center);
        label(paused ? "Paused" : $"{(long)ceil(race.countdown)}", @in: new NSRect(x + 24, y + 58, w - 48, 100), size: paused ? 44 : 80, weight: NSFont.Weight.bold, alignment: NSTextAlignment.center);
        label(paused ? "The race is waiting for you." : (stormWarningVisible ? "A storm is coming..." : "Get ready to race"), @in: new NSRect(x + 24, y + h - 106, w - 48, 25), size: 17, alignment: NSTextAlignment.center);
        rule(new NSRect(x + 28, y + h - 68, w - 56, 1));
        label(paused ? $"P / Esc  Resume    ·    {KeyEquivalent.command}R  Restart" : "WASD / ↑↓←→  Drive    ·    Shift  Boost", @in: new NSRect(x + 16, y + h - 46, w - 32, 24), size: 13, weight: NSFont.Weight.medium, tint: 0xffd78d, alignment: NSTextAlignment.center);
    }
    private void drawTownDeparture()
    {
        var races = new[] { race }.Concat(opponents.Select(opponent => opponent.race)).ToArray(); var names = new[] { playerName }.Concat(racerNames).ToArray();
        var w = min((CGFloat)310, bounds.width - 44);
        panel(22, 22, w, 178);
        label(escapeComplete ? "WELCOME TO MOS ASTER" : "OFF TO TOWN", @in: new NSRect(40, 37, w - 36, 23), size: 16, weight: NSFont.Weight.bold, tint: 0xffd78d);
        var order = DirtStandings.order(races);
        for (int rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var y = (CGFloat)(70 + rank * 23);
            label($"{rank + 1}.  {names[index]}", @in: new NSRect(40, y, w - 125, 20), size: 13, weight: NSFont.Weight.medium);
            label(time(races[index].elapsed), @in: new NSRect(w - 60, y, 70, 20), size: 12, weight: NSFont.Weight.medium);
        }
        label(escapeComplete ? $"Drive to explore · {KeyEquivalent.command}R to race again" : "Autopilot · Separate routes through town", @in: new NSRect(40, 169, w - 36, 20), size: 11, tint: 0xb8c2b6);
    }
    private void drawResults()
    {
        var races = new[] { race }.Concat(opponents.Select(opponent => opponent.race)).ToArray();
        var names = new[] { playerName }.Concat(racerNames).ToArray(); var colors = new[] { playerColor }.Concat(racerColors).ToArray();
        var order = DirtStandings.order(races);
        var complete = races.All(r => r.finished);
        CGFloat w = min((CGFloat)560, bounds.width - 44), h = 344;
        CGFloat x = bounds.width - w - 22, y = bounds.height - h - 22;
        panel(x, y, w, h);
        label(complete ? "FINAL CLASSIFICATION" : "LIVE CLASSIFICATION", @in: new NSRect(x + 24, y + 22, w - 48, 20), size: 11, weight: NSFont.Weight.bold, tint: 0xffd78d);
        label("Race finished", @in: new NSRect(x + 24, y + 44, w - 48, 42), size: 30, weight: NSFont.Weight.bold);
        CGFloat totalX = x + w - 216, lapX = x + w - 108;
        label("ROBOT", @in: new NSRect(x + 58, y + 106, 190, 20), size: 10, weight: NSFont.Weight.bold, tint: 0xb8c2b6);
        label("TOTAL", @in: new NSRect(totalX, y + 106, 96, 20), size: 10, weight: NSFont.Weight.bold, tint: 0xb8c2b6);
        label("BEST LAP", @in: new NSRect(lapX, y + 106, 96, 20), size: 10, weight: NSFont.Weight.bold, tint: 0xb8c2b6);
        for (int rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var rowY = y + 134 + (CGFloat)rank * 36; var result = races[index];
            rule(new NSRect(x + 24, rowY - 8, w - 48, 1));
            label($"{rank + 1}", @in: new NSRect(x + 24, rowY, 28, 24), size: 16, weight: NSFont.Weight.bold, tint: colors[index]);
            label(names[index], @in: new NSRect(x + 58, rowY, w - 282, 24), size: 16, weight: index == 0 ? NSFont.Weight.bold : NSFont.Weight.medium, tint: colors[index]);
            text(result.finished ? time(result.elapsed) : $"LAP {result.laps.Length + 1}/3", totalX, rowY, 14);
            text(minElement(result.laps) is double fastest ? time(fastest) : "—", lapX, rowY, 14);
        }
        label(saveError ?? (complete ? "Autopilot · Enjoy the cooldown lap" : "Autopilot · Waiting for the remaining finishers"), @in: new NSRect(x + 24, y + 282, w - 48, 22), size: 12, tint: 0xb8c2b6);
        label($"{KeyEquivalent.command}R  Race again    ·    Main Menu to leave", @in: new NSRect(x + 24, y + 309, w - 48, 22), size: 12, weight: NSFont.Weight.medium, tint: 0xffd78d);
    }

    // extension RaceHUD
    public void drawCourseMap(NSRect @in)
    {
        var map = @in;
        NSPoint point(double px, double pz) =>
            new NSPoint(map.maxX - (CGFloat)((px + 21) / 42) * map.width, map.maxY - (CGFloat)((pz + 20) / 42) * map.height);
        var coursePoints = Enumerable.Range(0, 161).Select(i =>
        {
            var p = DirtCourse.point((double)i * 2 * Math.PI / 160);
            return point(p.x, p.z);
        }).ToArray();
        var path = new NSBezierPath();
        for (int i = 0; i < coursePoints.Length; i++)
        {
            // PORT: Swift's loop variable `at`, renamed because C# forbids reusing the later local name.
            var coursePoint = coursePoints[i];
            if (i == 0) { path.move(coursePoint); } else { path.line(coursePoint); }
        }
        // Show race progress on the exact polyline drawn above, independent
        // of lateral position on the wide course or beyond its edges.
        NSPoint marker(double px, double pz)
        {
            var progress = DirtCourse.phase(px, pz) / (2 * Math.PI) * 160;
            var index = min(159, max(0, (int)progress));
            var fraction = (CGFloat)(progress - (double)index);
            NSPoint a = coursePoints[index], b = coursePoints[index + 1];
            return new NSPoint(a.x + (b.x - a.x) * fraction, a.y + (b.y - a.y) * fraction);
        }
        color(0xa7865e).setStroke(); path.lineWidth = 10.5; path.lineJoinStyle = NSBezierPath.LineJoinStyle.round; path.stroke();
        // Phase zero is the same crossing used by the race timer and track.
        var start = DirtCourse.point(0); var ahead = DirtCourse.point(0.001);
        NSPoint finishAt = point(start.x, start.z), finishAhead = point(ahead.x, ahead.z);
        NSGraphicsContext.saveGraphicsState();
        var transform = AffineTransform.translationByX(finishAt.x, finishAt.y);
        var oriented = transform;
        oriented.rotate(atan2(finishAhead.y - finishAt.y, finishAhead.x - finishAt.x));
        ((NSAffineTransform)oriented).concat();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 2; column++)
            {
                color((row + column) % 2 == 0 ? 0xf7eddbu : 0x18221du).setFill();
                new NSRect((CGFloat)column * 4 - 4, (CGFloat)row * 4 - 8, 4, 4).fill();
            }
        }
        color(0x18221d).setStroke();
        var border = new NSBezierPath(new NSRect(-4, -8, 8, 16));
        border.lineWidth = 1; border.stroke();
        NSGraphicsContext.restoreGraphicsState();
        text("START / FINISH", finishAt.x - 38, finishAt.y + 12, 9, true);
        for (int i = 0; i < opponents.Length; i++)
        {
            var opponent = opponents[i];
            var rivalAt = marker(opponent.simulation.x, opponent.simulation.z);
            color(racerColors[i]).setFill();
            NSBezierPath.ovalIn(new NSRect(rivalAt.x - 4, rivalAt.y - 4, 8, 8)).fill();
        }
        var at = marker(this.x, z);
        color(playerColor).setFill(); NSBezierPath.ovalIn(new NSRect(at.x - 4, at.y - 4, 8, 8)).fill();
        var direction = new NSBezierPath(); direction.move(at);
        direction.line(new NSPoint(at.x - sin(heading) * 10, at.y - cos(heading) * 10));
        color(playerColor).setStroke(); direction.lineWidth = 2; direction.stroke();
    }
}
