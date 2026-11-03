using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Vision;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Point2f = OpenCvSharp.Point2f;

namespace ExpressPackingMonitoring.UI;

public partial class MainWindow
{
    private const double DefaultObbAnimationDurationMs = 220;
    private const double MinObbAnimationDurationMs = 80;
    private const double MaxObbAnimationDurationMs = 240;
    private const double VisionObbCornerRadius = 6;
    private const long ObbTrackRetentionMs = 1500;
    private static readonly Brush VisionObbIdleStroke = new SolidColorBrush(Color.FromRgb(59, 130, 246));
    private static readonly Brush VisionObbIdleFill = new SolidColorBrush(Color.FromArgb(48, 59, 130, 246));
    private static readonly Brush VisionObbCandidateStroke = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly Brush VisionObbCandidateFill = new SolidColorBrush(Color.FromArgb(64, 245, 158, 11));
    private static readonly Brush VisionObbConfirmedStroke = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly Brush VisionObbConfirmedFill = new SolidColorBrush(Color.FromArgb(64, 34, 197, 94));
    private readonly List<AnimatedVisionObbBox> _animatedVisionObbBoxes = [];
    private bool _visionObbAnimationRunning;
    private int _nextVisionObbTrackId = 1;
    private AnimatedVisionObbBox? _selectedVisionObbTrack;
    private long _lastVisionObbTargetUpdateMs;
    private double _visionObbAnimationDurationMs = DefaultObbAnimationDurationMs;

    private sealed class AnimatedVisionObbBox
    {
        internal int Id { get; init; }
        internal required Rectangle Shape { get; init; }
        internal required RotateTransform Rotation { get; init; }
        internal required Point[] StartPoints { get; set; }
        internal required Point[] TargetPoints { get; set; }
        internal Point[] CurrentPoints { get; set; } = [];
        internal Point LastCenter { get; set; }
        internal Point Velocity { get; set; }
        internal long LastSeenMs { get; set; }
        internal long StartedAtMs { get; set; }
        internal double DurationMs { get; set; } = DefaultObbAnimationDurationMs;
        internal double StartOpacity { get; set; }
        internal double TargetOpacity { get; set; }
        internal bool RemoveWhenFinished { get; set; }
        internal bool Hidden { get; set; }
    }

    private readonly record struct VisionObbTrackMatch(
        AnimatedVisionObbBox Track,
        int TargetIndex,
        double Cost);

    internal readonly record struct VisionObbRectangleFrame(
        double Width,
        double Height,
        double Angle,
        Point TopLeft,
        double CornerRadius);

