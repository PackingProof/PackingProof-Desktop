using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 主界面预览里的画中画拖动框。
    ///
    /// 每一路接了设备的副画面各有一组"框 + 右下角把手"：按住框体拖动改位置，按住把手改大小，
    /// 单击进这一路的取景编辑屏。框按通道生成，以后开放第三、第四路不用改这里。
    ///
    /// 位置和尺寸都来自 <see cref="MainViewModel.TryResolveOverlayRect"/>，与合成用的是同一套策略，
    /// 所以框住哪里、画面就画在哪里。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>一路画中画的拖动控件。</summary>
        private sealed class OverlayBoxControls
        {
            internal required int ChannelNumber { get; init; }
            internal required Thumb Drag { get; init; }
            internal required Thumb Resize { get; init; }
            /// <summary>鼠标是否停在这一路画中画（含右下角把手）上：把手只在悬浮时出现。</summary>
            internal bool Hovered;
        }

        private readonly Dictionary<int, OverlayBoxControls> _overlayBoxControls = new();

        /// <summary>把每一路画中画的拖动框摆到它当前所在的位置；没有画面的或没接的那一路收起来。</summary>
        private void UpdateOverlayBoxes(MainViewModel vm)
        {
            var alive = new HashSet<int>();
            foreach (int channelNumber in vm.VisibleOverlayChannelNumbers)
            {
                alive.Add(channelNumber);
                if (!_overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
                {
                    controls = CreateOverlayBoxControls(channelNumber);
                    _overlayBoxControls[channelNumber] = controls;
                }

                UpdateOverlayBox(vm, controls);
            }

            foreach (KeyValuePair<int, OverlayBoxControls> entry in _overlayBoxControls)
            {
                if (alive.Contains(entry.Key))
                    continue;

                entry.Value.Drag.Visibility = Visibility.Collapsed;
                entry.Value.Resize.Visibility = Visibility.Collapsed;
            }
        }

        private OverlayBoxControls CreateOverlayBoxControls(int channelNumber)
        {
            // 框体就是一个完全透明的 Thumb：平时不画任何东西（压在画面上会被当成"画面被框住了"），
            // 鼠标移上来才由样式里的描边显示出来，此时才看得出可以拖。
            // 模板固定在 OverlayBoxDragThumbStyle 里，避免吃到系统默认 Thumb 外观画出一块底色。
            var drag = new Thumb
            {
                Tag = channelNumber,
                Cursor = Cursors.SizeAll,
                Visibility = Visibility.Collapsed,
                ToolTip = "按住拖动可调整副画面位置",
                Style = TryFindResource("OverlayBoxDragThumbStyle") as Style
            };
            drag.DragDelta += OverlayBox_DragDelta;
            drag.DragCompleted += OverlayBox_DragCompleted;
            drag.MouseEnter += OverlayBox_MouseEnter;
            drag.MouseLeave += OverlayBox_MouseLeave;

            var resize = new Thumb
            {
                Tag = channelNumber,
                Width = 16,
                Height = 16,
                Cursor = Cursors.SizeNWSE,
                Visibility = Visibility.Collapsed,
                ToolTip = "按住拖动可调整副画面大小",
                // 与识别框四角把手同一套外观：蓝色圆角小方块
                Style = TryFindResource("CameraGuideHandleStyle") as Style
            };
            resize.DragDelta += OverlayResize_DragDelta;
            resize.DragCompleted += OverlayResize_DragCompleted;
            resize.MouseEnter += OverlayBox_MouseEnter;
            resize.MouseLeave += OverlayBox_MouseLeave;

            OverlayBoxLayer.Children.Add(drag);
            OverlayBoxLayer.Children.Add(resize);
            return new OverlayBoxControls
            {
                ChannelNumber = channelNumber,
                Drag = drag,
                Resize = resize
            };
        }

        private void UpdateOverlayBox(MainViewModel vm, OverlayBoxControls controls)
        {
            controls.Resize.Visibility = controls.Hovered ? Visibility.Visible : Visibility.Collapsed;

            // 编辑态下画中画的位置/大小框没有意义：副摄取景屏显示的是那一路的整幅画面，
            // 放大取景屏则要无遮挡地看清主画面（副画面在这期间也不合成）。
            if (vm.IsPreviewGuideEditing)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            if (vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0 || videoRect.Height <= 0)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            if (!vm.TryResolveOverlayRect(
                    controls.ChannelNumber,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    out CameraOverlayRect rect))
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            // VideoImage 在父容器里不一定从 (0,0) 开始：画面按 Uniform 居中摆放，
            // 四周留出的黑边同样占父容器的坐标。拖动框挂在同一个容器上，
            // 坐标必须换算过去，否则整块框会比画面偏出这段黑边。
            UIElement overlayHost = OverlayBoxLayer;
            Point videoOrigin = VideoImage.TranslatePoint(
                new Point(videoRect.X, videoRect.Y),
                overlayHost);

            // 帧坐标 → 预览控件坐标（Uniform 缩放，两边黑边已由 videoRect 扣掉）。
            double scale = videoRect.Width / frame.PixelWidth;
            double left = videoOrigin.X + (rect.X * scale);
            double top = videoOrigin.Y + (rect.Y * scale);
            double width = rect.Width * scale;
            double height = rect.Height * scale;

            Place(controls.Drag, left, top, width, height);
            Place(
                controls.Resize,
                left + width - (controls.Resize.Width / 2),
                top + height - (controls.Resize.Height / 2),
                controls.Resize.Width,
                controls.Resize.Height);

            // 圆角跟合成本身用同一个半径（换算到当前预览缩放），框和画面才对得上。
            int cornerRadius = Math.Max(
                1,
                (int)Math.Round(CameraOverlayComposer.ResolveCornerRadius(rect.Width, rect.Height) * scale));
            OverlayBoxVisual.SetCornerRadius(controls.Drag, new CornerRadius(cornerRadius));

            controls.Drag.Visibility = Visibility.Visible;
            controls.Resize.Visibility = controls.Hovered ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void Place(FrameworkElement element, double left, double top, double width, double height)
        {
            if (width > 0)
                element.Width = width;
            if (height > 0)
                element.Height = height;

            // 位置用 RenderTransform 摆：只影响渲染、不触发布局 ——
            // 拖动窗口改大小时才会像取景框那样实时跟手（改 Canvas.Left/Top 要走一次布局，会晚一拍）。
            TranslateTransform offset = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            offset.X = left;
            offset.Y = top;
            element.RenderTransform = offset;
            Canvas.SetLeft(element, 0);
            Canvas.SetTop(element, 0);
        }

        private void OverlayBox_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber }
                || DataContext is not MainViewModel vm
                || vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0)
                return;
            if (!vm.TryResolveOverlayRect(channelNumber, frame.PixelWidth, frame.PixelHeight, out CameraOverlayRect current))
                return;

            // 控件像素增量 → 帧像素增量，再交给 ViewModel 夹紧落位。
            double scale = videoRect.Width / frame.PixelWidth;
            double frameX = current.X + (e.HorizontalChange / scale);
            double frameY = current.Y + (e.VerticalChange / scale);

            vm.SetOverlayPosition(channelNumber, frameX, frameY, frame.PixelWidth, frame.PixelHeight);
            UpdateOverlayBoxes(vm);
        }

        private void OverlayBox_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber } || DataContext is not MainViewModel vm)
                return;

            // 没有实际位移 = 单击这一路画中画：进入它的取景编辑（就像点图片进裁剪）。
            if (Math.Abs(e.HorizontalChange) < 2 && Math.Abs(e.VerticalChange) < 2)
            {
                vm.EnterOverlayPreviewEdit(channelNumber);
                UpdateOverlayBoxes(vm);
                UpdateCameraBarcodeGuide(vm);
                return;
            }

            vm.SaveOverlayPosition(channelNumber);
        }

        private void OverlayResize_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber }
                || DataContext is not MainViewModel vm
                || vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0)
                return;
            if (!vm.TryResolveOverlayRect(channelNumber, frame.PixelWidth, frame.PixelHeight, out CameraOverlayRect current))
                return;

            double scale = videoRect.Width / frame.PixelWidth;
            double targetWidthPixels = current.Width + (e.HorizontalChange / scale);
            vm.SetOverlayWidth(channelNumber, targetWidthPixels / frame.PixelWidth);
            UpdateOverlayBoxes(vm);
        }

        private void OverlayResize_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb { Tag: int channelNumber } && DataContext is MainViewModel vm)
                vm.SaveOverlayWidth(channelNumber);
        }

        private void OverlayBox_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Thumb { Tag: int channelNumber }
                && _overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
            {
                controls.Hovered = true;
                controls.Drag.BorderBrush = TryFindResource("AccentBlue") as Brush;
                controls.Resize.Visibility = Visibility.Visible;
            }
        }

        private void OverlayBox_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber }
                || !_overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
            {
                return;
            }

            // 鼠标可能只是移到了右下角把手上：把手探出框外，这一下也算"还停在画中画上"。
            // 不加这个判断，把手会在鼠标够到它之前就收起来，等于点不中。
            if (controls.Drag.IsMouseOver || controls.Resize.IsMouseOver)
                return;

            controls.Hovered = false;
            controls.Drag.BorderBrush = TryFindResource("TransparentBrush") as Brush;
            controls.Resize.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 画中画拖动框的附加属性。拖动框外观是 ControlTemplate 里的 Border，
    /// 模板里没法直接写"跟着合成圆角走"的值，所以用附加属性传给模板绑定。
    /// </summary>
    internal static class OverlayBoxVisual
    {
        internal static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.RegisterAttached(
                "CornerRadius",
                typeof(CornerRadius),
                typeof(OverlayBoxVisual),
                new PropertyMetadata(new CornerRadius(6)));

        internal static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
            element.SetValue(CornerRadiusProperty, value);

        internal static CornerRadius GetCornerRadius(DependencyObject element) =>
            (CornerRadius)element.GetValue(CornerRadiusProperty);
    }
}
