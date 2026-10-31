using System.Runtime.InteropServices;

namespace VoiceAec;

// Borderless resizable window: keeps the sizing frame (so all edges/corners
// still resize natively) but removes the native caption. The caption area is
// drawn by the web panel itself (drag region + custom window buttons).
// The remaining top frame strip is recolored to the panel's top pink via
// DWMWA_CAPTION_COLOR — hiding it would need client-covers-whole-window,
// which kills edge resizing because WM_NCHITTEST for pixels over the
// WebView2 child never reaches this form.
internal sealed class PanelForm : Form
{
    private const int WsCaption = 0x00C00000;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmSize = 0x0005;
    private const int SizeRestored = 0;
    private const int SizeMaximized = 2;
    private const int HtCaption = 2;
    private const int HtTop = 12;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~WsCaption;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // While maximized the sizing frame would leave a visible border/gap on
        // every screen edge, so let the client area cover the whole window.
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero && IsZoomed(Handle))
        {
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == WmGetMinMaxInfo && m.LParam != IntPtr.Zero)
        {
            // The sizing frame stays enabled for edge/corner resizing. A plain
            // borderless maximize would extend past the work area by the frame
            // thickness (and over the taskbar). Only clamp the maximized size and
            // keep the position relative to the current monitor, so multi-monitor
            // maximize never teleports to the neighbouring screen.
            var screen = Screen.FromHandle(Handle);
            var work = screen.WorkingArea;
            var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            info.MaxPosition.X = work.Left - screen.Bounds.Left;
            info.MaxPosition.Y = work.Top - screen.Bounds.Top;
            info.MaxSize.X = work.Width;
            info.MaxSize.Y = work.Height;
            Marshal.StructureToPtr(info, m.LParam, false);
        }
        else if (m.Msg == WmSize && (m.WParam == (IntPtr)SizeMaximized || m.WParam == (IntPtr)SizeRestored))
        {
            // Re-run WM_NCCALCSIZE so the maximized client area becomes
            // borderless (and the frame returns after restoring).
            _ = SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        else if (m.Msg == WmNcHitTest && !IsZoomed(Handle) && m.Result == (IntPtr)HtTop)
        {
            // The top sizing border is only a few pixels tall; make its middle
            // part behave like a title bar so dragging at the very top moves the
            // window. Corners/edges stay resizable.
            var value = m.LParam.ToInt64();
            var x = (short)(value & 0xFFFF);
            var bounds = Bounds;
            if (x > bounds.Left + 120 && x < bounds.Right - 120)
                m.Result = (IntPtr)HtCaption;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowTheme.ApplyPanelChrome(Handle);
    }
}

// Native window cosmetics: light rounded frame matching the panel palette.
// Caption colour is no longer used because the native caption is gone.
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;

    /// <summary>
    /// Starts the native caption move loop. On a maximized window Windows
    /// restores it first and keeps it under the cursor, matching the behaviour
    /// of a normal title bar (including edge snapping).
    /// </summary>
    internal static void BeginWindowDrag(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ReleaseCapture();
            SendMessage(hwnd, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
        catch
        {
            // user32 is always present; keep the panel usable on unexpected errors.
        }
    }

    internal static void ApplyPanelChrome(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            // Keep the light theme so the thin frame/shadow stay warm.
            var light = 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref light, sizeof(int));

            if (Environment.OSVersion.Version.Build < 22000) return;

            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            var border = ColorRef(0xF6, 0xE4, 0xEC);   // #F6E4EC soft pink edge
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            // The residual caption strip above the panel (resize frame, only
            // visible when restored) defaults to light gray; paint it the same
            // pink as the page's top wash so it blends in seamlessly.
            var caption = ColorRef(0xFC, 0xDC, 0xEC);
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch
        {
            // dwmapi is not available on very old systems; keep the default chrome.
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
