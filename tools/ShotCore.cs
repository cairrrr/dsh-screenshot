// ShotCore.cs - shared screenshot engine used by shot.exe (CLI) and shotd.exe (tray daemon).
// Compiled by build.ps1 with /codepage:65001, so the Chinese UI strings below are
// read as UTF-8 even though the file carries no BOM.
//
// Three modes:
//   Full    all monitors
//   Window  pick a window: hover highlights it, click captures it (Esc / right click cancels)
//   Region  drag a box (Esc / right click cancels)
//
// Overlay drawing note: the selection outline lives on its own colour-keyed window placed
// above the dimming window. Both are needed - a window using Form.Opacity blends *every*
// pixel it draws, which is what made the old outline look faint. Keeping the outline on a
// separate transparent window lets it render at full strength.
//
// Written against the .NET Framework 4.0 compiler: no C# 6+ syntax.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Renders a window's own content into a DC, even when it is covered.</summary>
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // 2 = PROCESS_PER_MONITOR_DPI_AWARE. Without this a scaled display is captured
    // at 1/scale (1707x1067 instead of 2560x1600 on a 150% display).
    // Must run before any screen geometry is read.
    public static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwareness(2); }
        catch (Exception) { }
    }

    public static bool TryGetForegroundWindowRect(out RECT rect)
    {
        rect = new RECT();
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        return TryGetWindowRect(h, out rect);
    }

    /// <summary>Visible frame of a window: DWM extended bounds when available.</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
    {
        rect = new RECT();
        if (hwnd == IntPtr.Zero) return false;

        // 9 = DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame, without the invisible
        // resize border that GetWindowRect includes on Win10/11.
        try
        {
            RECT r;
            if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                rect = r;
                return true;
            }
        }
        catch (Exception) { }

        return GetWindowRect(hwnd, out rect);
    }

    public static void MakeTopMost(IntPtr hwnd)
    {
        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception) { }
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch (Exception) { return string.Empty; }
    }

    public static string GetWindowClass(IntPtr hwnd)
    {
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>True for windows the DWM has hidden (minimised UWP apps and friends).</summary>
    public static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            int cloaked;
            // 14 = DWMWA_CLOAKED
            if (DwmGetWindowAttributeInt(hwnd, 14, out cloaked, sizeof(int)) == 0) return cloaked != 0;
        }
        catch (Exception) { }
        return false;
    }

    public static bool IsPickableWindow(IntPtr hwnd, IntPtr exclude)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == exclude) return false;
            if (hwnd == GetShellWindow()) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (IsIconic(hwnd)) return false;
            if (IsCloaked(hwnd)) return false;
            if (GetWindowTitle(hwnd).Length == 0) return false;

            string cls = GetWindowClass(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                cls == "Shell_SecondaryTrayWnd" || cls == "Windows.UI.Core.CoreWindow")
            {
                return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Topmost pickable window containing the point. EnumWindows walks the top-level
    /// windows in z-order, so the first hit is the one visually on top; our own overlay
    /// is skipped by handle so it does not shadow everything underneath it.
    /// </summary>
    public static IntPtr WindowAtPoint(Point point, IntPtr exclude)
    {
        IntPtr found = IntPtr.Zero;

        try
        {
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (found != IntPtr.Zero) return false;
                if (!IsPickableWindow(hWnd, exclude)) return true;

                RECT r;
                if (!TryGetWindowRect(hWnd, out r)) return true;

                Rectangle rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (!rect.Contains(point)) return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception) { }

        return found;
    }
}

