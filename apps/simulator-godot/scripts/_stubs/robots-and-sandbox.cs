// STUBS (stream robots-and-sandbox): minimal stand-ins for types owned by other streams, only the
// members PlayerCharacter.cs calls. Deleted by the orchestrator when the streams are merged.
using Marvin.Core;

namespace Marvin;

/// App.swift (AppController). The real port must be a `partial class` (PlayerCharacter.swift extends it).
public partial class AppController
{
    public Robot robot;
    public R2D2 r2d2;
    public ImportedRacer bb8, wallE;
    public RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    public Simulation simulation = new Simulation();
    public MainMenuView mainMenu;
    public RaceHUD raceHUD;
    public DirtWorld dirtWorld;
    public bool raceCameraLocked => false;
    public DirtOpponent[] opponents => System.Array.Empty<DirtOpponent>();
}

/// MainMenu.swift (MainMenuView).
public partial class MainMenuView
{
    public RacePerformance.Character character { get; private set; } = RacePerformance.Character.marvin;
}

/// RaceHUD.swift (RaceHUD).
public partial class RaceHUD
{
    public string playerName = "Marvin"; public uint playerColor = Robot.silverColor;
    public string[] racerNames = { "R2-D2", "BB-8", "WALL-E" };
    public uint[] racerColors = { 0x58baff, 0xff914b, 0xf2c94c };
}

/// DirtWorld.swift (DirtWorld).
public partial class DirtWorld
{
    public BinarySky sky { get; private set; }
    public void update(Simulation state, Simulation opponent, double dt, double modelScale, Simulation[] additional) { }
}

/// BinarySky.swift (BinarySky).
public partial class BinarySky
{
    public void updateShadowCenter(Double3 position) { }
}
