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
    public IReadOnlyList<int> Inputs { get; init; } = [];
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

    public IReadOnlyList<Monitor> Monitors { get { lock (_lock) return [.. _monitors]; } }

    /// <summary>Relit la liste des écrans (lent la première fois : quelques secondes par écran).</summary>
    public IReadOnlyList<Monitor> Refresh()
    {
        lock (_lock)
        {
            Release();
            var found = new List<(IntPtr HMon, string Device, bool Primary)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(h, ref mi)) found.Add((h, mi.Device, (mi.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);

            foreach (var (hmon, device, primary) in found.OrderBy(f => DisplayNumber(f.Device)))
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) continue;
                var arr = new PhysicalMonitor[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr)) continue;
                for (int i = 0; i < arr.Length; i++)
                {
                    string key = InterfaceId(device, i) ?? $"{device}#{i}";
                    if (!_caps.TryGetValue(key, out var caps))
                        _caps[key] = caps = ReadCaps(arr[i].Handle);
                    int? cur = ReadInput(arr[i].Handle);
                    var inputs = Inputs.FromCapabilities(caps);
                    if (inputs.Count == 0 && cur is int c) inputs = [c]; // écran muet sur ses capacités mais qui répond
                    _monitors.Add(new Monitor
                    {
                        Key = key, Device = device, IsPrimary = primary, Number = DisplayNumber(device),
                        Name = Inputs.Model(caps) ?? FriendlyName(arr[i].Description),
                        Inputs = inputs, Current = cur, Handle = arr[i].Handle,
                    });
                }
            }
            return [.. _monitors];
        }
    }

    /// <summary>Lit l'entrée affichée maintenant (null si l'écran ne répond pas).</summary>
    public int? ReadCurrent(Monitor m)
    {
        lock (_lock)
        {
            m.Current = ReadInput(m.Handle) ?? m.Current;
            return m.Current;
        }
    }

    /// <summary>
    /// Demande à l'écran de passer sur <paramref name="input"/>. Si Windows a perdu l'écran entre-temps (certains écrans
    /// « débranchent » le DisplayPort quand ils affichent une autre entrée), on le recherche par sa clé et on réessaie.
    /// </summary>
    public bool Switch(Monitor m, int input)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var target = _monitors.FirstOrDefault(x => x.Key == m.Key) ?? m;
                if (SetVCPFeature(target.Handle, Inputs.InputSourceCode, (uint)input))
                {
                    target.Current = m.Current = input;
                    return true;
                }
                Thread.Sleep(150);
                if (attempt == 1) Refresh(); // le verrou est réentrant ; les capacités restent en cache
            }
            return false;
        }
    }

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
    private struct Rect { public int Left, Top, Right, Bottom; }

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
