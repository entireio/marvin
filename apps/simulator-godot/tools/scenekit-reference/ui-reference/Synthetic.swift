
// ---- Appended by tools/scenekit-reference/ui-reference/build.sh (reference captures only).
extension DirtRace {
    /// The state a finished or running race would have (mirrors UISmoke.SyntheticRace in the Godot port).
    public static func synthetic(elapsed: Double, laps: [Double], countdown: Double = 0, progress: Double? = nil) -> DirtRace {
        var race = DirtRace()
        race.countdown = countdown; race.elapsed = elapsed; race.laps = laps
        race.progress = progress ?? 2 * .pi * Double(laps.count); race.lapStart = laps.reduce(0, +)
        return race
    }
}
