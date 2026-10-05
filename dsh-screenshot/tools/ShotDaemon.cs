// ShotDaemon.cs - resident tray app owning the screenshot hotkeys (shotd.exe).
//
// Why a resident process instead of a shortcut hotkey: on a machine where creating a
// process costs seconds, a hotkey that spawns an .exe per press is never fast. This
// process registers the hotkeys itself and captures in-process (~100 ms per press).
//
// It also serves a tiny loopback HTTP API so other local tools (the DSH web GUI
// plugin) can trigger the same instant capture:
//
//   GET http://127.0.0.1:<port>/shot?mode=Full|Window|Region
//     200 image/png + X-Shot-Name header, or 204 when the region drag was cancelled
//
// Requests carrying an Origin header are refused: a browser page must not be able to
// fire screenshots at this port. Only local processes talk to it.
//
// Hotkeys come from hotkeys.ini next to this exe (written with defaults if missing).
// A configured combination that another program already owns is not fatal: the daemon
// walks a fallback list and reports the combination it actually won in
// shots\daemon.txt, which is also where the tray menu reads it from.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// A plain hidden Control: it gives us a window handle for WM_HOTKEY *and* BeginInvoke,
// which is how the HTTP thread hands work to the UI thread.
internal sealed class UiWindow : Control
{
    private const int WM_HOTKEY = 0x0312;
    public event Action<int> Hotkey;

    public UiWindow()
    {
        IntPtr unused = Handle;      // force handle creation
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            Action<int> handler = Hotkey;
            if (handler != null) handler(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }
}

internal static class Daemon
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int BASE_ID = 100;
    private const int DEFAULT_PORT = 38901;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- window hiding (QQ-style: get the window you are looking at out of the shot) ---
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    // Full / Window / Region
    private static readonly string[] Modes = new string[] { "Full", "Window", "Region" };
    private static readonly string[] Labels = new string[] { "全屏截图", "窗口截图", "框选截图" };
    private static readonly string[] DefaultHotkeys = new string[] { "Ctrl+Alt+F1", "Ctrl+Alt+F2", "Ctrl+Alt+F3" };

    private static readonly string[] Configured = new string[3];
    private static readonly string[] Resolved = new string[3];
    private static readonly bool[] Registered = new bool[3];
    private static readonly uint[] ResolvedMods = new uint[3];
    private static readonly uint[] ResolvedVk = new uint[3];

    private static UiWindow window;
    private static NotifyIcon tray;
    private static string outDir;
    private static string statusFile;
    private static string configFile;
    private static int port = DEFAULT_PORT;
    private static int shotCount;
    private static bool busy;
    // set per request by HandleRequest; requests are served one at a time
    private static string currentAllowOrigin;
    // "foreground" | "none" | "title:<substring>" - see ShouldHideFor
    private static string hideBeforeCapture = "foreground";
    private static ToolStripMenuItem hideMenuItem;
    private static ToolStripMenuItem colorMenuItem;
    private static ToolStripMenuItem widthMenuItem;

    // Tray presets: one click sets the outline thickness (and writes it to hotkeys.ini).
    private static readonly int[] WidthPresets = new int[] { 2, 4, 6 };
    private static readonly string[] WidthLabels = new string[] { "细  2 像素", "中  4 像素", "粗  6 像素" };

    // Tray presets: one click sets both frame colours (and writes them to hotkeys.ini).
    private static readonly string[][] ColorPresets = new string[][]
    {
        new string[] { "红  #ff4040", "#ff4040" },
        new string[] { "正红  #ff0000", "#ff0000" },
        new string[] { "橙  #ff8f00", "#ff8f00" },
        new string[] { "黄  #ffc400", "#ffc400" },
        new string[] { "绿  #00c853", "#00c853" },
        new string[] { "青  #00bcd4", "#00bcd4" },
        new string[] { "蓝  #2f7bf6", "#2f7bf6" },
        new string[] { "紫  #a855f7", "#a855f7" },
        new string[] { "粉  #ff5fa2", "#ff5fa2" },
        new string[] { "白  #ffffff", "#ffffff" }
    };

