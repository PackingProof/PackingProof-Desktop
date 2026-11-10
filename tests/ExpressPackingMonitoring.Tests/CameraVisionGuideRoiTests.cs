using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Vision;
#if VISION_OBB
using ExpressPackingMonitoring.Vision.Obb;
#endif
using OpenCvSharp;
using Xunit;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;
using WpfPath = System.Windows.Shapes.Path;

namespace ExpressPackingMonitoring.Tests;

[Collection("WPF render tests")]
public sealed class CameraVisionGuideRoiTests
{
#if VISION_OBB
    [Fact]
    public void RealModelSmoke_UsesOnlyTheGuideCrop()
    {
        if (Environment.GetEnvironmentVariable(
                "EPM_VISION_GUIDE_MODEL_SMOKE") != "1")
        {
            Assert.Skip(
                "未设置 EPM_VISION_GUIDE_MODEL_SMOKE=1，跳过真实模型冒烟");
        }

        string modelPath = RequireEnvironmentPath(
            "EPM_VISION_GUIDE_MODEL_PATH");
        string samplePath = RequireEnvironmentPath(
            "EPM_VISION_GUIDE_SAMPLE_PATH");
        using Mat frame = Cv2.ImRead(samplePath, ImreadModes.Color);
        Assert.False(frame.Empty());

        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);
        CvRect guide = CameraBarcodeGuideLayout.ToFrameRect(
            frame.Width,
            frame.Height,
            geometry);
        IVisionObbDetector detector = VisionObbModule.Instance.TryLoad(
            modelPath,
            preferDirectMl: true)
            ?? throw new InvalidOperationException("真实视觉模型未能加载");
        using var processor = new CameraBarcodeVisionProcessor(
            new CameraBarcodeFrameDecoder(),
            () => (true, modelPath),
            cameraBarcodeDecodingEnabledProvider: () => false,
            visionObbDecodingEnabledProvider: () => true,
            detectorFactory: new FixedDetectorFactory(detector));

        _ = processor.ProcessFrame(
            frame,
            _ => true,
            geometry,
            updateVision: true,
            decodeBarcodes: false);

        Assert.NotEmpty(processor.ObbDetections);
        Assert.All(
            processor.ObbDetections,
            detection =>
            {
                Assert.InRange(detection.CenterX, guide.X, guide.Right);
                Assert.InRange(detection.CenterY, guide.Y, guide.Bottom);
            });
    }
