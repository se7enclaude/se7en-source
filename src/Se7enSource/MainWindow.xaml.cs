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
    // raccourcis Ctrl+Alt+Maj+1…9 : une entrée d'un écran chacun, dans l'ordre affiché
    private readonly List<(Services.Monitor Monitor, int Input)> _slots = [];
    private string? _lastMonitor;   // dernier écran changé (pour « revenir à l'entrée d'avant »)
    private bool _busy;             // un changement d'entrée est en cours (l'écran met ~1 s à répondre)
    private bool _exiting, _trayHintShown;
    private (string Key, int Input)? _renaming;
    private readonly HashSet<string> _showAll = [];   // écrans dont on montre aussi les prises vides

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        AutostartBox.IsChecked = Autostart.IsEnabled;

        _tray = new Forms.NotifyIcon { Text = "se7en Source", Icon = LoadTrayIcon(), Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        RebuildTrayMenu();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText.Visibility = Visibility.Collapsed; };

        // l'entrée peut aussi changer depuis les boutons de l'écran : on la relit quand la fenêtre est visible
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await PollCurrent();

        SourceInitialized += async (_, _) => await LoadMonitors();
        IsVisibleChanged += (_, _) => { if (IsVisible) _poll.Start(); else _poll.Stop(); };
        Closing += OnClosing;

        if (startMinimized) new WindowInteropHelper(this).EnsureHandle(); // raccourcis actifs, fenêtre cachée
        else Show();
    }

    // ---------------------------------------------------------------- écrans

    private async Task LoadMonitors()
    {
        RefreshButton.IsEnabled = false;
        _monitors = await Task.Run(_ddc.Refresh);
        RefreshButton.IsEnabled = true;
        // l'entrée affichée au lancement est celle du PC : on la nomme tout de suite (si rien n'est encore nommé)
        foreach (var m in _monitors.Where(x => x.Responds && x.Current is int))
            if (!m.Inputs.Any(i => _settings.HasCustomName(m.Key, i)))
            {
                _settings.Rename(m.Key, m.Current!.Value, "Ce PC");
                _settings.SetSignal(m.Key, m.Current!.Value, Signal.Present);
                Save();
            }
        RegisterHotkeys();
        BuildUi();
        RebuildTrayMenu();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        MonitorsPanel.Children.Clear();
        MonitorsPanel.Children.Add(LoadingText);
        await LoadMonitors();
    }

    private async Task PollCurrent()
    {
        if (_busy || _renaming != null) return;
        var live = _monitors.Where(m => m.Responds).ToList();
        if (live.Count == 0) return;
        var before = live.Select(m => m.Current).ToList();
        await Task.Run(() => { foreach (var m in live) _ddc.ReadCurrent(m); });
        if (!before.SequenceEqual(live.Select(m => m.Current)))
        {
            BuildUi();
            RebuildTrayMenu();
        }
    }

    private void SwitchTo(Services.Monitor m, int input, bool quiet = false) => _ = SwitchToAsync(m, input, quiet);

    private async Task<bool> SwitchToAsync(Services.Monitor m, int input, bool quiet = false)
    {
        if (_busy) return false;
        int? from = m.Current;
        if (from == input)
        {
            if (!quiet) Toast($"{_settings.NameOf(m.Key, input)} est déjà affiché sur {The(m)}.");
            return true;
        }
        _busy = true;
        bool ok = await Task.Run(() => _ddc.Switch(m, input));
        _busy = false;
        if (ok)
        {
            if (from is int f) _settings.Remember(m.Key, f, input);
            _lastMonitor = m.Key;
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
    private void SwitchBack()
    {
        var m = _monitors.FirstOrDefault(x => x.Key == _lastMonitor && x.Responds)
                ?? _monitors.FirstOrDefault(x => x.Responds && x.Current is int c && _settings.Previous(x.Key, c) != null);
        if (m?.Current is not int cur || _settings.Previous(m.Key, cur) is not int prev)
        {
            Toast("Pas encore d'entrée d'avant : change d'entrée une première fois.");
            return;
        }
        SwitchTo(m, prev);
    }

    /// <summary>
    /// Essai sans risque : l'écran passe sur l'entrée quelques secondes puis revient tout seul. Vérifie surtout le
    /// retour : certains écrans ne répondent plus une fois sur une autre entrée.
    /// </summary>
    private async Task TryRoundTrip(Services.Monitor m, int input)
    {
        if (m.Current is not int back || _busy) return;
        var answer = MessageBox.Show(this,
            $"{Cap(The(m))} va afficher {Inputs.PortName(input)} pendant 8 secondes, puis revenir tout seul sur " +
            $"{Inputs.PortName(back)}.\n\nSi rien n'est allumé sur {Inputs.PortName(input)}, il affichera « pas de signal » : c'est normal.\n\nOn essaie ?",
            "Essayer l'aller-retour", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Toast($"Essai : {The(m)} passe sur {Inputs.PortName(input)} et revient dans 8 secondes…", 12);
        if (!await SwitchToAsync(m, input, quiet: true))
        {
            Toast($"{Cap(The(m))} n'accepte pas l'ordre de changer d'entrée.");
            return;
        }
        await Task.Delay(8000);
        bool ok = await SwitchToAsync(m, back, quiet: true);
        await Task.Delay(1500);
        int? now = await Task.Run(() => _ddc.ReadCurrent(m));
        if (ok && now == back)
            Toast($"Aller-retour réussi : {Inputs.PortName(input)} puis retour sur {Inputs.PortName(back)}. Tout marche, y compris le raccourci pour revenir.", 10);
        else
            Toast($"Le retour n'a pas marché : une fois sur {Inputs.PortName(input)}, l'écran ne répond plus au PC. " +
                  "Pour revenir, utilise le bouton de l'écran (ou active sa « détection automatique de l'entrée »).", 14);
        Activate();
    }

    /// <summary>Détecte les appareils branchés : l'écran passe sur chaque prise et on regarde s'il y reste.</summary>
    private async Task DetectDevices(Services.Monitor m)
    {
        if (_busy || m.Current is not int pc) return;
        var others = m.Inputs.Where(i => i != pc).ToList();
        var answer = MessageBox.Show(this,
            $"{Cap(The(m))} va passer environ 6 secondes sur chaque autre prise ({string.Join(", ", others.Select(Inputs.PortName))}), " +
            $"puis revenir sur {Label(m, pc)}. Il va s'éteindre et se rallumer plusieurs fois : c'est normal.\n\n" +
            "Allume d'abord la PS5 (ou l'appareil à trouver). On y va ?",
            "Détecter les appareils branchés", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var stayed = new Dictionary<int, bool>();
        for (int k = 0; k < others.Count; k++)
        {
            int input = others[k];
            Toast($"Détection {k + 1}/{others.Count} : {Inputs.PortName(input)}…", 15);
            if (!await SwitchToAsync(m, input, quiet: true))
            {
                await SwitchToAsync(m, pc, quiet: true);
                Toast($"{Cap(The(m))} n'accepte pas l'ordre de changer d'entrée.");
                return;
            }
            await Task.Delay(6000);
            int? now = await Task.Run(() => _ddc.ReadCurrent(m));
            stayed[input] = now == input;
        }
        await SwitchToAsync(m, pc, quiet: true);
        // pour la touche « revenir », l'entrée d'avant n'est pas une prise essayée pendant la détection
        _settings.Recent.Remove(m.Key);

        var result = Detection.Interpret(pc, stayed);
        foreach (var (input, signal) in result) _settings.SetSignal(m.Key, input, signal);
        Save();
        BuildUi();
        RebuildTrayMenu();
        var found = result.Where(r => r.Key != pc && r.Value == Signal.Present).Select(r => Inputs.PortName(r.Key)).ToList();
        if (result.Where(r => r.Key != pc).All(r => r.Value == Signal.Unknown))
            Toast("Cet écran reste sur chaque prise même sans image : impossible de savoir lesquelles sont branchées. " +
                  "Allume l'appareil, puis clique sur ses prises une par une.", 14);
        else if (found.Count == 0)
            Toast("Aucun autre appareil allumé trouvé. Allume la PS5 (pas en veille) et relance la détection.", 12);
        else
            Toast($"Appareil branché sur {string.Join(" et ", found)}. Clique sur le crayon pour lui donner un nom (ex. PS5).", 12);
        Activate();
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
        int i = _slots.FindIndex(s => s.Monitor.Key == m.Key && s.Input == input);
        return i >= 0 ? $"Ctrl+Alt+Maj+{i + 1}" : null;
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
            var hint = Text("L'écran ne dit pas où sont branchés tes appareils : il annonce toutes ses prises. Pour trouver ta PS5, " +
                            "allume-la (pas en mode repos), puis clique sur « Détecter les appareils branchés » ci-dessous.", "BodyText");
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
                Style = Res<Style>("GhostButton"), Content = "Détecter les appareils branchés", IsEnabled = !_busy,
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
        foreach (var m in _monitors.Where(x => x.Responds))
            foreach (int input in m.Inputs)
                if (_slots.Count < 9) _slots.Add((m, input));

        var failed = new List<string>();
        for (int i = 0; i < _slots.Count; i++)
        {
            var (m, input) = _slots[i];
            if (!_hotkeys.Register(Key.D1 + i, () => SwitchTo(m, input))) failed.Add($"Ctrl+Alt+Maj+{i + 1}");
        }
        if (!_hotkeys.Register(Key.D0, SwitchBack)) failed.Add("Ctrl+Alt+Maj+0");
        if (failed.Count > 0) Toast("Raccourcis déjà pris par une autre appli : " + string.Join(", ", failed));
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
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _ddc.Dispose();
        Application.Current.Shutdown();
    }
}
