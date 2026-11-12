using System.Runtime.InteropServices;
using Se7enSource.Core;

namespace Se7enSource.Services;

/// <summary>Un écran branché, vu par DDC/CI (l'ordre « change d'entrée » passe par le câble vidéo).</summary>
public sealed class Monitor
{
    /// <summary>Clé stable (identifiant du branchement : « AOCA610…UID4353 »), pour retrouver les noms donnés aux entrées.</summary>
    public required string Key { get; init; }
    public required string Device { get; init; }          // \\.\DISPLAY1
    public required string Name { get; init; }            // « AG276QZD2 »
    public required bool IsPrimary { get; init; }
    public required int Number { get; init; }            // 1, 2… comme dans les réglages d'affichage de Windows
    /// <summary>Rectangle de l'écran sur le bureau Windows (pixels) : sa position, gauche ou droite.</summary>
    public required (int Left, int Top, int Right, int Bottom) Bounds { get; init; }
    /// <summary>« de gauche », « de droite »… (voir Layout.PositionNames).</summary>
    public string Position { get; set; } = "";
    public string Title => string.IsNullOrEmpty(Position) ? $"Écran {Number}" : $"Écran {Position}";
    /// <summary>Prises : celles des capacités, plus celles retenues dans les réglages et l'entrée affichée.</summary>
    public IReadOnlyList<int> Inputs { get; internal set; } = [];
    /// <summary>Chaîne de capacités DDC/CI brute (pour le rapport de diagnostic).</summary>
    public string? Caps { get; init; }
    public int? Current { get; set; }
    /// <summary>L'écran répond en DDC/CI (sinon : option désactivée dans son menu, ou adaptateur qui la bloque).</summary>
    public bool Responds => Inputs.Count > 0 || Current != null;
    internal IntPtr Handle;
}

/// <summary>DDC/CI par l'API Windows (dxva2) : lire la source d'entrée (VCP 0x60), la changer.</summary>
public sealed class Ddc : IDisposable
{
    private readonly object _lock = new();
    private readonly List<Monitor> _monitors = [];
    // la chaîne de capacités met 1 à 2 s à arriver : gardée par écran
    private readonly Dictionary<string, string?> _caps = [];
    // prises connues par écran hors capacités (réglages : nommées, ou appareil vu), fusionnées dans Monitor.Inputs
    private Dictionary<string, IReadOnlyList<int>> _known = [];

    public IReadOnlyList<Monitor> Monitors { get { lock (_lock) return [.. _monitors]; } }

    /// <summary>
    /// Les écrans ont été recherchés à nouveau pendant un changement d'entrée (les anciens objets Monitor ne sont plus
    /// valides) : à relire avec <see cref="Monitors"/>. Appelé hors du fil de l'interface.
    /// </summary>
    public event Action? Reenumerated;

    /// <summary>Prises retenues dans les réglages, par écran (fusionnées à la prochaine recherche).</summary>
    public void SetKnownInputs(Dictionary<string, IReadOnlyList<int>> known)
    {
        lock (_lock) _known = known;
    }