    [STAThread]
    private static void Main(string[] args)
    {
        bool isNew;
        using (Mutex mutex = new Mutex(true, "Local\\DshShotDaemon", out isNew))
        {
            if (!isNew) return;                       // one instance only

            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string toolsDir = Path.GetDirectoryName(Application.ExecutablePath);
            outDir = ShotCore.DefaultOutDir();
            statusFile = Path.Combine(outDir, "daemon.txt");
            configFile = Path.Combine(toolsDir, "hotkeys.ini");
            try { Directory.CreateDirectory(outDir); } catch (Exception) { }

            LoadConfig();

            window = new UiWindow();
            window.Hotkey += OnHotkeyPressed;
            RegisterAll();

            BuildTray();
            WriteStatus();
            Welcome();
            StartHttpServer();

            Application.Run();

            for (int i = 0; i < Modes.Length; i++)
            {
                if (Registered[i]) UnregisterHotKey(window.Handle, BASE_ID + i);
            }
            if (tray != null) tray.Visible = false;
        }
    }

    // --- hotkeys -------------------------------------------------------------

    private static void RegisterAll()
    {
        List<string> fallbacks = BuildFallbackList();

        for (int i = 0; i < Modes.Length; i++)
        {
            List<string> candidates = new List<string>();
            candidates.Add(Configured[i]);
            candidates.AddRange(fallbacks);

            for (int c = 0; c < candidates.Count; c++)
            {
                uint mods, vk;
                if (!TryParseHotkey(candidates[c], out mods, out vk)) continue;
                if (RegisterHotKey(window.Handle, BASE_ID + i, mods | MOD_NOREPEAT, vk))
                {
                    Resolved[i] = candidates[c];
                    ResolvedMods[i] = mods;
                    ResolvedVk[i] = vk;
                    Registered[i] = true;
                    break;
                }
            }
        }
    }

    private static List<string> BuildFallbackList()
    {
        List<string> list = new List<string>();
        for (int f = 1; f <= 12; f++)
        {
            list.Add("Ctrl+Alt+F" + f.ToString(CultureInfo.InvariantCulture));
        }
        for (int f = 1; f <= 12; f++)
        {
            list.Add("Ctrl+Shift+F" + f.ToString(CultureInfo.InvariantCulture));
        }
        return list;
    }

    private static void LoadConfig()
    {
        for (int i = 0; i < Modes.Length; i++) Configured[i] = DefaultHotkeys[i];
        port = DEFAULT_PORT;

        try
        {
            if (!File.Exists(configFile))
            {
                WriteDefaultConfig();
                return;
            }

            string[] lines = File.ReadAllLines(configFile, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;

                if (string.Compare(key, "Port", true, CultureInfo.InvariantCulture) == 0)
                {
                    int p;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                        && p >= 1024 && p <= 65535)
                    {
                        port = p;
                    }
                    continue;
                }

                if (string.Compare(key, "HideBeforeCapture", true, CultureInfo.InvariantCulture) == 0)
                {
                    hideBeforeCapture = NormalizeHideSpec(value);
                    continue;
                }

                if (string.Compare(key, "OutlineWidth", true, CultureInfo.InvariantCulture) == 0)
                {
                    int w;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out w)
                        && w >= 1 && w <= 20)
                    {
                        ShotCore.OutlineWidth = w;
                    }
                    continue;
                }

                if (string.Compare(key, "RegionColor", true, CultureInfo.InvariantCulture) == 0)
                {
                    Color c;
                    if (TryParseColor(value, out c)) ShotCore.RegionColor = c;
                    continue;
                }

                if (string.Compare(key, "WindowColor", true, CultureInfo.InvariantCulture) == 0)
                {
                    Color c;
                    if (TryParseColor(value, out c)) ShotCore.WindowColor = c;
                    continue;
                }

