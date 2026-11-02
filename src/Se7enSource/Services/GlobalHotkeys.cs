using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Se7enSource.Services;

/// <summary>
/// Raccourcis clavier qui marchent même quand on est en jeu (RegisterHotKey).
/// <para>
/// Pour Windows, AltGr = Ctrl+Alt : sur un clavier français, AltGr+à (@), AltGr+é (~), AltGr+" (#)…
/// tomberaient sur nos raccourcis. Quand le raccourci arrive avec Alt Gr (Alt droit) enfoncé,
/// on le rend tel quel : le raccourci est retiré un instant et la touche est renvoyée à l'appli au premier
/// plan, qui reçoit son caractère. Les raccourcis se font donc avec Ctrl + Alt gauche + Maj.
/// </para>
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    // Ctrl+Alt+Maj : pas de conflit avec les raccourcis Ctrl+Alt de se7en Voice
    private const uint Modifiers = ModControl | ModAlt | ModShift | ModNoRepeat;
    private const int VkRightAlt = 0xA5;

    private readonly HwndSource _source;
    private readonly Dictionary<int, (uint Vk, Action Action)> _keys = [];
    private int _nextId = 0x5E70;

    public GlobalHotkeys(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Enregistre Ctrl+Alt+Maj+<paramref name="key"/>. Retourne false si une autre app l'utilise déjà.</summary>
    public bool Register(Key key, Action action)
    {
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, Modifiers, vk)) return false;
        _keys[id] = (vk, action);
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _keys.TryGetValue(wParam.ToInt32(), out var hk))
        {
            if (IsDown(VkRightAlt)) GiveBack(wParam.ToInt32(), hk.Vk); // AltGr : un caractère, pas un raccourci
            else hk.Action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Rend la touche à l'appli au premier plan (Alt Gr toujours enfoncé : elle tape son caractère).</summary>
    private void GiveBack(int id, uint vk)
    {
        UnregisterHotKey(_source.Handle, id);
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var inputs = new[]
        {
            KeyInput((ushort)vk, scan, 0),
            KeyInput((ushort)vk, scan, KeyEventKeyUp),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        // remis en place une fois la frappe passée (sinon on la rattraperait nous-mêmes)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_keys.ContainsKey(id) && !RegisterHotKey(_source.Handle, id, Modifiers, vk))
                _keys.Remove(id);
        };
        timer.Start();
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        foreach (var id in _keys.Keys) UnregisterHotKey(_source.Handle, id);
        _keys.Clear();
        _source.RemoveHook(Hook);
    }

    // ---------- Win32 ----------

    private const uint InputKeyboard = 1, KeyEventKeyUp = 0x2;

    private static Input KeyInput(ushort vk, ushort scan, uint flags) =>
        new() { Type = InputKeyboard, U = new InputUnion { Ki = new KeybdInput { Vk = vk, Scan = scan, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Ki;
        [FieldOffset(0)] public MouseInput Mi; // donne à l'union la taille attendue par SendInput
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
