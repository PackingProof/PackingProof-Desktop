using System;
using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 管理悬浮小窗与主窗口的联动。
    /// 参照会议软件的做法：主窗口最小化时自动弹出小窗，还原时自动收起，
    /// 主窗口始终留在任务栏而不是被隐藏，避免影响托盘、单实例激活和扫码焦点逻辑。
    /// 主界面不提供入口按钮，最小化是唯一的进入方式。
    /// </summary>
    internal sealed class FloatingPreviewController : IDisposable
    {
        private readonly Window _mainWindow;
        private readonly MainViewModel _viewModel;

        private FloatingPreviewWindow? _floatingWindow;
        private WindowState _stateBeforeFloating = WindowState.Maximized;
        private bool _restoringMainWindow;
        private bool _dismissedForCurrentMinimize;
        private bool _disposed;

        public FloatingPreviewController(Window mainWindow, MainViewModel viewModel)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            _mainWindow.StateChanged += MainWindow_StateChanged;
            _mainWindow.Closed += MainWindow_Closed;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_disposed || _restoringMainWindow) return;

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                // 自动弹出：店员切到快递助手时不必再回来点按钮。
                // 本轮最小化里已经手动叉掉过，就不再反复弹出来打扰。
                if (_floatingWindow == null && !_dismissedForCurrentMinimize)
                    ShowFloatingWindow();
                return;
            }

            _stateBeforeFloating = _mainWindow.WindowState;
            _dismissedForCurrentMinimize = false;
            CloseFloatingWindow();
        }

        private void ShowFloatingWindow()
        {
            try
            {
                _floatingWindow = new FloatingPreviewWindow(_viewModel, RestoreMainWindow);
                _floatingWindow.Closed += FloatingWindow_Closed;
                _viewModel.RefreshFloatingPreviewStatus();
                _viewModel.IsFloatingPreviewActive = true;
                _floatingWindow.Show();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("FloatingPreview", $"打开悬浮小窗失败：{ex.Message}");
                _floatingWindow = null;
                _viewModel.IsFloatingPreviewActive = false;
            }
        }

        private void FloatingWindow_Closed(object? sender, EventArgs e)
        {
            if (_floatingWindow != null)
                _floatingWindow.Closed -= FloatingWindow_Closed;

            // 主窗口还在最小化时关掉小窗，说明是店员点了叉，本轮不要再自动弹回来。
            if (_mainWindow.WindowState == WindowState.Minimized && !_restoringMainWindow)
                _dismissedForCurrentMinimize = true;

            _floatingWindow = null;
            _viewModel.IsFloatingPreviewActive = false;
        }

        private void RestoreMainWindow()
        {
            _restoringMainWindow = true;
            try
            {
                _mainWindow.Show();
                _mainWindow.WindowState = _stateBeforeFloating == WindowState.Minimized
                    ? WindowState.Normal
                    : _stateBeforeFloating;
                _mainWindow.Activate();
            }
            finally
            {
                _restoringMainWindow = false;
            }
        }

        /// <summary>主窗口真正关闭后再收起小窗；关闭确认被取消时小窗应当保持原样。</summary>
        private void MainWindow_Closed(object? sender, EventArgs e) => CloseFloatingWindow();

        public void CloseFloatingWindow() => _floatingWindow?.CloseFromOwner();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _mainWindow.StateChanged -= MainWindow_StateChanged;
            _mainWindow.Closed -= MainWindow_Closed;
            CloseFloatingWindow();
        }
    }
}
