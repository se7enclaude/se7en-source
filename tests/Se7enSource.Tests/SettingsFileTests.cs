using Se7enSource.Core;

namespace Se7enSource.Tests;

public class SettingsFileTests
{
    [Fact]
    public void A_file_with_null_lists_still_loads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"se7en-source-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Names": null, "Recent": { "A": null }, "Signals": null, "Slots": { "1": null } }""");
            var s = Settings.Load(path);
            Assert.Equal("HDMI 1", s.NameOf("A", 0x11));
            Assert.Null(s.Previous("A", 0x11));
            Assert.Equal(Signal.Unknown, s.SignalOf("A", 0x11));
            Assert.Empty(s.Slots);
            s.Rename("A", 0x11, "PS5");
            s.Remember("A", 0x0F, 0x11);
            Assert.Equal("PS5", s.NameOf("A", 0x11));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_older_file_without_shortcuts_gets_the_same_numbers_as_before()
    {
        string path = Path.Combine(Path.GetTempPath(), $"se7en-source-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Names": { "A": { "15": "Ce PC" } } }""");
            var s = Settings.Load(path);
            Assert.True(s.AssignSlots([("A", 0x0F), ("A", 0x11), ("B", 0x0F)]));
            Assert.Equal(1, s.SlotOf("A", 0x0F));
            Assert.Equal(2, s.SlotOf("A", 0x11));
            Assert.Equal(3, s.SlotOf("B", 0x0F));
            s.Save(path);
            Assert.Equal(3, Settings.Load(path).SlotOf("B", 0x0F));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void A_silent_monitor_does_not_shift_the_shortcuts_of_the_others()
    {
        var s = new Settings();
        s.AssignSlots([("A", 0x0F), ("A", 0x11), ("B", 0x0F), ("B", 0x11)]);
        // l'écran A ne répond plus : B garde ses numéros
        Assert.False(s.AssignSlots([("B", 0x0F), ("B", 0x11)]));
        Assert.Equal(3, s.SlotOf("B", 0x0F));
        Assert.Equal(4, s.SlotOf("B", 0x11));
        // une prise apprise plus tard prend le premier numéro libre
        Assert.True(s.AssignSlots([("A", 0x0F), ("A", 0x11), ("A", 0x12), ("B", 0x0F), ("B", 0x11)]));
        Assert.Equal(5, s.SlotOf("A", 0x12));
    }

    [Fact]
    public void Known_inputs_are_the_named_ones_and_those_where_a_device_was_seen()
    {
        var s = new Settings();
        s.Rename("A", 0x11, "PS5");
        s.SetSignal("A", 0x13, Signal.Present);
        s.SetSignal("A", 0x12, Signal.Absent);
        Assert.Equal([0x11, 0x13], s.KnownInputs("A"));
        Assert.Empty(s.KnownInputs("B"));
    }
}
