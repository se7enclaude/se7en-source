using System.Text.RegularExpressions;

namespace Se7enSource.Core;

/// <summary>
/// Entrées d'un écran, d'après sa « chaîne de capacités » DDC/CI : <c>vcp(... 60(0F 10 11 12) ...)</c> liste les valeurs
/// acceptées par le code 0x60 (source d'entrée). Les numéros suivent la norme MCCS (0x0F = DisplayPort 1, 0x11 = HDMI 1…).
/// </summary>
public static partial class Inputs
{
    /// <summary>Code VCP de la source d'entrée.</summary>
    public const byte InputSourceCode = 0x60;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x01] = "VGA 1", [0x02] = "VGA 2", [0x03] = "DVI 1", [0x04] = "DVI 2",
        [0x05] = "Composite 1", [0x06] = "Composite 2", [0x07] = "S-Vidéo 1", [0x08] = "S-Vidéo 2",
        [0x09] = "Tuner 1", [0x0A] = "Tuner 2", [0x0B] = "Tuner 3", [0x0C] = "Composante 1",
        [0x0D] = "Composante 2", [0x0E] = "Composante 3", [0x0F] = "DisplayPort 1", [0x10] = "DisplayPort 2",
        [0x11] = "HDMI 1", [0x12] = "HDMI 2", [0x13] = "HDMI 3", [0x14] = "HDMI 4",
        [0x15] = "Thunderbolt", [0x1B] = "USB-C", [0x19] = "USB-C 2",
    };

    /// <summary>Nom de la prise pour une valeur MCCS (« HDMI 1 »), ou « Entrée 0x.. » si elle n'est pas standard.</summary>
    public static string PortName(int value) =>
        Names.TryGetValue(value & 0xFF, out var n) ? n : $"Entrée 0x{value:X2}";

    /// <summary>Type de prise, pour l'icône : « hdmi », « dp », « usbc » ou « other ».</summary>
    public static string PortKind(int value) => (value & 0xFF) switch
    {
        >= 0x11 and <= 0x14 => "hdmi",
        0x0F or 0x10 => "dp",
        0x1B or 0x19 or 0x15 => "usbc",
        _ => "other",
    };

    /// <summary>Valeurs acceptées pour la source d'entrée, dans l'ordre de la chaîne de capacités (vide si absentes).</summary>
    public static IReadOnlyList<int> FromCapabilities(string? caps)
    {
        if (string.IsNullOrEmpty(caps)) return [];
        var vcp = VcpBlock().Match(caps);
        if (!vcp.Success) return [];
        // dans le bloc vcp(...), « 60( » au niveau 0 : on lit jusqu'à la parenthèse fermante
        string body = vcp.Groups[1].Value;
        int depth = 0;
        for (int i = 0; i + 2 < body.Length; i++)
        {
            char c = body[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && (i == 0 || body[i - 1] == ' ') && string.Compare(body, i, "60(", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int end = body.IndexOf(')', i + 3);
                if (end < 0) return [];
                var list = new List<int>();
                foreach (var tok in body[(i + 3)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok, System.Globalization.NumberStyles.HexNumber, null, out int v) && v > 0 && !list.Contains(v))
                        list.Add(v);
                return list;
            }
        }
        return [];
    }

    /// <summary>Modèle annoncé par l'écran (<c>model(AG276QZD2)</c>), ou null.</summary>
    public static string? Model(string? caps)
    {
        if (string.IsNullOrEmpty(caps)) return null;
        var m = ModelField().Match(caps);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // vcp( ... ) avec des parenthèses imbriquées sur un niveau ou plus : on prend jusqu'à la parenthèse qui ferme vcp(
    private static Regex VcpBlock() => VcpRegex();

    [GeneratedRegex(@"vcp\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex VcpRegex();

    [GeneratedRegex(@"model\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ModelField();
}
