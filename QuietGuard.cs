using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("QuietGuard")]
[assembly: System.Reflection.AssemblyProduct("QuietGuard")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace QuietGuard
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                Glyph.WriteIco(args[1]);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "QuietGuard_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("QuietGuard is already running – look for its icon next to the clock in the bottom right.",
                        "QuietGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message, "QuietGuard",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                bool startHidden = Array.Exists(args, a => a.Equals("/min", StringComparison.OrdinalIgnoreCase));
                Application.Run(new MainForm(startHidden));
            }
        }
    }

    class Settings
    {
        public string Device = "";
        public int Threshold = 70;
        public int Seconds = 3;
        public int Cooldown = 20;
        public bool UseSchedule = true;
        public int FromMin = 21 * 60;
        public int ToMin = 7 * 60;
        public bool Banner = true;
        public bool Sound = true;
        public bool Toast = false;
        public int SoundId = 0;
        public int Volume = 70;
        public bool Paused = false;

        static string AppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string Folder { get { return Path.Combine(AppData, "QuietGuard"); } }
        static string FilePath { get { return Path.Combine(Folder, "settings.txt"); } }
        static string LegacyFilePath { get { return Path.Combine(AppData, "LautWaechter", "einstellungen.txt"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                string file = File.Exists(FilePath) ? FilePath : LegacyFilePath;
                if (!File.Exists(file)) return s;
                foreach (var line in File.ReadAllLines(file))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Device": s.Device = v; break;
                        case "Threshold": s.Threshold = Clamp(I(v, s.Threshold), 0, 100); break;
                        case "Seconds": s.Seconds = Clamp(I(v, s.Seconds), 1, 30); break;
                        case "Cooldown": s.Cooldown = Clamp(I(v, s.Cooldown), 5, 600); break;
                        case "UseSchedule": s.UseSchedule = B(v, s.UseSchedule); break;
                        case "FromMin": s.FromMin = Clamp(I(v, s.FromMin), 0, 1439); break;
                        case "ToMin": s.ToMin = Clamp(I(v, s.ToMin), 0, 1439); break;
                        case "Banner": s.Banner = B(v, s.Banner); break;
                        case "Sound": s.Sound = B(v, s.Sound); break;
                        case "Toast": s.Toast = B(v, s.Toast); break;
                        case "SoundId": s.SoundId = Clamp(I(v, s.SoundId), 0, Chime.Names.Length - 1); break;
                        case "Volume": s.Volume = Clamp(I(v, s.Volume), 0, 100); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(FilePath, new[]
                {
                    "Device=" + Device,
                    "Threshold=" + Threshold,
                    "Seconds=" + Seconds,
                    "Cooldown=" + Cooldown,
                    "UseSchedule=" + UseSchedule,
                    "FromMin=" + FromMin,
                    "ToMin=" + ToMin,
                    "Banner=" + Banner,
                    "Sound=" + Sound,
                    "Toast=" + Toast,
                    "SoundId=" + SoundId,
                    "Volume=" + Volume,
                });
            }
            catch { }
        }

        static int I(string v, int d) { int r; return int.TryParse(v, out r) ? r : d; }
        static bool B(string v, bool d) { bool r; return bool.TryParse(v, out r) ? r : d; }
        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }

    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "QuietGuard";
        const string LegacyName = "LautWaechter";

        static string Command { get { return "\"" + Application.ExecutablePath + "\" /min"; } }

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return k != null && (k.GetValue(Name) != null || k.GetValue(LegacyName) != null);
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue(Name, Command);
                else k.DeleteValue(Name, false);
                k.DeleteValue(LegacyName, false);
            }
        }

        public static void RefreshPath()
        {
            try { if (IsEnabled()) Set(true); } catch { }
        }
    }

    class MicMeter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
        }

        [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
        static extern int waveInGetDevCaps(IntPtr deviceId, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint deviceId, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

        const int WHDR_DONE = 1;
        const uint WAVE_MAPPER = uint.MaxValue;
        const int SampleRate = 16000;
        const int BufferSamples = SampleRate / 10;
        const int BufferCount = 4;

        public event Action<double> Level;
        public event Action<string> Error;

        Thread thread;
        volatile bool running;

        public bool Running { get { return running; } }

        public static List<string> GetDevices()
        {
            var list = new List<string>();
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEINCAPS();
                if (waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                    list.Add(caps.szPname);
            }
            return list;
        }

        public void Start(int deviceIndex)
        {
            Stop();
            running = true;
            thread = new Thread(() => Run(deviceIndex)) { IsBackground = true, Name = "Microphone" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null)
            {
                thread.Join(2000);
                thread = null;
            }
        }

        public void Dispose() { Stop(); }

        void Run(int deviceIndex)
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate,
                wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2,
            };
            IntPtr h;
            int r = waveInOpen(out h, deviceIndex < 0 ? WAVE_MAPPER : (uint)deviceIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0)
            {
                running = false;
                if (Error != null) Error("Could not open the microphone (error " + r + "). Is one connected, and is microphone access allowed for desktop apps under Settings › Privacy & security › Microphone?");
                return;
            }

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            int flagsOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
            int recordedOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");
            int bytes = BufferSamples * 2;
            var hdrs = new IntPtr[BufferCount];
            var data = new IntPtr[BufferCount];
            var samples = new short[BufferSamples];
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    data[i] = Marshal.AllocHGlobal(bytes);
                    hdrs[i] = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(new WAVEHDR { lpData = data[i], dwBufferLength = (uint)bytes }, hdrs[i], false);
                    waveInPrepareHeader(h, hdrs[i], hdrSize);
                    waveInAddBuffer(h, hdrs[i], hdrSize);
                }
                waveInStart(h);

                int next = 0;
                while (running)
                {
                    int flags = Marshal.ReadInt32(hdrs[next], flagsOffset);
                    if ((flags & WHDR_DONE) == 0) { Thread.Sleep(15); continue; }

                    int n = Math.Min(Marshal.ReadInt32(hdrs[next], recordedOffset) / 2, BufferSamples);
                    if (n > 0)
                    {
                        Marshal.Copy(data[next], samples, 0, n);
                        double mean = 0;
                        for (int i = 0; i < n; i++) mean += samples[i];
                        mean /= n;
                        double sum = 0;
                        for (int i = 0; i < n; i++) { double d = samples[i] - mean; sum += d * d; }
                        double rms = Math.Sqrt(sum / n) / 32768.0;
                        double db = rms > 0 ? 20 * Math.Log10(rms) : -100;
                        if (Level != null) Level(db);
                    }

                    Marshal.WriteInt32(hdrs[next], flagsOffset, flags & ~WHDR_DONE);
                    waveInAddBuffer(h, hdrs[next], hdrSize);
                    next = (next + 1) % BufferCount;
                }
            }
            finally
            {
                waveInReset(h);
                for (int i = 0; i < BufferCount; i++)
                {
                    if (hdrs[i] != IntPtr.Zero)
                    {
                        waveInUnprepareHeader(h, hdrs[i], hdrSize);
                        Marshal.FreeHGlobal(hdrs[i]);
                    }
                    if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                }
                waveInClose(h);
            }
        }
    }

    static class Chime
    {
        public static readonly string[] Names = { "Gong", "Bell", "Ping", "Beeps", "Alarm", "Soft" };
        const int Rate = 22050;

        public static MemoryStream Create(int id, int volume)
        {
            double[] buf;
            switch (id)
            {
                case 1:
                    buf = new double[(int)(Rate * 1.6)];
                    Add(buf, 0, 1046.5, 1.6, 1, 2.5, 0.002, 1, 1, 2.0, 0.45, 2.76, 0.3, 5.4, 0.12);
                    break;
                case 2:
                    buf = new double[(int)(Rate * 0.5)];
                    Add(buf, 0, 1568, 0.5, 1, 9, 0.002, 1, 1, 2, 0.15);
                    break;
                case 3:
                    buf = new double[(int)(Rate * 0.45)];
                    for (int i = 0; i < 3; i++) Add(buf, i * 0.16, 1200, 0.1, 1, 0, 0.004);
                    break;
                case 4:
                    buf = new double[(int)(Rate * 0.86)];
                    for (int i = 0; i < 6; i++) Add(buf, i * 0.14, i % 2 == 0 ? 880 : 1175, 0.12, 1, 2, 0.004, 1, 1, 3, 0.15);
                    break;
                case 5:
                    buf = new double[(int)(Rate * 1.2)];
                    foreach (double f in new[] { 523.25, 659.25, 783.99 }) Add(buf, 0, f, 1.2, 1, 2.2, 0.08);
                    break;
                default:
                    buf = new double[(int)(Rate * 1.1)];
                    Add(buf, 0, 880, 0.5, 1, 6, 0.005, 1, 1, 2, 0.2);
                    Add(buf, 0.16, 660, 0.9, 1.1, 4.5, 0.005, 1, 1, 2, 0.2);
                    break;
            }

            double peak = 0;
            foreach (double v in buf) peak = Math.Max(peak, Math.Abs(v));
            double gain = peak > 0 ? 0.85 * Math.Pow(volume / 100.0, 1.7) / peak : 0;

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int dataLen = buf.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            foreach (double v in buf) w.Write((short)(v * gain * 32767));
            w.Flush();
            ms.Position = 0;
            return ms;
        }

        static void Add(double[] buf, double at, double freq, double dur, double gain, double decay, double attack, params double[] partials)
        {
            int s0 = (int)(at * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && s0 + i < buf.Length; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Min(1, t / attack) * Math.Exp(-decay * t) * Math.Min(1, (n - i) / (Rate * 0.01));
                double v = 0;
                if (partials.Length == 0) v = Math.Sin(2 * Math.PI * freq * t);
                else for (int k = 0; k + 1 < partials.Length; k += 2) v += partials[k + 1] * Math.Sin(2 * Math.PI * freq * partials[k] * t);
                buf[s0 + i] += v * gain * env;
            }
        }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(11, 14, 20);
        public static readonly Color Card = Color.FromArgb(20, 25, 34);
        public static readonly Color Border = Color.FromArgb(34, 41, 55);
        public static readonly Color Surface = Color.FromArgb(28, 34, 47);
        public static readonly Color SurfaceHover = Color.FromArgb(38, 46, 62);
        public static readonly Color Text = Color.FromArgb(241, 245, 249);
        public static readonly Color Muted = Color.FromArgb(139, 148, 166);
        public static readonly Color Faint = Color.FromArgb(91, 101, 119);
        public static readonly Color Violet = Color.FromArgb(139, 92, 246);
        public static readonly Color Cyan = Color.FromArgb(34, 211, 238);
        public static readonly Color Green = Color.FromArgb(34, 197, 94);
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);
        public static readonly Color Red = Color.FromArgb(239, 68, 68);

        static float scale;
        public static float Scale
        {
            get
            {
                if (scale == 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
                return scale;
            }
        }

        public static Font Ui(float size) { return new Font("Segoe UI", size); }
        public static Font Semi(float size) { return new Font("Segoe UI Semibold", size); }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static LinearGradientBrush Accent(RectangleF r, float angle = 0f)
        {
            return Gradient(r, Violet, Cyan, angle);
        }

        public static LinearGradientBrush AccentDeep(RectangleF r)
        {
            return Gradient(r, Color.FromArgb(124, 58, 237), Color.FromArgb(8, 145, 178), 0f);
        }

        static LinearGradientBrush Gradient(RectangleF r, Color a, Color b, float angle)
        {
            var rr = RectangleF.Inflate(r, 1, 1);
            if (rr.Width < 2) rr.Width = 2;
            if (rr.Height < 2) rr.Height = 2;
            return new LinearGradientBrush(rr, a, b, angle);
        }

        public static void DrawPill(Graphics g, Rectangle area, string text, Color c, Font font, bool dot)
        {
            var ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int h = (int)(ts.Height * 1.7f), padX = (int)(ts.Height * 0.75f), dotW = dot ? (int)(ts.Height * 0.65f) : 0;
            var r = new Rectangle(area.Right - (ts.Width + 2 * padX + dotW) - 1, area.Y + (area.Height - h) / 2, ts.Width + 2 * padX + dotW, h);
            using (var path = Glyph.RoundRect(r, h / 2f))
            {
                using (var b = new SolidBrush(Mix(Card, c, 0.16f))) g.FillPath(b, path);
                using (var p = new Pen(Mix(Card, c, 0.4f))) g.DrawPath(p, path);
            }
            if (dot)
            {
                float d = ts.Height * 0.4f;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, r.X + padX * 0.85f, r.Y + (h - d) / 2f, d, d);
            }
            TextRenderer.DrawText(g, text, font, new Rectangle(r.X + padX + dotW, r.Y, ts.Width + 4, h), c,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;

        public static void StyleWindow(IntPtr h, Color caption, Color border)
        {
            try
            {
                int on = 1, round = 2;
                int cap = ColorTranslator.ToWin32(caption), txt = ColorTranslator.ToWin32(Text), brd = ColorTranslator.ToWin32(border);
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
                DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref brd, 4);
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref cap, 4);
                DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref txt, 4);
            }
            catch { }
        }
    }

    static class Glyph
    {
        public static readonly Color TrayGray = Color.FromArgb(156, 163, 175);

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Paint(Graphics g, RectangleF r, Brush bg, Brush bars)
        {
            float s = r.Width;
            using (var path = RoundRect(r, s * 0.24f)) g.FillPath(bg, path);
            float bw = s * 0.16f, gap = s * 0.09f;
            float x0 = r.X + (s - (3 * bw + 2 * gap)) / 2, bottom = r.Y + s * 0.77f;
            float[] heights = { 0.26f, 0.42f, 0.58f };
            for (int i = 0; i < 3; i++)
            {
                float h = s * heights[i];
                using (var path = RoundRect(new RectangleF(x0 + i * (bw + gap), bottom - h, bw, h), bw / 2))
                    g.FillPath(bars, path);
            }
        }

        public static Bitmap Draw(int s, Color c1, Color c2)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, s, s), c1, c2, 45f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Paint(g, new RectangleF(0, 0, s - 1, s - 1), b, Brushes.White);
            }
            return bmp;
        }

        public static Icon MakeIcon(Color c1, Color c2)
        {
            using (var bmp = Draw(32, c1, c2))
                return Icon.FromHandle(bmp.GetHicon());
        }

        public static void WriteIco(string path)
        {
            int[] sizes = { 256, 48, 32, 16 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Draw(s, Theme.Violet, Theme.Cyan))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }

    abstract class Painted : Control
    {
        protected bool Hover, Pressed;
        protected Point MousePos;

        protected Painted()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected Graphics Begin(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        protected static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c, TextFormatFlags f)
        {
            TextRenderer.DrawText(g, text, font, r, c, f | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e) { MousePos = e.Location; if (Hover) Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Glyph.RoundRect(r, 14 * Theme.Scale))
            {
                using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
        }
    }

    class GlyphBox : Painted
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = Theme.Accent(r, 45f)) Glyph.Paint(g, r, b, Brushes.White);
        }
    }

    class Pill : Painted
    {
        public Color Tint = Theme.Muted;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            if (Text != "") Theme.DrawPill(g, ClientRectangle, Text, Tint, Font, true);
        }
    }

    class Toggle : Painted
    {
        readonly WinTimer anim = new WinTimer { Interval = 15 };
        bool on;
        float knob;
        public event EventHandler CheckedChanged;

        public Toggle()
        {
            Cursor = Cursors.Hand;
            anim.Tick += (s, e) =>
            {
                float target = on ? 1 : 0;
                knob += (target - knob) * 0.3f;
                if (Math.Abs(target - knob) < 0.02f) { knob = target; anim.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                if (Visible && IsHandleCreated) anim.Start();
                else knob = on ? 1 : 0;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            float h = Height * 0.78f, w = h * 1.8f;
            var track = new RectangleF(Width - w - 1, (Height - h) / 2, w, h);
            using (var path = Glyph.RoundRect(track, h / 2))
            {
                using (var b = new SolidBrush(Hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                if (knob > 0)
                    using (var b = new LinearGradientBrush(RectangleF.Inflate(track, 1, 1),
                        Theme.Alpha(Theme.Violet, (int)(255 * knob)), Theme.Alpha(Theme.Cyan, (int)(255 * knob)), 0f))
                        g.FillPath(b, path);
            }
            float pad = h * 0.15f, d = h - 2 * pad;
            float x = track.X + pad + knob * (w - 2 * pad - d);
            using (var b = new SolidBrush(Theme.Mix(Theme.Muted, Color.White, knob)))
                g.FillEllipse(b, x, track.Y + pad, d, d);

            DrawText(g, Text, Font, new Rectangle(0, 0, (int)track.X - 8, Height), Enabled ? Theme.Text : Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    class Slider : Painted
    {
        int val;
        bool dragging;
        public int Minimum = 0, Maximum = 100;
        public event EventHandler ValueChanged;
        public event EventHandler Released;

        public Slider() { Cursor = Cursors.Hand; }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        float ThumbD { get { return Height * 0.62f; } }

        RectangleF Track
        {
            get
            {
                float th = Math.Max(4, Height * 0.2f);
                return new RectangleF(ThumbD / 2, (Height - th) / 2, Width - ThumbD, th);
            }
        }

        void SetFromX(int x)
        {
            var t = Track;
            float p = Math.Max(0, Math.Min(1, (x - t.X) / t.Width));
            Value = Minimum + (int)Math.Round(p * (Maximum - Minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled) { dragging = true; SetFromX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Invalidate();
            if (Released != null) Released(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled) Value += Math.Sign(e.Delta);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var t = Track;
            float d = ThumbD, cy = Height / 2f;
            float cx = t.X + t.Width * (val - Minimum) / (float)(Maximum - Minimum);

            using (var path = Glyph.RoundRect(t, t.Height / 2))
            using (var b = new SolidBrush(Theme.Surface))
                g.FillPath(b, path);

            var fill = new RectangleF(t.X, t.Y, Math.Max(t.Height, cx - t.X), t.Height);
            using (var path = Glyph.RoundRect(fill, t.Height / 2))
            {
                if (Enabled) using (var b = Theme.Accent(t)) g.FillPath(b, path);
                else using (var b = new SolidBrush(Theme.Faint)) g.FillPath(b, path);
            }

            if (Enabled && (Hover || dragging))
                using (var b = new SolidBrush(Theme.Alpha(Theme.Cyan, 45)))
                    g.FillEllipse(b, cx - d * 0.78f, cy - d * 0.78f, d * 1.56f, d * 1.56f);
            using (var b = new SolidBrush(Enabled ? Color.White : Theme.Muted))
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            if (Enabled)
                using (var b = Theme.Accent(t))
                    g.FillEllipse(b, cx - d * 0.2f, cy - d * 0.2f, d * 0.4f, d * 0.4f);
        }
    }

    class Stepper : Painted
    {
        readonly WinTimer repeat = new WinTimer();
        int val, dir;
        public int Minimum = 0, Maximum = 100, Step = 1;
        public bool Wrap;
        public Func<int, string> Format = v => v.ToString();
        public event EventHandler ValueChanged;

        public Stepper()
        {
            Cursor = Cursors.Hand;
            repeat.Tick += (s, e) => { repeat.Interval = 70; Value += dir * Step; };
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = value;
                if (Wrap)
                {
                    int span = Maximum - Minimum;
                    v = ((v - Minimum) % span + span) % span + Minimum;
                }
                else v = Math.Max(Minimum, Math.Min(Maximum, v));
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        RectangleF MinusRect { get { return new RectangleF(0, 0, Height, Height); } }
        RectangleF PlusRect { get { return new RectangleF(Width - Height, 0, Height, Height); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            dir = MinusRect.Contains(e.Location) ? -1 : PlusRect.Contains(e.Location) ? 1 : 0;
            if (dir == 0) return;
            Value += dir * Step;
            repeat.Interval = 400;
            repeat.Start();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); repeat.Stop(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); repeat.Stop(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled) Value += Math.Sign(e.Delta) * Step;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Glyph.RoundRect(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            DrawButton(g, MinusRect, false);
            DrawButton(g, PlusRect, true);
            DrawText(g, Format(val), Font, new Rectangle(Height, 0, Width - 2 * Height, Height),
                Enabled ? Theme.Text : Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void DrawButton(Graphics g, RectangleF r, bool plus)
        {
            bool hot = Enabled && Hover && r.Contains(MousePos);
            var c = RectangleF.Inflate(r, -r.Height * 0.15f, -r.Height * 0.15f);
            using (var b = new SolidBrush(hot ? (Pressed ? Theme.Border : Theme.SurfaceHover) : Theme.Surface))
                g.FillEllipse(b, c);
            float s = r.Height * 0.15f, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var p = new Pen(!Enabled ? Theme.Faint : hot ? Theme.Text : Theme.Muted, Math.Max(1.5f, r.Height * 0.055f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - s, cy, cx + s, cy);
                if (plus) g.DrawLine(p, cx, cy - s, cx, cy + s);
            }
        }
    }

    class FlatButton : Painted
    {
        public bool Primary;
        public bool PlayIcon;

        public FlatButton() { Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Glyph.RoundRect(r, Height * 0.28f))
            {
                if (Primary)
                {
                    using (var b = Theme.AccentDeep(r)) g.FillPath(b, path);
                    if (Hover || Pressed)
                        using (var b = new SolidBrush(Pressed ? Color.FromArgb(50, Color.Black) : Color.FromArgb(30, Color.White)))
                            g.FillPath(b, path);
                }
                else
                {
                    using (var b = new SolidBrush(Pressed ? Theme.Border : Hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                }
            }

            if (PlayIcon)
            {
                float s = Height * 0.2f, cx = Width / 2f + s * 0.15f, cy = Height / 2f;
                using (var b = new SolidBrush(Hover ? Theme.Cyan : Theme.Text))
                    g.FillPolygon(b, new[] { new PointF(cx - s * 0.7f, cy - s), new PointF(cx + s, cy), new PointF(cx - s * 0.7f, cy + s) });
            }
            else
            {
                DrawText(g, Text, Font, ClientRectangle, Primary ? Color.White : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    class Dropdown : Painted
    {
        public readonly List<string> Items = new List<string>();
        public bool MicIcon;
        int sel = -1;
        public event EventHandler SelectedIndexChanged;

        public Dropdown() { Cursor = Cursors.Hand; }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (sel == value) return;
                sel = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = Font };
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem(Items[i]) { Padding = new Padding(4, 5, 4, 5) };
                if (i == sel) { item.Tag = "sel"; item.Font = new Font(Font, FontStyle.Bold); }
                item.Click += (s, a) => SelectedIndex = idx;
                menu.Items.Add(item);
            }
            menu.MinimumSize = new Size(Width, 0);
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new Point(0, Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Glyph.RoundRect(r, Height * 0.28f))
            {
                using (var b = new SolidBrush(Enabled && Hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }

            float h = Height;
            Color fg = Enabled ? Theme.Text : Theme.Faint;
            int textX = (int)(h * 0.42f);
            if (MicIcon)
            {
                float mx = h * 0.55f, mw = h * 0.2f, mh = h * 0.32f, top = h * 0.22f;
                using (var p = new Pen(Enabled ? Theme.Cyan : Theme.Faint, Math.Max(1.5f, h * 0.05f)))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    using (var cap = Glyph.RoundRect(new RectangleF(mx - mw / 2, top, mw, mh), mw / 2)) g.DrawPath(p, cap);
                    var arc = new RectangleF(mx - mw * 0.95f, top + mh * 0.35f, mw * 1.9f, mh * 0.95f);
                    g.DrawArc(p, arc, 0, 180);
                    g.DrawLine(p, mx, arc.Bottom, mx, arc.Bottom + h * 0.1f);
                }
                textX = (int)(h * 1.0f);
            }
            DrawText(g, SelectedItem ?? "", Font, new Rectangle(textX, 0, Width - textX - (int)(h * 0.9f), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            float s = h * 0.11f, cx = Width - h * 0.5f, cy = h / 2f;
            using (var p = new Pen(Enabled ? Theme.Muted : Theme.Faint, Math.Max(1.5f, h * 0.05f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
            }
        }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.Faint : "sel".Equals(e.Item.Tag) ? Theme.Cyan : Theme.Text;
            base.OnRenderItemText(e);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.SurfaceHover; } }
        public override Color MenuItemSelected { get { return Theme.SurfaceHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.SurfaceHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.SurfaceHover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    class Readout : Painted
    {
        public int Value = -1;
        public string State = "";
        public Color StateColor = Theme.Muted;
        readonly Font big = Theme.Semi(30F), small = Theme.Ui(9.5F), pill = Theme.Semi(9F);

        static float Ascent(Graphics g, Font f)
        {
            var ff = f.FontFamily;
            return f.SizeInPoints * g.DpiY / 72f * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            string num = Value < 0 ? "–" : Value.ToString();
            var size = TextRenderer.MeasureText(g, num, big, Size.Empty, TextFormatFlags.NoPadding);
            int y = (Height - size.Height) / 2;
            DrawText(g, num, big, new Rectangle(0, y, size.Width + 4, size.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.Top);

            float baseline = y + Ascent(g, big);
            var ss = TextRenderer.MeasureText(g, "/ 100", small, Size.Empty, TextFormatFlags.NoPadding);
            DrawText(g, "/ 100", small, new Rectangle(size.Width + (int)(6 * Theme.Scale), (int)(baseline - Ascent(g, small)), ss.Width + 4, ss.Height),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.Top);

            if (State != "") Theme.DrawPill(g, ClientRectangle, State, StateColor, pill, true);
        }
    }

    class LevelMeter : Painted
    {
        double level, peak;
        DateTime peakAt;
        public int Threshold = 70;

        public void SetLevel(double l)
        {
            level = l;
            if (l >= peak || (DateTime.Now - peakAt).TotalSeconds > 1.2) { peak = l; peakAt = DateTime.Now; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            const int n = 30;
            float top = Height * 0.24f, gap = Math.Max(2f, Width / 150f);
            float sw = (Width - gap * (n - 1)) / n, sh = Height - top;
            int peakIdx = (int)Math.Floor(peak / 100.0 * n - 0.5);

            for (int i = 0; i < n; i++)
            {
                double center = (i + 0.5) * 100.0 / n;
                Color c = center >= Threshold ? Theme.Red : center >= Threshold * 0.85 ? Theme.Amber : Theme.Green;
                bool lit = level >= center;
                Color fill = lit ? c : i == peakIdx ? Theme.Mix(Theme.Card, c, 0.7f) : Theme.Mix(Theme.Card, c, 0.13f);
                var r = new RectangleF(i * (sw + gap), top, sw, sh);
                using (var path = Glyph.RoundRect(r, Math.Min(sw, sh) * 0.3f))
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);
            }

            float x = Math.Max(2, Math.Min(Width - 2, (float)(Threshold / 100.0 * n) * (sw + gap) - gap / 2));
            float tw = top * 0.9f;
            g.FillPolygon(Brushes.White, new[] { new PointF(x - tw / 2, 0), new PointF(x + tw / 2, 0), new PointF(x, top * 0.8f) });
            using (var p = new Pen(Color.FromArgb(230, Color.White), Math.Max(1.5f, Width / 220f)))
                g.DrawLine(p, x, top, x, Height);
        }
    }

    class WarnBanner : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;

        readonly WinTimer timer = new WinTimer { Interval = 30 };
        readonly Font title = Theme.Semi(22F), sub = Theme.Ui(11F);
        int elapsed;

        public WarnBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Red;
            DoubleBuffered = true;
            Opacity = 0;
            Size = new Size((int)(480 * Theme.Scale), (int)(104 * Theme.Scale));
            timer.Tick += OnTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle, Theme.Red, Color.FromArgb(252, 165, 165));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(153, 27, 27), Color.FromArgb(225, 29, 72), 0f))
                g.FillRectangle(b, ClientRectangle);

            float h = Height, s = h * 0.54f;
            var icon = new RectangleF(h * 0.24f, (h - s) / 2, s, s);
            using (var b = new SolidBrush(Color.FromArgb(50, Color.White))) Glyph.Paint(g, icon, b, Brushes.White);

            int tx = (int)(icon.Right + h * 0.2f);
            TextRenderer.DrawText(g, "Too loud!", title, new Rectangle(tx, 0, Width - tx, (int)(h * 0.6f)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Please keep it down a little.", sub, new Rectangle(tx, (int)(h * 0.62f), Width - tx, (int)(h * 0.35f)),
                Color.FromArgb(254, 226, 226), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        public void Flash()
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (int)(40 * Theme.Scale));
            elapsed = 0;
            Opacity = 0.05;
            if (!Visible) Show();
            timer.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            elapsed += timer.Interval;
            if (elapsed < 150) Opacity = 0.96 * elapsed / 150.0;
            else if (elapsed < 3000) Opacity = 0.96;
            else
            {
                double o = 0.96 - (elapsed - 3000) / 600.0;
                if (o <= 0.02) { timer.Stop(); Hide(); }
                else Opacity = o;
            }
        }
    }

    class MainForm : Form
    {
        readonly Settings settings = Settings.Load();
        readonly MicMeter meter = new MicMeter();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly WarnBanner banner = new WarnBanner();
        readonly SoundPlayer chime = new SoundPlayer();
        readonly Icon iconApp = Glyph.MakeIcon(Theme.Violet, Theme.Cyan);
        readonly Icon iconAmber = Glyph.MakeIcon(Theme.Amber, Theme.Amber);
        readonly Icon iconRed = Glyph.MakeIcon(Theme.Red, Theme.Red);
        readonly Icon iconGray = Glyph.MakeIcon(Glyph.TrayGray, Glyph.TrayGray);
        readonly ToolTip statusTip = new ToolTip();

        Dropdown deviceBox, soundBox;
        LevelMeter meterBar;
        Readout readout;
        Pill statusPill;
        Label thresholdValue, volumeValue;
        Slider thresholdBar, volumeBar;
        Stepper secondsBox, cooldownBox, fromPicker, toPicker;
        Toggle scheduleCheck, bannerCheck, soundCheck, toastCheck, autostartCheck;
        FlatButton testButton, pauseButton, playButton;
        ToolStripMenuItem pauseItem;

        bool allowShow, exiting, loading, closeHintShown;
        double displayLevel, loudSeconds;
        DateTime lastWarn = DateTime.MinValue;
        string micError;

        public MainForm(bool startHidden)
        {
            allowShow = !startHidden;
            BuildUi();
            BuildTray();
            LoadDevices();
            ApplySettingsToUi();
            RebuildSound();

            meter.Level += db => Post(() => OnLevel(db));
            meter.Error += msg => Post(() => OnMicError(msg));

            if (!IsHandleCreated) CreateHandle();
            Autostart.RefreshPath();

            var clock = new WinTimer { Interval = 15000 };
            clock.Tick += (s, e) => { UpdateStatus(); EnsureMeterState(); };
            clock.Start();

            UpdateStatus();
            EnsureMeterState();
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle, Theme.Bg, Theme.Border);
        }

        static void Caption(Control p, string text, int x, int y)
        {
            p.Controls.Add(new Label { Text = text, Font = Theme.Semi(7.5F), ForeColor = Theme.Faint, AutoSize = true, Location = new Point(x, y) });
        }

        static void RowLabel(Control p, string text, int x, int y)
        {
            p.Controls.Add(new Label { Text = text, ForeColor = Theme.Text, AutoSize = true, Location = new Point(x, y) });
        }

        static void Divider(Control p, int x, int y, int w)
        {
            p.Controls.Add(new Label { Location = new Point(x, y), Size = new Size(w, 1), BackColor = Theme.Border });
        }

        Toggle AddToggle(Control p, string text, int x, int y, int w)
        {
            var t = new Toggle { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
            t.CheckedChanged += OnSettingChanged;
            p.Controls.Add(t);
            return t;
        }

        Stepper AddStepper(Control p, int x, int y, int w, int min, int max, int step, Func<int, string> format)
        {
            var s = new Stepper
            {
                Location = new Point(x, y), Size = new Size(w, 32), Font = Theme.Semi(9.5F),
                Minimum = min, Maximum = max, Step = step, Format = format,
            };
            s.ValueChanged += OnSettingChanged;
            p.Controls.Add(s);
            return s;
        }

        static Label ValueLabel(Control p, int x, int y, int w, int h)
        {
            var l = new Label
            {
                Location = new Point(x, y), Size = new Size(w, h), Font = Theme.Semi(10F),
                ForeColor = Theme.Cyan, TextAlign = ContentAlignment.MiddleRight,
            };
            p.Controls.Add(l);
            return l;
        }

        void BuildUi()
        {
            SuspendLayout();
            Text = "QuietGuard";
            Font = Theme.Ui(9.5F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            Icon = iconApp;

            const int M = 20, Gap = 14, LW = 360, RW = 360, Top = 80, BtnH = 42;
            const int rx = M + LW + Gap;

            Controls.Add(new GlyphBox { Location = new Point(M, 20), Size = new Size(40, 40) });
            Controls.Add(new Label { Text = "QuietGuard", Font = Theme.Semi(17F), AutoSize = true, Location = new Point(M + 52, 14) });
            Controls.Add(new Label { Text = "Keeps you quiet at night.", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(M + 54, 46) });
            statusPill = new Pill { Location = new Point(rx + RW - 260, 24), Size = new Size(260, 32), Font = Theme.Semi(9F) };
            Controls.Add(statusPill);

            var rules = new Card { Location = new Point(rx, Top), Size = new Size(RW, 226) };
            Controls.Add(rules);
            Caption(rules, "RULES", 18, 16);
            RowLabel(rules, "Alert after", 18, 46);
            secondsBox = AddStepper(rules, RW - 18 - 140, 40, 140, 1, 30, 1, v => v + " sec");
            RowLabel(rules, "Pause between alerts", 18, 86);
            cooldownBox = AddStepper(rules, RW - 18 - 140, 80, 140, 5, 600, 5, v => v + " sec");
            Divider(rules, 18, 128, RW - 36);
            scheduleCheck = AddToggle(rules, "Only active at night", 18, 140, RW - 36);
            fromPicker = AddStepper(rules, 18, 176, 136, 0, 1440, 30, FormatTime);
            fromPicker.Wrap = true;
            rules.Controls.Add(new Label
            {
                Text = "to", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(154, 176), Size = new Size(RW - 36 - 272, 32),
            });
            toPicker = AddStepper(rules, RW - 18 - 136, 176, 136, 0, 1440, 30, FormatTime);
            toPicker.Wrap = true;

            int warnTop = Top + rules.Height + Gap;
            var warn = new Card { Location = new Point(rx, warnTop), Size = new Size(RW, 238) };
            Controls.Add(warn);
            Caption(warn, "ALERTS", 18, 16);
            bannerCheck = AddToggle(warn, "On-screen banner", 18, 42, RW - 36);
            toastCheck = AddToggle(warn, "Windows notification", 18, 76, RW - 36);
            soundCheck = AddToggle(warn, "Play sound", 18, 110, RW - 36);

            soundBox = new Dropdown { Location = new Point(18, 146), Size = new Size(RW - 36 - 46, 36) };
            soundBox.Items.AddRange(Chime.Names);
            soundBox.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                SaveFromUi();
                RebuildSound();
                PlayChime();
            };
            warn.Controls.Add(soundBox);
            playButton = new FlatButton { PlayIcon = true, Location = new Point(RW - 18 - 38, 146), Size = new Size(38, 36) };
            playButton.Click += (s, e) => PlayChime();
            statusTip.SetToolTip(playButton, "Preview sound");
            warn.Controls.Add(playButton);

            RowLabel(warn, "Volume", 18, 197);
            volumeBar = new Slider { Location = new Point(100, 190), Size = new Size(RW - 18 - 100 - 50, 30) };
            volumeBar.ValueChanged += OnSettingChanged;
            volumeBar.Released += (s, e) => PlayChime();
            warn.Controls.Add(volumeBar);
            volumeValue = ValueLabel(warn, RW - 18 - 48, 190, 48, 30);

            int bottom = warnTop + warn.Height;

            var live = new Card { Location = new Point(M, Top), Size = new Size(LW, bottom - Top - BtnH - Gap) };
            Controls.Add(live);
            Caption(live, "LIVE LEVEL", 18, 16);
            readout = new Readout { Location = new Point(18, 32), Size = new Size(LW - 36, 60) };
            live.Controls.Add(readout);
            meterBar = new LevelMeter { Location = new Point(18, 100), Size = new Size(LW - 36, 46) };
            live.Controls.Add(meterBar);

            Caption(live, "THRESHOLD", 18, 166);
            thresholdValue = ValueLabel(live, LW - 18 - 60, 160, 60, 22);
            thresholdBar = new Slider { Location = new Point(14, 186), Size = new Size(LW - 28, 30) };
            thresholdBar.ValueChanged += OnSettingChanged;
            live.Controls.Add(thresholdBar);
            live.Controls.Add(new Label
            {
                Text = "Talk at your normal volume, then set the threshold a little above the level you see.",
                ForeColor = Theme.Muted, Font = Theme.Ui(8.75F), Location = new Point(18, 222), Size = new Size(LW - 36, 36),
            });

            int lh = live.Height;
            autostartCheck = new Toggle { Text = "Start with Windows", Location = new Point(18, lh - 18 - 28), Size = new Size(LW - 36, 28) };
            autostartCheck.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                try { Autostart.Set(autostartCheck.Checked); }
                catch (Exception ex) { MessageBox.Show("Could not change autostart:\n" + ex.Message, "QuietGuard"); }
            };
            live.Controls.Add(autostartCheck);
            deviceBox = new Dropdown { MicIcon = true, Location = new Point(18, lh - 18 - 28 - 14 - 38), Size = new Size(LW - 36, 38) };
            deviceBox.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                SaveFromUi();
                meter.Stop();
                micError = null;
                EnsureMeterState();
                UpdateStatus();
            };
            live.Controls.Add(deviceBox);
            Caption(live, "MICROPHONE", 18, deviceBox.Top - 22);
            Divider(live, 18, deviceBox.Top - 36, LW - 36);

            pauseButton = new FlatButton { Text = "Pause", Location = new Point(M, bottom - BtnH), Size = new Size(120, BtnH) };
            pauseButton.Click += (s, e) => TogglePause();
            Controls.Add(pauseButton);
            testButton = new FlatButton { Text = "Test alert", Primary = true, Font = Theme.Semi(10F), Location = new Point(M + 130, bottom - BtnH), Size = new Size(LW - 130, BtnH) };
            testButton.Click += (s, e) => Warn();
            Controls.Add(testButton);

            ClientSize = new Size(rx + RW + M, bottom + M);
            ResumeLayout(false);
            PerformLayout();
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = Theme.Ui(9.5F) };
            menu.Items.Add(new ToolStripMenuItem("Open", null, (s, e) => ShowWindow()) { Padding = new Padding(4, 5, 4, 5) });
            pauseItem = new ToolStripMenuItem("Pause", null, (s, e) => TogglePause()) { Padding = new Padding(4, 5, 4, 5) };
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit", null, (s, e) => ExitApp()) { Padding = new Padding(4, 5, 4, 5) });

            tray.ContextMenuStrip = menu;
            tray.Icon = iconApp;
            tray.Text = "QuietGuard";
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = true;
        }

        void LoadDevices()
        {
            loading = true;
            deviceBox.Items.Clear();
            deviceBox.Items.Add("Windows default microphone");
            deviceBox.Items.AddRange(MicMeter.GetDevices());
            int idx = settings.Device == "" ? 0 : deviceBox.Items.IndexOf(settings.Device);
            deviceBox.SelectedIndex = idx < 0 ? 0 : idx;
            deviceBox.Invalidate();
            loading = false;
        }

        void ApplySettingsToUi()
        {
            loading = true;
            thresholdBar.Value = settings.Threshold;
            secondsBox.Value = settings.Seconds;
            cooldownBox.Value = settings.Cooldown;
            scheduleCheck.Checked = settings.UseSchedule;
            fromPicker.Value = settings.FromMin;
            toPicker.Value = settings.ToMin;
            bannerCheck.Checked = settings.Banner;
            soundCheck.Checked = settings.Sound;
            toastCheck.Checked = settings.Toast;
            soundBox.SelectedIndex = settings.SoundId;
            volumeBar.Value = settings.Volume;
            try { autostartCheck.Checked = Autostart.IsEnabled(); } catch { }
            loading = false;
            RefreshDependentUi();
        }

        void SaveFromUi()
        {
            settings.Device = deviceBox.SelectedIndex > 0 ? deviceBox.SelectedItem : "";
            settings.Threshold = thresholdBar.Value;
            settings.Seconds = secondsBox.Value;
            settings.Cooldown = cooldownBox.Value;
            settings.UseSchedule = scheduleCheck.Checked;
            settings.FromMin = fromPicker.Value;
            settings.ToMin = toPicker.Value;
            settings.Banner = bannerCheck.Checked;
            settings.Sound = soundCheck.Checked;
            settings.Toast = toastCheck.Checked;
            settings.SoundId = Math.Max(0, soundBox.SelectedIndex);
            settings.Volume = volumeBar.Value;
            settings.Save();
        }

        void RefreshDependentUi()
        {
            thresholdValue.Text = settings.Threshold.ToString();
            volumeValue.Text = settings.Volume + " %";
            meterBar.Threshold = settings.Threshold;
            meterBar.Invalidate();
            fromPicker.Enabled = toPicker.Enabled = settings.UseSchedule;
            soundBox.Enabled = volumeBar.Enabled = settings.Sound;
        }

        void OnSettingChanged(object sender, EventArgs e)
        {
            if (loading) return;
            int oldVolume = settings.Volume;
            SaveFromUi();
            if (settings.Volume != oldVolume) RebuildSound();
            RefreshDependentUi();
            UpdateStatus();
            EnsureMeterState();
        }

        bool IsActiveNow()
        {
            if (settings.Paused) return false;
            if (!settings.UseSchedule) return true;
            var now = DateTime.Now;
            int m = now.Hour * 60 + now.Minute, f = settings.FromMin, t = settings.ToMin;
            if (f == t) return true;
            return f < t ? (m >= f && m < t) : (m >= f || m < t);
        }

        void EnsureMeterState()
        {
            bool want = IsActiveNow() || Visible;
            if (want && !meter.Running)
            {
                meter.Start(deviceBox.SelectedIndex - 1);
            }
            else if (!want && meter.Running)
            {
                meter.Stop();
                displayLevel = 0;
                loudSeconds = 0;
                meterBar.SetLevel(0);
                readout.Value = -1;
                readout.State = "Off";
                readout.StateColor = Theme.Muted;
                readout.Invalidate();
            }
            if (!IsActiveNow()) SetTrayIcon(iconGray);
        }

        void OnLevel(double db)
        {
            if (!meter.Running) return;
            if (micError != null) { micError = null; UpdateStatus(); }

            double level = Math.Max(0, Math.Min(100, (db + 60) / 60 * 100));
            displayLevel = level > displayLevel ? level : displayLevel * 0.8 + level * 0.2;
            meterBar.SetLevel(displayLevel);

            readout.Value = (int)Math.Round(displayLevel);
            if (displayLevel >= settings.Threshold) { readout.State = "Too loud"; readout.StateColor = Theme.Red; }
            else if (displayLevel >= settings.Threshold * 0.85) { readout.State = "Close"; readout.StateColor = Theme.Amber; }
            else { readout.State = "Quiet"; readout.StateColor = Theme.Green; }
            readout.Invalidate();

            if (!IsActiveNow())
            {
                loudSeconds = 0;
                SetTrayIcon(iconGray);
                return;
            }

            if (level >= settings.Threshold) loudSeconds = Math.Min(settings.Seconds, loudSeconds + 0.1);
            else loudSeconds = Math.Max(0, loudSeconds - 0.05);

            double sinceWarn = (DateTime.Now - lastWarn).TotalSeconds;
            if (loudSeconds >= settings.Seconds && sinceWarn >= settings.Cooldown)
            {
                Warn();
                loudSeconds = 0;
                sinceWarn = 0;
            }

            if (sinceWarn < 4) SetTrayIcon(iconRed);
            else if (loudSeconds >= settings.Seconds * 0.4) SetTrayIcon(iconAmber);
            else SetTrayIcon(iconApp);
        }

        void RebuildSound()
        {
            var old = chime.Stream;
            chime.Stream = Chime.Create(settings.SoundId, settings.Volume);
            if (old != null) old.Dispose();
        }

        void PlayChime()
        {
            try { chime.Stream.Position = 0; chime.Play(); } catch { }
        }

        void Warn()
        {
            lastWarn = DateTime.Now;
            if (settings.Banner) banner.Flash();
            if (settings.Sound) PlayChime();
            if (settings.Toast)
                tray.ShowBalloonTip(4000, "Too loud!", "You've been pretty loud for a while.", ToolTipIcon.Warning);
        }

        void OnMicError(string msg)
        {
            if (micError != msg)
                tray.ShowBalloonTip(6000, "QuietGuard: no microphone", msg, ToolTipIcon.Error);
            micError = msg;
            UpdateStatus();
        }

        void TogglePause()
        {
            settings.Paused = !settings.Paused;
            loudSeconds = 0;
            UpdateStatus();
            EnsureMeterState();
        }

        void UpdateStatus()
        {
            string text, tip;
            Color color;
            if (micError != null)
            {
                text = "No microphone"; tip = "no microphone"; color = Theme.Red;
            }
            else if (settings.Paused)
            {
                text = "Paused"; tip = "paused"; color = Theme.Amber;
            }
            else if (!IsActiveNow())
            {
                text = "Active from " + FormatTime(settings.FromMin);
                tip = "active from " + FormatTime(settings.FromMin); color = Theme.Muted;
            }
            else
            {
                text = "Active – listening"; tip = "active"; color = Theme.Green;
            }
            statusPill.Text = text;
            statusPill.Tint = color;
            statusPill.Invalidate();
            statusTip.SetToolTip(statusPill, micError ?? "");
            pauseButton.Text = settings.Paused ? "Resume" : "Pause";
            pauseItem.Text = pauseButton.Text;
            tray.Text = "QuietGuard – " + tip;
            if (!IsActiveNow()) SetTrayIcon(iconGray);
        }

        static string FormatTime(int min) { return (min / 60).ToString("00") + ":" + (min % 60).ToString("00"); }

        void SetTrayIcon(Icon icon)
        {
            if (tray.Icon != icon) tray.Icon = icon;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (deviceBox != null) EnsureMeterState();
        }

        void ShowWindow()
        {
            allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!closeHintShown)
                {
                    closeHintShown = true;
                    tray.ShowBalloonTip(4000, "QuietGuard is still running",
                        "It keeps listening in the background. Quit it from the icon in the bottom right.",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            meter.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