/// <summary>
/// Draws a full-strength outline (and label) over everything else. Colour-keyed, so the
/// areas it does not paint are see-through and mouse messages fall straight through to
/// the dimming window underneath; WS_EX_NOACTIVATE keeps focus where it was.
/// </summary>
internal sealed class HighlightForm : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private Color lineColor = Color.FromArgb(255, 47, 123, 246);
    private int lineWidth = 4;
    private Rectangle frame = Rectangle.Empty;
    private string label = string.Empty;
    private bool active;
    // This window spans the whole virtual screen and is never resized. Resizing a
    // colour-keyed layered window exposes regions that show up black until they are
    // repainted, which is exactly what happened while the frame followed the mouse.
    private Rectangle screenOrigin = Rectangle.Empty;
    private Rectangle lastDirty = Rectangle.Empty;

    public HighlightForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;
        Bounds = new Rectangle(-20, -20, 1, 1);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: keep it out of alt-tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Call once before Show: fixes the window to the whole virtual screen.</summary>
    public void Initialize(Rectangle screen)
    {
        screenOrigin = screen;
        Bounds = screen;
    }

    public void ShowFrame(Rectangle screenFrame, string text, Color color, int width)
    {
        lineColor = color;
        lineWidth = width;
        frame = screenFrame;
        label = text == null ? string.Empty : text;
        active = true;
        RepaintFrameArea();
    }

    public void ClearFrame()
    {
        active = false;
        label = string.Empty;
        RepaintFrameArea();
        frame = Rectangle.Empty;
    }

    /// <summary>Invalidates the union of the previous and the new frame area.</summary>
    private void RepaintFrameArea()
    {
        Rectangle dirty = DirtyRect();
        if (!lastDirty.IsEmpty) dirty = Rectangle.Union(dirty, lastDirty);
        lastDirty = DirtyRect();

        if (dirty.Width > 0 && dirty.Height > 0) Invalidate(dirty);
    }

    /// <summary>Client-space area the outline and its label can touch.</summary>
    private Rectangle DirtyRect()
    {
        if (!active || frame.Width <= 0 || frame.Height <= 0) return Rectangle.Empty;

        Rectangle local = new Rectangle(
            frame.X - screenOrigin.X, frame.Y - screenOrigin.Y, frame.Width, frame.Height);
        local.Inflate(lineWidth + 6, lineWidth + 6);
        local.Intersect(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)));
        return local;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Repaint only the invalidated strip and fill it with the key colour, so the rest
        // of this full-screen window stays transparent. Clearing the whole surface on
        // every mouse move would be far too expensive.
        using (SolidBrush key = new SolidBrush(Color.Fuchsia))
        {
            e.Graphics.FillRectangle(key, e.ClipRectangle);
        }
        if (!active || frame.Width <= 0 || frame.Height <= 0) return;

        Rectangle local = new Rectangle(
            frame.X - screenOrigin.X, frame.Y - screenOrigin.Y, frame.Width, frame.Height);

        ShotCore.DrawFrameOutline(e.Graphics, local, label, lineColor, lineWidth, ClientSize);
    }
}

internal static class ShotCore
{
    public static readonly string[] Modes = new string[] { "Full", "Window", "Region" };

    /// <summary>Frame colours; settable from hotkeys.ini (RegionColor / WindowColor).</summary>
    public static Color RegionColor = Color.FromArgb(255, 255, 64, 64);
    public static Color WindowColor = Color.FromArgb(255, 255, 64, 64);
    /// <summary>Outline thickness for both selection overlays; settable from hotkeys.ini.</summary>
    public static int OutlineWidth = 2;

    public static string DefaultOutDir()
    {
        // the exes live in <workspace>\tools, shots go to <workspace>\shots
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        return Path.Combine(Path.GetDirectoryName(exeDir), "shots");
    }

    // Returns the saved file path, or null when the user cancelled the picking step.
    public static string Take(string mode, string outDir, bool toClipboard, bool sound)
    {
        Rectangle screen = SystemInformation.VirtualScreen;
        Bitmap bmp = null;

        try
        {
            if (mode == "Region")
            {
                Rectangle? picked = SelectRegion(screen);
                if (!picked.HasValue) return null;
                bmp = Capture(picked.Value);
            }
            else if (mode == "Window")
            {
                IntPtr pickedHwnd;
                Rectangle? picked = SelectWindow(screen, out pickedHwnd);
                if (!picked.HasValue) return null;

                Rectangle target = picked.Value;
                target.Intersect(screen);
                if (target.Width < 8 || target.Height < 8) target = screen;
                bmp = CaptureWindowContent(pickedHwnd, target);
            }
            else
            {
                bmp = Capture(screen);
            }

            Directory.CreateDirectory(outDir);

            string file = UniquePath(Path.Combine(outDir, string.Format(CultureInfo.InvariantCulture,
                "shot-{0:yyyyMMdd-HHmmss}-{1}.png", DateTime.Now, mode.ToLowerInvariant())));

            bmp.Save(file, ImageFormat.Png);
            File.Copy(file, Path.Combine(outDir, "latest.png"), true);

            if (toClipboard) CopyToClipboard(bmp);
            if (sound) PlayShutter();

            return file;
        }
        finally
        {
            if (bmp != null) bmp.Dispose();
        }
    }

