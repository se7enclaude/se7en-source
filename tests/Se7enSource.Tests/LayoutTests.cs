using Se7enSource.Core;

namespace Se7enSource.Tests;

public class LayoutTests
{
    [Fact]
    public void Two_side_by_side_screens()
    {
        // le principal à gauche en (0,0), le second à droite : l'ordre reçu ne compte pas
        Assert.Equal(["de droite", "de gauche"], Layout.PositionNames([(2560, 0, 2560, 1440), (0, 0, 2560, 1440)]));
        // second écran placé à gauche du principal (coordonnées négatives), un peu décalé en hauteur
        Assert.Equal(["de gauche", "de droite"], Layout.PositionNames([(-1920, 200, 1920, 1080), (0, 0, 2560, 1440)]));
    }

    [Fact]
    public void Three_screens_and_stacked_screens()
    {
        Assert.Equal(["de gauche", "du milieu", "de droite"],
            Layout.PositionNames([(0, 0, 1920, 1080), (1920, 0, 1920, 1080), (3840, 0, 1920, 1080)]));
        Assert.Equal(["du bas", "du haut"], Layout.PositionNames([(0, 0, 2560, 1440), (300, -1080, 1920, 1080)]));
        Assert.Equal([""], Layout.PositionNames([(0, 0, 1920, 1080)]));
    }

    [Fact]
    public void Detection_needs_proof_that_the_screen_returns_by_itself()
    {
        // PC en DisplayPort 1 ; l'écran est revenu tout seul sur DP 2 et HDMI 2, est resté sur HDMI 1 : la PS5 est en HDMI 1
        var r = Detection.Interpret(0x0F, new Dictionary<int, bool> { [0x10] = false, [0x11] = true, [0x12] = false });
        Assert.Equal(Signal.Present, r[0x0F]);
        Assert.Equal(Signal.Present, r[0x11]);
        Assert.Equal(Signal.Absent, r[0x10]);
        Assert.Equal(Signal.Absent, r[0x12]);
        // écran qui reste partout (même sur « pas de signal ») : on ne peut rien conclure
        var u = Detection.Interpret(0x0F, new Dictionary<int, bool> { [0x10] = true, [0x11] = true });
        Assert.Equal(Signal.Unknown, u[0x11]);
    }
}
