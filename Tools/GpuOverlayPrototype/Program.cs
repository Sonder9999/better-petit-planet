using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using BetterPetitPlanet.MusicOverlay.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace GpuOverlayPrototype;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string Option(string key, string fallback)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        string log = Option("--log", Path.Combine(Path.GetTempPath(), "BetterPetitPlanet.GpuOverlayPrototype.log"));
        void Log(string text) => File.AppendAllText(log, DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (args.Contains("--backdrop"))
            {
                Application.EnableVisualStyles();
                using var form = new BackdropForm(Log, int.Parse(Option("--seconds", "30")));
                Application.Run(form);
                return 0;
            }
            int seconds = int.Parse(Option("--seconds", "30"));
            using var backdrop = args.Contains("--no-backdrop") ? null : Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                ArgumentList = { "--backdrop", "--seconds", seconds.ToString(), "--log", log }
            });
            Log("START version=layered-no-sla-topmost-v5.1 pid=" + Environment.ProcessId + "; visual correctness and visible cadence require desktop observation.");
            using var host = new GpuOverlayHost(new MovingNotes());
            host.SetBounds(120, 150, 960, 540);
            if (backdrop == null || args.Contains("--self-test")) host.Show();
            nint backdropHwnd = 0;
            bool backdropClosed = false;
            var runClock = Stopwatch.StartNew();
            for (int second = 0; runClock.Elapsed.TotalSeconds < seconds && host.Failure == null && !backdropClosed; second++)
            {
                for (int poll = 0; poll < 20 && runClock.Elapsed.TotalSeconds < seconds; poll++)
                {
                    Thread.Sleep(50);
                    if (!args.Contains("--self-test") && backdrop != null)
                    {
                        if (backdrop.HasExited) { host.Hide(); backdropClosed = true; break; }
                        if (backdropHwnd == 0) { backdrop.Refresh(); backdropHwnd = backdrop.MainWindowHandle; }
                        if (backdropHwnd == 0) { host.Hide(); continue; }
                        if (!IsWindow(backdropHwnd)) { host.Hide(); backdropClosed = true; break; }
                        if (IsIconic(backdropHwnd) || !IsWindowVisible(backdropHwnd)) { host.Hide(); continue; }
                        nint foreground = GetForegroundWindow();
                        GetWindowThreadProcessId(foreground, out uint foregroundPid);
                        if (foregroundPid != backdrop.Id) { host.Hide(); continue; }
                        if (GetClientRect(backdropHwnd, out var client))
                        {
                            var origin = new NativePoint();
                            if (ClientToScreen(backdropHwnd, ref origin) && client.Right > 0 && client.Bottom > 0)
                            {
                                host.SetBounds(origin.X, origin.Y, client.Right, client.Bottom);
                                host.Show();
                            }
                            else host.Hide();
                        }
                        else host.Hide();
                    }
                }
                if (args.Contains("--self-test"))
                {
                    if (second == 3) { host.SetBounds(180, 180, 800, 450); Log("RESIZE 800x450"); }
                    if (second == 5) { host.Hide(); Log("HIDE"); }
                    if (second == 6) { host.Show(); Log("SHOW"); }
                    if (second == 8) { host.RequestDeviceRecreation(); Log("REQUEST_DEVICE_RECREATION (synthetic lifecycle check)"); }
                    if (second == 10) { host.SetBounds(120, 150, 960, 540); Log("RESTORE_BOUNDS"); }
                }
                Log("STAT " + host.Statistics);
            }
            host.Dispose();
            Log("HANDLE_AFTER_DISPOSE " + host.Handle);
            if (args.Contains("--self-test") && host.Failure == null)
            {
                using var reopened = new GpuOverlayHost(new MovingNotes());
                reopened.SetBounds(120, 150, 960, 540);
                reopened.Show();
                Thread.Sleep(1000);
                reopened.Dispose();
                Log("REOPEN frames=" + reopened.Statistics.Frames + " handleAfterDispose=" + reopened.Handle + " failure=" + reopened.Failure);
            }
            Log(host.Failure == null ? "CLEAN_EXIT " + host.Statistics : "FAILURE " + host.Failure);
            if (backdrop is { HasExited: false }) backdrop.CloseMainWindow();
            return host.Failure == null ? 0 : 1;
        }
        catch (Exception ex) { Log("FAILURE " + ex); return 1; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private sealed class MovingNotes : IGpuOverlayContent
    {
        private ID2D1SolidColorBrush? _note, _line, _rim;
        public void OnDeviceCreated(ID2D1DeviceContext context)
        {
            _note = context.CreateSolidColorBrush(new Color4(0.25f, 1f, 0.65f, 0.9f));
            _line = context.CreateSolidColorBrush(new Color4(0.4f, 0.75f, 1f, 0.55f));
            _rim = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.95f));
        }
        public void Render(in GpuOverlayFrame frame)
        {
            var context = frame.Context;
            float judge = frame.HeightDip * 0.86f;
            context.DrawLine(new Vector2(20, judge), new Vector2(frame.WidthDip - 20, judge), _line!, 3);
            for (int lane = 0; lane < 7; lane++)
            {
                float x = (lane + 1) * frame.WidthDip / 8;
                context.DrawLine(new Vector2(x, 16), new Vector2(x, judge), _line!, 1);
                float y = (float)((frame.MonotonicMs * 0.18 + lane * 73) % (judge - 28)) + 14;
                var ellipse = new Ellipse(new Vector2(x, y), 18, 18);
                context.FillEllipse(ellipse, _note!);
                context.DrawEllipse(ellipse, _rim!, 2);
            }
        }
        public void OnDeviceDestroyed()
        {
            _note?.Dispose(); _note = null;
            _line?.Dispose(); _line = null;
            _rim?.Dispose(); _rim = null;
        }
        public void Dispose() => OnDeviceDestroyed();
    }

    private sealed class BackdropForm : Form
    {
        public BackdropForm(Action<string> log, int seconds)
        {
            Text = "GPU prototype v5.1 — no SLA — click / minimize / close — " + seconds + "s";
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(90, 90);
            ClientSize = new System.Drawing.Size(1020, 640);
            DoubleBuffered = true;
            var button = new Button { Text = "Click through GPU overlay: 0", Bounds = new System.Drawing.Rectangle(350, 430, 320, 60) };
            int clicks = 0;
            button.Click += (_, _) => { button.Text = "Click through GPU overlay: " + ++clicks; log("BACKDROP_BUTTON_CLICK " + clicks); };
            Controls.Add(button);
            var deadline = Stopwatch.StartNew();
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) => Text = "GPU prototype v5.1 — no SLA — click / minimize / close — " + Math.Max(0, seconds - (int)deadline.Elapsed.TotalSeconds) + "s";
            timer.Start();
            FormClosed += (_, _) => timer.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            for (int y = 0; y < ClientSize.Height; y += 64)
                for (int x = 0; x < ClientSize.Width; x += 64)
                    e.Graphics.FillRectangle((x / 64 + y / 64) % 2 == 0 ? System.Drawing.Brushes.MidnightBlue : System.Drawing.Brushes.LightSteelBlue, x, y, 64, 64);
            e.Graphics.DrawString("CHECKERBOARD MUST REMAIN VISIBLE BETWEEN MOVING GREEN NOTES", Font, System.Drawing.Brushes.Orange, 20, 20);
        }
    }
}
