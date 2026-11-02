using Se7enSource.Core;

namespace Se7enSource.Tests;

public class DiagnosisTests
{
    const int DP1 = 0x0F, DP2 = 0x10, HDMI1 = 0x11, HDMI2 = 0x12;

    private static List<Sample> Trace(params (double Seconds, int? Input)[] parts)
    {
        var list = new List<Sample>();
        double t = 0;
        foreach (var (len, input) in parts)
            for (double x = 0; x < len; x += 0.5, t += 0.5)
                list.Add(new Sample(t, input));
        return list;
    }

    [Fact]
    public void Real_trace_of_a_screen_that_searches_without_finding_the_console()
    {
        // relevé réel (écran AOC, PC en HDMI 1, PS5 en 4K via une carte de capture) : il cherche puis revient
        var trace = Trace((21, HDMI1), (0.6, null), (4, HDMI1), (0.8, null), (3, HDMI1), (0.6, null), (6, HDMI1),
                          (3, DP1), (3, DP2), (1, HDMI1), (7, null), (3, DP2), (1, HDMI1), (5, null), (20, HDMI1));
        Assert.Equal(Behaviour.Scanning, Diagnosis.Analyze(HDMI1, trace).Behaviour);
    }

    [Fact]
    public void Console_found_when_the_screen_stays_on_it()
    {
        var v = Diagnosis.Analyze(HDMI1, Trace((5, HDMI1), (12, HDMI2), (4, HDMI1)));
        Assert.Equal(Behaviour.Stayed, v.Behaviour);
        Assert.Equal(HDMI2, v.Input);
        // code « à lui » (écran qui numérote ses prises autrement) : trouvé quand même
        Assert.Equal(0x90, Diagnosis.Analyze(HDMI1, Trace((3, HDMI1), (10, 0x90))).Input);
    }

    [Fact]
    public void Silent_unchanged_and_automatic_tests()
    {
        Assert.Equal(Behaviour.Silent, Diagnosis.Analyze(DP1, Trace((3, DP1), (15, null), (2, DP1))).Behaviour);
        Assert.Equal(Behaviour.Unchanged, Diagnosis.Analyze(DP1, Trace((30, DP1))).Behaviour);
        // test automatique de HDMI 2 : l'écran y reste
        Assert.Equal(Behaviour.Stayed, Diagnosis.Analyze(DP1, Trace((8, HDMI2)), HDMI2).Behaviour);
        // il revient tout seul sur le PC au bout de 3 s : rien sur cette prise
        Assert.Equal(Behaviour.Returned, Diagnosis.Analyze(DP1, Trace((3, HDMI2), (5, DP1)), HDMI2).Behaviour);
    }
}