    public static void Prune(string dir, int keep)
    {
        if (keep <= 0) return;
        try
        {
            FileInfo[] files = new DirectoryInfo(dir).GetFiles("shot-*.png");
            if (files.Length <= keep) return;

            Array.Sort(files, delegate(FileInfo a, FileInfo b)
            {
                return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            });

            for (int i = keep; i < files.Length; i++)
            {
                try { files[i].Delete(); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private static void CopyToClipboard(Bitmap bmp)
    {
        // Another process can hold the clipboard open; never fail a shot over it.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetImage(bmp); return; }
            catch (Exception) { System.Threading.Thread.Sleep(40); }
        }
    }

    private static void PlayShutter()
    {
        try { System.Media.SystemSounds.Asterisk.Play(); }
        catch (Exception) { }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        for (int n = 2; n < 1000; n++)
        {
            string candidate = Path.Combine(dir, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".png");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static Bitmap Capture(Rectangle rect)
    {
        Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    /// <summary>
    /// Captures a window's own content. PrintWindow asks the window to draw itself, so
    /// whatever happens to be covering it on screen does not end up in the picture. When
    /// that yields nothing usable (some hardware-accelerated or minimised windows refuse),
    /// it falls back to grabbing the screen rectangle instead.
    /// </summary>
    private static Bitmap CaptureWindowContent(IntPtr hwnd, Rectangle fallback)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                Native.RECT r;
                if (Native.TryGetWindowRect(hwnd, out r))
                {
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w > 8 && h > 8 && w < 20000 && h < 20000)
                    {
                        Bitmap shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        bool ok;
                        using (Graphics g = Graphics.FromImage(shot))
                        {
                            IntPtr hdc = g.GetHdc();
                            try { ok = Native.PrintWindow(hwnd, hdc, 2); }   // PW_RENDERFULLCONTENT
                            finally { g.ReleaseHdc(hdc); }
                        }
                        if (ok && !LooksEmpty(shot)) return shot;
                        shot.Dispose();
                    }
                }
            }
            catch (Exception) { }
        }
        return Capture(fallback);
    }

    /// <summary>
    /// True when PrintWindow claimed success but drew nothing: the bitmap stays fully
    /// transparent or pure black. A uniformly *coloured* window is a perfectly good
    /// capture, so uniform does not count as empty.
    /// </summary>
    private static bool LooksEmpty(Bitmap bmp)
    {
        try
        {
            int stepX = Math.Max(1, bmp.Width / 24);
            int stepY = Math.Max(1, bmp.Height / 24);

            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.A != 0 && (c.R > 8 || c.G > 8 || c.B > 8)) return false;
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The dim layer: uniform dark, receives all mouse and key input.</summary>
    private static Form CreateDimOverlay(Rectangle screen, Cursor cursor, double opacity)
    {
        Form dim = new Form();
        dim.FormBorderStyle = FormBorderStyle.None;
        dim.StartPosition = FormStartPosition.Manual;
        dim.AutoScaleMode = AutoScaleMode.None;
        dim.ShowInTaskbar = false;
        dim.TopMost = true;
        dim.KeyPreview = true;
        dim.Cursor = cursor;
        dim.BackColor = Color.FromArgb(255, 10, 10, 14);
        dim.Opacity = opacity;
        dim.Bounds = screen;
        return dim;
    }

    private static void PaintDim(object sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(255, 12, 12, 12));
    }

    /// <summary>
    /// Outline plus its label, drawn exactly the way the original region overlay drew it:
    /// a coloured rectangle and a line of white text at its top-left corner. Shared by the
    /// region overlay and the window picker.
    /// </summary>
    internal static void DrawFrameOutline(Graphics g, Rectangle local, string label, Color color, int width, Size client)
    {
        using (Pen pen = new Pen(color, width))
        {
            g.DrawRectangle(pen, local);
        }

        if (string.IsNullOrEmpty(label)) return;

        using (Font font = new Font("Segoe UI", 10f))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            g.DrawString(label, font, brush, local.X + 6, local.Y + 6);
        }
    }

    private static Rectangle? SelectRegion(Rectangle screen)
    {
        Rectangle selection = Rectangle.Empty;
        Point anchor = Point.Empty;
        bool dragging = false;
        bool accepted = false;

        // The dim layer is painted once and never invalidated again: its content never
        // changes. The moving outline lives on the full-opacity highlight window, and only
        // the strip it actually touched is repainted. Doing BOTH per mouse move - a
        // full-screen dim repaint plus a second full-screen window - is what made dragging
        // stutter before; the outline's washed-out look came from the dim layer's 22%
        // opacity, which is why it now lives on its own window.
        Form dim = CreateDimOverlay(screen, Cursors.Cross, 0.22);
        dim.Paint += PaintDim;

        HighlightForm highlight = new HighlightForm();
        highlight.Initialize(screen);

        dim.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                anchor = e.Location;
                dragging = true;
                selection = Rectangle.Empty;
                highlight.ClearFrame();
            }
            else if (e.Button == MouseButtons.Right)
            {
                accepted = false;
                dim.Close();
            }
        };

