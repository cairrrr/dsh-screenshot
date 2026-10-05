// ShotMain.cs - one-shot command line front end (shot.exe).
//
//   shot.exe Full            all monitors
//   shot.exe Window          foreground window
//   shot.exe Region          drag a box (Esc or right click cancels)
//
// Options: --out <dir>  --delay <ms>  --keep <n>  --no-clipboard  --no-sound

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Native.EnableDpiAwareness();

        string mode = "Full";
        string outDir = null;
        int delayMs = 0;
        int keep = 300;
        bool toClipboard = true;
        bool sound = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "full": mode = "Full"; break;
                case "window": mode = "Window"; break;
                case "region": mode = "Region"; break;
                case "--out": if (i + 1 < args.Length) outDir = args[++i]; break;
                case "--delay": if (i + 1 < args.Length) delayMs = ToInt(args[++i], 0); break;
                case "--keep": if (i + 1 < args.Length) keep = ToInt(args[++i], 300); break;
                case "--no-clipboard": toClipboard = false; break;
                case "--no-sound": sound = false; break;
            }
        }

        if (outDir == null) outDir = ShotCore.DefaultOutDir();
        if (delayMs > 0) System.Threading.Thread.Sleep(delayMs);

        try
        {
            string path = ShotCore.Take(mode, outDir, toClipboard, sound);
            if (path == null) return 2;                 // region drag cancelled
            ShotCore.Prune(outDir, keep);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static int ToInt(string s, int fallback)
    {
        int v;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
    }
}
