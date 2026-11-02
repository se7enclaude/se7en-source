using Se7enSource.Core;

namespace Se7enSource.Tests;

public class InputsTests
{
    // chaîne réelle d'un AOC AG276QZD2 (raccourcie), lue sur le PC de l'utilisateur
    private const string Aoc = "(prot(monitor)type(LCD)model(AG276QZD2)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 0C 10 12 " +
        "14(05 06 08 0B) 16 18 1A 52 60(0F 10 11 12 ) 62 86(02 23 05 01 0B 0C 0D 0E 0F 10 11 12 13) 8D AC AE B2 B6 C0 C6 C8 " +
        "CA CC(01 02 03 04 05 06 07 09 0A 0B 0C 0D 0E 12 14 16 1E 24 ) D6(01 04 05) DC(00 0B 0C 0D 0E 0F 10) DF " +
        "E2A006(00 01 02 03) F8E161(00 01 02 03) FF)mswhql(1)asset_eep(40)mccs_ver(2.2))";

    [Fact]
    public void Reads_the_inputs_of_a_real_monitor()
    {
        Assert.Equal([0x0F, 0x10, 0x11, 0x12], Inputs.FromCapabilities(Aoc));
        Assert.Equal("AG276QZD2", Inputs.Model(Aoc));
        Assert.Equal(["DisplayPort 1", "DisplayPort 2", "HDMI 1", "HDMI 2"], Inputs.FromCapabilities(Aoc).Select(Inputs.PortName));
    }

    [Theory]
    [InlineData("vcp(10 12 60(11 12 1B) 62)", new[] { 0x11, 0x12, 0x1B })]
    [InlineData("vcp(60( 01 03 0f ))", new[] { 0x01, 0x03, 0x0F })]  // minuscules et espaces
    [InlineData("vcp(14(05 60) 60(11))", new[] { 0x11 })]             // « 60 » dans une autre liste : ignoré
    [InlineData("vcp(10 12 62)", new int[0])]                        // pas de source d'entrée annoncée
    [InlineData("", new int[0])]
    public void Parses_input_lists(string caps, int[] expected) =>
        Assert.Equal(expected, Inputs.FromCapabilities(caps));

    [Fact]
    public void Unknown_inputs_still_have_a_name()
    {
        Assert.Equal("Entrée 0x31", Inputs.PortName(0x31));
        Assert.Equal("hdmi", Inputs.PortKind(0x11));
        Assert.Equal("dp", Inputs.PortKind(0x0F));
        Assert.Equal("HDMI 1", Inputs.PortName(0x0111)); // octet haut ignoré (certains écrans le remplissent)
    }
}

public class SettingsTests
{
    [Fact]
    public void Names_and_back_toggle()
    {
        var s = new Settings();
        Assert.Equal("HDMI 1", s.NameOf("m", 0x11));
        s.Rename("m", 0x11, "  PS5 ");
        Assert.Equal("PS5", s.NameOf("m", 0x11));
        s.Rename("m", 0x11, "");
        Assert.False(s.HasCustomName("m", 0x11));

        Assert.Null(s.Previous("m", 0x0F));
        s.Remember("m", 0x0F, 0x11);      // PC -> PS5
        Assert.Equal(0x0F, s.Previous("m", 0x11));
        s.Remember("m", 0x11, 0x0F);      // retour au PC
        Assert.Equal(0x11, s.Previous("m", 0x0F));
    }

    [Fact]
    public void Saves_and_loads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"se7en-source-{Guid.NewGuid():N}", "settings.json");
        var s = new Settings();
        s.Rename("m", 0x12, "PS5");
        s.Remember("m", 0x0F, 0x12);
        s.Save(path);
        var t = Settings.Load(path);
        Assert.Equal("PS5", t.NameOf("m", 0x12));
        Assert.Equal(0x0F, t.Previous("m", 0x12));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        Assert.NotNull(Settings.Load(path)); // fichier absent : réglages vides
    }
}