    private void UpdateCameraVisionObbBoxes(
        MainViewModel vm,
        bool targetCadenceChanged = false)
    {
        if ((!vm.ShouldRunVisionObb && !vm.ShouldRunSeparateCameraSmartZoom)
            || vm.IsCameraSleeping
            || vm.IsZoomingActive
            || vm.IsPreviewGuideEditing)
        {
            ClearAnimatedVisionObbBoxes();
            return;
        }

        if (vm.VideoFrame == null)
        {
            ClearAnimatedVisionObbBoxes();
            return;
        }

        double sourceWidth = vm.CameraFrameSize.Width;
        double sourceHeight = vm.CameraFrameSize.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            ClearAnimatedVisionObbBoxes();
            return;
        }

        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
            sourceWidth,
            sourceHeight,
            VideoImage.ActualWidth,
            VideoImage.ActualHeight);
        if (videoRect.IsEmpty || videoRect.Width <= 0 || videoRect.Height <= 0)
        {
            ClearAnimatedVisionObbBoxes();
            return;
        }

        Point origin = VideoImage.TranslatePoint(
            new Point(videoRect.X, videoRect.Y),
            CameraVisionObbLayer);
        double scale = videoRect.Width / sourceWidth;
        long nowMs = Environment.TickCount64;
        if (targetCadenceChanged)
        {
            if (_lastVisionObbTargetUpdateMs > 0)
            {
                _visionObbAnimationDurationMs = ResolveObbAnimationDurationMs(
                    nowMs - _lastVisionObbTargetUpdateMs);
            }
            _lastVisionObbTargetUpdateMs = nowMs;
        }
        Point[][] targets = vm.CameraVisionObbDetections
            .Select(detection => BuildObbDisplayPoints(detection, origin, scale))
            .ToArray();
        int selectedTargetIndex = -1;
        if (vm.CameraVisionSelectedObbDetection is { } selected)
        {
            for (int index = 0; index < vm.CameraVisionObbDetections.Count; index++)
            {
                if (vm.CameraVisionObbDetections[index].Equals(selected))
                {
                    selectedTargetIndex = index;
                    break;
                }
            }
        }
        SynchronizeAnimatedVisionObbBoxes(
            targets,
            selectedTargetIndex,
            _visionObbAnimationDurationMs);
        UpdateVisionObbBoxColors(vm);
    }

    private void SynchronizeAnimatedVisionObbBoxes(
        IReadOnlyList<Point[]> targets,
        int selectedTargetIndex,
        double animationDurationMs)
    {
        long nowMs = Environment.TickCount64;
        _animatedVisionObbBoxes.RemoveAll(box =>
        {
            if (!box.Hidden || nowMs - box.LastSeenMs <= ObbTrackRetentionMs)
                return false;
            CameraVisionObbLayer.Children.Remove(box.Shape);
            return true;
        });

        var matches = new List<VisionObbTrackMatch>();
        foreach (AnimatedVisionObbBox track in _animatedVisionObbBoxes)
        {
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                if (TryCalculateTrackMatchCost(
                        track,
                        targets[targetIndex],
                        nowMs,
                        out double cost))
                {
                    matches.Add(new VisionObbTrackMatch(track, targetIndex, cost));
                }
            }
        }

        matches.Sort((left, right) => left.Cost.CompareTo(right.Cost));
        var matchedTargets = new bool[targets.Count];
        var matchedTracks = new HashSet<AnimatedVisionObbBox>();
        var targetTracks = new AnimatedVisionObbBox?[targets.Count];
        foreach (VisionObbTrackMatch match in matches)
        {
            if (matchedTracks.Contains(match.Track) || matchedTargets[match.TargetIndex])
                continue;
            matchedTracks.Add(match.Track);
            matchedTargets[match.TargetIndex] = true;
            targetTracks[match.TargetIndex] = match.Track;
            UpdateTrackFromTarget(
                match.Track,
                targets[match.TargetIndex],
                nowMs,
                animationDurationMs);
        }

        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            if (matchedTargets[targetIndex])
                continue;
            targetTracks[targetIndex] = CreateTrack(
                targets[targetIndex],
                nowMs,
                animationDurationMs);
        }

        _selectedVisionObbTrack =
            selectedTargetIndex >= 0 && selectedTargetIndex < targetTracks.Length
                ? targetTracks[selectedTargetIndex]
                : null;

        foreach (AnimatedVisionObbBox track in _animatedVisionObbBoxes
                     .Where(track => !matchedTracks.Contains(track)))
        {
            double elapsedMs = Math.Max(0, nowMs - track.LastSeenMs);
            Point[] coasted = ShiftPoints(
                track.CurrentPoints,
                track.Velocity.X * elapsedMs,
                track.Velocity.Y * elapsedMs);
            track.TargetPoints = coasted;
            track.StartPoints = track.CurrentPoints;
            track.StartedAtMs = nowMs;
            track.DurationMs = Math.Max(animationDurationMs, DefaultObbAnimationDurationMs);
            track.StartOpacity = track.Shape.Opacity;
            track.TargetOpacity = 0;
            track.RemoveWhenFinished = true;
        }

        EnsureVisionObbAnimation();
    }

    private void UpdateTrackFromTarget(
        AnimatedVisionObbBox track,
        Point[] target,
        long nowMs,
        double animationDurationMs)
    {
        Point targetCenter = GetCenter(target);
        long elapsedMs = nowMs - track.LastSeenMs;
        if (elapsedMs > 0)
        {
            var observedVelocity = new Point(
                (targetCenter.X - track.LastCenter.X) / elapsedMs,
                (targetCenter.Y - track.LastCenter.Y) / elapsedMs);
            track.Velocity = new Point(
                track.Velocity.X * 0.65 + observedVelocity.X * 0.35,
                track.Velocity.Y * 0.65 + observedVelocity.Y * 0.35);
        }

        track.LastCenter = targetCenter;
        track.LastSeenMs = nowMs;
        track.Hidden = false;
        track.Shape.Visibility = Visibility.Visible;
        track.DurationMs = animationDurationMs;
        StartBoxAnimation(track, target, nowMs, removeWhenFinished: false);
    }

    private AnimatedVisionObbBox CreateTrack(
        Point[] target,
        long nowMs,
        double animationDurationMs)
    {
        var rotation = new RotateTransform();
        var rectangle = new Rectangle
        {
            Stroke = null,
            Fill = null,
            StrokeThickness = 3,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = rotation
        };
        Point center = GetCenter(target);
        Point[] start = target
            .Select(point => new Point(
                center.X + (point.X - center.X) * 0.94,
                center.Y + (point.Y - center.Y) * 0.94))
            .ToArray();
        SetRectangleFrame(rectangle, rotation, start);
        CameraVisionObbLayer.Children.Add(rectangle);
        var created = new AnimatedVisionObbBox
        {
            Id = _nextVisionObbTrackId++,
            Shape = rectangle,
            Rotation = rotation,
            StartPoints = start,
            TargetPoints = target,
            CurrentPoints = start,
            LastCenter = center,
            LastSeenMs = nowMs,
            DurationMs = animationDurationMs,
            StartOpacity = 0,
            TargetOpacity = 1
        };
        StartBoxAnimation(created, target, nowMs, removeWhenFinished: false);
        _animatedVisionObbBoxes.Add(created);
        return created;
    }

    private static bool TryCalculateTrackMatchCost(
        AnimatedVisionObbBox track,
        Point[] target,
        long nowMs,
        out double cost)
    {
        cost = double.MaxValue;
        if (track.CurrentPoints.Length != 4 || target.Length != 4)
            return false;

        double elapsedMs = Math.Max(0, nowMs - track.LastSeenMs);
        Point predictedCenter = new(
            track.LastCenter.X + track.Velocity.X * elapsedMs,
            track.LastCenter.Y + track.Velocity.Y * elapsedMs);
        Point targetCenter = GetCenter(target);
        double centerDistance = Distance(predictedCenter, targetCenter);
        Rect trackBounds = GetBounds(track.CurrentPoints);
        Rect targetBounds = GetBounds(target);
        double maxDiagonal = Math.Max(
            20,
            Math.Max(
                Math.Sqrt(trackBounds.Width * trackBounds.Width + trackBounds.Height * trackBounds.Height),
                Math.Sqrt(targetBounds.Width * targetBounds.Width + targetBounds.Height * targetBounds.Height)));
        if (centerDistance > Math.Max(80, maxDiagonal * 2.5))
            return false;

        Point[] predictedPoints = ShiftPoints(
            track.CurrentPoints,
            track.Velocity.X * elapsedMs,
            track.Velocity.Y * elapsedMs);
        double iou = AxisAlignedIoU(predictedPoints, target);
        double trackArea = Math.Max(1, trackBounds.Width * trackBounds.Height);
        double targetArea = Math.Max(1, targetBounds.Width * targetBounds.Height);
        double sizeSimilarity = Math.Min(trackArea, targetArea) / Math.Max(trackArea, targetArea);
        cost = 0.60 * Math.Min(1, centerDistance / maxDiagonal)
               + 0.25 * (1 - iou)
               + 0.15 * (1 - sizeSimilarity);
        return true;
    }

    private static Point[] ShiftPoints(
        IReadOnlyList<Point> points,
        double dx,
        double dy)
        => points
            .Select(point => new Point(point.X + dx, point.Y + dy))
            .ToArray();

    private static double Distance(Point left, Point right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Rect GetBounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
            return Rect.Empty;
        double left = points.Min(point => point.X);
        double top = points.Min(point => point.Y);
        double right = points.Max(point => point.X);
        double bottom = points.Max(point => point.Y);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static double AxisAlignedIoU(
        IReadOnlyList<Point> left,
        IReadOnlyList<Point> right)
    {
        Rect a = GetBounds(left);
        Rect b = GetBounds(right);
        Rect intersection = Rect.Intersect(a, b);
        if (intersection.IsEmpty)
            return 0;
        double intersectionArea = intersection.Width * intersection.Height;
        double unionArea = a.Width * a.Height + b.Width * b.Height - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    private void UpdateVisionObbBoxColors(MainViewModel vm)
    {
        foreach (AnimatedVisionObbBox box in _animatedVisionObbBoxes)
        {
            bool selected = ReferenceEquals(box, _selectedVisionObbTrack);
            VisionObbVisualState state = ResolveVisionObbBoxVisualState(
                vm.IsCameraBarcodeCandidate,
                vm.IsCameraBarcodeConfirmed,
                selected);
            (string StrokeKey, string FillKey, Brush Stroke, Brush Fill) colors = state switch
            {
                VisionObbVisualState.Confirmed => (
                    "CameraConfirmedStroke",
                    "VisionObbConfirmedFill",
                    VisionObbConfirmedStroke,
                    VisionObbConfirmedFill),
                VisionObbVisualState.Candidate => (
                    "CameraCandidateStroke",
                    "VisionObbCandidateFill",
                    VisionObbCandidateStroke,
                    VisionObbCandidateFill),
                _ => (
                    "CameraGuideStroke",
                    "VisionObbIdleFill",
                    VisionObbIdleStroke,
                    VisionObbIdleFill)
            };
            box.Shape.Stroke = TryFindResource(colors.StrokeKey) as Brush ?? colors.Stroke;
            box.Shape.Fill = TryFindResource(colors.FillKey) as Brush ?? colors.Fill;
        }
    }

    internal static VisionObbVisualState ResolveVisionObbVisualState(
        bool candidate,
        bool confirmed)
        => confirmed
            ? VisionObbVisualState.Confirmed
            : candidate
                ? VisionObbVisualState.Candidate
                : VisionObbVisualState.Idle;

    internal static VisionObbVisualState ResolveVisionObbBoxVisualState(
        bool candidate,
        bool confirmed,
        bool selected)
        => selected
            ? ResolveVisionObbVisualState(candidate, confirmed)
            : VisionObbVisualState.Idle;

    private static void StartBoxAnimation(
        AnimatedVisionObbBox box,
        Point[] target,
        long nowMs,
        bool removeWhenFinished)
    {
        box.StartPoints = box.CurrentPoints.Length == 4
            ? box.CurrentPoints
            : target;
        box.TargetPoints = target;
        box.StartedAtMs = nowMs;
        box.StartOpacity = box.Shape.Opacity;
        box.TargetOpacity = removeWhenFinished ? 0 : 1;
        box.RemoveWhenFinished = removeWhenFinished;
    }

    private void EnsureVisionObbAnimation()
    {
        if (_visionObbAnimationRunning || _animatedVisionObbBoxes.Count == 0)
            return;
        _visionObbAnimationRunning = true;
        CompositionTarget.Rendering += OnVisionObbAnimationFrame;
    }

    private void OnVisionObbAnimationFrame(object? sender, EventArgs e)
    {
        long nowMs = Environment.TickCount64;
        bool running = false;
        for (int index = _animatedVisionObbBoxes.Count - 1; index >= 0; index--)
        {
            AnimatedVisionObbBox box = _animatedVisionObbBoxes[index];
            double progress = Math.Clamp(
                (nowMs - box.StartedAtMs) / Math.Max(1, box.DurationMs),
                0,
                1);
            double eased = EaseInOutCubic(progress);
            box.CurrentPoints = InterpolatePoints(
                box.StartPoints,
                box.TargetPoints,
                eased);
            SetRectangleFrame(
                box.Shape,
                box.Rotation,
                box.CurrentPoints);
            box.Shape.Opacity = box.StartOpacity
                                + (box.TargetOpacity - box.StartOpacity) * eased;
            if (progress < 1)
            {
                running = true;
                continue;
            }

            if (box.RemoveWhenFinished)
            {
                box.Hidden = true;
                box.Shape.Visibility = Visibility.Collapsed;
            }
        }

        if (!running)
        {
            CompositionTarget.Rendering -= OnVisionObbAnimationFrame;
            _visionObbAnimationRunning = false;
        }
    }

    private void ClearAnimatedVisionObbBoxes()
    {
        if (_visionObbAnimationRunning)
        {
            CompositionTarget.Rendering -= OnVisionObbAnimationFrame;
            _visionObbAnimationRunning = false;
        }
        _animatedVisionObbBoxes.Clear();
        _selectedVisionObbTrack = null;
        _lastVisionObbTargetUpdateMs = 0;
        _visionObbAnimationDurationMs = DefaultObbAnimationDurationMs;
        CameraVisionObbLayer.Children.Clear();
    }

    internal static VisionObbRectangleFrame ResolveRectangleFrame(
        IReadOnlyList<Point> points)
    {
        if (points.Count != 4)
            return new VisionObbRectangleFrame(0, 0, 0, new Point(), 0);
        double width = Math.Max(1, Distance(points[0], points[1]));
        double height = Math.Max(1, Distance(points[1], points[2]));
        double angle = Math.Atan2(
            points[1].Y - points[0].Y,
            points[1].X - points[0].X) * 180.0 / Math.PI;
        Point center = GetCenter(points);
        return new VisionObbRectangleFrame(
            width,
            height,
            angle,
            new Point(center.X - width / 2, center.Y - height / 2),
            Math.Min(VisionObbCornerRadius, Math.Min(width, height) / 2));
    }

    private static void SetRectangleFrame(
        Rectangle rectangle,
        RotateTransform rotation,
        IReadOnlyList<Point> points)
    {
        VisionObbRectangleFrame frame = ResolveRectangleFrame(points);
        rectangle.Width = frame.Width;
        rectangle.Height = frame.Height;
        rectangle.RadiusX = frame.CornerRadius;
        rectangle.RadiusY = frame.CornerRadius;
        rotation.Angle = frame.Angle;
        rotation.CenterX = frame.Width / 2;
        rotation.CenterY = frame.Height / 2;
        Canvas.SetLeft(rectangle, frame.TopLeft.X);
        Canvas.SetTop(rectangle, frame.TopLeft.Y);
    }

    private static Point GetCenter(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
            return new Point();
        double x = 0;
        double y = 0;
        foreach (Point point in points)
        {
            x += point.X;
            y += point.Y;
        }
        return new Point(x / points.Count, y / points.Count);
    }

    internal static double EaseInOutCubic(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    internal static double ResolveObbAnimationDurationMs(double targetIntervalMs)
        => Math.Clamp(
            targetIntervalMs,
            MinObbAnimationDurationMs,
            MaxObbAnimationDurationMs);

    internal static Point[] InterpolatePoints(
        IReadOnlyList<Point> start,
        IReadOnlyList<Point> target,
        double progress)
    {
        if (start.Count != target.Count)
            return target.ToArray();
        var result = new Point[start.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new Point(
                start[index].X + (target[index].X - start[index].X) * progress,
                start[index].Y + (target[index].Y - start[index].Y) * progress);
        }
        return result;
    }

    internal static Point[] BuildObbDisplayPoints(
        in VisionObbDetection detection,
        Point origin,
        double scale)
    {
        Point2f[] corners = VisionObbGeometry.ToCorners(detection);
        return corners
            .Select(corner => new Point(
                origin.X + corner.X * scale,
                origin.Y + corner.Y * scale))
            .ToArray();
    }
}

public enum VisionObbVisualState
{
    Idle,
    Candidate,
    Confirmed
}
