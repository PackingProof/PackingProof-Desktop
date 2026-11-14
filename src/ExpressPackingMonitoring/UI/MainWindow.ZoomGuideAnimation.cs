using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ExpressPackingMonitoring.UI;

public partial class MainWindow
{
    private const double ZoomGuideTargetStabilityMs = 180;
    private const double ZoomGuideImmediateMoveRatio = 0.18;
    private const double ZoomGuideSmoothTimeSeconds = 0.28;
    private const double ZoomGuideMaxSpeed = 900;
    private const double ZoomGuideLostCrossFadeMs = 1000;
    private const double ZoomGuideAcquiredCrossFadeMs = 500;
    private bool _zoomGuideAnimationRunning;
    private bool _zoomGuideHasCurrentRect;
    private bool? _zoomGuideSmartMode;
    private Rect _zoomGuideCurrentRect;
    private Rect _zoomGuideTargetRect;
    private Rect? _zoomGuidePendingRect;
    private long _zoomGuidePendingSinceMs;
    private long _zoomGuideLastFrameMs;
    private double _zoomGuideVelocityX;
    private double _zoomGuideVelocityY;
    private double _zoomGuideVelocityWidth;
    private double _zoomGuideVelocityHeight;

    internal static bool RequiresZoomGuideFade(
        bool? currentSmartMode,
        bool nextSmartMode) =>
        currentSmartMode != nextSmartMode;

    internal static double ResolveZoomGuideCrossFadeMs(bool useSmartTarget) =>
        useSmartTarget
            ? ZoomGuideAcquiredCrossFadeMs
            : ZoomGuideLostCrossFadeMs;

    private void StartZoomGuideCrossFade(
        Rect defaultTarget,
        Rect? smartTarget)
    {
        bool useSmartTarget = smartTarget.HasValue;
        if (smartTarget is { } currentSmartTarget)
            SetZoomGuideRectDirect(ZoomGuideBoxHost, currentSmartTarget);
        else
            SetZoomGuideRectDirect(DefaultZoomGuideBoxHost, defaultTarget);

        double crossFadeMs = ResolveZoomGuideCrossFadeMs(useSmartTarget);
        FadeZoomGuideBox(
            DefaultZoomGuideBoxHost,
            useSmartTarget ? 0 : 1,
            crossFadeMs);
        FadeZoomGuideBox(
            ZoomGuideBoxHost,
            useSmartTarget ? 1 : 0,
            crossFadeMs);
        _zoomGuideSmartMode = useSmartTarget;
    }

    private void SetZoomGuideRectDirect(Grid host, Rect target)
    {
        if (ReferenceEquals(host, ZoomGuideBoxHost))
        {
            StopZoomGuideAnimation();
            _zoomGuideCurrentRect = target;
            _zoomGuideTargetRect = target;
            _zoomGuideHasCurrentRect = true;
            _zoomGuidePendingRect = null;
            ResetZoomGuideVelocity();
        }
        ApplyZoomGuideRect(host, target);
        host.Visibility = Visibility.Visible;
    }

