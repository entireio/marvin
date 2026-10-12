// Godot-only: the Settings screen's "Shadow quality" row (MainMenuView). The macOS Settings screen has no such control:
// SceneKit draws its soft shadows at a cost the Mac's GPU can afford, and the Godot port offers two configurations
// (PORTING.md, "Known deviations"; docs/performance.md, "Hard shadows and camera mesh LODs" and "Near shadow splits").
//   Fast (the default): hard shadows with near splits, the robots' mesh LODs for the camera, no base-terrain shadow.
//   Exact: SoftHigh shadows with SceneKit's penumbrae, the robots' full meshes (SceneKitCalibration.ApplyExact).
// The choice is stored with the other settings (UserDefaults, user://UserDefaults.cfg) and applies at once, in the menu
// and in a race or sandbox entered afterwards. The game launched without a game-mode flag starts with the stored choice
// (Main.cs); game modes (smoke tests, benchmarks, probes, the playthrough) keep the default unless their menu row is
// used. MARVIN_SCN_CAL, when set, chooses the configuration instead (tests and experiments): the row then only changes
// the stored choice.
namespace Marvin;

public static class ShadowQualitySetting
{
    /// <summary>The UserDefaults key: "Exact" or "Fast" (absent: Fast).</summary>
    public const string key = "shadowQuality";

    /// <summary>The stored choice: Exact (true) or Fast (false, the default).</summary>
    public static bool exact
    {
        get => UserDefaults.standard.@string(key) == "Exact";
        set => UserDefaults.standard.set(value ? "Exact" : "Fast", key);
    }

    /// <summary>MARVIN_SCN_CAL chooses the configuration; the stored choice does not apply.</summary>
    public static bool overridden => SceneKitCalibration.Overridden;

    /// <summary>Whether the configuration in use is the one Exact (true) or Fast (false) selects (always true while
    /// overridden).</summary>
    public static bool applied(bool exactChoice) => overridden
        || (SceneKitCalibration.HardShadows != exactChoice && SceneKitCalibration.MeshLodForCamera != exactChoice
            && SceneKitCalibration.BaseTerrainCastsShadow == exactChoice);

    /// <summary>
    /// Applies the stored choice (main thread): the calibration preset, Godot's filter quality and the robots' mesh-LOD
    /// twins (SceneKitRuntime.ShadowConfigurationChanged), and the base terrain's shadow of a race world already built
    /// (a world built later reads SceneKitCalibration.BaseTerrainCastsShadow itself).
    /// </summary>
    public static void apply(DirtWorld world = null)
    {
        if (overridden) return;
        if (exact) SceneKitCalibration.ApplyExact(); else SceneKitCalibration.ApplyFast();
        SceneKitRuntime.ShadowConfigurationChanged();
        if (world?.baseTerrain is SCNNode terrain) terrain.castsShadow = SceneKitCalibration.BaseTerrainCastsShadow;
    }
}
