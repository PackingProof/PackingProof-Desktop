#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void LoadConfig()
        {
            bool containsRemovedStorageQuota = false;
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string configJson = File.ReadAllText(_configFilePath, System.Text.Encoding.UTF8);
                    containsRemovedStorageQuota = configJson.Contains("\"QuotaGB\"", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Config", $"Failed to inspect config migration markers: {ex.Message}");
                }
            }

            Config = WorkstationConfigStore.Load();
            _currentMode = AppConfig.NormalizeRecordingMode(Config.RecordingMode);

            bool configMigrated = containsRemovedStorageQuota;
            if (Config.VideoCqp <= 0)
            {
                Config.VideoCqp = 25;
                configMigrated = true;
            }
            if (Config.AudioSyncOffsetMs == 400)
            {
                Config.AudioSyncOffsetMs = 0;
                configMigrated = true;
            }
            if (!WorkstationRoles.IsKnown(Config.WorkstationRole))
            {
                Config.WorkstationRole = WorkstationRoles.CameraMonitor;
                configMigrated = true;
            }
            int normalizedAudioSyncOffsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            if (Config.AudioSyncOffsetMs != normalizedAudioSyncOffsetMs)
            {
                Config.AudioSyncOffsetMs = normalizedAudioSyncOffsetMs;
                configMigrated = true;
            }
            configMigrated = AppConfig.NormalizeAfterLoad(Config) || configMigrated;
            if (configMigrated)
                SaveConfig();

            // Apply Theme
            if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
            }
            else
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(ExpressPackingMonitoring.Themes.AppTheme.Auto);
            }
        }
        private bool SaveConfig(bool notifyUser = false) => SaveConfig(Config, notifyUser);

        private bool SaveConfig(AppConfig config, bool notifyUser = false)
        {
            if (WorkstationConfigStore.TrySave(config, out string error))
                return true;

            if (notifyUser)
                ShowToast($"配置保存失败，请检查磁盘空间或权限: {error}", ToastSeverity.Error);
            return false;
        }

        public void OpenSettings() => OpenSettings(selectRecordingCache: false);

        private void OpenSettings(bool selectRecordingCache)
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("处理中：编码器环境检测中，请稍后打开设置...", ToastSeverity.Information);
                return;
            }
            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(this, clonedConfig, DiskUsagePercent, DiskUsageText, IsRecording);
                if (selectRecordingCache)
                    settingsWin.SelectRecordingCacheTab();
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                if (mainWindow != null) settingsWin.Owner = mainWindow;
                mainWindow?.SuspendCapsLockForModalWindow();
                try
                {
                    settingsWin.ShowDialog();
                }
                finally
                {
                    mainWindow?.ResumeCapsLockAfterModalWindow();
                }
            }
            catch (Exception ex) { ShowToast($"设置错误: {ex.Message}", ToastSeverity.Error); }
        }

        internal enum CameraRestartAction
        {
            None,
            RestartNow,
            DeferUntilRecordingEnds
        }

        internal static CameraRestartAction GetCameraRestartAction(
            AppConfig current,
            AppConfig next,
            bool isRecording)
        {
            if (!AppConfig.RequiresCameraRestart(current, next))
                return CameraRestartAction.None;

            return isRecording
                ? CameraRestartAction.DeferUntilRecordingEnds
                : CameraRestartAction.RestartNow;
        }

        public async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
        {
            try
            {
                    AppConfig.NormalizeAfterLoad(nextConfig);
                    CameraRestartAction cameraRestartAction = GetCameraRestartAction(
                        Config,
                        nextConfig,
                        IsRecording);
                    bool themeChanged = Config.Theme != nextConfig.Theme;
                    bool globalKeyChanged = Config.EnableGlobalKeyboard != nextConfig.EnableGlobalKeyboard;
                    bool cameraBarcodeChanged = Config.EnableCameraBarcodeRecognition != nextConfig.EnableCameraBarcodeRecognition;
                    bool workstationChanged = !string.Equals(
                        Config.DeploymentPreset,
                        nextConfig.DeploymentPreset,
                        StringComparison.OrdinalIgnoreCase);
                    bool aiTtsChanged = Config.EnableAiTts != nextConfig.EnableAiTts
                        || Config.AiTtsEngine != nextConfig.AiTtsEngine;
                    bool webServerChanged = Config.EnableWebServer != nextConfig.EnableWebServer
                        || Config.WebServerPort != nextConfig.WebServerPort
                        || Config.TranscodeCacheMaxMB != nextConfig.TranscodeCacheMaxMB
                        || Config.EnableOrderInfoLog != nextConfig.EnableOrderInfoLog
                        || Config.EnableExtensionApi != nextConfig.EnableExtensionApi
                        || Config.RequireWebAccessKey != nextConfig.RequireWebAccessKey
                        || !string.Equals(Config.WebAccessKey, nextConfig.WebAccessKey, StringComparison.Ordinal)
                        || (string.Equals(
                                DeploymentPresets.Normalize(nextConfig.DeploymentPreset),
                                DeploymentPresets.RecordingHost,
                                StringComparison.Ordinal)
                            && !string.Equals(Config.NodeName, nextConfig.NodeName, StringComparison.Ordinal));
                    bool computerNicknameChanged =
                        !string.Equals(Config.NodeName, nextConfig.NodeName, StringComparison.Ordinal)
                        || Config.NodeNameCustomized != nextConfig.NodeNameCustomized;
                    bool webServerNeedsRecovery = nextConfig.EnableWebServer && _webServer == null;

                    if (workstationChanged)
                        return await RunPurposeSwitchAsync(nextConfig);

                    if (!SaveConfig(nextConfig, notifyUser: true))
                        return false;
                    // 必须先切换到 nextConfig，录制结束后的 RestartCamera 才会读取新的网络摄像头地址/协议。
                    Config = nextConfig;
                    RefreshArchiveBackupSummary();
                    if (computerNicknameChanged && IsRecordingWorkstation)
                        QueueRecordingWorkstationHeartbeat(force: true);
                    if (cameraBarcodeChanged)
                        ResetCameraBarcodeRecognition();

                    // 同步语音服务配置
                    if (_speechService != null)
                    {
                        _speechService.EnableSoundPrompt = Config.EnableSoundPrompt;
                        _speechService.MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech;
                        _speechService.EnableAiTts = Config.EnableAiTts;
                        _speechService.AiTtsEngine = Config.AiTtsEngine;
                        _speechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                        _speechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                        _speechService.AiTtsSpeed = Config.AiTtsSpeed;
                        _speechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                        _speechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                        _speechService.UpdateBreakWords(Config.TtsBreakWords);
                        if (aiTtsChanged && Config.EnableAiTts && !_speechService.IsAiTtsAvailable)
                            _speechService.InitAiTts();
                    }

                    if (themeChanged)
                    {
                        if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
                        {
                            ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                        }
                    }
                    ForceCheckDiskAndCleanup();
                    RunRecordingCacheCleanup();

                    ApplyGlobalKeyboardConfig();
                    if (globalKeyChanged && _globalKeyHook != null)
                    {
                        if (Config.EnableGlobalKeyboard)
                            _globalKeyHook.Start();
                        else
                            _globalKeyHook.Stop();
                    }
                    bool webServerApplied = true;
                    bool webServerShouldApply = (webServerChanged || webServerNeedsRecovery) && !workstationChanged;
                    if (webServerShouldApply)
                    {
                        ShowToast("正在应用局域网服务设置...", ToastSeverity.Information);
                        webServerApplied = await RestartWebServerAsync(allowAccessSetup: Config.EnableWebServer);
                    }
                    else if (!webServerChanged && !webServerNeedsRecovery)
                    {
                        _ = RefreshWorkstationStatusAsync();
                    }

                    if (cameraRestartAction != CameraRestartAction.None)
                    {
                        if (cameraRestartAction == CameraRestartAction.DeferUntilRecordingEnds)
                        {
                            ShowToast("配置已保存，摄像头配置将在录制结束后生效", ToastSeverity.Information);
                            _pendingCameraRestart = true;
                        }
                        else
                        {
                            ShowToast("配置已保存，重启相机", ToastSeverity.Information);
                            _consecutiveRestartFailures = 0;
                            RestartCamera();
                        }
                    }
                    else
                    {
                        if (!webServerShouldApply || webServerApplied)
                            ShowToast(webServerShouldApply ? "配置已保存，局域网服务已应用" : "提示：配置已保存");
                    }
                    return true;
            }
            catch (Exception ex)
            {
                ShowToast($"设置错误: {ex.Message}", ToastSeverity.Error);
                return false;
            }
        }

        public async void RunStartupSetupFlowsIfNeeded(System.Windows.Window owner)
        {
            bool isExistingUser = Config.FirstUseWizardCompleted;
            if (!isExistingUser)
            {
                await RunFirstUseSetupWizardIfNeededAsync(owner);
                if (_isDisposed || !Config.FirstUseWizardCompleted)
                    return;
                await RunRecordingWorkstationHostBindingPromptIfNeededAsync(owner);
                return;
            }

            await RunRecordingWorkstationHostBindingPromptIfNeededAsync(owner);
            if (_isDisposed)
                return;
            RunCameraBarcodeUpgradePromptIfNeeded(owner);
            await RunMobileConnectionSetupPromptIfNeededAsync(owner);
        }

        private async Task RunFirstUseSetupWizardIfNeededAsync(System.Windows.Window owner)
        {
            if (Config.FirstUseWizardCompleted || _isDisposed)
                return;

            bool pausedCamera = false;
            try
            {
                _isSetupWizardActive = true;
                if (!IsRecording)
                {
                    pausedCamera = StopCamera();
                    if (!pausedCamera)
                    {
                        ShowToast("摄像头未能停止，暂时无法打开配置向导", ToastSeverity.Warning);
                        return;
                    }
                }

                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var wizard = new FirstUseSetupWizardWindow(clonedConfig, allowSkip: false) { Owner = owner };
                MainWindow setupOwner = owner as MainWindow;
                setupOwner?.SuspendCapsLockForModalWindow();

                bool accepted;
                try
                {
                    accepted = wizard.ShowDialog() == true;
                }
                finally
                {
                    setupOwner?.ResumeCapsLockAfterModalWindow();
                }

                if (!accepted)
                    return;

                AppConfig nextConfig = wizard.WasSkipped ? clonedConfig : wizard.ResultConfig;
                AppConfig.ApplyFirstUseDefaults(nextConfig);
                AppConfig.NormalizeAfterLoad(nextConfig);
                if (!SaveConfig(nextConfig, notifyUser: true))
                    return;
                Config = nextConfig;
                ResetCameraBarcodeRecognition();
                ApplyGlobalKeyboardConfig();
                if (_globalKeyHook != null)
                {
                    if (Config.EnableGlobalKeyboard)
                        _globalKeyHook.Start();
                    else
                        _globalKeyHook.Stop();
                }

                if (Config.EnableWebServer)
                {
                    ShowToast("正在应用局域网服务设置...", ToastSeverity.Information);
                    bool webServerReady = await RestartWebServerAsync(allowAccessSetup: true);
                    _webServerStartupTask = Task.FromResult(webServerReady);
                    if (!webServerReady)
                        return;
                }

                ShowToast(
                    wizard.WasSkipped ? "已跳过配置向导" : "配置向导已完成",
                    wizard.WasSkipped ? ToastSeverity.Information : ToastSeverity.Success);
                ShowMobileConnectionSetupPromptIfReady(owner);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("SetupWizard", "First-use setup wizard failed", ex);
                ShowToast($"配置向导错误: {ex.Message}", ToastSeverity.Error);
            }
            finally
            {
                _isSetupWizardActive = false;
                if (pausedCamera && !IsRecording && !_isDisposed)
                {
                    _consecutiveRestartFailures = 0;
                    RestartCamera();
                }
            }
        }

        private void RunCameraBarcodeUpgradePromptIfNeeded(System.Windows.Window owner)
        {
            if (_isDisposed || !AppConfig.ShouldPromptCameraBarcodeUpgrade(Config))
                return;

            var dialog = new CameraBarcodeUpgradeDialog { Owner = owner };
            bool enableRecognition = dialog.ShowDialog() == true;
            var nextConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            AppConfig.ApplyCameraBarcodeUpgradeChoice(nextConfig, enableRecognition);
            if (!SaveConfig(nextConfig, notifyUser: true))
                return;

            Config = nextConfig;
            ResetCameraBarcodeRecognition();
            RuntimeLog.Info("CameraBarcode", $"Upgrade choice saved enabled={Config.EnableCameraBarcodeRecognition}");
            ShowToast(enableRecognition
                ? "已启用摄像头识别面单"
                : "已保留当前设置，可随时在设置中开启");
        }

        private async Task RunMobileConnectionSetupPromptIfNeededAsync(System.Windows.Window owner)
        {
            if (_isDisposed || !AppConfig.ShouldPromptMobileConnection(Config))
                return;

            Task<bool> startupTask = _webServerStartupTask;
            if (startupTask != null)
            {
                bool webServerReady;
                try
                {
                    webServerReady = await startupTask;
                }
                catch
                {
                    return;
                }

                if (!webServerReady)
                    return;
            }

            ShowMobileConnectionSetupPromptIfReady(owner);
        }

        private void ShowMobileConnectionSetupPromptIfReady(System.Windows.Window owner)
        {
            if (!AppConfig.ShouldPromptMobileConnection(Config)
                || !TryGetMobileConnectionUrl(out string url))
            {
                return;
            }

            ShowMobileConnectionWindow(owner, url);

            var nextConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            AppConfig.MarkMobileConnectionSetupCompleted(nextConfig);
            if (!SaveConfig(nextConfig, notifyUser: true))
                return;

            Config = nextConfig;
            RuntimeLog.Info("MobileConnection", "Mobile connection setup prompt completed");
        }

        public bool SuspendCameraForSetupWizard()
        {
            if (IsRecording || _isDisposed)
                return false;

            _isSetupWizardActive = true;
            if (StopCamera())
                return true;

            _isSetupWizardActive = false;
            ShowToast("摄像头未能停止，暂时无法打开配置向导", ToastSeverity.Warning);
            return false;
        }

        public void ResumeCameraAfterSetupWizard()
        {
            _isSetupWizardActive = false;
            if (IsRecording || _isDisposed)
                return;

            _consecutiveRestartFailures = 0;
            RestartCamera();
        }

    }
}