    private static void FadeZoomGuideBox(
        Grid host,
        double targetOpacity,
        double durationMs)
    {
        host.Visibility = Visibility.Visible;
        double currentOpacity = host.Opacity;
        var fade = new DoubleAnimation(
            currentOpacity,
            targetOpacity,
            TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        host.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void SetZoomGuideTargetRect(Rect target)
    {
        if (target.IsEmpty || target.Width <= 0 || target.Height <= 0)
        {
            HideZoomGuideBox();
            return;
        }

        long nowMs = Environment.TickCount64;
        if (!_zoomGuideHasCurrentRect)
        {
            _zoomGuideCurrentRect = target;
            _zoomGuideTargetRect = target;
            _zoomGuideHasCurrentRect = true;
            ApplyZoomGuideRect(ZoomGuideBoxHost, target);
            return;
        }

        if (AreZoomGuideRectsClose(_zoomGuideCurrentRect, target, 0.5))
        {
            _zoomGuideCurrentRect = target;
            _zoomGuideTargetRect = target;
            _zoomGuidePendingRect = null;
            ResetZoomGuideVelocity();
            ApplyZoomGuideRect(ZoomGuideBoxHost, target);
            StopZoomGuideAnimation();
            return;
        }

        if (IsZoomGuideTargetNear(_zoomGuideCurrentRect, target))
        {
            _zoomGuidePendingRect = null;
            AcceptZoomGuideTarget(target, nowMs);
            return;
        }

        if (_zoomGuidePendingRect is not { } pending
            || !IsSameZoomGuideTarget(pending, target))
        {
            _zoomGuidePendingRect = target;
            _zoomGuidePendingSinceMs = nowMs;
            EnsureZoomGuideAnimation(nowMs);
            return;
        }
        if (nowMs - _zoomGuidePendingSinceMs < ZoomGuideTargetStabilityMs)
        {
            EnsureZoomGuideAnimation(nowMs);
            return;
        }

        _zoomGuidePendingRect = null;
        AcceptZoomGuideTarget(target, nowMs);
    }

    private void AcceptZoomGuideTarget(Rect target, long nowMs)
    {
        _zoomGuideTargetRect = target;
        EnsureZoomGuideAnimation(nowMs);
    }

    private void EnsureZoomGuideAnimation(long nowMs)
    {
        if (!_zoomGuideAnimationRunning)
        {
            _zoomGuideAnimationRunning = true;
            _zoomGuideLastFrameMs = nowMs;
            CompositionTarget.Rendering += OnZoomGuideAnimationFrame;
        }
    }

    private void OnZoomGuideAnimationFrame(object? sender, EventArgs e)
    {
        long nowMs = Environment.TickCount64;
        if (_zoomGuidePendingRect is { } pending
            && nowMs - _zoomGuidePendingSinceMs >= ZoomGuideTargetStabilityMs)
        {
            _zoomGuidePendingRect = null;
            _zoomGuideTargetRect = pending;
        }
        double deltaSeconds = Math.Clamp(
            (nowMs - _zoomGuideLastFrameMs) / 1000.0,
            0.001,
            0.05);
        _zoomGuideLastFrameMs = nowMs;
        double x = SmoothDampZoomGuideComponent(
            _zoomGuideCurrentRect.X,
            _zoomGuideTargetRect.X,
            ref _zoomGuideVelocityX,
            deltaSeconds);
        double y = SmoothDampZoomGuideComponent(
            _zoomGuideCurrentRect.Y,
            _zoomGuideTargetRect.Y,
            ref _zoomGuideVelocityY,
            deltaSeconds);
        double width = SmoothDampZoomGuideComponent(
            _zoomGuideCurrentRect.Width,
            _zoomGuideTargetRect.Width,
            ref _zoomGuideVelocityWidth,
            deltaSeconds);
        double height = SmoothDampZoomGuideComponent(
            _zoomGuideCurrentRect.Height,
            _zoomGuideTargetRect.Height,
            ref _zoomGuideVelocityHeight,
            deltaSeconds);
        _zoomGuideCurrentRect = new Rect(
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height));
        ApplyZoomGuideRect(ZoomGuideBoxHost, _zoomGuideCurrentRect);
        if (_zoomGuidePendingRect == null
            && AreZoomGuideRectsClose(
                _zoomGuideCurrentRect,
                _zoomGuideTargetRect,
                0.5))
        {
            _zoomGuideCurrentRect = _zoomGuideTargetRect;
            ApplyZoomGuideRect(ZoomGuideBoxHost, _zoomGuideCurrentRect);
            StopZoomGuideAnimation();
        }
    }

    private static void ApplyZoomGuideRect(Grid boxHost, Rect rect)
    {
        boxHost.Width = rect.Width;
        boxHost.Height = rect.Height;
        if (boxHost.Parent is not FrameworkElement host)
            return;

        var transform = boxHost.RenderTransform as TranslateTransform
            ?? new TranslateTransform();
        transform.X = rect.X - ((host.ActualWidth - rect.Width) / 2.0);
        transform.Y = rect.Y - ((host.ActualHeight - rect.Height) / 2.0);
        boxHost.RenderTransform = transform;
    }

    private void HideZoomGuideBox()
    {
        StopZoomGuideAnimation();
        HideZoomGuideHost(DefaultZoomGuideBoxHost);
        HideZoomGuideHost(ZoomGuideBoxHost);
        _zoomGuideHasCurrentRect = false;
        _zoomGuideSmartMode = null;
        _zoomGuidePendingRect = null;
        ResetZoomGuideVelocity();
    }

    private static void HideZoomGuideHost(Grid host)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        host.Opacity = 1;
        host.Width = 0;
        host.Height = 0;
        host.Visibility = Visibility.Collapsed;
        host.RenderTransform = null;
    }

