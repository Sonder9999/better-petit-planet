using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameCapture;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.View;

public partial class CaptureTestWindow : FluentWindow
{
    private IGameCapture? _capture;
    private readonly InstrumentDetector? _instrumentDetector;
    private OpenCvSharp.Size _cacheSize;
    private readonly Stopwatch _fpsStopwatch = new();
    private int _frameCount;
    private double _currentFps;
    private long _lastFpsUpdate;
    private long _lastInstrumentCheck;

    private static readonly SolidColorBrush DetectedBrush = new(Color.FromRgb(16, 180, 60));
    private static readonly SolidColorBrush NotDetectedBrush = new(Color.FromRgb(230, 70, 70));
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(150, 150, 150));

    private readonly Border[] _btnRois;

    static CaptureTestWindow()
    {
        DetectedBrush.Freeze();
        NotDetectedBrush.Freeze();
        NeutralBrush.Freeze();
    }

    public CaptureTestWindow()
    {
        InitializeComponent();

        _btnRois = [BtnRoi0, BtnRoi1, BtnRoi2, BtnRoi3, BtnRoi4, BtnRoi5, BtnRoi6];
        _instrumentDetector = App.GetService<InstrumentDetector>() ?? new InstrumentDetector();

        // 订阅全局乐器状态更新（OCR在后台异步运行，UI线程绝不阻塞）
        if (_instrumentDetector != null)
        {
            _instrumentDetector.DetectionUpdated += OnInstrumentDetectionUpdated;
            if (_instrumentDetector.LatestResult != null)
            {
                UpdateInstrumentUi(_instrumentDetector.LatestResult);
            }
        }

        Closed += (sender, args) =>
        {
            CompositionTarget.Rendering -= Loop;
            if (_instrumentDetector != null)
            {
                _instrumentDetector.DetectionUpdated -= OnInstrumentDetectionUpdated;
            }
            try
            {
                _capture?.Stop();
                _capture?.Dispose();
                _capture = null;
            }
            catch
            {
                // Ignored
            }
        };
    }

    private void OnInstrumentDetectionUpdated(InstrumentDetectionResult res)
    {
        Dispatcher.InvokeAsync(() => UpdateInstrumentUi(res));
    }

    private void UpdateInstrumentUi(InstrumentDetectionResult res)
    {
        TxtInstrument.Text = res.StatusSummary;
        TxtInstrument.Foreground = res.IsDetected ? DetectedBrush : NotDetectedBrush;

        var activeBrush = res.IsDetected ? DetectedBrush : NeutralBrush;
        foreach (var btn in _btnRois)
        {
            btn.BorderBrush = activeBrush;
        }
    }

    public void StartCapture(IntPtr hWnd, CaptureModes captureMode)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _capture = GameCaptureFactory.Create(captureMode);
            _capture.Start(hWnd);

            TxtMode.Text = $"模式: {captureMode}";
            _fpsStopwatch.Restart();
            CompositionTarget.Rendering += Loop;
        }
        catch (Exception ex)
        {
            TxtMode.Text = $"启动失败: {ex.Message}";
        }
    }

    private int _isOcrBusy;

    private void Loop(object? sender, EventArgs e)
    {
        if (_capture == null) return;

        var sw = Stopwatch.StartNew();
        using var captureFrame = _capture.Capture();
        var mat = captureFrame?.Frame;
        sw.Stop();

        if (mat != null && !mat.Empty())
        {
            long captureMs = sw.ElapsedMilliseconds;
            _frameCount++;

            long now = _fpsStopwatch.ElapsedMilliseconds;
            if (now - _lastFpsUpdate >= 500)
            {
                _currentFps = _frameCount * 1000.0 / (now - _lastFpsUpdate);
                _frameCount = 0;
                _lastFpsUpdate = now;

                TxtResolution.Text = $"分辨率: {mat.Width} x {mat.Height}";
                TxtCaptureTime.Text = $"耗时: {captureMs} ms";
                TxtFps.Text = $"帧率: {_currentFps:F1} FPS";
            }

            if (_cacheSize != mat.Size())
            {
                DisplayCaptureResultImage.Source = mat.ToWriteableBitmap();
                _cacheSize = mat.Size();

                // 更新自动拾取 ROI 视觉框
                var roi = PetitRoiCalculator.Calculate(mat.Width, mat.Height);
                Canvas.SetLeft(RoiBorder, roi.X);
                Canvas.SetTop(RoiBorder, roi.Y);
                RoiBorder.Width = roi.Width;
                RoiBorder.Height = roi.Height;

                // 更新 7 个独立音符按键小方块视觉选框 (依据 2560x1440 原生标注比例自适应)
                for (int i = 0; i < 7 && i < _btnRois.Length; i++)
                {
                    var b = InstrumentTargets.ButtonBounds[i];
                    int bx = (int)Math.Round(b.X * mat.Width / (double)InstrumentTargets.BaseWidth);
                    int by = (int)Math.Round(b.Y * mat.Height / (double)InstrumentTargets.BaseHeight);
                    int bw = (int)Math.Round(b.W * mat.Width / (double)InstrumentTargets.BaseWidth);
                    int bh = (int)Math.Round(b.H * mat.Height / (double)InstrumentTargets.BaseHeight);

                    Canvas.SetLeft(_btnRois[i], bx);
                    Canvas.SetTop(_btnRois[i], by);
                    _btnRois[i].Width = bw;
                    _btnRois[i].Height = bh;
                }
            }
            else if (DisplayCaptureResultImage.Source is WriteableBitmap wb)
            {
                wb.Lock();
                WriteableBitmapConverter.ToWriteableBitmap(mat, wb);
                wb.AddDirtyRect(new System.Windows.Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight));
                wb.Unlock();
            }

            // 若后台截图器未运行，测试窗口低频（约 600ms）在后台异步任务中触发一次 OCR 检测，严禁阻塞 UI 线程
            if (now - _lastInstrumentCheck >= 600)
            {
                _lastInstrumentCheck = now;
                if (_instrumentDetector != null && System.Threading.Interlocked.CompareExchange(ref _isOcrBusy, 1, 0) == 0)
                {
                    var frameCopy = mat.Clone();
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            _instrumentDetector.DetectFull(frameCopy);
                        }
                        catch
                        {
                            // Ignored
                        }
                        finally
                        {
                            frameCopy.Dispose();
                            System.Threading.Interlocked.Exchange(ref _isOcrBusy, 0);
                        }
                    });
                }
            }
        }
    }
}