                for (int m = 0; m < Modes.Length; m++)
                {
                    if (string.Compare(key, Modes[m], true, CultureInfo.InvariantCulture) == 0)
                    {
                        Configured[m] = value;
                    }
                }
            }
        }
        catch (Exception) { }
    }

    private static void WriteDefaultConfig()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 截屏服务热键配置");
            sb.AppendLine("# 语法: 修饰键+修饰键+按键  例如 Ctrl+Alt+F1 / Ctrl+Shift+S / Alt+PrintScreen");
            sb.AppendLine("# 修饰键: Ctrl Alt Shift Win    按键: A-Z 0-9 F1-F24 PrintScreen Home End Insert Space");
            sb.AppendLine("# 改完保存, 然后在托盘菜单里选“重新加载热键配置”, 或重启截屏服务。");
            sb.AppendLine("# 若某个组合已被别的程序占用, 服务会自动换用其它空闲组合, 实际生效的键见 shots\\daemon.txt。");
            sb.AppendLine();
            sb.AppendLine("Full=" + DefaultHotkeys[0]);
            sb.AppendLine("Window=" + DefaultHotkeys[1]);
            sb.AppendLine("Region=" + DefaultHotkeys[2]);
            sb.AppendLine();
            sb.AppendLine("# 本机 HTTP 接口端口, 供 DSH Web 界面的截屏按钮调用 (仅监听 127.0.0.1)");
            sb.AppendLine("Port=" + DEFAULT_PORT.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("# 截图前是否先隐藏前台窗口 (QQ 那种“让开再截”的效果)。");
            sb.AppendLine("# 只对全屏和框选生效; 窗口截图永远不隐藏, 否则就没东西可截了。");
            sb.AppendLine("#   foreground        隐藏当前前台窗口 (默认)");
            sb.AppendLine("#   none              不隐藏");
            sb.AppendLine("#   title:关键字      只隐藏标题含该关键字的窗口, 例如 title:DeepSeek Harness");
            sb.AppendLine("HideBeforeCapture=foreground");
            sb.AppendLine();
            sb.AppendLine("# 框选和选窗口时高亮框线的粗细 (像素, 1-20, 默认 2)");
            sb.AppendLine("OutlineWidth=2");
            sb.AppendLine();
            sb.AppendLine("# 框线颜色: #RRGGBB / #RGB / 颜色名 (red blue green yellow orange magenta cyan pink white ...)");
            sb.AppendLine("# 注意: 框选的框线画在压暗遮罩层上(不透明度 22%), 所以看起来会比色值淡一些;");
            sb.AppendLine("#       选窗口的框线画在全彩层上, 呈现的就是色值本身。");
            sb.AppendLine("RegionColor=#ff4040");
            sb.AppendLine("WindowColor=#ff4040");
            File.WriteAllText(configFile, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception) { }
    }

    private static string ConfigJson()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"outlineWidth\":").Append(ShotCore.OutlineWidth.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"regionColor\":\"").Append(ColorToHex(ShotCore.RegionColor)).Append('"');
        sb.Append(",\"windowColor\":\"").Append(ColorToHex(ShotCore.WindowColor)).Append('"');
        sb.Append(",\"hideBeforeCapture\":\"").Append(hideBeforeCapture).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Applies ?outlineWidth= / regionColor= / windowColor= / hide= from the GUI menu.</summary>
    private static void ApplySettings(string query)
    {
        string v = QueryValue(query, "outlineWidth");
        if (!string.IsNullOrEmpty(v))
        {
            int w;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) && w >= 1 && w <= 20)
            {
                ShotCore.OutlineWidth = w;
                SaveSetting("OutlineWidth", w.ToString(CultureInfo.InvariantCulture));
            }
        }

        v = QueryValue(query, "regionColor");
        if (!string.IsNullOrEmpty(v))
        {
            Color c;
            if (TryParseColor(v, out c))
            {
                ShotCore.RegionColor = c;
                SaveSetting("RegionColor", ColorToHex(c));
            }
        }

        v = QueryValue(query, "windowColor");
        if (!string.IsNullOrEmpty(v))
        {
            Color c;
            if (TryParseColor(v, out c))
            {
                ShotCore.WindowColor = c;
                SaveSetting("WindowColor", ColorToHex(c));
            }
        }

        v = QueryValue(query, "hide");
        if (!string.IsNullOrEmpty(v))
        {
            hideBeforeCapture = NormalizeHideSpec(v);
            SaveSetting("HideBeforeCapture", hideBeforeCapture);
        }

        // the tray check marks and the status file belong to the UI thread
        try
        {
            window.BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    RefreshColorChecks();
                    RefreshWidthChecks();
                    if (hideMenuItem != null) hideMenuItem.Checked = HideBeforeCaptureEnabled;
                    WriteStatus();
                }
                catch (Exception) { }
            });
        }
        catch (Exception) { }
    }

    private static bool TryParseColor(string value, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrEmpty(value)) return false;

        string v = value.Trim();
        if (v.Length == 0) return false;

        if (v[0] != '#')
        {
            // a colour name, or a bare hex string like ff4040
            Color named = Color.FromName(v);
            if (named.IsKnownColor)
            {
                color = Color.FromArgb(255, named);
                return true;
            }
        }
        else
        {
            v = v.Substring(1);
        }

        if (v.Length == 3)
        {
            v = new string(new char[] { v[0], v[0], v[1], v[1], v[2], v[2] });
        }

        if (v.Length != 6) return false;

        int r, g, b;
        if (int.TryParse(v.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(v.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(v.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
        {
            color = Color.FromArgb(255, r, g, b);
            return true;
        }
        return false;
    }

    private static string ColorToHex(Color c)
    {
        return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", c.R, c.G, c.B);
    }

    private static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrEmpty(text)) return false;

        string[] parts = text.Split('+');
        uint m = 0;
        uint key = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0) continue;

            string u = p.ToUpperInvariant();
            if (u == "CTRL" || u == "CONTROL") m |= MOD_CONTROL;
            else if (u == "ALT") m |= MOD_ALT;
            else if (u == "SHIFT") m |= MOD_SHIFT;
            else if (u == "WIN" || u == "WINDOWS") m |= MOD_WIN;
            else
            {
                uint k;
                if (!TryParseKey(u, out k)) return false;
                key = k;
            }
        }

        if (key == 0) return false;
        mods = m;
        vk = key;
        return true;
    }

    private static bool TryParseKey(string u, out uint vk)
    {
        vk = 0;

        if (u.Length == 1)
        {
            char c = u[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = (uint)c;
                return true;
            }
        }

        if (u.Length >= 2 && u[0] == 'F')
        {
            int n;
            if (int.TryParse(u.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                && n >= 1 && n <= 24)
            {
                vk = (uint)(0x70 + n - 1);
                return true;
            }
        }

        switch (u)
        {
            case "PRINTSCREEN": case "PRTSC": case "PRTSCR": vk = 0x2C; return true;
            case "INSERT": case "INS": vk = 0x2D; return true;
            case "DELETE": case "DEL": vk = 0x2E; return true;
            case "HOME": vk = 0x24; return true;
            case "END": vk = 0x23; return true;
            case "PAGEUP": case "PGUP": vk = 0x21; return true;
            case "PAGEDOWN": case "PGDN": vk = 0x22; return true;
            case "SPACE": vk = 0x20; return true;
            case "PAUSE": vk = 0x13; return true;
            case "SCROLLLOCK": vk = 0x91; return true;
        }

        return false;
    }

    private static void OnHotkeyPressed(int id)
    {
        int index = id - BASE_ID;
        if (index < 0 || index >= Modes.Length) return;
        CaptureMode(Modes[index], true);
    }

    // --- capture -------------------------------------------------------------

    /// <summary>UI thread only. Returns the saved path, or null when nothing was captured.</summary>
    private static string CaptureMode(string mode, bool notify)
    {
        return CaptureMode(mode, notify, null);
    }

    /// <summary>
    /// UI thread only. <paramref name="hideOverride"/> null means "use the configured
    /// setting"; otherwise it is a hide spec such as "foreground" or "none".
    /// </summary>
    private static string CaptureMode(string mode, bool notify, string hideOverride)
    {
        if (busy) return null;                        // never stack overlays
        busy = true;

        IntPtr hidden = IntPtr.Zero;
        try
        {
            string spec = hideOverride == null ? hideBeforeCapture : NormalizeHideSpec(hideOverride);
            if (ShouldHideFor(mode, spec))
            {
                hidden = HideWindowForCapture(spec);
                if (hidden != IntPtr.Zero) Thread.Sleep(220);   // let the screen settle
            }

            string path = ShotCore.Take(mode, outDir, true, true);
            ShotCore.Prune(outDir, 300);
            if (path != null)
            {
                shotCount++;
                if (notify && tray != null)
                {
                    try
                    {
                        tray.ShowBalloonTip(1200, "已截图 " + mode,
                            Path.GetFileName(path), ToolTipIcon.Info);
                    }
                    catch (Exception) { }
                }
            }
            WriteStatus();
            return path;
        }
        catch (Exception) { return null; }
        finally
        {
            RestoreWindow(hidden);
            busy = false;
        }
    }

    // --- QQ-style "get the window out of the way" ----------------------------

    private static string NormalizeHideSpec(string value)
    {
        if (string.IsNullOrEmpty(value)) return "foreground";
        string v = value.Trim();
        if (v.Length == 0) return "foreground";

        string lower = v.ToLowerInvariant();
        if (lower == "0" || lower == "off" || lower == "false" || lower == "none" || lower == "no") return "none";
        if (lower == "1" || lower == "on" || lower == "true" || lower == "yes" || lower == "foreground") return "foreground";
        if (lower.StartsWith("title:")) return v;
        return v;                                     // treat anything else as a title keyword
    }

    // Window mode hides too now that it opens a picker: the window in front is usually
    // the GUI asking for the shot, and it would cover whatever the user wants to point at.
    private static bool ShouldHideFor(string mode, string spec)
    {
        if (string.IsNullOrEmpty(spec) || spec == "none") return false;
        return true;
    }

    private static bool HideBeforeCaptureEnabled
    {
        get { return hideBeforeCapture != "none"; }
    }

    /// <summary>Hides the chosen window and returns its handle (IntPtr.Zero if none).</summary>
    private static IntPtr HideWindowForCapture(string spec)
    {
        try
        {
            IntPtr target = IntPtr.Zero;

            if (spec == "foreground")
            {
                target = GetForegroundWindow();
            }
            else if (spec.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                target = FindWindowByTitle(spec.Substring(6).Trim());
            }
            else
            {
                target = FindWindowByTitle(spec);
            }

            if (target != IntPtr.Zero) target = GetAncestor(target, 2);   // GA_ROOT
            if (!IsSafeToHide(target)) return IntPtr.Zero;

            ShowWindow(target, 0);                    // SW_HIDE: no minimise animation
            return target;
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    private static void RestoreWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (!IsWindow(hwnd)) return;
            ShowWindow(hwnd, 5);                      // SW_SHOW
            SetForegroundWindow(hwnd);
        }
        catch (Exception) { }
    }

    private static bool IsSafeToHide(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindow(hwnd)) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (hwnd == GetShellWindow()) return false;          // the desktop itself

            string cls = GetWindowClass(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                cls == "Shell_SecondaryTrayWnd" || cls == "Button" || cls == "SysShadow")
            {
                return false;
            }

            // a real app window has a title; skipping untitled ones keeps us away from
            // tool windows and hidden helper windows
            if (GetWindowTitle(hwnd).Length == 0) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static IntPtr FindWindowByTitle(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;

        try
        {
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                string title = GetWindowTitle(hWnd);
                if (title.Length == 0) return true;
                if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false;                     // stop enumerating
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }

        return found;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch (Exception) { return string.Empty; }
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        try
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>Any thread. Marshals to the UI thread and waits.</summary>
    private static string CaptureFromOtherThread(string mode, int timeoutMs, string hideOverride)
    {
        string result = null;
        ManualResetEvent done = new ManualResetEvent(false);

        try
        {
            window.BeginInvoke((MethodInvoker)delegate
            {
                try { result = CaptureMode(mode, false, hideOverride); }
                catch (Exception) { }
                finally { done.Set(); }
            });
        }
        catch (Exception) { return null; }

        if (!done.WaitOne(timeoutMs)) return null;
        return result;
    }

    // --- loopback HTTP API ---------------------------------------------------

    private static void StartHttpServer()
    {
        Thread thread = new Thread(delegate() { ServeLoop(port); });
        thread.IsBackground = true;
        thread.Name = "dsh-shot-http";
        thread.Start();
    }

    private static void ServeLoop(int listenPort)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, listenPort);
            listener.Start();
        }
        catch (Exception) { return; }                 // port taken: hotkeys still work

        while (true)
        {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.SendTimeout = 180000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        HandleRequest(stream);
                    }
                }
            }
            catch (Exception) { }
        }
    }

    private static void HandleRequest(NetworkStream stream)
    {
        string requestLine = ReadLine(stream);
        if (string.IsNullOrEmpty(requestLine)) return;

        string origin = null;
        string userAgent = null;
        string header;
        do
        {
            header = ReadLine(stream);
            if (header == null) continue;
            if (header.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = header.Substring(7).Trim();
            else if (header.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) userAgent = header.Substring(11).Trim();
        }
        while (!string.IsNullOrEmpty(header));

        // A browser page must not be able to fire screenshots at this port. The one
        // exception is the DSH GUI itself, which runs on loopback: its composer button
        // calls this service directly when no same-origin proxy is available.
        currentAllowOrigin = null;
        if (!string.IsNullOrEmpty(origin))
        {
            if (IsLoopbackOrigin(origin)) currentAllowOrigin = origin;
            else
            {
                Respond(stream, 403, "Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("browser origin refused"), null);
                return;
            }
        }

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            Respond(stream, 400, "Bad Request", "text/plain; charset=utf-8", new byte[0], null);
            return;
        }

        if (string.Equals(parts[0], "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Respond(stream, 204, "No Content", "text/plain; charset=utf-8", new byte[0], null);
            return;
        }

        string target = parts[1];
        string path = target;
        string query = string.Empty;
        int q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target.Substring(0, q);
            query = target.Substring(q + 1);
        }

        if (string.Equals(path, "/shot", StringComparison.OrdinalIgnoreCase))
        {
            string mode = QueryValue(query, "mode");
            if (mode != "Full" && mode != "Window" && mode != "Region") mode = "Full";

            // ?hide=0|1|foreground|none|title:<substring> overrides the configured setting
            string hide = QueryValue(query, "hide");
            string hideOverride = string.IsNullOrEmpty(hide) ? null : NormalizeHideSpec(hide);

            string saved = CaptureFromOtherThread(mode, 180000, hideOverride);
            if (saved == null)
            {
                Respond(stream, 204, "No Content", "text/plain; charset=utf-8", new byte[0], null);
                return;
            }

            byte[] bytes = File.ReadAllBytes(saved);
            Respond(stream, 200, "OK", "image/png", bytes, Path.GetFileName(saved));
            return;
        }

        // Live appearance settings for the GUI's own menu: frame colour and thickness.
        if (string.Equals(path, "/config", StringComparison.OrdinalIgnoreCase))
        {
            Respond(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(ConfigJson()), null);
            return;
        }

        if (string.Equals(path, "/set", StringComparison.OrdinalIgnoreCase))
        {
            ApplySettings(query);
            Respond(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(ConfigJson()), null);
            return;
        }

        // The GUI's injected script reports what it found here, so a missing button can
        // be diagnosed from the outside without opening DevTools.
        if (string.Equals(path, "/beacon", StringComparison.OrdinalIgnoreCase))
        {
            AppendBeacon(query, userAgent);
            Respond(stream, 204, "No Content", "text/plain; charset=utf-8", new byte[0], null);
            return;
        }

        Respond(stream, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"), null);
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        try
        {
            Uri uri = new Uri(origin);
            return uri.IsLoopback;
        }
        catch (Exception) { return false; }
    }

    private static void AppendBeacon(string query, string userAgent)
    {
        try
        {
            StringBuilder line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            line.Append("  ").Append(query);
            if (!string.IsNullOrEmpty(userAgent)) line.Append("  ua=").Append(userAgent);

            string file = Path.Combine(outDir, "beacon.log");
            File.AppendAllText(file, line.ToString() + Environment.NewLine, new UTF8Encoding(true));
        }
        catch (Exception) { }
    }

    private static string QueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        string[] pairs = query.Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            int eq = pairs[i].IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(pairs[i].Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }
        }
        return string.Empty;
    }

    private static void Respond(NetworkStream stream, int code, string reason, string contentType, byte[] body, string shotName)
    {
        StringBuilder head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(code.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        if (shotName != null) head.Append("X-Shot-Name: ").Append(shotName).Append("\r\n");
        if (currentAllowOrigin != null)
        {
            head.Append("Access-Control-Allow-Origin: ").Append(currentAllowOrigin).Append("\r\n");
            head.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
            head.Append("Access-Control-Expose-Headers: X-Shot-Name, X-Shot-Source\r\n");
        }
        head.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        try
        {
            stream.Write(headBytes, 0, headBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        catch (Exception) { }
    }

    private static string ReadLine(NetworkStream stream)
    {
        MemoryStream buffer = new MemoryStream();
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == '\n')
            {
                byte[] bytes = buffer.ToArray();
                int len = bytes.Length;
                if (len > 0 && bytes[len - 1] == (byte)'\r') len--;
                return Encoding.UTF8.GetString(bytes, 0, len);
            }
            buffer.WriteByte((byte)b);
            if (buffer.Length > 8192) break;          // header line cap
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    // --- tray ----------------------------------------------------------------

    private static void BuildTray()
    {
        ContextMenuStrip menu = new ContextMenuStrip();

        for (int i = 0; i < Modes.Length; i++)
        {
            int index = i;
            string text = Registered[i]
                ? Labels[i] + "   " + Resolved[i]
                : Labels[i] + "   (无可用热键)";

            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate(object s, EventArgs e) { CaptureMode(Modes[index], true); };
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        hideMenuItem = new ToolStripMenuItem("截图前隐藏前台窗口（QQ 式）");
        hideMenuItem.CheckOnClick = false;            // state is owned by hideBeforeCapture
        hideMenuItem.Click += delegate(object s, EventArgs e) { ToggleHideBeforeCapture(); };
        hideMenuItem.Checked = HideBeforeCaptureEnabled;
        menu.Items.Add(hideMenuItem);

        colorMenuItem = new ToolStripMenuItem("框线颜色");
        for (int i = 0; i < ColorPresets.Length; i++)
        {
            string hex = ColorPresets[i][1];
            ToolStripMenuItem preset = new ToolStripMenuItem(ColorPresets[i][0]);
            preset.Tag = hex;
            preset.Click += delegate(object s, EventArgs e)
            {
                ApplyColorPreset((string)((ToolStripMenuItem)s).Tag);
            };
            colorMenuItem.DropDownItems.Add(preset);
        }
        RefreshColorChecks();
        menu.Items.Add(colorMenuItem);

        widthMenuItem = new ToolStripMenuItem("框线粗细");
        for (int i = 0; i < WidthPresets.Length; i++)
        {
            int w = WidthPresets[i];
            ToolStripMenuItem preset = new ToolStripMenuItem(WidthLabels[i]);
            preset.Tag = w;
            preset.Click += delegate(object s, EventArgs e)
            {
                ApplyWidthPreset((int)((ToolStripMenuItem)s).Tag);
            };
            widthMenuItem.DropDownItems.Add(preset);
        }
        RefreshWidthChecks();
        menu.Items.Add(widthMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem reload = new ToolStripMenuItem("重新加载热键配置");
        reload.Click += delegate(object s, EventArgs e) { ReloadHotkeys(); };
        menu.Items.Add(reload);

        ToolStripMenuItem open = new ToolStripMenuItem("打开截图文件夹");
        open.Click += delegate(object s, EventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + outDir + "\""); }
            catch (Exception) { }
        };
        menu.Items.Add(open);

        ToolStripMenuItem edit = new ToolStripMenuItem("编辑热键配置");
        edit.Click += delegate(object s, EventArgs e)
        {
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + configFile + "\""); }
            catch (Exception) { }
        };
        menu.Items.Add(edit);

        ToolStripMenuItem quit = new ToolStripMenuItem("退出截屏服务");
        quit.Click += delegate(object s, EventArgs e) { Application.ExitThread(); };
        menu.Items.Add(quit);

        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.Text = "截屏服务";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate(object s, EventArgs e) { CaptureMode("Full", true); };
    }

    private static void ApplyColorPreset(string hex)
    {
        Color c;
        if (!TryParseColor(hex, out c)) return;

        ShotCore.RegionColor = c;
        ShotCore.WindowColor = c;
        SaveSetting("RegionColor", hex);
        SaveSetting("WindowColor", hex);
        RefreshColorChecks();
        WriteStatus();

        if (tray != null)
        {
            try { tray.ShowBalloonTip(1200, "框线颜色", hex, ToolTipIcon.Info); }
            catch (Exception) { }
        }
    }

    private static void ApplyWidthPreset(int width)
    {
        if (width < 1 || width > 20) return;

        ShotCore.OutlineWidth = width;
        SaveSetting("OutlineWidth", width.ToString(CultureInfo.InvariantCulture));
        RefreshWidthChecks();
        WriteStatus();

        if (tray != null)
        {
            try { tray.ShowBalloonTip(1200, "框线粗细", width.ToString(CultureInfo.InvariantCulture) + " 像素", ToolTipIcon.Info); }
            catch (Exception) { }
        }
    }

    private static void RefreshWidthChecks()
    {
        if (widthMenuItem == null) return;
        for (int i = 0; i < widthMenuItem.DropDownItems.Count && i < WidthPresets.Length; i++)
        {
            ((ToolStripMenuItem)widthMenuItem.DropDownItems[i]).Checked = ShotCore.OutlineWidth == WidthPresets[i];
        }
    }

    private static void RefreshColorChecks()
    {
        if (colorMenuItem == null) return;
        for (int i = 0; i < colorMenuItem.DropDownItems.Count && i < ColorPresets.Length; i++)
        {
            Color c;
            bool same = TryParseColor(ColorPresets[i][1], out c)
                && ShotCore.RegionColor.ToArgb() == c.ToArgb()
                && ShotCore.WindowColor.ToArgb() == c.ToArgb();
            ((ToolStripMenuItem)colorMenuItem.DropDownItems[i]).Checked = same;
        }
    }

    private static void ToggleHideBeforeCapture()
    {
        hideBeforeCapture = HideBeforeCaptureEnabled ? "none" : "foreground";
        if (hideMenuItem != null) hideMenuItem.Checked = HideBeforeCaptureEnabled;
        SaveSetting("HideBeforeCapture", hideBeforeCapture);
        WriteStatus();
        if (tray != null)
        {
            try
            {
                tray.ShowBalloonTip(1500, "截图前隐藏前台窗口",
                    HideBeforeCaptureEnabled ? "已开启（全屏与框选用）" : "已关闭", ToolTipIcon.Info);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Rewrites one key in hotkeys.ini, leaving every other line alone.</summary>
    private static void SaveSetting(string key, string value)
    {
        try
        {
            string[] lines = File.Exists(configFile) ? File.ReadAllLines(configFile, Encoding.UTF8) : new string[0];
            bool replaced = false;
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                int eq = trimmed.IndexOf('=');
                if (!replaced && eq > 0 &&
                    string.Compare(trimmed.Substring(0, eq).Trim(), key, true, CultureInfo.InvariantCulture) == 0)
                {
                    sb.AppendLine(key + "=" + value);
                    replaced = true;
                }
                else
                {
                    sb.AppendLine(lines[i]);
                }
            }

            if (!replaced) sb.AppendLine(key + "=" + value);
            File.WriteAllText(configFile, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception) { }
    }

    private static void ReloadHotkeys()
    {
        for (int i = 0; i < Modes.Length; i++)
        {
            if (Registered[i]) UnregisterHotKey(window.Handle, BASE_ID + i);
            Registered[i] = false;
        }
        LoadConfig();
        RegisterAll();
        BuildTrayMenuText();
        if (hideMenuItem != null) hideMenuItem.Checked = HideBeforeCaptureEnabled;
        RefreshColorChecks();
        RefreshWidthChecks();
        WriteStatus();
        Welcome();
    }

    private static void BuildTrayMenuText()
    {
        if (tray == null || tray.ContextMenuStrip == null) return;
        for (int i = 0; i < Modes.Length; i++)
        {
            if (i >= tray.ContextMenuStrip.Items.Count) return;
            tray.ContextMenuStrip.Items[i].Text = Registered[i]
                ? Labels[i] + "   " + Resolved[i]
                : Labels[i] + "   (无可用热键)";
        }
    }

    private static Icon MakeIcon()
    {
        Bitmap bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (SolidBrush body = new SolidBrush(Color.FromArgb(255, 214, 48, 48)))
            {
                g.FillEllipse(body, 0, 0, 15, 15);
            }
            using (Pen ring = new Pen(Color.White, 2f))
            {
                g.DrawEllipse(ring, 4, 4, 7, 7);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void Welcome()
    {
        if (tray == null) return;

        int ok = 0;
        for (int i = 0; i < Registered.Length; i++) if (Registered[i]) ok++;

        try
        {
            if (ok == Modes.Length)
            {
                tray.ShowBalloonTip(2500, "截屏服务已启动",
                    "全屏 " + Resolved[0] + "    窗口 " + Resolved[1] + "    框选 " + Resolved[2],
                    ToolTipIcon.Info);
            }
            else
            {
                tray.ShowBalloonTip(4000, "截屏服务：热键冲突",
                    ok.ToString(CultureInfo.InvariantCulture) + "/3 注册成功，详见 shots\\daemon.txt",
                    ToolTipIcon.Warning);
            }
        }
        catch (Exception) { }
    }

    private static void WriteStatus()
    {
        try
        {
            System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("started=" + self.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("pid=" + self.Id.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("outdir=" + outDir);
            sb.AppendLine("config=" + configFile);
            sb.AppendLine("http=http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/shot");
            sb.AppendLine("hideBeforeCapture=" + hideBeforeCapture);
            sb.AppendLine("outlineWidth=" + ShotCore.OutlineWidth.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("regionColor=" + ColorToHex(ShotCore.RegionColor));
            sb.AppendLine("windowColor=" + ColorToHex(ShotCore.WindowColor));
            sb.AppendLine("shots=" + shotCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("updated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            for (int i = 0; i < Modes.Length; i++)
            {
                sb.AppendLine(Modes[i].ToLowerInvariant()
                    + "= " + Resolved[i]
                    + " registered=" + (Registered[i] ? "True" : "False")
                    + (string.Compare(Resolved[i], Configured[i], true, CultureInfo.InvariantCulture) == 0
                        ? "" : " (configured " + Configured[i] + " unavailable)"));
            }
            sb.AppendLine("menu=" + string.Join("|", Labels) + "|重新加载热键配置|打开截图文件夹|编辑热键配置|退出截屏服务");
            File.WriteAllText(statusFile, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception) { }
    }
}
