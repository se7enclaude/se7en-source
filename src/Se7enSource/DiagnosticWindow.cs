using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Se7enSource.Core;
using Se7enSource.Services;

namespace Se7enSource;

/// <summary>
/// Diagnostic complet, pour « je ne trouve pas ma console » : état DDC/CI de chaque écran, test de chaque prise, test avec
/// les boutons de l'écran (l'appli relit l'entrée toutes les 0,5 s), conclusion en clair et rapport à copier.
/// </summary>
public sealed class DiagnosticWindow : Window
{
    private readonly Ddc _ddc;
    private readonly Settings _settings;
    private readonly IReadOnlyList<Services.Monitor> _monitors;
    private readonly Action<bool> _setBusy;
    private readonly Action _changed;
    private readonly StringBuilder _report = new();
    private readonly StackPanel _body = new();
    private readonly WrapPanel _screens = new();
    private Services.Monitor? _selected;
    private readonly Button _autoButton, _manualButton;
    private readonly TextBlock _status;
    private bool _running;

    public DiagnosticWindow(Window owner, Ddc ddc, Settings settings, IReadOnlyList<Services.Monitor> monitors, Action<bool> setBusy, Action changed)
    {
        Owner = owner;
        _ddc = ddc;
        _settings = settings;
        _monitors = monitors;
        _setBusy = setBusy;
        _changed = changed;
        Title = "se7en Source · Diagnostic";
        Width = 760;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res<Brush>("WindowBrush");
        FontFamily = Res<FontFamily>("FontSans");
        UseLayoutRounding = true;

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock { Text = "DIAGNOSTIC", Style = Res<Style>("TitleText") });
        head.Children.Add(Body("Pour comprendre pourquoi une console (ou un autre appareil) n'est pas trouvée. Lis la conclusion en bas " +
                               "de chaque test ; « Copier le rapport » donne tout le détail à envoyer.", 13));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // bas : écran testé, tests, rapport
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        // écran à tester : un bouton par écran qui répond (celui choisi est inversé, comme une entrée affichée)
        _selected = monitors.FirstOrDefault(x => x.Responds);
        foreach (var m in monitors.Where(x => x.Responds))
        {
            var b = new Button
            {
                Style = Res<Style>("TileButton"), Content = new TextBlock { Text = m.Title, FontFamily = Res<FontFamily>("FontMedium"), FontSize = 13 },
                Height = 44, Margin = new Thickness(0, 0, 8, 8), Tag = m == _selected ? "on" : null,
            };
            b.Click += (_, _) =>
            {
                if (_running) return;
                _selected = m;
                foreach (Button x in _screens.Children) x.Tag = null;
                b.Tag = "on";
            };
            _screens.Children.Add(b);
        }
        var pick = new StackPanel();
        pick.Children.Add(new TextBlock { Text = "ÉCRAN À TESTER", Style = Res<Style>("LabelText"), Margin = new Thickness(2, 0, 0, 8) });
        pick.Children.Add(_screens);
        bar.Children.Add(pick);
        bar.Children.Add(new Border { Width = 1 });
        _manualButton = Ghost("Test avec les boutons de l'écran (60 s)", async () => await ManualTest());
        _autoButton = Ghost("Tester chaque prise", async () => await AutoTest());
        var actions = new WrapPanel();
        actions.Children.Add(_manualButton);
        actions.Children.Add(_autoButton);
        actions.Children.Add(Ghost("Copier le rapport", () =>
        {
            try { Clipboard.SetText(_report.ToString()); Status("Rapport copié : colle-le dans un message (Ctrl+V)."); }
            catch (Exception) { Status("Impossible de copier : réessaie."); }
            return Task.CompletedTask;
        }));
        _status = new TextBlock { Style = Res<Style>("BodyText"), FontSize = 13, Foreground = Res<Brush>("TextPrimaryBrush"), Margin = new Thickness(2, 6, 0, 0) };
        var bottom = new StackPanel();
        bottom.Children.Add(bar);
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        if (_selected == null)
        {
            _autoButton.IsEnabled = _manualButton.IsEnabled = false;
        }
        Closing += (_, e) => { if (_running) e.Cancel = true; };
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        CheckScreens();
    }

    // ---------------------------------------------------------------- 1. écrans

    private void CheckScreens()
    {
        _report.AppendLine($"se7en Source {typeof(DiagnosticWindow).Assembly.GetName().Version?.ToString(3)} · {Environment.OSVersion.VersionString} · {DateTime.Now:dd/MM/yyyy HH:mm}");
        var card = Card("1 · Les écrans");
        if (_monitors.Count == 0) card.Children.Add(Body("Aucun écran trouvé par Windows.", 14));
        foreach (var m in _monitors)
        {
            _report.AppendLine();
            _report.AppendLine($"[{m.Title}] {m.Name} · Windows écran {m.Number}{(m.IsPrimary ? " (principal)" : "")} · {m.Device} · {m.Key}");
            _report.AppendLine($"  DDC/CI : {(m.Responds ? "répond" : "NE RÉPOND PAS")} · entrée affichée : {Port(m.Current)}");
            _report.AppendLine($"  prises annoncées : {(m.Inputs.Count > 0 ? string.Join(", ", m.Inputs.Select(i => $"{Inputs.PortName(i)} (0x{i:X2})")) : "aucune")}");
            _report.AppendLine($"  capacités : {m.Caps ?? "(aucune réponse)"}");

            string line;
            if (!m.Responds)
                line = $"✗ {m.Title} ({m.Name}) ne répond pas au PC. Active « DDC/CI » dans le menu de l'écran (boutons sous ou derrière " +
                       "l'écran), puis « Actualiser ». Si c'est déjà fait : la liaison bloque ces ordres (dock, adaptateur, câble HDMI " +
                       "vers USB-C, certains écrans en HDMI). Branche cet écran au PC en DisplayPort si possible. Sans ça, l'appli ne peut " +
                       "rien faire sur cet écran.";
            else if (m.Caps == null)
                line = $"! {m.Title} répond, mais n'annonce pas ses prises : seules celles vues en test apparaîtront. Il est sur {Port(m.Current)}.";
            else
                line = $"✓ {m.Title} ({m.Name}) répond. Il affiche {Port(m.Current)} (le PC). Prises : {string.Join(", ", m.Inputs.Select(Inputs.PortName))}.";
            card.Children.Add(Body(line, 14));
        }
        card.Children.Add(Body("Rappel : une console n'a qu'une sortie HDMI, elle ne peut être que sur une prise HDMI de l'écran.", 13, dim: true));
    }

    // ---------------------------------------------------------------- 2. test avec les boutons de l'écran

    private async Task ManualTest()
    {
        if (Selected() is not Services.Monitor m || _running) return;
        int pc = await Task.Run(() => _ddc.ReadRaw(m)) ?? m.Current ?? 0;
        var card = Card($"Test avec les boutons de l'écran · {m.Title}");
        card.Children.Add(Body("Pendant 60 secondes : avec les boutons de l'écran, choisis l'entrée de ta console, laisse-la affichée " +
                               "au moins 10 secondes, puis reviens sur le PC (boutons de l'écran, ou Ctrl+Alt+Maj+0).", 14));
        var live = Body("", 13, dim: true);
        card.Children.Add(live);
        List<Sample> samples;
        Running(true);
        try { samples = await Record(m, 60, s => live.Text = s); }
        finally { Running(false); }
        var v = Diagnosis.Analyze(pc, samples);
        Report($"Test avec les boutons ({m.Title}, PC sur {Port(pc)})", samples, v);
        card.Children.Add(Conclusion(m, pc, v, manual: true));
    }

    // ---------------------------------------------------------------- 3. test automatique de chaque prise

    private async Task AutoTest()
    {
        if (Selected() is not Services.Monitor m || _running) return;
        int pc = await Task.Run(() => _ddc.ReadRaw(m)) ?? m.Current ?? 0;
        var others = m.Inputs.Where(i => i != pc).ToList();
        if (others.Count == 0) { Status("Cet écran n'annonce pas d'autre prise que celle du PC."); return; }
        var ok = MessageBox.Show(this, $"{m.Title} va passer environ 8 secondes sur chaque autre prise ({string.Join(", ", others.Select(Inputs.PortName))}), " +
                                       $"puis revenir sur {Port(pc)}. Il va s'éteindre et se rallumer : c'est normal.\n\nAllume d'abord la console. On y va ?",
                                 "Tester chaque prise", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        var card = Card($"Test de chaque prise · {m.Title}");
        var live = Body("", 13, dim: true);
        card.Children.Add(live);
        var found = new List<int>();
        var results = new List<(int Input, Verdict V, bool Accepted)>();
        Running(true);
        try
        {
            foreach (int input in others)
            {
                live.Text = $"{Inputs.PortName(input)}…";
                bool accepted = await Task.Run(() => _ddc.Switch(m, input));
                var samples = accepted ? await Record(m, 8, s => live.Text = $"{Inputs.PortName(input)} · {s}") : [];
                var v = Diagnosis.Analyze(pc, samples, input);
                results.Add((input, v, accepted));
                Report($"Prise {Inputs.PortName(input)} (0x{input:X2}){(accepted ? "" : " : ordre refusé")}", samples, v);
                if (v.Behaviour == Behaviour.Stayed) found.Add(input);
            }
        }
        finally
        {
            await Task.Run(() => _ddc.Switch(m, pc));
            await Task.Delay(2500);
            int? back = await Task.Run(() => _ddc.ReadRaw(m));
            _report.AppendLine($"  retour sur {Port(pc)} : {(back == pc ? "confirmé" : $"NON confirmé (lu : {Port(back)})")}");
            Running(false);
        }

        live.Text = "";
        foreach (var (input, v, accepted) in results)
            card.Children.Add(Body($"{Inputs.PortName(input)} : " + (!accepted ? "l'écran refuse l'ordre de passer sur cette prise" : v.Behaviour switch
            {
                Behaviour.Stayed => "l'écran y reste → un appareil allumé y envoie une image ✓",
                Behaviour.Scanning => "l'écran cherche une image sur ses autres prises → rien ici",
                Behaviour.Silent => "l'écran ne répond plus au PC pendant qu'il y est → impossible de savoir",
                _ => "l'écran revient tout seul sur le PC → rien ici",
            }), 14));

        foreach (int input in found)
        {
            _settings.SetSignal(m.Key, input, Signal.Present);
            if (!_settings.HasCustomName(m.Key, input) && found.Count == 1) _settings.Rename(m.Key, input, "Console");
        }
        foreach (var (input, v, accepted) in results.Where(r => r.Accepted && r.V.Behaviour is Behaviour.Returned or Behaviour.Scanning))
            _settings.SetSignal(m.Key, input, Signal.Absent);
        _changed();

        if (found.Count > 0)
            card.Children.Add(Verdict($"Trouvé : un appareil envoie une image sur {string.Join(" et ", found.Select(Inputs.PortName))}." +
                                      (found.Count == 1 && _settings.NameOf(m.Key, found[0]) == "Console" ? " Je l'ai nommé « Console » (change le nom avec le crayon)." : ""), good: true));
        else if (results.All(r => r.Accepted && r.V.Behaviour == Behaviour.Silent))
            card.Children.Add(Verdict("L'écran ne répond plus au PC dès qu'il quitte sa prise : impossible de voir où est la console par ce " +
                                      "test. Fais plutôt le « Test avec les boutons de l'écran ».", good: false));
        else
            card.Children.Add(Verdict(NoImageAdvice(m), good: false));
    }

    // ---------------------------------------------------------------- relevé et conclusions

    private async Task<List<Sample>> Record(Services.Monitor m, int seconds, Action<string> progress)
    {
        var samples = new List<Sample>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            int? v = await Task.Run(() => _ddc.ReadRaw(m));
            samples.Add(new Sample(clock.Elapsed.TotalSeconds, v));
            progress($"{seconds - (int)clock.Elapsed.TotalSeconds} s · l'écran dit : {Port(v)}");
            await Task.Delay(500);
        }
        return samples;
    }

    /// <summary>Test en cours : boutons grisés, l'appli principale ne relit plus l'écran, fenêtre non fermable.</summary>
    private void Running(bool on)
    {
        _running = on;
        _setBusy(on);
        _autoButton.IsEnabled = _manualButton.IsEnabled = _screens.IsEnabled = !on;
    }

    private UIElement Conclusion(Services.Monitor m, int pc, Verdict v, bool manual)
    {
        switch (v.Behaviour)
        {
            case Behaviour.Stayed when v.Input is int found:
                _settings.SetSignal(m.Key, found, Signal.Present);
                if (!_settings.HasCustomName(m.Key, found)) _settings.Rename(m.Key, found, "Console");
                _changed();
                return Verdict($"Trouvé : l'écran est resté {v.Seconds:0} s sur la prise « {Inputs.PortName(found)} » (code 0x{found:X2}). " +
                               $"Elle est retenue sous le nom « {_settings.NameOf(m.Key, found)} » : un clic dessus (ou son raccourci) y passe.", good: true);
            case Behaviour.Scanning:
                return Verdict("L'écran a cherché une image sur ses prises sans en garder aucune : il ne reçoit RIEN de la console. " +
                               "Ce n'est pas l'appli : même ses boutons ne l'affichent pas.\n\n" + NoImageAdvice(m), good: false);
            case Behaviour.Silent:
                return Verdict($"L'écran a quitté le PC mais ne répondait plus pendant {v.Seconds:0} s : il affichait sûrement la console, " +
                               "mais cet écran ne dit rien au PC quand il n'est pas sur sa prise. Les clics de l'appli peuvent y aller ; " +
                               "pour revenir, les boutons de l'écran seront nécessaires. Lance aussi « Tester chaque prise » pour savoir laquelle.", good: false);
            case Behaviour.Unchanged:
                return Verdict($"L'écran est resté sur {Port(pc)} pendant tout le test : rien n'a été changé. Recommence en choisissant " +
                               "l'entrée de la console avec les boutons de l'écran pendant les 60 secondes.", good: false);
            default:
                return Verdict("L'écran a quitté le PC puis y est revenu tout seul sans rester ailleurs : il ne reçoit pas d'image de la console.\n\n" +
                               NoImageAdvice(m), good: false);
        }
    }

    private static string NoImageAdvice(Services.Monitor m) =>
        "À vérifier :\n" +
        "• La console est vraiment allumée : voyant BLANC fixe (orange = mode repos, aucune image).\n" +
        "• Le câble HDMI est bien enfoncé des deux côtés, sur une prise HDMI de " + (m.Title.StartsWith("Écran ") ? "l'é" + m.Title[1..] : m.Title) + ". Essaie le câble fourni avec la console, ou un autre.\n" +
        "• Une carte de capture (Elgato…) entre la console et l'écran ? La console envoie peut-être de la 4K que l'écran refuse " +
        "(écran 1080p/1440p). Branche la console directement sur l'écran, règle-la en 1440p ou 1080p (PS5 : Paramètres › Écran et vidéo › " +
        "Sortie vidéo › Résolution), puis remets la carte ; ou règle l'EDID de la carte sur celui de l'écran.\n" +
        "• PS5 : mode sans échec (bouton d'alimentation 7 s jusqu'au 2e bip, manette en USB) › « Changer la résolution vidéo ».\n" +
        "• Menu de l'écran : un réglage « Mode HDMI », « HDMI 2.0/2.1 » ou « Mode console » peut bloquer l'image.";

    private void Report(string title, List<Sample> samples, Verdict v)
    {
        _report.AppendLine();
        _report.AppendLine($"{title} → {v.Behaviour}{(v.Input is int i ? $" sur 0x{i:X2}" : "")}");
        foreach (var r in Diagnosis.Runs(samples))
            _report.AppendLine($"  {r.Start,5:0.0}–{r.Start + r.Length,5:0.0} s  {Port(r.Input)}");
    }

    private static string Port(int? input) => input is int i ? $"{Inputs.PortName(i)} (0x{i:X2})" : "pas de réponse";

    private Services.Monitor? Selected() => _selected;

    private void Status(string text) => _status.Text = text;

    // ---------------------------------------------------------------- éléments

    private StackPanel Card(string title)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), Style = Res<Style>("LabelText"), FontSize = 13, Foreground = Res<Brush>("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        _body.Children.Add(new Border { Style = Res<Style>("Panel"), Margin = new Thickness(0, 0, 0, 12), Child = stack });
        stack.Loaded += (_, _) => stack.BringIntoView();
        return stack;
    }

    private TextBlock Body(string text, double size, bool dim = false) => new()
    {
        Text = text, Style = Res<Style>("BodyText"), FontSize = size, Margin = new Thickness(0, 0, 0, 8),
        Foreground = dim ? Res<Brush>("TextTertiaryBrush") : Res<Brush>("TextSecondaryBrush"),
    };

    private Border Verdict(string text, bool good) => new()
    {
        Background = Res<Brush>("RaisedBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12),
        Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1),
        BorderBrush = good ? Res<Brush>("GreenBrush") : Res<Brush>("AmberBrush"),
        Child = new TextBlock { Text = text, Style = Res<Style>("BodyText"), FontSize = 14, Foreground = Res<Brush>("TextPrimaryBrush") },
    };

    private Button Ghost(string text, Func<Task> action)
    {
        var b = new Button { Style = Res<Style>("GhostButton"), Content = text, Margin = new Thickness(0, 0, 10, 8) };
        b.Click += async (_, _) => await action();
        return b;
    }

    private T Res<T>(string key) => (T)FindResource(key);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
