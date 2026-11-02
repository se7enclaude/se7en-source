namespace Se7enSource.Core;

/// <summary>Où est chaque écran sur le bureau : « Écran de gauche » parle plus que « Écran 1 ».</summary>
public static class Layout
{
    /// <summary>
    /// Noms de position, dans l'ordre reçu : « de gauche », « de droite », « du milieu », « du haut », « du bas »,
    /// ou « n° 3 en partant de la gauche » au-delà de trois. Vide quand il n'y a qu'un écran.
    /// </summary>
    public static IReadOnlyList<string> PositionNames(IReadOnlyList<(int Left, int Top, int Width, int Height)> screens)
    {
        int n = screens.Count;
        var names = new string[n];
        if (n <= 1) return n == 1 ? [""] : [];

        double Cx(int i) => screens[i].Left + screens[i].Width / 2.0;
        double Cy(int i) => screens[i].Top + screens[i].Height / 2.0;
        // empilés (l'un au-dessus de l'autre) si les centres sont alignés en largeur
        bool stacked = Enumerable.Range(0, n).All(i => Math.Abs(Cx(i) - Cx(0)) < Math.Min(screens[i].Width, screens[0].Width) / 2.0);
        var order = Enumerable.Range(0, n).OrderBy(i => stacked ? Cy(i) : Cx(i)).ToList();
        for (int rank = 0; rank < n; rank++)
        {
            int i = order[rank];
            names[i] = (stacked, n, rank) switch
            {
                (false, _, 0) when n <= 3 => "de gauche",
                (false, _, _) when n <= 3 && rank == n - 1 => "de droite",
                (false, 3, 1) => "du milieu",
                (true, 2, 0) => "du haut",
                (true, 2, 1) => "du bas",
                (true, _, _) => $"n° {rank + 1} en partant du haut",
                _ => $"n° {rank + 1} en partant de la gauche",
            };
        }
        return names;
    }
}

/// <summary>Ce qu'on sait d'une prise après la détection.</summary>
public enum Signal { Unknown, Present, Absent }

/// <summary>
/// Détection des appareils branchés : l'écran ne dit pas quelles prises ont un signal (DDC/CI ne donne que l'entrée
/// affichée). On le fait passer sur chaque prise : un écran qui « cherche » revient tout seul sur une prise vivante
/// quand il n'y a rien, et reste là où un appareil allumé envoie une image.
/// </summary>
public static class Detection
{
    /// <param name="pc">L'entrée du PC (elle a forcément un signal).</param>
    /// <param name="stayed">Pour chaque autre prise essayée : l'écran y était-il encore après quelques secondes ?</param>
    public static Dictionary<int, Signal> Interpret(int pc, IReadOnlyDictionary<int, bool> stayed)
    {
        var result = new Dictionary<int, Signal> { [pc] = Signal.Present };
        // si l'écran n'est jamais revenu tout seul, rester ne prouve rien : il peut aussi rester sur « pas de signal »
        bool returnsByItself = stayed.Values.Any(s => !s);
        foreach (var (input, s) in stayed)
            if (input != pc)
                result[input] = !s ? Signal.Absent : returnsByItself ? Signal.Present : Signal.Unknown;
        return result;
    }
}
