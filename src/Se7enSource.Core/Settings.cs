using System.Text.Json;

namespace Se7enSource.Core;

/// <summary>Réglages : nom donné à chaque entrée (« PS5 », « PC »), par écran. Fichier JSON dans %AppData%\Se7enSource.</summary>
public sealed class Settings
{
    /// <summary>Par écran (clé stable, voir Monitor.Key) : valeur MCCS de l'entrée -> nom choisi.</summary>
    public Dictionary<string, Dictionary<int, string>> Names { get; set; } = [];

    /// <summary>Par écran : les deux dernières entrées choisies (pour la touche « revenir »).</summary>
    public Dictionary<string, List<int>> Recent { get; set; } = [];

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

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new();
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