    /// <summary>Relit la liste des écrans (lent la première fois : quelques secondes par écran).</summary>
    /// <param name="rereadCaps">Relire aussi les chaînes de capacités (bouton « Actualiser »).</param>
    public IReadOnlyList<Monitor> Refresh(bool rereadCaps = false)
    {
        lock (_lock)
        {
            Release();
            if (rereadCaps) _caps.Clear();
            var found = new List<(IntPtr HMon, string Device, bool Primary, Rect Rect)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(h, ref mi)) found.Add((h, mi.Device, (mi.Flags & 1) != 0, mi.Monitor));
                return true;
            }, IntPtr.Zero);

            // de gauche à droite (puis de haut en bas), comme les écrans sur le bureau
            foreach (var (hmon, device, primary, rect) in found.OrderBy(f => f.Rect.Left).ThenBy(f => f.Rect.Top))
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) continue;
                var arr = new PhysicalMonitor[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr)) continue;
                for (int i = 0; i < arr.Length; i++)
                {
                    string key = InterfaceId(device, i) ?? $"{device}#{i}";
                    // on ne garde que les réponses complètes : une chaîne tronquée (sans la liste des prises) est relue la fois suivante
                    if (!_caps.TryGetValue(key, out var caps))
                    {
                        caps = ReadCaps(arr[i].Handle);
                        if (Inputs.FromCapabilities(caps).Count > 0) _caps[key] = caps;
                    }
                    int? cur = ReadInput(arr[i].Handle);
                    var inputs = Inputs.FromCapabilities(caps).ToList();
                    // prises apprises hors capacités (certains écrans numérotent leurs prises à leur façon), entrée affichée
                    if (_known.TryGetValue(key, out var known))
                        foreach (int k in known) if (!inputs.Contains(k)) inputs.Add(k);
                    if (cur is int c && !inputs.Contains(c)) inputs.Add(c);
                    _monitors.Add(new Monitor
                    {
                        Key = key, Device = device, IsPrimary = primary, Number = DisplayNumber(device),
                        Bounds = (rect.Left, rect.Top, rect.Right, rect.Bottom),
                        Name = Inputs.Model(caps) ?? FriendlyName(arr[i].Description),
                        Inputs = inputs, Current = cur, Handle = arr[i].Handle, Caps = caps,
                    });
                }
            }
            var names = Layout.PositionNames([.. _monitors.Select(m => (m.Bounds.Left, m.Bounds.Top, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top))]);
            for (int i = 0; i < _monitors.Count; i++) _monitors[i].Position = names[i];
            return [.. _monitors];
        }
    }

    /// <summary>Lit l'entrée affichée maintenant (null si l'écran ne répond pas).</summary>
    public int? ReadCurrent(Monitor m)
    {
        lock (_lock)
        {
            // l'objet reçu peut dater d'avant une nouvelle recherche des écrans (poignée détruite) : on le retrouve par sa clé
            if (Find(m) is not Monitor target) return m.Current; // écran disparu : on ne touche pas à une poignée détruite
            target.Current = ReadInput(target.Handle) ?? target.Current;
            return m.Current = target.Current;
        }
    }

    /// <summary>Une seule lecture de l'entrée, sans garder l'ancienne valeur : null si l'écran ne répond pas (diagnostic).</summary>
    public int? ReadRaw(Monitor m)
    {
        lock (_lock)
        {
            if (Find(m) is not Monitor target) return null;
            return GetVCPFeatureAndVCPFeatureReply(target.Handle, Inputs.InputSourceCode, IntPtr.Zero, out uint cur, out _)
                ? (int)(cur & 0xFF) : null;
        }
    }

    /// <summary>
    /// Demande à l'écran de passer sur <paramref name="input"/>. Si Windows a perdu l'écran entre-temps (certains écrans
    /// « débranchent » le DisplayPort quand ils affichent une autre entrée), on le recherche par sa clé et on réessaie.
    /// </summary>
    public bool Switch(Monitor m, int input)
    {
        bool ok = false, reenumerated = false;
        lock (_lock)
        {
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                var target = Find(m);
                if (target != null && SetVCPFeature(target.Handle, Inputs.InputSourceCode, (uint)input))
                {
                    target.Current = m.Current = input;
                    ok = true;
                    break;
                }
                Thread.Sleep(150);
                if (attempt == 1) { Refresh(); reenumerated = true; } // le verrou est réentrant ; les capacités restent en cache
            }
        }
        if (reenumerated) Reenumerated?.Invoke(); // hors du verrou
        return ok;
    }

    /// <summary>L'écran de même clé dans la liste actuelle (l'objet reçu peut dater d'avant une nouvelle recherche).</summary>
    private Monitor? Find(Monitor m) => _monitors.Contains(m) ? m : _monitors.FirstOrDefault(x => x.Key == m.Key);

    private static string? ReadCaps(IntPtr h)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (GetCapabilitiesStringLength(h, out uint len) && len > 0)
            {
                var buf = new byte[len];
                if (CapabilitiesRequestAndCapabilitiesReply(h, buf, len))
                    return System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private static int? ReadInput(IntPtr h)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (GetVCPFeatureAndVCPFeatureReply(h, Inputs.InputSourceCode, IntPtr.Zero, out uint cur, out _))
                return (int)(cur & 0xFF);
            Thread.Sleep(60);
        }
        return null;
    }

    private static int DisplayNumber(string device) =>
        int.TryParse(new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), out int n) ? n : 99;

    private static string FriendlyName(string description) =>
        string.IsNullOrWhiteSpace(description) || description.Contains("Generic", StringComparison.OrdinalIgnoreCase) ? "Écran" : description.Trim();

    /// <summary>« \\?\DISPLAY#AOCA610#5&amp;…&amp;UID4353#{…} » : stable tant que l'écran reste sur la même prise.</summary>
    private static string? InterfaceId(string device, int index)
    {
        var dd = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevices(device, (uint)index, ref dd, 1) || string.IsNullOrEmpty(dd.DeviceId)) return null;
        var parts = dd.DeviceId.Split('#');
        return parts.Length >= 3 ? $"{parts[1]}#{parts[2]}" : dd.DeviceId;
    }

    private void Release()
    {
        foreach (var m in _monitors) DestroyPhysicalMonitor(m.Handle);
        _monitors.Clear();
    }

    public void Dispose()
    {
        lock (_lock) Release();
    }

    // ---------- Win32 ----------

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint devNum, ref DisplayDevice dd, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr handle);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, byte[] caps, uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);
}
