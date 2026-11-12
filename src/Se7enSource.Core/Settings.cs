using System.Text.Json;

namespace Se7enSource.Core;

/// <summary>Réglages : nom donné à chaque entrée (« PS5 », « PC »), par écran. Fichier JSON dans %AppData%\Se7enSource.</summary>
public sealed class Settings
{
    /// <summary>Par écran (clé stable, voir Monitor.Key) : valeur MCCS de l'entrée -> nom choisi.</summary>
    public Dictionary<string, Dictionary<int, string>> Names { get; set; } = [];

    /// <summary>Par écran : les deux dernières entrées choisies (pour la touche « revenir »).</summary>
    public Dictionary<string, List<int>> Recent { get; set; } = [];

    /// <summary>Par écran : ce que la dernière détection a vu sur chaque prise.</summary>
    public Dictionary<string, Dictionary<int, Signal>> Signals { get; set; } = [];

    /// <summary>Raccourcis Ctrl+Alt+Maj+1…9 : numéro -> écran et entrée. Gardés d'une fois sur l'autre, même si un écran ne répond pas.</summary>
    public Dictionary<int, Slot> Slots { get; set; } = [];

    public const int SlotCount = 9;

    public Signal SignalOf(string monitor, int input) =>
        Signals.TryGetValue(monitor, out var d) && d.TryGetValue(input, out var s) ? s : Signal.Unknown;

    public void SetSignal(string monitor, int input, Signal signal)
    {
        if (!Signals.TryGetValue(monitor, out var d)) Signals[monitor] = d = [];
        d[input] = signal;
    }

    public string NameOf(string monitor, int input) =>
        Names.TryGetValue(monitor, out var d) && d.TryGetValue(input, out var n) && !string.IsNullOrWhiteSpace(n) ? n : Inputs.PortName(input);

    public bool HasCustomName(string monitor, int input) =>
        Names.TryGetValue(monitor, out var d) && d.TryGetValue(input, out var n) && !string.IsNullOrWhiteSpace(n);

    public void Rename(string monitor, int input, string? name)
    {
        if (!Names.TryGetValue(monitor, out var d)) Names[monitor] = d = [];
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name == Inputs.PortName(input)) d.Remove(input);
        else d[input] = name.Length > 24 ? name[..24] : name;
    }

    /// <summary>Note qu'on vient de passer de <paramref name="from"/> à <paramref name="to"/>.</summary>
    public void Remember(string monitor, int from, int to)
    {
        if (from == to) return;
        Recent[monitor] = [to, from];
    }

    /// <summary>L'entrée où revenir (l'avant-dernière choisie), ou null.</summary>
    public int? Previous(string monitor, int current)
    {
        if (!Recent.TryGetValue(monitor, out var r) || r.Count < 2) return null;
        int other = r[0] == current ? r[1] : r[0];
        return other == current ? null : other;
    }

    /// <summary>Entrées connues d'un écran en dehors de ses capacités : nommées, ou où un appareil a été vu.</summary>
    public IReadOnlyList<int> KnownInputs(string monitor)
    {
        var list = new List<int>();
        if (Names.TryGetValue(monitor, out var names))
            list.AddRange(names.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key));
        if (Signals.TryGetValue(monitor, out var signals))
            list.AddRange(signals.Where(kv => kv.Value == Signal.Present).Select(kv => kv.Key));
        return [.. list.Where(i => i > 0).Distinct().Order()];
    }

    /// <summary>Numéro du raccourci d'une entrée (1 à 9), ou null.</summary>
    public int? SlotOf(string monitor, int input)
    {
        foreach (var (n, s) in Slots)
            if (s.Monitor == monitor && s.Input == input) return n;
        return null;
    }

    /// <summary>
    /// Donne un numéro de raccourci à chaque entrée disponible qui n'en a pas encore, sans toucher aux numéros déjà donnés
    /// (un écran muet ne décale plus ceux des autres). Plus de numéro libre : on reprend celui d'une entrée absente.
    /// Renvoie true si quelque chose a changé (à enregistrer).
    /// </summary>
    public bool AssignSlots(IEnumerable<(string Monitor, int Input)> available)
    {
        bool changed = false;
        foreach (int bad in Slots.Where(kv => kv.Key < 1 || kv.Key > SlotCount || kv.Value == null || string.IsNullOrEmpty(kv.Value.Monitor))
                                 .Select(kv => kv.Key).ToList())
        {
            Slots.Remove(bad);
            changed = true;
        }
        var avail = available.Distinct().ToList();
        foreach (var (monitor, input) in avail)
        {
            if (SlotOf(monitor, input) != null) continue;
            int free = Enumerable.Range(1, SlotCount).FirstOrDefault(n => !Slots.ContainsKey(n));
            if (free == 0)
                free = Enumerable.Range(1, SlotCount).FirstOrDefault(n => !avail.Contains((Slots[n].Monitor, Slots[n].Input)));
            if (free == 0) break;
            Slots[free] = new Slot { Monitor = monitor, Input = input };
            changed = true;
        }
        return changed;
    }

    /// <summary>Un fichier modifié à la main peut contenir « null » : on remet des listes vides.</summary>
    public Settings Normalize()
    {
        Names = Clean(Names);
        Recent = Clean(Recent);
        Signals = Clean(Signals);
        Slots ??= [];
        foreach (int bad in Slots.Where(kv => kv.Value == null || string.IsNullOrEmpty(kv.Value.Monitor)).Select(kv => kv.Key).ToList())
            Slots.Remove(bad);
        foreach (var (key, names) in Names)
            foreach (int bad in names.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) names.Remove(bad);
        return this;
    }

    private static Dictionary<string, T> Clean<T>(Dictionary<string, T>? d) where T : class
    {
        if (d == null) return [];
        foreach (var key in d.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Remove(key);
        return d;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new()).Normalize();
        }
        catch (Exception) { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

/// <summary>Un raccourci : l'écran (clé stable) et l'entrée.</summary>
public sealed class Slot
{
    public string Monitor { get; set; } = "";
    public int Input { get; set; }
}
