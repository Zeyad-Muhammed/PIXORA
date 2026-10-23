using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using PIXORA.App.Interop;
using PIXORA.Core;

namespace PIXORA.App.Services;

public enum HotkeyAction { RegionCapture = 1, FullScreenCapture, WindowCapture, ToggleRecording, OpenEditor }

public enum TrayCommand { Open = 100, CaptureRegion, CaptureWindow, CaptureFullScreen, ToggleRecording, OpenLibrary, Settings, Quit }

/// <summary>
/// Owns a hidden Win32 window on its own thread for global hotkeys and the notification-area (tray) icon.
/// Events are marshalled back to the UI thread.
/// </summary>
public sealed class ShellHost : IDisposable
{
    private const uint TrayCallback = Win32.WM_APP + 1;
    private const uint MsgApplyHotkeys = Win32.WM_APP + 2;
    private const uint MsgTray = Win32.WM_APP + 3;
    private const uint MsgBalloon = Win32.WM_APP + 4;
    private const uint MsgQuit = Win32.WM_APP + 5;

    private readonly DispatcherQueue _ui;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Win32.WndProc? _proc; // keep alive for the native callback
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _trayVisible;
    private bool _recording;
    private HotkeySettings _pendingHotkeys = new();
    private bool _wantTray;
    private (string Title, string Text, uint Flags) _pendingBalloon;
    private readonly object _gate = new();

    public event Action<HotkeyAction>? HotkeyPressed;
    public event Action<TrayCommand>? TrayInvoked;

    /// <summary>Shortcuts that could not be registered because another app owns them.</summary>
    public IReadOnlyList<string> HotkeyConflicts { get; private set; } = Array.Empty<string>();

    public ShellHost(DispatcherQueue ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "PIXORA shell host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
    }

    private void Run()
    {
        _proc = WndProc;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = "PIXORA.ShellHost",
        };
        Win32.RegisterClassEx(ref wc);
        _hwnd = Win32.CreateWindowEx(0, wc.lpszClassName, "PIXORA Shell Host", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "pixora.ico");
        _icon = Win32.LoadImage(IntPtr.Zero, iconPath, Win32.IMAGE_ICON, 32, 32, Win32.LR_LOADFROMFILE);
        _ready.Set();
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    public void ApplyHotkeys(HotkeySettings hotkeys)
    {
        lock (_gate) _pendingHotkeys = hotkeys;
        Win32.PostMessage(_hwnd, MsgApplyHotkeys, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetTrayVisible(bool visible)
    {
        lock (_gate) _wantTray = visible;
        Win32.PostMessage(_hwnd, MsgTray, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetRecording(bool recording)
    {
        _recording = recording;
        Win32.PostMessage(_hwnd, MsgTray, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Shows a notification balloon from the tray icon (requires the tray icon to be visible).</summary>
    public void Notify(string title, string text, bool warning = false)
    {
        lock (_gate) _pendingBalloon = (title, text, warning ? Win32.NIIF_WARNING : Win32.NIIF_INFO);
        Win32.PostMessage(_hwnd, MsgBalloon, IntPtr.Zero, IntPtr.Zero);
    }

    public bool TrayVisible => _trayVisible;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                var action = (HotkeyAction)wParam.ToInt32();
                _ui.TryEnqueue(() => HotkeyPressed?.Invoke(action));
                return IntPtr.Zero;
            case MsgApplyHotkeys:
                RegisterHotkeys();
                return IntPtr.Zero;
            case MsgTray:
                UpdateTray();
                return IntPtr.Zero;
            case MsgBalloon:
                ShowBalloon();
                return IntPtr.Zero;
            case MsgQuit:
                RemoveTray();
                for (int i = 1; i <= 5; i++) Win32.UnregisterHotKey(_hwnd, i);
                Win32.DestroyWindow(_hwnd);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            case TrayCallback:
                int evt = lParam.ToInt32() & 0xFFFF;
                if (evt is Win32.WM_LBUTTONUP or Win32.WM_LBUTTONDBLCLK)
                    _ui.TryEnqueue(() => TrayInvoked?.Invoke(TrayCommand.Open));
                else if (evt is Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU)
                    ShowMenu();
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RegisterHotkeys()
    {
        HotkeySettings hk;
        lock (_gate) hk = _pendingHotkeys;
        for (int i = 1; i <= 5; i++) Win32.UnregisterHotKey(_hwnd, i);
        var conflicts = new List<string>();
        void Reg(HotkeyAction a, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !HotkeyParser.TryParse(text, out var mods, out var vk) || vk == 0) return;
            if (!Win32.RegisterHotKey(_hwnd, (int)a, (uint)mods | Win32.MOD_NOREPEAT, vk))
            {
                conflicts.Add(text);
                Log.Warn($"Hotkey {text} is already in use by another application.");
            }
        }
        Reg(HotkeyAction.RegionCapture, hk.RegionCapture);
        Reg(HotkeyAction.FullScreenCapture, hk.FullScreenCapture);
        Reg(HotkeyAction.WindowCapture, hk.WindowCapture);
        Reg(HotkeyAction.ToggleRecording, hk.ToggleRecording);
        Reg(HotkeyAction.OpenEditor, hk.OpenEditor);
        HotkeyConflicts = conflicts;
    }

    private Win32.NOTIFYICONDATA BaseData() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = TrayCallback,
        hIcon = _icon,
        szTip = _recording ? "PIXORA Capture Studio — recording" : "PIXORA Capture Studio",
        szInfo = "",
        szInfoTitle = "",
    };

    private void UpdateTray()
    {
        bool want;
        lock (_gate) want = _wantTray;
        var d = BaseData();
        d.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        if (want && !_trayVisible)
        {
            _trayVisible = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
            d.uVersion = 4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref d);
        }
        else if (want) Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
        else if (!want && _trayVisible) RemoveTray();
    }

    private void RemoveTray()
    {
        if (!_trayVisible) return;
        var d = BaseData();
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
        _trayVisible = false;
    }

    private void ShowBalloon()
    {
        if (!_trayVisible) return;
        (string title, string text, uint flags) b;
        lock (_gate) b = _pendingBalloon;
        var d = BaseData();
        d.uFlags = Win32.NIF_INFO;
        d.szInfoTitle = b.title.Length > 63 ? b.title[..63] : b.title;
        d.szInfo = b.text.Length > 255 ? b.text[..255] : b.text;
        d.dwInfoFlags = b.flags;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
    }

    private void ShowMenu()
    {
        var menu = Win32.CreatePopupMenu();
        void Item(TrayCommand c, string text) => Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)c, text);
        Item(TrayCommand.Open, "Open PIXORA");
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
        Item(TrayCommand.CaptureRegion, "Capture region");
        Item(TrayCommand.CaptureWindow, "Capture window");
        Item(TrayCommand.CaptureFullScreen, "Capture full screen");
        Item(TrayCommand.ToggleRecording, _recording ? "Stop recording" : "Record screen");
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
        Item(TrayCommand.OpenLibrary, "Library");
        Item(TrayCommand.Settings, "Settings");
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
        Item(TrayCommand.Quit, "Quit PIXORA");

        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(_hwnd);
        uint cmd = Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        if (cmd != 0) _ui.TryEnqueue(() => TrayInvoked?.Invoke((TrayCommand)cmd));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, MsgQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
    }
}
