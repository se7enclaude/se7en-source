namespace Se7enSource.Core;

/// <summary>Ce qu'a fait l'écran pendant qu'on l'observait.</summary>
public enum Behaviour
{
    /// <summary>Il est resté sur une autre prise que celle du PC : un appareil y envoie une image.</summary>
    Stayed,
    /// <summary>Il a parcouru ses prises les unes après les autres : il cherche une image et n'en trouve pas.</summary>
    Scanning,
    /// <summary>Il est revenu sur le PC (ou n'en est pas parti) sans s'arrêter ailleurs.</summary>
    Returned,
    /// <summary>Il ne répondait plus au PC : il affiche autre chose mais ne dit pas quoi.</summary>
    Silent,
    /// <summary>Il n'a jamais quitté la prise du PC (rien n'a été changé).</summary>
    Unchanged,
}

/// <summary>Un relevé : instant (secondes) et entrée que l'écran dit afficher (null : pas de réponse).</summary>
public readonly record struct Sample(double Time, int? Input);

public sealed record Verdict(Behaviour Behaviour, int? Input, double Seconds);

/// <summary>
/// Diagnostic : on relit l'entrée de l'écran toutes les demi-secondes pendant qu'il change de prise, et on en tire ce qui
/// se passe. C'est ce relevé qui a montré, chez le premier utilisateur, que l'écran « cherchait » sans jamais garder la
/// console (image 4K refusée par un écran 1440p, via une carte de capture).
/// </summary>
public static class Diagnosis
{
    /// <summary>Durée minimale sur une prise pour dire qu'il y « reste » (un écran qui cherche passe 1 à 4 s par prise).</summary>
    public const double StaySeconds = 5;

    /// <summary>Morceaux consécutifs de même valeur : (entrée, début, durée).</summary>
    public static List<(int? Input, double Start, double Length)> Runs(IReadOnlyList<Sample> samples)
    {
        var runs = new List<(int? Input, double Start, double Length)>();
        for (int i = 0; i < samples.Count; i++)
        {
            double end = i + 1 < samples.Count ? samples[i + 1].Time : samples[i].Time + 0.5;
            if (runs.Count > 0 && runs[^1].Input == samples[i].Input)
                runs[^1] = (runs[^1].Input, runs[^1].Start, end - runs[^1].Start);
            else
                runs.Add((samples[i].Input, samples[i].Time, end - samples[i].Time));
        }
        return runs;
    }

    /// <param name="pc">Prise du PC.</param>
    /// <param name="target">Prise visée par un test automatique (null : test avec les boutons de l'écran).</param>
    public static Verdict Analyze(int pc, IReadOnlyList<Sample> samples, int? target = null)
    {
        var runs = Runs(samples);
        if (runs.Count == 0) return new Verdict(Behaviour.Silent, null, 0);

        // une autre prise gardée assez longtemps : la plus longue
        var stay = runs.Where(r => r.Input is int v && v != pc && (target == null || v == target) && r.Length >= StaySeconds)
                       .OrderByDescending(r => r.Length).FirstOrDefault();
        if (stay.Input is int found) return new Verdict(Behaviour.Stayed, found, stay.Length);

        var others = runs.Where(r => r.Input is int v && v != pc).ToList();
        // plusieurs prises différentes, chacune brièvement : l'écran cherche
        if (others.Select(r => r.Input).Distinct().Count() >= 2 || others.Count >= 3)
            return new Verdict(Behaviour.Scanning, null, others.Sum(r => r.Length));

        var silent = runs.Where(r => r.Input == null).OrderByDescending(r => r.Length).FirstOrDefault();
        if (silent.Length >= StaySeconds) return new Verdict(Behaviour.Silent, null, silent.Length);

        if (others.Count > 0 || runs.Any(r => r.Input == null)) return new Verdict(Behaviour.Returned, null, 0);
        return new Verdict(Behaviour.Unchanged, null, 0);
    }
}