        dim.MouseMove += delegate(object s, MouseEventArgs e)
        {
            if (!dragging) return;

            selection = new Rectangle(
                Math.Min(anchor.X, e.X),
                Math.Min(anchor.Y, e.Y),
                Math.Abs(anchor.X - e.X),
                Math.Abs(anchor.Y - e.Y));

            highlight.ShowFrame(
                new Rectangle(selection.X + screen.X, selection.Y + screen.Y,
                    selection.Width, selection.Height),
                selection.Width.ToString(CultureInfo.InvariantCulture) + " x " +
                selection.Height.ToString(CultureInfo.InvariantCulture),
                RegionColor, OutlineWidth);
        };

        dim.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = false;
            if (selection.Width >= 8 && selection.Height >= 8)
            {
                accepted = true;
                dim.Close();
            }
            else
            {
                selection = Rectangle.Empty;
                highlight.ClearFrame();
            }
        };

        dim.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                accepted = false;
                dim.Close();
            }
        };

        // shown from the dim window's Shown event, so the outline sits above the dim
        dim.Shown += delegate(object s, EventArgs e)
        {
            highlight.Show();
            Native.MakeTopMost(highlight.Handle);
            dim.Activate();
        };

        dim.FormClosed += delegate(object s, FormClosedEventArgs e)
        {
            try { highlight.Close(); highlight.Dispose(); } catch (Exception) { }
        };

        dim.ShowDialog();

        if (!accepted) return null;
        System.Threading.Thread.Sleep(120);         // let the overlay disappear before grabbing
        return new Rectangle(selection.X + screen.X, selection.Y + screen.Y, selection.Width, selection.Height);
    }

    /// <summary>Hover a window to highlight it, click to capture it.</summary>
    private static Rectangle? SelectWindow(Rectangle screen, out IntPtr pickedHwnd)
    {
        IntPtr hovered = IntPtr.Zero;
        Rectangle hoveredFrame = Rectangle.Empty;
        bool accepted = false;
        IntPtr chosen = IntPtr.Zero;
        pickedHwnd = IntPtr.Zero;

        Form dim = CreateDimOverlay(screen, Cursors.Hand, 0.30);
        HighlightForm highlight = new HighlightForm();
        highlight.Initialize(screen);

        dim.MouseMove += delegate(object s, MouseEventArgs e)
        {
            Point onScreen = new Point(e.X + screen.X, e.Y + screen.Y);
            IntPtr h = Native.WindowAtPoint(onScreen, dim.Handle);

            if (h == hovered) return;
            hovered = h;

            Native.RECT r;
            if (h != IntPtr.Zero && Native.TryGetWindowRect(h, out r))
            {
                hoveredFrame = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

                string title = Native.GetWindowTitle(h);
                if (title.Length > 70) title = title.Substring(0, 70) + "…";
                string text = title + "    " +
                    hoveredFrame.Width.ToString(CultureInfo.InvariantCulture) + " x " +
                    hoveredFrame.Height.ToString(CultureInfo.InvariantCulture);

                highlight.ShowFrame(hoveredFrame, text, WindowColor, OutlineWidth);
            }
            else
            {
                hoveredFrame = Rectangle.Empty;
                highlight.ClearFrame();
            }
        };

        dim.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                accepted = false;
                dim.Close();
                return;
            }
            if (e.Button == MouseButtons.Left && hovered != IntPtr.Zero && hoveredFrame.Width > 0)
            {
                chosen = hovered;
                accepted = true;
                dim.Close();
            }
        };

        dim.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                accepted = false;
                dim.Close();
            }
        };

        dim.Paint += PaintDim;

        dim.Shown += delegate(object s, EventArgs e)
        {
            highlight.Show();
            Native.MakeTopMost(highlight.Handle);
            dim.Activate();
        };

        dim.FormClosed += delegate(object s, FormClosedEventArgs e)
        {
            try { highlight.Close(); highlight.Dispose(); } catch (Exception) { }
        };

        dim.ShowDialog();

        if (!accepted || hoveredFrame.Width <= 0) return null;
        System.Threading.Thread.Sleep(120);         // let the overlay disappear before grabbing
        pickedHwnd = chosen;
        return hoveredFrame;
    }
}