    private void StopZoomGuideAnimation()
    {
        if (_zoomGuideAnimationRunning)
        {
            CompositionTarget.Rendering -= OnZoomGuideAnimationFrame;
            _zoomGuideAnimationRunning = false;
        }
    }

    internal static bool IsZoomGuideTargetNear(Rect current, Rect target)
    {
        double currentCenterX = current.X + (current.Width / 2.0);
        double currentCenterY = current.Y + (current.Height / 2.0);
        double targetCenterX = target.X + (target.Width / 2.0);
        double targetCenterY = target.Y + (target.Height / 2.0);
        double distance = Math.Sqrt(
            Math.Pow(targetCenterX - currentCenterX, 2)
            + Math.Pow(targetCenterY - currentCenterY, 2));
        double referenceSize = Math.Max(
            1,
            Math.Min(current.Width, current.Height));
        return distance <= referenceSize * ZoomGuideImmediateMoveRatio;
    }

    private static bool IsSameZoomGuideTarget(Rect left, Rect right)
    {
        double leftCenterX = left.X + (left.Width / 2.0);
        double leftCenterY = left.Y + (left.Height / 2.0);
        double rightCenterX = right.X + (right.Width / 2.0);
        double rightCenterY = right.Y + (right.Height / 2.0);
        double distance = Math.Sqrt(
            Math.Pow(rightCenterX - leftCenterX, 2)
            + Math.Pow(rightCenterY - leftCenterY, 2));
        double referenceSize = Math.Max(
            1,
            Math.Min(left.Width, left.Height));
        return distance <= referenceSize * ZoomGuideImmediateMoveRatio;
    }

    internal static double SmoothDampZoomGuideComponent(
        double current,
        double target,
        ref double velocity,
        double deltaSeconds)
    {
        double smoothTime = Math.Max(0.0001, ZoomGuideSmoothTimeSeconds);
        double omega = 2.0 / smoothTime;
        double x = omega * deltaSeconds;
        double decay = 1.0 / (1.0 + x + (0.48 * x * x) + (0.235 * x * x * x));
        double change = current - target;
        double maxChange = ZoomGuideMaxSpeed * smoothTime;
        change = Math.Clamp(change, -maxChange, maxChange);
        double adjustedTarget = current - change;
        double temp = (velocity + (omega * change)) * deltaSeconds;
        velocity = (velocity - (omega * temp)) * decay;
        double output = adjustedTarget + ((change + temp) * decay);
        if ((target - current > 0) == (output > target))
        {
            output = target;
            velocity = 0;
        }
        return output;
    }

    private void ResetZoomGuideVelocity()
    {
        _zoomGuideVelocityX = 0;
        _zoomGuideVelocityY = 0;
        _zoomGuideVelocityWidth = 0;
        _zoomGuideVelocityHeight = 0;
    }

    private static bool AreZoomGuideRectsClose(Rect left, Rect right, double tolerance) =>
        Math.Abs(left.X - right.X) <= tolerance
        && Math.Abs(left.Y - right.Y) <= tolerance
        && Math.Abs(left.Width - right.Width) <= tolerance
        && Math.Abs(left.Height - right.Height) <= tolerance;
}
