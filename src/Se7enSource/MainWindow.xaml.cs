using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Se7enSource.Core;
using Se7enSource.Services;
using Forms = System.Windows.Forms;

namespace Se7enSource;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Se7enSource", "settings.json");

    private readonly Ddc _ddc = new();
    private readonly Settings _settings = Settings.Load(SettingsPath);
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _toastTimer;
    private GlobalHotkeys? _hotkeys;
    private IReadOnlyList<Services.Monitor> _monitors = [];
    // raccourcis Ctrl+Alt+Maj+1…9 enregistrés en ce moment : numéro -> écran et entrée (numéros gardés dans les réglages)
    private readonly Dictionary<int, (string Monitor, int Input)> _slots = [];
    private string? _lastMonitor;   // dernier écran changé (pour « revenir à l'entrée d'avant »)
    private bool _switching;        // un ordre de changement d'entrée est en cours (l'écran met ~1 s à répondre)
    private string? _operation;     // séquence longue en cours (aller-retour, détection, test du diagnostic) : les autres ordres attendent
    private bool _watching;         // test « avec les boutons de l'écran » du diagnostic : relevé arrêté, raccourcis actifs
    private bool _polling, _loading;
    private readonly DispatcherTimer _reload; // nouvelle recherche des écrans après un changement d'affichage ou une sortie de veille
    private DiagnosticWindow? _diagnostic;
    private bool _exiting, _trayHintShown;
    private (string Key, int Input)? _renaming;
    private readonly HashSet<string> _showAll = [];
    // apprentissage : entrée attendue par écran, entrée inattendue vue N fois de suite, dernier ordre envoyé
    private readonly Dictionary<string, int> _expected = [];
    private readonly Dictionary<string, (int Input, int Count)> _pending = [];
    private DateTime _lastSwitchAt = DateTime.MinValue;
    private bool _openDiagnostic;   // écrans dont on montre aussi les prises vides

    public MainWindow(bool startMinimized, bool diagnostic = false)
    {
        _openDiagnostic = diagnostic;
        InitializeComponent();
        AutostartBox.IsChecked = Autostart.IsEnabled;

        _tray = new Forms.NotifyIcon { Text = "se7en Source", Icon = LoadTrayIcon(), Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        RebuildTrayMenu();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText.Visibility = Visibility.Collapsed; };

        // l'entrée peut aussi changer depuis les boutons de l'écran : on la relit (moins souvent quand la fenêtre est cachée)
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _poll.Tick += async (_, _) => await PollCurrent();

        // écran rebranché, résolution changée, sortie de veille : les poignées DDC/CI ne valent plus rien, on recherche les écrans
        _reload = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reload.Tick += async (_, _) => await ReloadAfterChange();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ddc.Reenumerated += () => Dispatcher.BeginInvoke(SyncMonitors);

        SourceInitialized += async (_, _) => await LoadMonitors();
        IsVisibleChanged += (_, _) => _poll.Interval = TimeSpan.FromSeconds(IsVisible ? 3 : 15);
        _poll.Start();
        Closing += OnClosing;

        if (startMinimized) new WindowInteropHelper(this).EnsureHandle(); // raccourcis actifs, fenêtre cachée
        else Show();
    }

    // ---------------------------------------------------------------- écrans

    private bool Locked => _switching || _operation != null;

    private async Task LoadMonitors(bool rereadCaps = false)
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            _ddc.SetKnownInputs(KnownInputs());
            _monitors = await Task.Run(() => _ddc.Refresh(rereadCaps));
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
        // l'entrée affichée au lancement est celle du PC : on la nomme tout de suite (si rien n'est encore nommé)
        foreach (var m in _monitors.Where(x => x.Responds && x.Current is int))
            if (!m.Inputs.Any(i => _settings.HasCustomName(m.Key, i)))
            {
                _settings.Rename(m.Key, m.Current!.Value, "Ce PC");
                _settings.SetSignal(m.Key, m.Current!.Value, Signal.Present);
                Save();
            }
        AfterMonitorsChanged();
        if (_openDiagnostic) // « se7en Source.exe --diagnostic » : ouvre directement le diagnostic (dépannage)
        {
            _openDiagnostic = false;
            OnDiagnostic(this, new RoutedEventArgs());
        }
    }

    private void AfterMonitorsChanged()
    {
        RegisterHotkeys();
        BuildUi();
        RebuildTrayMenu();
    }

    /// <summary>Un ordre a dû rechercher les écrans (Windows les avait perdus) : on reprend les nouveaux objets.</summary>
    private void SyncMonitors()
    {
        if (_loading || _exiting) return; // la recherche en cours donnera la liste à jour
        _monitors = _ddc.Monitors;
        AfterMonitorsChanged();
    }

    /// <summary>Prises retenues dans les réglages, par écran : elles apparaissent même si l'écran ne les annonce pas.</summary>
    private Dictionary<string, IReadOnlyList<int>> KnownInputs() =>
        _settings.Names.Keys.Concat(_settings.Signals.Keys).Distinct().ToDictionary(k => k, _settings.KnownInputs);

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ScheduleReload);

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(ScheduleReload);
    }

    private void ScheduleReload()
    {
        if (_exiting) return;
        _reload.Stop(); // plusieurs événements de suite : une seule recherche, 2 s après le dernier
        _reload.Start();
    }

    private async Task ReloadAfterChange()
    {
        _reload.Stop();
        if (_exiting) return;
        if (Locked || _watching || _loading) { _reload.Start(); return; } // après l'opération en cours
        await LoadMonitors();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (Locked || _watching) { Toast(Wait()); return; }
        MonitorsPanel.Children.Clear();
        MonitorsPanel.Children.Add(LoadingText);
        await LoadMonitors(rereadCaps: true);
    }

    private async Task PollCurrent()
    {
        if (_polling || _loading || Locked || _watching || _renaming != null) return;
        var live = _monitors.Where(m => m.Responds).ToList();
        if (live.Count == 0) return;
        _polling = true; // un relevé lent (écran qui tarde) ne doit pas se superposer au suivant
        try
        {
            var before = live.Select(m => m.Current).ToList();
            var inputsBefore = live.Select(m => m.Inputs.Count).ToList();
            await Task.Run(() => { foreach (var m in live) _ddc.ReadCurrent(m); });
            if (Locked || _watching || _loading) return; // un ordre est parti pendant le relevé : l'apprentissage attendra
            bool learned = live.Aggregate(false, (any, m) => Learn(m) | any);
            if (!inputsBefore.SequenceEqual(live.Select(m => m.Inputs.Count))) RegisterHotkeys();
            if (learned || !before.SequenceEqual(live.Select(m => m.Current)))
            {
                BuildUi();
                RebuildTrayMenu();
            }
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// Apprentissage : l'écran affiche une autre entrée que celle demandée par l'appli (ses boutons, ou il a basculé
    /// tout seul sur la console qui s'allume) et y reste : un appareil envoie une image sur cette prise. On la note,
    /// avec le code exact que l'écran donne pour elle (certains écrans numérotent leurs prises à leur façon).
    /// </summary>
    private bool Learn(Services.Monitor m)
    {
        if (m.Current is not int cur) return false;
        // juste après un ordre de l'appli, l'écran peut parcourir ses prises en cherchant une image : on attend
        if (DateTime.UtcNow - _lastSwitchAt < TimeSpan.FromSeconds(25) || !_expected.TryGetValue(m.Key, out int expected))
        {
            _expected[m.Key] = cur;
            _pending.Remove(m.Key);
            return false;
        }
        if (cur == expected) { _pending.Remove(m.Key); return false; }
        int seen = _pending.TryGetValue(m.Key, out var p) && p.Input == cur ? p.Count + 1 : 1;
        _pending[m.Key] = (cur, seen);
        if (seen < 3) return false; // il y reste (une dizaine de secondes) : ce n'est pas un passage en cherchant
        _pending.Remove(m.Key);
        _expected[m.Key] = cur;
        _settings.Remember(m.Key, expected, cur); // Ctrl+Alt+Maj+0 ramène sur l'entrée d'avant
        _lastMonitor = m.Key;
        bool known = _settings.SignalOf(m.Key, cur) == Signal.Present || _settings.HasCustomName(m.Key, cur);
        _settings.SetSignal(m.Key, cur, Signal.Present);
        Save();
        AddInput(m, cur);
        if (!known)
            Toast($"Appareil trouvé sur {The(m)} : il envoie une image sur la prise « {Inputs.PortName(cur)} ». " +
                  "Clique sur son crayon pour lui donner un nom (ex. PS5).", 15);
        return true;
    }

    /// <summary>Une prise vue sur l'écran mais absente de ses capacités : on l'ajoute à ses tuiles.</summary>
    private void AddInput(Services.Monitor m, int input)
    {
        if (m.Inputs.Contains(input)) return;
        m.Inputs = [.. m.Inputs, input];
        var same = _ddc.Monitors.FirstOrDefault(x => x.Key == m.Key);
        if (same != null && same != m && !same.Inputs.Contains(input)) same.Inputs = [.. same.Inputs, input];
        _ddc.SetKnownInputs(KnownInputs());
    }

    private string Wait() => _watching && _operation == null && !_switching
        ? "Patiente : un test du diagnostic est en cours."
        : $"Patiente : {_operation ?? "un changement d'entrée"} est en cours.";

    /// <summary>Prend le verrou pour une séquence longue : aucun autre ordre ne passe tant qu'elle dure.</summary>
    private bool BeginOperation(string what)
    {
        if (Locked || _watching || _loading)
        {
            Toast(_loading && !Locked && !_watching ? "Patiente : recherche des écrans en cours." : Wait());
            return false;
        }
        _operation = what;
        BuildUi();
        return true;
    }

    private void EndOperation()
    {
        _operation = null;
        BuildUi();
        RebuildTrayMenu();
    }

    private bool CanStartOperation()
    {
        if (!Locked && !_watching && !_loading) return true;
        Toast(_loading && !Locked && !_watching ? "Patiente : recherche des écrans en cours." : Wait());
        return false;
    }

    private void SwitchTo(Services.Monitor m, int input, bool quiet = false) => _ = SwitchToAsync(m, input, quiet);

    /// <param name="owned">Appelé par la séquence qui tient le verrou (aller-retour, détection).</param>
    private async Task<bool> SwitchToAsync(Services.Monitor m, int input, bool quiet = false, bool owned = false)
    {
        if (_switching || (_operation != null && !owned))
        {
            if (!quiet) Toast(Wait());
            return false;
        }
        int? from = m.Current;
        if (from == input)
        {
            if (!quiet) Toast($"{_settings.NameOf(m.Key, input)} est déjà affiché sur {The(m)}.");
            return true;
        }
        _switching = true;
        _lastSwitchAt = DateTime.UtcNow;
        bool ok;
        try { ok = await Task.Run(() => _ddc.Switch(m, input)); }
        finally
        {
            _lastSwitchAt = DateTime.UtcNow;
            _switching = false;
        }
        if (ok)
        {
            if (from is int f) _settings.Remember(m.Key, f, input);
            _lastMonitor = m.Key;
            _expected[m.Key] = input;
            Save();
            if (!quiet) Toast($"{m.Title} → {Label(m, input)}");
        }
        else if (!quiet)
        {
            Toast($"{Cap(The(m))} n'a pas répondu. S'il affiche une autre entrée, Windows l'a peut-être perdu : " +
                  "utilise le bouton de l'écran cette fois-ci, puis « Actualiser ».");
        }
        BuildUi();
        RebuildTrayMenu();
        return ok;
    }

    /// <summary>Ctrl+Alt+Maj+0 : l'écran changé en dernier revient sur l'entrée d'avant.</summary>
    private void SwitchBack() => _ = SwitchBackAsync();

    private async Task SwitchBackAsync()
    {
        if (Locked) { Toast(Wait()); return; }
        var m = _monitors.FirstOrDefault(x => x.Key == _lastMonitor && x.Responds)
                ?? _monitors.FirstOrDefault(x => x.Responds && x.Current is int c && _settings.Previous(x.Key, c) != null);
        if (m == null)
        {
            Toast("Pas encore d'entrée d'avant : change d'entrée une première fois.");
            return;
        }
        // l'entrée a pu changer avec les boutons de l'écran sans être relue (test du diagnostic, fenêtre cachée) : on la relit
        int? raw = await Task.Run(() => _ddc.ReadRaw(m));
        if (raw is int r) m.Current = r;
        if (m.Current is not int cur || _settings.Previous(m.Key, cur) is not int prev)
        {
            Toast("Pas encore d'entrée d'avant : change d'entrée une première fois.");
            return;
        }
        await SwitchToAsync(m, prev);
    }

    /// <summary>
    /// Essai sans risque : l'écran passe sur l'entrée quelques secondes puis revient tout seul. Vérifie surtout le
    /// retour : certains écrans ne répondent plus une fois sur une autre entrée.
    /// </summary>
    private async Task TryRoundTrip(Services.Monitor m, int input)
    {
        if (m.Current is not int back || !CanStartOperation()) return;
        var answer = MessageBox.Show(this,
            $"{Cap(The(m))} va afficher {Inputs.PortName(input)} pendant 8 secondes, puis revenir tout seul sur " +
            $"{Inputs.PortName(back)}.\n\nSi rien n'est allumé sur {Inputs.PortName(input)}, il affichera « pas de signal » : c'est normal.\n\nOn essaie ?",
            "Essayer l'aller-retour", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK || !BeginOperation("un essai d'aller-retour")) return;

        try
        {
            Toast($"Essai : {The(m)} passe sur {Inputs.PortName(input)} et revient dans 8 secondes…", 12);
            if (!await SwitchToAsync(m, input, quiet: true, owned: true))
            {
                Toast($"{Cap(The(m))} n'accepte pas l'ordre de changer d'entrée.");
                return;
            }
            await Task.Delay(8000);
            bool ok = await SwitchToAsync(m, back, quiet: true, owned: true);
            await Task.Delay(1500);
            // vraie lecture : la valeur gardée est celle que l'appli vient d'écrire, elle ne prouve rien
            int? now = await Task.Run(() => _ddc.ReadRaw(m));
            if (now is int n) m.Current = n;
            if (ok && now == back)
                Toast($"Aller-retour réussi : {Inputs.PortName(input)} puis retour sur {Inputs.PortName(back)}. Tout marche, y compris le raccourci pour revenir.", 10);
            else if (ok && now == null)
                Toast($"Retour envoyé, mais non confirmé : l'écran ne répond pas au PC. S'il n'affiche pas {Inputs.PortName(back)}, " +
                      "utilise le bouton de l'écran (ou active sa « détection automatique de l'entrée »).", 14);
            else
                Toast($"Le retour n'a pas marché : une fois sur {Inputs.PortName(input)}, l'écran ne répond plus au PC. " +
                      "Pour revenir, utilise le bouton de l'écran (ou active sa « détection automatique de l'entrée »).", 14);
        }
        finally { EndOperation(); }
        Activate();
    }

    /// <summary>Détecte les appareils branchés : l'écran passe sur chaque prise et on regarde s'il y reste.</summary>
    private async Task DetectDevices(Services.Monitor m)
    {
        if (m.Current is not int pc || !CanStartOperation()) return;
        var others = m.Inputs.Where(i => i != pc).ToList();
        var answer = MessageBox.Show(this,
            $"{Cap(The(m))} va passer environ 6 secondes sur chaque autre prise ({string.Join(", ", others.Select(Inputs.PortName))}), " +
            $"puis revenir sur {Label(m, pc)}. Il va s'éteindre et se rallumer plusieurs fois : c'est normal.\n\n" +
            "Allume d'abord la PS5 (ou l'appareil à trouver). On y va ?",
            "Détecter les appareils branchés", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK || !BeginOperation("la détection des appareils")) return;

        var stayed = new Dictionary<int, bool>();
        var unconfirmed = new List<int>(); // l'écran ne répondait plus : on ne sait pas, rien n'est changé pour cette prise
        try
        {
            for (int k = 0; k < others.Count; k++)
            {
                int input = others[k];
                Toast($"Détection {k + 1}/{others.Count} : {Inputs.PortName(input)}…", 15);
                if (!await SwitchToAsync(m, input, quiet: true, owned: true))
                {
                    await SwitchToAsync(m, pc, quiet: true, owned: true);
                    Toast($"{Cap(The(m))} n'accepte pas l'ordre de changer d'entrée.");
                    return;
                }
                await Task.Delay(6000);
                // vraie lecture : la valeur gardée est celle que l'appli vient d'écrire
                int? now = await Task.Run(() => _ddc.ReadRaw(m));
                if (now is int n) stayed[input] = n == input;
                else unconfirmed.Add(input);
            }
            await SwitchToAsync(m, pc, quiet: true, owned: true);
        }
        finally { EndOperation(); }
        // pour la touche « revenir », l'entrée d'avant n'est pas une prise essayée pendant la détection
        _settings.Recent.Remove(m.Key);

        var result = Detection.Interpret(pc, stayed);
        foreach (var (input, signal) in result) _settings.SetSignal(m.Key, input, signal);
        Save();
        BuildUi();
        RebuildTrayMenu();
        var found = result.Where(r => r.Key != pc && r.Value == Signal.Present).Select(r => Inputs.PortName(r.Key)).ToList();
        if (unconfirmed.Count == others.Count)
            Toast("L'écran ne répond plus au PC dès qu'il quitte sa prise : impossible de savoir où est branché l'appareil. " +
                  "Affiche-le une fois avec les boutons de l'écran (l'appli retient sa prise), ou bouton « Diagnostic ».", 14);
        else if (result.Where(r => r.Key != pc).All(r => r.Value == Signal.Unknown))
            Toast("Cet écran reste sur chaque prise même sans image : impossible de savoir lesquelles sont branchées. " +
                  "Allume l'appareil, puis clique sur ses prises une par une.", 14);
        else if (found.Count == 0)
            Toast("Aucun autre appareil allumé trouvé. Allume la PS5 (pas en veille) et relance la détection.", 12);
        else
            Toast($"Appareil branché sur {string.Join(" et ", found)}. Clique sur le crayon pour lui donner un nom (ex. PS5).", 12);
        Activate();
    }

    private void OnDiagnostic(object sender, RoutedEventArgs e)
    {
        if (_diagnostic != null) // une seule fenêtre de diagnostic : on ramène celle qui est ouverte
        {
            if (_diagnostic.WindowState == WindowState.Minimized) _diagnostic.WindowState = WindowState.Normal;
            _diagnostic.Activate();
            return;
        }
        var w = new DiagnosticWindow(this, _ddc, _settings, _monitors, BeginDiagnosticTest, EndDiagnosticTest, () =>
        {
            Save();
            BuildUi();
            RebuildTrayMenu();
            RegisterHotkeys();
        });
        _diagnostic = w;
        w.Closed += (_, _) => _diagnostic = null;
        w.Show();
    }

    /// <summary>
    /// Test du diagnostic. Automatique : il tient le verrou (aucun autre ordre). Avec les boutons de l'écran : seul le
    /// relevé s'arrête, les raccourcis restent actifs (Ctrl+Alt+Maj+0 pour revenir sur le PC, comme le dit la fenêtre).
    /// </summary>
    private bool BeginDiagnosticTest(bool manual)
    {
        if (Locked || _watching || _loading) return false;
        if (manual) _watching = true;
        else _operation = "le test du diagnostic";
        BuildUi();
        return true;
    }

    private void EndDiagnosticTest()
    {
        _watching = false;
        _operation = null;
        BuildUi();
        RebuildTrayMenu();
    }

    private void OnIdentify(object sender, RoutedEventArgs e)
    {
        foreach (var m in _monitors)
            new IdentifyWindow(m.Bounds, m.Title,
                $"{m.Name} · Windows : écran {m.Number}{(m.IsPrimary ? " · principal" : "")}" +
                (m.Responds ? "" : " · ne répond pas (DDC/CI désactivé)")).Show();
    }

    private static string The(Services.Monitor m) => "l'" + char.ToLowerInvariant(m.Title[0]) + m.Title[1..];

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    // ---------------------------------------------------------------- interface

    private string Label(Services.Monitor m, int input) =>
        _settings.HasCustomName(m.Key, input) ? $"{_settings.NameOf(m.Key, input)} ({Inputs.PortName(input)})" : Inputs.PortName(input);

    private string? ShortcutOf(Services.Monitor m, int input)
    {
        foreach (var (n, s) in _slots)
            if (s.Monitor == m.Key && s.Input == input) return $"Ctrl+Alt+Maj+{n}";
        return null;
    }

    private void BuildUi()
    {
        MonitorsPanel.Children.Clear();
        if (_monitors.Count == 0)
        {
            MonitorsPanel.Children.Add(Text("Aucun écran trouvé. Vérifie les câbles, puis « Actualiser ».", "BodyText"));
            return;
        }
        foreach (var m in _monitors) MonitorsPanel.Children.Add(MonitorCard(m));
    }

    private Border MonitorCard(Services.Monitor m)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 12) };
        var title = Text(m.Title.ToUpperInvariant(), "LabelText", strong: true);
        title.FontSize = 13.5;
        head.Children.Add(title);
        head.Children.Add(Text($"  ·  {(m.Responds ? m.Name : "ne répond pas")}", "LabelText"));
        if (m.IsPrimary) head.Children.Add(Chip("principal"));
        head.Children.Add(Chip($"Windows : écran {m.Number}"));
        stack.Children.Add(head);

        if (!m.Responds)
        {
            stack.Children.Add(Text(
                "Cet écran ne répond pas aux ordres du PC. Ouvre son menu (bouton sous ou derrière l'écran), cherche " +
                "« DDC/CI » (souvent dans Réglages, Système ou Autres) et mets-le sur « Oui », puis clique sur « Actualiser ». " +
                "Un adaptateur ou un dock USB-C peut aussi bloquer ces ordres.", "BodyText"));
            return new Border { Style = Res<Style>("Panel"), Margin = new Thickness(0, 0, 0, 14), Child = stack, Opacity = 0.85 };
        }

        // l'écran annonce toutes ses prises, branchées ou non : après une détection, les prises vides sont rangées
        bool Shown(int i) => _showAll.Contains(m.Key) || m.Current == i || _settings.HasCustomName(m.Key, i)
                             || _settings.SignalOf(m.Key, i) != Signal.Absent;
        var empty = m.Inputs.Where(i => !Shown(i)).ToList();
        bool deviceKnown = m.Inputs.Any(i => i != m.Current && (_settings.SignalOf(m.Key, i) == Signal.Present || _settings.HasCustomName(m.Key, i)));
        if (!deviceKnown && m.Inputs.Count > 1)
        {
            var hint = Text("L'écran ne dit pas où sont branchés tes appareils : il annonce toutes ses prises. Le plus simple : " +
                            "affiche une fois ta PS5 avec les boutons de l'écran, l'appli retient sa prise toute seule (au bout de " +
                            "10 secondes). Ou allume-la et clique sur « Détecter les appareils branchés ». Toujours rien ? Bouton « Diagnostic » en haut.", "BodyText");
            hint.FontSize = 13;
            hint.Margin = new Thickness(2, 0, 0, 12);
            stack.Children.Add(hint);
        }

        var tiles = new WrapPanel();
        foreach (int input in m.Inputs.Where(Shown)) tiles.Children.Add(Tile(m, input));
        stack.Children.Add(tiles);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (m.Inputs.Count > 1)
        {
            var detect = new Button
            {
                Style = Res<Style>("GhostButton"), Content = "Détecter les appareils branchés", IsEnabled = !Locked && !_watching,
                ToolTip = "L'écran passe quelques secondes sur chaque prise : il reste là où un appareil allumé envoie une image",
            };
            detect.Click += (_, _) => _ = DetectDevices(m);
            row.Children.Add(detect);
        }
        if (empty.Count > 0 || _showAll.Contains(m.Key))
        {
            int hidden = m.Inputs.Count(i => _settings.SignalOf(m.Key, i) == Signal.Absent && m.Current != i && !_settings.HasCustomName(m.Key, i));
            var toggle = new Button
            {
                Style = Res<Style>("GhostButton"), Margin = new Thickness(8, 0, 0, 0),
                Content = _showAll.Contains(m.Key) ? "Masquer les prises vides" : $"Afficher les prises vides ({hidden})",
            };
            toggle.Click += (_, _) => { if (!_showAll.Remove(m.Key)) _showAll.Add(m.Key); BuildUi(); };
            row.Children.Add(toggle);
        }
        stack.Children.Add(row);
        return new Border { Style = Res<Style>("Panel"), Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    private Button Tile(Services.Monitor m, int input)
    {
        bool on = m.Current == input;
        var fg = on ? Res<Brush>("TextInverseBrush") : Res<Brush>("TextStrongBrush");
        var dim = on ? new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)) : Res<Brush>("TextTertiaryBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // haut : prise + état
        var top = new DockPanel();
        var signal = _settings.SignalOf(m.Key, input);
        var state = Text(on ? "AFFICHÉ" : signal switch { Signal.Present => "BRANCHÉ", Signal.Absent => "RIEN", _ => "" }, "MonoText");
        state.ToolTip = signal switch
        {
            Signal.Present => "Un appareil allumé envoie une image sur cette prise (vu à la dernière détection)",
            Signal.Absent => "Rien d'allumé sur cette prise à la dernière détection",
            _ => null,
        };
        state.Foreground = !on && signal == Signal.Present ? Res<Brush>("GreenBrush") : dim;
        DockPanel.SetDock(state, Dock.Right);
        top.Children.Add(state);
        var port = new StackPanel { Orientation = Orientation.Horizontal };
        port.Children.Add(new TextBlock
        {
            Text = Inputs.PortKind(input) switch { "hdmi" => "", "dp" => "", "usbc" => "", _ => "" },
            FontFamily = Res<FontFamily>("FontIcons"), FontSize = 13, Foreground = dim, VerticalAlignment = VerticalAlignment.Center,
        });
        var portName = Text(_settings.HasCustomName(m.Key, input) ? Inputs.PortName(input) : "Entrée sans nom", "MonoText");
        portName.Foreground = dim;
        portName.Margin = new Thickness(7, 0, 0, 0);
        port.Children.Add(portName);
        top.Children.Add(port);
        grid.Children.Add(top);

        // milieu : le nom (ou la zone pour le changer)
        FrameworkElement name;
        if (_renaming == (m.Key, input))
        {
            var box = new TextBox { Style = Res<Style>("NameBox"), Text = _settings.HasCustomName(m.Key, input) ? _settings.NameOf(m.Key, input) : "" };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { FinishRename(m, input, box.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { _renaming = null; BuildUi(); e.Handled = true; }
            };
            box.LostKeyboardFocus += (_, _) => { if (_renaming == (m.Key, input)) FinishRename(m, input, box.Text); };
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            name = box;
        }
        else
        {
            var tb = Text(_settings.NameOf(m.Key, input), "TitleText");
            tb.FontFamily = Res<FontFamily>("FontSemi");
            tb.FontSize = 22;
            tb.Foreground = fg;
            if (!_settings.HasCustomName(m.Key, input)) tb.Opacity = on ? 0.8 : 0.7;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            name = tb;
        }
        name.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetRow(name, 1);
        grid.Children.Add(name);

        // bas : raccourci, renommer, essayer
        var bottom = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(MiniButton("", "Donner un nom (ex. PS5, PC)", fg, () => { _renaming = (m.Key, input); BuildUi(); }));
        if (!on)
            actions.Children.Add(MiniButton("", "Essayer : passer dessus 8 secondes puis revenir tout seul", fg, () => _ = TryRoundTrip(m, input)));
        DockPanel.SetDock(actions, Dock.Right);
        bottom.Children.Add(actions);
        var sc = Text(ShortcutOf(m, input) ?? "", "MonoText");
        sc.Foreground = dim;
        sc.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(sc);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        var b = new Button
        {
            Style = Res<Style>("TileButton"), Tag = on ? "on" : null, Content = grid, Width = 236, Height = 132,
            Margin = new Thickness(0, 0, 12, 12), Foreground = fg,
            ToolTip = on ? "Entrée affichée en ce moment" : $"Passer {The(m)} sur {Inputs.PortName(input)}",
        };
        b.Click += (_, _) => { if (_renaming == null) SwitchTo(m, input); };
        return b;
    }

    private void FinishRename(Services.Monitor m, int input, string text)
    {
        _renaming = null;
        _settings.Rename(m.Key, input, text);
        Save();
        BuildUi();
        RebuildTrayMenu();
    }

    private Button MiniButton(string glyph, string tip, Brush fg, Action action)
    {
        var b = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(0), Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0),
            Content = new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("FontIcons"), FontSize = 12, Foreground = fg },
            ToolTip = tip,
        };
        b.Click += (_, e) => { e.Handled = true; action(); };
        return b;
    }

    private Border Chip(string text) => new()
    {
        Background = Res<Brush>("RaisedBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, "MonoText"),
    };

    private TextBlock Text(string text, string style, bool strong = false)
    {
        var t = new TextBlock { Text = text, Style = Res<Style>(style) };
        if (strong) t.Foreground = Res<Brush>("TextPrimaryBrush");
        return t;
    }

    private T Res<T>(string key) => (T)FindResource(key);

    private void Toast(string message, int seconds = 6)
    {
        ToastText.Text = message;
        ToastText.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
        if (!IsVisible) _tray.ShowBalloonTip(2500, "se7en Source", message, Forms.ToolTipIcon.None);
    }

    private void Save()
    {
        try { _settings.Save(SettingsPath); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- raccourcis

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).Handle);
        _slots.Clear();
        // numéros gardés dans les réglages, par écran et par entrée : un écran muet ne décale pas ceux des autres
        var available = _monitors.Where(x => x.Responds).SelectMany(m => m.Inputs.Select(i => (m.Key, i))).ToList();
        if (_settings.AssignSlots(available)) Save();
        foreach (var (n, slot) in _settings.Slots)
            if (available.Contains((slot.Monitor, slot.Input))) _slots[n] = (slot.Monitor, slot.Input);

        var failed = new List<string>();
        foreach (var (n, (key, input)) in _slots.OrderBy(s => s.Key))
            if (!_hotkeys.Register(Key.D0 + n, () => SwitchToSlot(key, input))) failed.Add($"Ctrl+Alt+Maj+{n}");
        if (!_hotkeys.Register(Key.D0, SwitchBack)) failed.Add("Ctrl+Alt+Maj+0");
        if (failed.Count > 0) Toast("Raccourcis déjà pris par une autre appli : " + string.Join(", ", failed));
    }

    /// <summary>Raccourci pressé : l'écran est retrouvé par sa clé (la liste a pu être relue depuis).</summary>
    private void SwitchToSlot(string key, int input)
    {
        if (_monitors.FirstOrDefault(x => x.Key == key && x.Responds) is Services.Monitor m) SwitchTo(m, input);
        else Toast("Cet écran ne répond pas en ce moment : « Actualiser », puis réessaie.");
    }

    // ---------------------------------------------------------------- icône près de l'horloge

    private void RebuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        foreach (var m in _monitors.Where(x => x.Responds))
        {
            menu.Items.Add(new Forms.ToolStripMenuItem($"{m.Title} · {m.Name}") { Enabled = false });
            foreach (int input in m.Inputs)
            {
                var mon = m;
                int inp = input;
                var item = new Forms.ToolStripMenuItem(Label(m, input), null, (_, _) => SwitchTo(mon, inp))
                {
                    Checked = m.Current == input,
                    ShortcutKeyDisplayString = ShortcutOf(m, input)?.Replace(" ", ""),
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        menu.Items.Add(new Forms.ToolStripMenuItem("Revenir à l'entrée d'avant", null, (_, _) => SwitchBack())
        { ShortcutKeyDisplayString = "Ctrl+Alt+Maj+0" });
        menu.Items.Add("Ouvrir se7en Source", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitApp());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        return stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = PollCurrent();
    }

    // ---------------------------------------------------------------- fenêtre

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartBox.IsChecked == true); }
        catch (Exception) { Toast("Impossible de changer le lancement avec Windows."); }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true; // on reste près de l'horloge : les raccourcis continuent de marcher
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "se7en Source", "Toujours là, près de l'horloge : Ctrl+Alt+Maj+1, 2… changent l'entrée.", Forms.ToolTipIcon.None);
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        _reload.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _ddc.Dispose();
        Application.Current.Shutdown();
    }
}