#endif

    [Fact]
    public void EnhancedVisionRunsOnGuideCropAndMapsDetectionBackToTheFrame()
    {
        using var frame = new Mat(
            new CvSize(1000, 800),
            MatType.CV_8UC3,
            Scalar.White);
        var detector = new RecordingDetector(
            new VisionObbDetection(
                CenterX: 100,
                CenterY: 50,
                Width: 80,
                Height: 40,
                AngleDegrees: 18,
                Score: 0.96f,
                ClassId: 3));
        using var processor = new CameraBarcodeVisionProcessor(
            new CameraBarcodeFrameDecoder(),
            () => (true, "vision-obb.onnx"),
            cameraBarcodeDecodingEnabledProvider: () => false,
            visionObbDecodingEnabledProvider: () => true,
            detectorFactory: new FixedDetectorFactory(detector));
        var geometry = new CameraBarcodeGuideGeometry(
            WidthRatio: 0.6,
            HeightRatio: 0.5,
            OffsetX: 1,
            OffsetY: 1);

        string? result = processor.ProcessFrame(
            frame,
            _ => true,
            geometry,
            updateVision: true,
            decodeBarcodes: false);

        Assert.Null(result);
        Assert.Equal(new CvSize(600, 400), detector.InputSize);
        VisionObbDetection mapped = Assert.Single(processor.ObbDetections);
        Assert.Equal(500, mapped.CenterX);
        Assert.Equal(450, mapped.CenterY);
        Assert.Equal(18, mapped.AngleDegrees);
        Assert.Equal(3, mapped.ClassId);
    }

    [Fact]
    public void SmartZoomOnlyStillUsesTheOriginalFullFramePath()
    {
        using var frame = new Mat(
            new CvSize(1000, 800),
            MatType.CV_8UC3,
            Scalar.White);
        var detector = new RecordingDetector(
            new VisionObbDetection(100, 50, 80, 40, 0, 0.96f));
        using var processor = new CameraBarcodeVisionProcessor(
            new CameraBarcodeFrameDecoder(),
            () => (true, "vision-obb.onnx"),
            cameraBarcodeDecodingEnabledProvider: () => false,
            visionObbDecodingEnabledProvider: () => false,
            detectorFactory: new FixedDetectorFactory(detector));

        _ = processor.ProcessFrame(
            frame,
            _ => true,
            new CameraBarcodeGuideGeometry(0.5, 0.5, 1, -1),
            updateVision: true,
            decodeBarcodes: false);

        Assert.Equal(new CvSize(1000, 800), detector.InputSize);
        VisionObbDetection detection = Assert.Single(processor.ObbDetections);
        Assert.Equal(100, detection.CenterX);
        Assert.Equal(50, detection.CenterY);
    }

    [Fact]
    public void MarkerGeometryContainsRoundedCornersAndFourMidpointSegments()
    {
        PathGeometry geometry = MainWindow.BuildVisionGuideMarkerGeometry(
            width: 240,
            height: 160);

        Assert.Equal(8, geometry.Figures.Count);
        Assert.Equal(1.5, geometry.Bounds.Left, 3);
        Assert.Equal(1.5, geometry.Bounds.Top, 3);
        Assert.Equal(238.5, geometry.Bounds.Right, 3);
        Assert.Equal(158.5, geometry.Bounds.Bottom, 3);
        Assert.All(
            geometry.Figures.Where(figure =>
                figure.Segments.OfType<QuadraticBezierSegment>().Any()),
            figure =>
            {
                Assert.False(figure.IsClosed);
                Assert.False(figure.IsFilled);
                Assert.Single(figure.Segments.OfType<QuadraticBezierSegment>());
                Assert.Equal(2, figure.Segments.OfType<LineSegment>().Count());
            });
        Assert.Equal(
            4,
            geometry.Figures.Count(figure =>
                figure.Segments.Count == 1
                && figure.Segments[0] is LineSegment));
    }

    [Fact]
    public void MainWindowUsesCornersForEnhancedVisionAndDashedBoxForEditing()
    {
        string xaml = ReadRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        int cornerPath = xaml.IndexOf(
            "x:Name=\"CameraVisionGuideMarkers\"",
            StringComparison.Ordinal);
        int guideBox = xaml.IndexOf(
            "x:Name=\"CameraBarcodeGuideBox\"",
            StringComparison.Ordinal);

        Assert.True(cornerPath >= 0 && guideBox > cornerPath);
        Assert.Contains(
            "{Binding IsCameraVisionGuideMarkerMode}",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "StrokeDashArray=\"6 3\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "{Binding IsCameraBarcodeGuideEditable}",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsCameraVisionFullFrameMode",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StrokeDashArray",
            SliceCornerPath(xaml, cornerPath, guideBox),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoundedCornersRenderWithoutAnInnerBorder()
    {
        RunOnStaThread(() =>
        {
            const int width = 320;
            const int height = 200;
            var path = new WpfPath
            {
                Data = MainWindow.BuildVisionGuideMarkerGeometry(
                    width,
                    height),
                Stroke = Brushes.Black,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Stretch = Stretch.None
            };
            var root = new Grid
            {
                Width = width,
                Height = height,
                Background = Brushes.White
            };
            root.Children.Add(path);
            root.Measure(new System.Windows.Size(width, height));
            root.Arrange(new System.Windows.Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(root);

            Assert.True(CountDarkPixels(bitmap, new Int32Rect(0, 0, 50, 50)) > 0);
            Assert.True(CountDarkPixels(bitmap, new Int32Rect(width - 50, 0, 50, 50)) > 0);
            Assert.True(CountDarkPixels(bitmap, new Int32Rect(0, height - 50, 50, 50)) > 0);
            Assert.True(CountDarkPixels(bitmap, new Int32Rect(width - 50, height - 50, 50, 50)) > 0);
            Assert.Equal(
                0,
                CountDarkPixels(bitmap, new Int32Rect(60, 60, width - 120, height - 120)));

            if (Environment.GetEnvironmentVariable("EPM_VISION_GUIDE_SHOT") == "1")
                SaveScreenshot(bitmap);
        });
    }

    private static int CountDarkPixels(BitmapSource bitmap, Int32Rect area)
    {
        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        bitmap.CopyPixels(area, pixels, stride, 0);
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] < 96
                && pixels[index + 1] < 96
                && pixels[index + 2] < 96)
            {
                count++;
            }
        }
        return count;
    }

    private static void SaveScreenshot(BitmapSource bitmap)
    {
        string root = FindRepositoryRoot();
        string folder = Path.Combine(
            root,
            "artifacts",
            "ui-shots",
            "vision-guide");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(folder, "rounded-corners.png"));
        encoder.Save(stream);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF 渲染超时");
        if (failure != null)
            throw new Xunit.Sdk.XunitException($"WPF 渲染失败：{failure}");
    }

    private static string SliceCornerPath(string xaml, int start, int end) =>
        xaml[start..end];

    private static string ReadRepositoryFile(params string[] parts)
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine([root, .. parts]));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }

    private static string RequireEnvironmentPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
        {
            throw new FileNotFoundException(
                $"环境变量 {name} 没有指向存在的文件");
        }
        return value;
    }

    private sealed class FixedDetectorFactory(IVisionObbDetector detector)
        : IVisionObbDetectorFactory
    {
        public bool IsAvailable => true;

        public IVisionObbDetector? TryLoad(
            string? modelPath,
            bool preferDirectMl = true) => detector;

        public VisionModelMetadata? ReadModelMetadata(string modelPath) => null;
    }

    private sealed class RecordingDetector(VisionObbDetection detection)
        : IVisionObbDetector
    {
        internal CvSize InputSize { get; private set; }
        public string ExecutionProvider => "test";

        public IReadOnlyList<VisionObbDetection> Detect(Mat frame)
        {
            InputSize = frame.Size();
            return [detection];
        }

        public void Dispose()
        {
        }
    }
}
