using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;
using AForge.Video.DirectShow;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private bool PrepareDirectAudioPipe()
        {
            try
            {
                DeleteAudioTempFile(StopAudioRecording());
                lock (_audioLock)
                {
                    try { _audioPipeServer?.Dispose(); } catch { }
                    _currentAudioPipeName = $"PackingProof-audio-{Environment.ProcessId}-{Guid.NewGuid():N}";
                    _audioPipeServer = new System.IO.Pipes.NamedPipeServerStream(
                        _currentAudioPipeName,
                        System.IO.Pipes.PipeDirection.Out,
                        1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous,
                        64 * 1024,
                        64 * 1024);
                    _audioPipeConnectionTask = _audioPipeServer.WaitForConnectionAsync();
                    _currentAudioUsesDirectAac = true;
                    _audioBytesWritten = 0;
                    _audioWriteFailed = false;
                    _audioCaptureUnstable = false;
                }
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Audio", "Failed to prepare direct AAC pipe", ex);
                WriteAudioDiagnostic($"实时 AAC 管道初始化失败: {ex.Message}");
                return false;
            }
        }

        private bool StartAudioRecording(string? audioFilePath, bool directAac = false)
        {
            try
            {
                if (!directAac)
                    DeleteAudioTempFile(StopAudioRecording());

                var device = ResolveAudioEndpoint();
                if (device == null)
                {
                    Debug.WriteLine("[Audio] 未找到可用麦克风端点");
                    WriteAudioDiagnostic("未找到可用麦克风端点");
                    return false;
                }

                var capture = CreateWasapiCapture(device);
                var targetFormat = CreatePcm16WaveFormat(capture.WaveFormat);
                WaveFileWriter? writer = null;
                Stream output;
                string outputKind;
                if (directAac)
                {
                    System.IO.Pipes.NamedPipeServerStream pipe;
                    Task connectionTask;
                    lock (_audioLock)
                    {
                        pipe = _audioPipeServer;
                        connectionTask = _audioPipeConnectionTask;
                    }
                    if (pipe == null
                        || connectionTask == null
                        || !connectionTask.Wait(TimeSpan.FromSeconds(3))
                        || !pipe.IsConnected)
                    {
                        capture.Dispose();
                        throw new IOException("FFmpeg 未在限定时间内连接实时 AAC 管道");
                    }
                    output = pipe;
                    outputKind = "实时 AAC 管道";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(audioFilePath))
                        throw new IOException("WAV 临时文件路径无效");
                    Directory.CreateDirectory(Path.GetDirectoryName(audioFilePath)!);
                    writer = new WaveFileWriter(audioFilePath, targetFormat);
                    output = writer;
                    outputKind = "WAV";
                }
                var writeQueue = new BlockingCollection<byte[]>(boundedCapacity: 150);
                var writeTask = Task.Run(() => AudioStreamWriteLoop(output, writeQueue, outputKind));

                lock (_audioLock)
                {
                    _audioCapture = capture;
                    _audioWriter = writer;
                    _audioTargetFormat = targetFormat;
                    _audioWriteQueue = writeQueue;
                    _audioFileWriteTask = writeTask;
                    _currentAudioFilePath = audioFilePath;
                    _currentAudioUsesDirectAac = directAac;
                    _audioInitialOffsetBytesRemaining = directAac
                        ? CalculateInitialAudioOffsetBytes(Config.AudioSyncOffsetMs, targetFormat)
                        : 0;
                    _audioStopRequested = false;
                    _audioRestarting = false;
                    _lastAudioDataAt = DateTime.Now;
                    _lastAudioPacketAt = DateTime.Now;
                    _audioSuppressUntil = DateTime.MinValue;
                    _audioBytesWritten = 0;
                    _audioPeakSinceLastCheck = 0;
                    _audioBytesSinceLastCheck = 0;
                    _silentAudioCheckCount = 0;
                    _audioMonitorLogTick = 0;
                    _audioConvertFailureCount = 0;
                    _audioSelectedSourceChannel = -1;
                    _audioResamplePosition = 0;
                    _audioPreviousSourceSample = 0;
                    _audioHasPreviousSourceSample = false;
                    _audioCaptureUnstable = false;
                    _audioGapCount = 0;
                    _audioMaxGapMs = 0;
                    _audioGapPaddingBytes = 0;
                    _audioWriteFailed = false;
                    _audioWriteQueueFullLogged = false;
                    _audioWriteQueueFullReported = false;
                    _audioFailedForCurrentRecording = false;
                    _audioMonitorCts = new CancellationTokenSource();

                    // 预录视频从事件前开始，而麦克风在录像启动后才有采样数据。
                    // 用等长静音补齐音频起点，避免合成后整条音轨提前。
                    double leadingSilenceSeconds = Math.Clamp(_activePreRecordSeconds, 0, 5);
                    int leadingSilenceBytes = (int)Math.Min(
                        int.MaxValue,
                        leadingSilenceSeconds * targetFormat.AverageBytesPerSecond);
                    leadingSilenceBytes -= leadingSilenceBytes % Math.Max(1, targetFormat.BlockAlign);
                    if (leadingSilenceBytes > 0)
                    {
                        if (directAac)
                            _audioInitialOffsetBytesRemaining += leadingSilenceBytes;
                        else
                        {
                            byte[] silence = new byte[Math.Min(leadingSilenceBytes, targetFormat.AverageBytesPerSecond)];
                            int remaining = leadingSilenceBytes;
                            while (remaining > 0)
                            {
                                int count = Math.Min(remaining, silence.Length);
                                byte[] chunk = count == silence.Length ? silence : new byte[count];
                                if (!_audioWriteQueue!.TryAdd(chunk))
                                {
                                    MarkAudioWriteQueueFull();
                                    break;
                                }
                                _audioBytesWritten += count;
                                remaining -= count;
                            }
                        }
                        WriteAudioDiagnostic($"预录音频起点补静音: seconds={leadingSilenceSeconds:F3}, bytes={leadingSilenceBytes}");
                    }
                }

                if (directAac && !string.IsNullOrWhiteSpace(_currentVideoFilePath))
                    WriteEmbeddedAudioMarker(_currentVideoFilePath, 0, Config.AudioSyncOffsetMs);

                capture.StartRecording();
                _audioMonitorTask = Task.Run(() => AudioCaptureMonitorLoop(_audioMonitorCts.Token));
                Debug.WriteLine($"[Audio] 开始录音: {device.FriendlyName}");
                WriteAudioDiagnostic($"开始录音: device={device.FriendlyName}, sourceFormat={capture.WaveFormat}, targetFormat={targetFormat}, output={outputKind}");
                WriteAudioDiagnostic($"WASAPI 采集模式: eventSync=true, bufferMs=20, syncOffsetMs={(directAac ? Config.AudioSyncOffsetMs : 0)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 启动失败: {ex.Message}");
                WriteAudioDiagnostic($"启动失败: {ex.Message}");
                StopAudioRecording();
                DeleteAudioTempFile(audioFilePath);
                DeleteEmbeddedAudioMarker(_currentVideoFilePath);
                return false;
            }
        }

        internal static int CalculateInitialAudioOffsetBytes(int offsetMs, WaveFormat format)
        {
            int clamped = Math.Clamp(offsetMs, -5000, 5000);
            int blockAlign = Math.Max(1, format.BlockAlign);
            long bytes = (long)Math.Abs(clamped) * format.AverageBytesPerSecond / 1000;
            bytes -= bytes % blockAlign;
            int bounded = (int)Math.Min(int.MaxValue, bytes);
            return clamped < 0 ? -bounded : bounded;
        }

        private string? StopAudioRecording()
        {
            WasapiCapture? capture;
            WaveFileWriter? writer;
            BlockingCollection<byte[]>? writeQueue;
            Task? writeTask;
            bool writeFailed;
            byte[]? resampleTailBytes;
            string? audioFilePath;
            CancellationTokenSource? monitorCts;
            Task? monitorTask;
            System.IO.Pipes.NamedPipeServerStream? pipe;
            bool directAac;

            lock (_audioLock)
            {
                _audioStopRequested = true;
                capture = _audioCapture;
                monitorCts = _audioMonitorCts;
                monitorTask = _audioMonitorTask;
                _audioMonitorCts = null;
                _audioMonitorTask = null;
                _audioRestarting = false;
            }

            try { monitorCts?.Cancel(); } catch { }
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { monitorTask?.Wait(1000); } catch { }
            try { monitorCts?.Dispose(); } catch { }

            lock (_audioLock)
            {
                writer = _audioWriter;
                writeQueue = _audioWriteQueue;
                writeTask = _audioFileWriteTask;
                writeFailed = _audioWriteFailed;
                resampleTailBytes = FlushResamplerTail(_audioPreviousSourceSample, _audioHasPreviousSourceSample, ref _audioResamplePosition);
                audioFilePath = _currentAudioFilePath;
                pipe = _audioPipeServer;
                directAac = _currentAudioUsesDirectAac;
                _audioCapture = null;
                _audioWriter = null;
                _audioTargetFormat = null;
                _audioPipeServer = null;
                _audioPipeConnectionTask = null;
                _currentAudioPipeName = null;
                _currentAudioUsesDirectAac = false;
                _audioInitialOffsetBytesRemaining = 0;
                _audioWriteQueue = null;
                _audioFileWriteTask = null;
                _currentAudioFilePath = null;
            }

            if (resampleTailBytes != null && resampleTailBytes.Length > 0 && writeQueue != null && !writeQueue.IsAddingCompleted && !writeFailed)
            {
                try
                {
                    if (writeQueue.TryAdd(resampleTailBytes))
                        _audioBytesWritten += resampleTailBytes.Length;
                    else
                        writeFailed = true;
                }
                catch
                {
                    writeFailed = true;
                }
            }
            try { writeQueue?.CompleteAdding(); } catch { }

            bool writeCompleted = true;
            try
            {
                if (writeTask != null)
                    writeCompleted = writeTask.Wait(5000);
            }
            catch
            {
                writeCompleted = false;
            }
            if (!writeCompleted)
            {
                writeFailed = true;
                WriteAudioDiagnostic($"{(directAac ? "实时 AAC 管道" : "WAV")}写入超时，已标记本次音频异常");
            }
            if (_audioWriteQueueFullLogged && !_audioWriteQueueFullReported)
            {
                _audioWriteQueueFullReported = true;
                WriteAudioDiagnostic($"{(directAac ? "实时 AAC 管道" : "WAV")}写入队列已满，已标记本次音频异常");
            }
            if (writeTask == null)
            {
                try { writer?.Flush(); } catch { }
                try { writer?.Dispose(); } catch { }
                try { pipe?.Dispose(); } catch { }
            }
            try { writeQueue?.Dispose(); } catch { }

            lock (_audioLock)
            {
                writeFailed = writeFailed || _audioWriteFailed;
            }

            if (directAac)
            {
                if (writeFailed || _audioCaptureUnstable)
                {
                    _audioFailedForCurrentRecording = true;
                    WriteAudioDiagnostic(
                        $"实时 AAC 音频异常: writeFailed={writeFailed}, gaps={_audioGapCount}, maxGapMs={_audioMaxGapMs:F0}, paddedBytes={_audioGapPaddingBytes}");
                }
                else if (_audioBytesWritten > 0 && !string.IsNullOrWhiteSpace(_currentVideoFilePath))
                {
                    WriteEmbeddedAudioMarker(_currentVideoFilePath, _audioBytesWritten, Config.AudioSyncOffsetMs);
                }
                return null;
            }

            if (string.IsNullOrEmpty(audioFilePath)) return null;

            if (writeFailed)
            {
                _audioFailedForCurrentRecording = true;
                PersistAudioFailureDiagnostic(audioFilePath, "WAV 写入失败、队列满或停止超时，已放弃本次音频");
                DeleteAudioTempFile(audioFilePath);
                return null;
            }

            try
            {
                if (IsCompletedAudioFileUsable(audioFilePath))
                {
                    if (_audioCaptureUnstable)
                    {
                        _audioFailedForCurrentRecording = true;
                        PersistAudioFailureDiagnostic(audioFilePath, $"WAV 采集不稳定: gaps={_audioGapCount}, maxGapMs={_audioMaxGapMs:F0}, paddedBytes={_audioGapPaddingBytes}");
                        WriteAudioDiagnostic($"WAV 采集不稳定，跳过 MP4 合成并保留诊断文件: gaps={_audioGapCount}, maxGapMs={_audioMaxGapMs:F0}, paddedBytes={_audioGapPaddingBytes}");
                    }
                    return audioFilePath;
                }
            }
            catch { }

            _audioFailedForCurrentRecording = true;
            PersistAudioFailureDiagnostic(audioFilePath, "WAV 文件不可用或完整性校验失败，已放弃本次音频");
            DeleteAudioTempFile(audioFilePath);
            return null;
        }

        private static void PersistAudioFailureDiagnostic(string audioFilePath, string reason)
        {
            try
            {
                string logPath = Path.ChangeExtension(audioFilePath, ".audio.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {reason}{Environment.NewLine}");
                RuntimeLog.Warn("Audio", $"{reason}, file={Path.GetFileName(audioFilePath)}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Audio", $"Failed to persist audio diagnostic for {Path.GetFileName(audioFilePath)}: {ex.Message}");
            }
        }

        private bool IsCompletedAudioFileUsable(string audioFilePath)
        {
            if (!File.Exists(audioFilePath) || new FileInfo(audioFilePath).Length <= 44)
                return false;

            try
            {
                using var reader = new WaveFileReader(audioFilePath);
                long dataBytes = reader.Length;
                long expectedBytes = _audioBytesWritten;
                long toleranceBytes = Math.Max(reader.WaveFormat.AverageBytesPerSecond, expectedBytes / 100);
                double durationSeconds = reader.TotalTime.TotalSeconds;
                bool byteCountOk = expectedBytes <= 0 || Math.Abs(dataBytes - expectedBytes) <= toleranceBytes;
                bool durationOk = durationSeconds > 0 && durationSeconds < TimeSpan.FromHours(12).TotalSeconds;

                if (!byteCountOk || !durationOk)
                {
                    WriteAudioDiagnostic($"WAV 完整性校验失败: dataBytes={dataBytes}, expectedBytes={expectedBytes}, duration={durationSeconds:F1}s, tolerance={toleranceBytes}");
                    return false;
                }
                WriteAudioDiagnostic($"WAV 完整性校验通过: dataBytes={dataBytes}, duration={durationSeconds:F1}s");
                return true;
            }
            catch (Exception ex)
            {
                WriteAudioDiagnostic($"WAV 完整性校验异常: {ex.Message}");
                return false;
            }
        }

        private WasapiCapture CreateWasapiCapture(MMDevice device)
        {
            var capture = new WasapiCapture(device, true, 20)
            {
                ShareMode = AudioClientShareMode.Shared
            };

            capture.DataAvailable += (_, e) =>
            {
                string? diagnosticMessage = null;
                lock (_audioLock)
                {
                    WaveFormat? targetFormat = _audioTargetFormat;
                    if (targetFormat == null || e.BytesRecorded <= 0) return;
                    var now = DateTime.Now;
                    _lastAudioPacketAt = now;
                    int selectedChannel = _audioSelectedSourceChannel;
                    byte[]? pcmBytes = ConvertCaptureBufferToPcm16(
                        e.Buffer,
                        e.BytesRecorded,
                        capture.WaveFormat,
                        targetFormat,
                        ref selectedChannel,
                        ref _audioResamplePosition,
                        ref _audioPreviousSourceSample,
                        ref _audioHasPreviousSourceSample);
                    if (pcmBytes == null || pcmBytes.Length == 0)
                    {
                        _audioConvertFailureCount++;
                        if (_audioConvertFailureCount == 1 || _audioConvertFailureCount % 10 == 0)
                            diagnosticMessage = $"麦克风格式暂不支持转换: format={capture.WaveFormat}, bytes={e.BytesRecorded}, failures={_audioConvertFailureCount}";
                    }
                    else
                    {
                        _audioConvertFailureCount = 0;
                        bool suppressing = now < _audioSuppressUntil;
                        if (suppressing)
                        {
                            Array.Clear(pcmBytes, 0, pcmBytes.Length);
                            _audioSelectedSourceChannel = -1;
                            _audioResamplePosition = 0;
                            _audioPreviousSourceSample = 0;
                            _audioHasPreviousSourceSample = false;
                        }
                        else if (selectedChannel != _audioSelectedSourceChannel)
                        {
                            _audioSelectedSourceChannel = selectedChannel;
                            diagnosticMessage = $"麦克风输入通道已选择: channel={selectedChannel}, sourceChannels={capture.WaveFormat.Channels}";
                        }
                        var gapDiagnostic = PadAudioGapIfNeeded(now);
                        if (!string.IsNullOrEmpty(gapDiagnostic))
                            diagnosticMessage = string.IsNullOrEmpty(diagnosticMessage)
                                ? gapDiagnostic
                                : $"{diagnosticMessage}; {gapDiagnostic}";
                        UpdateAudioLevelStats(pcmBytes, pcmBytes.Length, targetFormat);
                        _audioBytesWritten += EnqueueAudioBytes(pcmBytes);
                        _lastAudioDataAt = DateTime.Now;
                    }
                }
                if (!string.IsNullOrEmpty(diagnosticMessage))
                    WriteAudioDiagnostic(diagnosticMessage);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                {
                    Debug.WriteLine($"[Audio] 录音停止异常: {e.Exception.Message}");
                    WriteAudioDiagnostic($"录音停止异常: {e.Exception.Message}");
                }

                if (ShouldRestartAudioCapture())
                    _ = Task.Run(() => RestartAudioCapture("stopped"));
            };

            return capture;
        }

        private string? PadAudioGapIfNeeded(DateTime now)
        {
            WaveFormat? targetFormat = _audioTargetFormat;
            if (targetFormat == null || _lastAudioDataAt == DateTime.MinValue) return null;

            double gapMs = (now - _lastAudioDataAt).TotalMilliseconds;
            if (gapMs <= 750) return null;

            int bytesPerSecond = targetFormat.AverageBytesPerSecond;
            int blockAlign = Math.Max(1, targetFormat.BlockAlign);
            int silenceBytes = (int)(bytesPerSecond * (gapMs / 1000.0));
            silenceBytes -= silenceBytes % blockAlign;
            if (silenceBytes <= 0) return null;

            _audioGapCount++;
            _audioGapPaddingBytes += silenceBytes;
            if (gapMs > _audioMaxGapMs)
                _audioMaxGapMs = gapMs;

            byte[] silence = new byte[Math.Min(silenceBytes, bytesPerSecond)];
            int remaining = silenceBytes;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, silence.Length);
                if (chunk == silence.Length)
                {
                    _audioBytesWritten += EnqueueAudioBytes(silence);
                }
                else
                {
                    byte[] partialSilence = new byte[chunk];
                    _audioBytesWritten += EnqueueAudioBytes(partialSilence);
                }
                remaining -= chunk;
            }
            bool unstable = gapMs >= 3000 || _audioGapPaddingBytes >= bytesPerSecond * 5L;
            if (unstable)
            {
                _audioCaptureUnstable = true;
                _audioFailedForCurrentRecording = true;
                return $"录音采集断流过长: gapMs={gapMs:F0}, gaps={_audioGapCount}, paddedBytes={_audioGapPaddingBytes}, maxGapMs={_audioMaxGapMs:F0}";
            }
            Debug.WriteLine($"[Audio] 补齐录音间隙: {gapMs:F0}ms");
            return $"补齐录音间隙: {gapMs:F0}ms, silenceBytes={silenceBytes}";
        }

        private int EnqueueAudioBytes(byte[] bytes)
        {
            if (_audioWriteFailed || _audioWriteQueue == null || _audioWriteQueue.IsAddingCompleted || bytes.Length == 0) return 0;
            try
            {
                int acceptedBytes = 0;
                if (_currentAudioUsesDirectAac && _audioInitialOffsetBytesRemaining > 0)
                {
                    int remainingSilence = _audioInitialOffsetBytesRemaining;
                    byte[] silence = new byte[Math.Min(remainingSilence, 48_000 * 2)];
                    while (remainingSilence > 0)
                    {
                        int count = Math.Min(remainingSilence, silence.Length);
                        byte[] chunk = count == silence.Length ? silence : new byte[count];
                        if (!_audioWriteQueue.TryAdd(chunk))
                        {
                            MarkAudioWriteQueueFull();
                            return acceptedBytes;
                        }
                        acceptedBytes += count;
                        remainingSilence -= count;
                    }
                    _audioInitialOffsetBytesRemaining = 0;
                }
                else if (_currentAudioUsesDirectAac && _audioInitialOffsetBytesRemaining < 0)
                {
                    int discard = Math.Min(-_audioInitialOffsetBytesRemaining, bytes.Length);
                    _audioInitialOffsetBytesRemaining += discard;
                    if (discard >= bytes.Length)
                        return 0;
                    bytes = bytes[discard..];
                }

                if (!_audioWriteQueue.TryAdd(bytes))
                {
                    MarkAudioWriteQueueFull();
                    return acceptedBytes;
                }
                return acceptedBytes + bytes.Length;
            }
            catch
            {
                MarkAudioWriteQueueFull();
                return 0;
            }
        }

        private void MarkAudioWriteQueueFull()
        {
            _audioWriteFailed = true;
            _audioWriteQueueFullLogged = true;
        }

        private void AudioStreamWriteLoop(Stream output, BlockingCollection<byte[]> queue, string outputKind)
        {
            try
            {
                foreach (var bytes in queue.GetConsumingEnumerable())
                    output.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] {outputKind}写入异常: {ex.Message}");
                lock (_audioLock)
                {
                    _audioWriteFailed = true;
                }
                WriteAudioDiagnostic($"{outputKind}写入异常: {ex.Message}");
            }
            finally
            {
                try { output.Flush(); } catch { }
                try { output.Dispose(); } catch { }
            }
        }

        private void UpdateAudioLevelStats(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            short peak;
            bool knownFormat = TryGetAudioPeak(buffer, bytesRecorded, format, out peak);

            if (knownFormat && peak > _audioPeakSinceLastCheck)
                _audioPeakSinceLastCheck = peak;
            else if (!knownFormat)
                _audioPeakSinceLastCheck = short.MaxValue;

            _audioBytesSinceLastCheck += bytesRecorded;
        }

        internal static WaveFormat CreatePcm16WaveFormat(WaveFormat sourceFormat)
        {
            return new WaveFormat(48000, 16, 1);
        }

        internal static byte[]? ConvertCaptureBufferToPcm16(
            byte[] buffer,
            int bytesRecorded,
            WaveFormat sourceFormat,
            WaveFormat targetFormat,
            ref int selectedSourceChannel,
            ref double resamplePosition,
            ref short previousSourceSample,
            ref bool hasPreviousSourceSample)
        {
            if (bytesRecorded <= 0) return Array.Empty<byte>();

            int sourceChannels = sourceFormat.Channels;
            int targetChannels = targetFormat.Channels;
            int blockAlign = sourceFormat.BlockAlign;
            int bitsPerSample = sourceFormat.BitsPerSample;
            if (sourceChannels <= 0 || targetChannels != 1 || blockAlign <= 0 || bitsPerSample <= 0) return null;

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0 || blockAlign < sourceChannels * bytesPerSample) return null;

            int frames = bytesRecorded / blockAlign;
            if (frames <= 0) return Array.Empty<byte>();

            bool isFloat = IsFloatWaveFormat(sourceFormat);
            bool isPcm = IsPcmWaveFormat(sourceFormat);
            if (!isFloat && !isPcm) return null;

            int selectedChannel = SelectAudioSourceChannel(buffer, frames, sourceChannels, blockAlign, bytesPerSample, isFloat, selectedSourceChannel);
            if (selectedChannel != selectedSourceChannel)
                selectedSourceChannel = selectedChannel;

            short[] monoSamples = new short[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = frame * blockAlign;
                int sampleOffset = frameOffset + selectedChannel * bytesPerSample;
                monoSamples[frame] = isFloat
                    ? ReadFloatSampleAsPcm16(buffer, sampleOffset, bytesPerSample)
                    : ReadPcmSampleAsPcm16(buffer, sampleOffset, bytesPerSample);
            }
            return ResampleMonoPcm16ToBytes(monoSamples, sourceFormat.SampleRate, targetFormat.SampleRate, ref resamplePosition, ref previousSourceSample, ref hasPreviousSourceSample);
        }

        private static byte[] ResampleMonoPcm16ToBytes(
            short[] sourceSamples,
            int sourceSampleRate,
            int targetSampleRate,
            ref double resamplePosition,
            ref short previousSourceSample,
            ref bool hasPreviousSourceSample)
        {
            if (sourceSamples.Length == 0) return Array.Empty<byte>();

            if (sourceSampleRate == targetSampleRate)
            {
                previousSourceSample = sourceSamples[^1];
                hasPreviousSourceSample = true;
                return Pcm16SamplesToBytes(sourceSamples);
            }

            int prefix = hasPreviousSourceSample ? 1 : 0;
            short[] samples = new short[sourceSamples.Length + prefix];
            if (hasPreviousSourceSample)
                samples[0] = previousSourceSample;
            Array.Copy(sourceSamples, 0, samples, prefix, sourceSamples.Length);

            if (samples.Length < 2)
            {
                previousSourceSample = samples[^1];
                hasPreviousSourceSample = true;
                return Array.Empty<byte>();
            }

            double step = (double)sourceSampleRate / targetSampleRate;
            var output = new List<short>(Math.Max(1, (int)(sourceSamples.Length / step) + 2));
            while (resamplePosition + 1 < samples.Length)
            {
                int index = (int)resamplePosition;
                double fraction = resamplePosition - index;
                double sample = samples[index] + (samples[index + 1] - samples[index]) * fraction;
                output.Add((short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue));
                resamplePosition += step;
            }

            resamplePosition -= samples.Length - 1;
            if (resamplePosition < 0) resamplePosition = 0;
            previousSourceSample = sourceSamples[^1];
            hasPreviousSourceSample = true;
            return Pcm16SamplesToBytes(output);
        }

        internal static byte[]? FlushResamplerTail(short previousSourceSample, bool hasPreviousSourceSample, ref double resamplePosition)
        {
            if (!hasPreviousSourceSample || resamplePosition <= 0) return null;

            var output = new List<short>(1);
            if (resamplePosition < 1)
                output.Add(previousSourceSample);
            resamplePosition = 0;
            return output.Count == 0 ? null : Pcm16SamplesToBytes(output);
        }

        private static byte[] Pcm16SamplesToBytes(IReadOnlyList<short> samples)
        {
            byte[] output = new byte[samples.Count * 2];
            int outOffset = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                short sample = samples[i];
                output[outOffset++] = (byte)(sample & 0xff);
                output[outOffset++] = (byte)((sample >> 8) & 0xff);
            }
            return output;
        }

        private static int SelectAudioSourceChannel(byte[] buffer, int frames, int channels, int blockAlign, int bytesPerSample, bool isFloat, int currentChannel)
        {
            if (channels <= 1)
                return 0;

            long[] energy = new long[channels];
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = frame * blockAlign;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = frameOffset + channel * bytesPerSample;
                    short sample = isFloat
                        ? ReadFloatSampleAsPcm16(buffer, sampleOffset, bytesPerSample)
                        : ReadPcmSampleAsPcm16(buffer, sampleOffset, bytesPerSample);
                    energy[channel] += Math.Abs((int)sample);
                }
            }

            int selected = 0;
            long strongest = energy[0];
            for (int channel = 1; channel < channels; channel++)
            {
                if (energy[channel] > strongest)
                {
                    selected = channel;
                    strongest = energy[channel];
                }
            }

            long activeThreshold = (long)frames * 16;
            if (currentChannel < 0 || currentChannel >= channels)
                return strongest > activeThreshold ? selected : 0;

            long currentEnergy = energy[currentChannel];
            bool candidateIsActive = strongest > activeThreshold;
            bool candidateClearlyStronger = strongest > Math.Max(currentEnergy * 4, currentEnergy + activeThreshold);
            if (selected != currentChannel && candidateIsActive && candidateClearlyStronger)
                return selected;

            selected = currentChannel;
            return selected;
        }

        private static bool IsFloatWaveFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
            if (format.Encoding != WaveFormatEncoding.Extensible) return false;

            return format is WaveFormatExtensible extensible
                && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }

        private static bool IsPcmWaveFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.Pcm) return true;
            if (format.Encoding != WaveFormatEncoding.Extensible) return false;

            return format is WaveFormatExtensible extensible
                && extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        }

        private static short ReadFloatSampleAsPcm16(byte[] buffer, int offset, int bytesPerSample)
        {
            if (bytesPerSample != 4 || offset + 4 > buffer.Length) return 0;
            float value = BitConverter.ToSingle(buffer, offset);
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0;
            value = Math.Clamp(value, -1.0f, 1.0f);
            return (short)Math.Round(value * short.MaxValue);
        }

        private static short ReadPcmSampleAsPcm16(byte[] buffer, int offset, int bytesPerSample)
        {
            if (offset + bytesPerSample > buffer.Length) return 0;
            return bytesPerSample switch
            {
                1 => (short)((buffer[offset] - 128) << 8),
                2 => BitConverter.ToInt16(buffer, offset),
                3 => (short)(ReadInt24(buffer, offset) >> 8),
                4 => (short)(BitConverter.ToInt32(buffer, offset) >> 16),
                _ => 0
            };
        }

        private static int ReadInt24(byte[] buffer, int offset)
        {
            int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xff000000);
            return value;
        }

        internal static bool TryGetAudioPeak(byte[] buffer, int bytesRecorded, WaveFormat format, out short peak)
        {
            peak = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    int scaled = (int)Math.Clamp(Math.Abs(sample) * short.MaxValue, 0, short.MaxValue);
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            if (format.Encoding != WaveFormatEncoding.Pcm)
                return false;

            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    short abs = sample == short.MinValue ? short.MaxValue : (short)Math.Abs(sample);
                    if (abs > peak) peak = abs;
                }
                return true;
            }

            if (format.BitsPerSample == 24)
            {
                for (int i = 0; i + 2 < bytesRecorded; i += 3)
                {
                    int sample = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16);
                    if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                    int scaled = Math.Min(short.MaxValue, Math.Abs(sample >> 8));
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            if (format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    int sample = BitConverter.ToInt32(buffer, i);
                    int scaled = Math.Min(short.MaxValue, Math.Abs(sample >> 16));
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            return false;
        }

        private async Task AudioCaptureMonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token);
                    if (token.IsCancellationRequested) break;

                    DateTime lastDataAt;
                    bool shouldMonitor;
                    short peak;
                    long bytes;
                    int silentCount;
                    bool shouldLogLevel;
                    bool shouldReportQueueFull;
                    lock (_audioLock)
                    {
                        shouldMonitor = !_audioStopRequested && _audioTargetFormat != null && _audioCapture != null;
                        lastDataAt = _lastAudioDataAt;
                        if (_lastAudioPacketAt > lastDataAt)
                            lastDataAt = _lastAudioPacketAt;
                        peak = _audioPeakSinceLastCheck;
                        bytes = _audioBytesSinceLastCheck;
                        _audioPeakSinceLastCheck = 0;
                        _audioBytesSinceLastCheck = 0;

                        if (shouldMonitor && bytes > 0 && peak <= 1)
                            _silentAudioCheckCount++;
                        else if (bytes > 0 && peak > 1)
                            _silentAudioCheckCount = 0;
                        silentCount = _silentAudioCheckCount;
                        _audioMonitorLogTick++;
                        shouldLogLevel = silentCount > 0 || _audioMonitorLogTick % 5 == 0;
                        shouldReportQueueFull = _audioWriteQueueFullLogged && !_audioWriteQueueFullReported;
                        if (shouldReportQueueFull)
                            _audioWriteQueueFullReported = true;
                    }

                    if (shouldReportQueueFull)
                        WriteAudioDiagnostic($"{(_currentAudioUsesDirectAac ? "实时 AAC 管道" : "WAV")}写入队列已满，已标记本次音频异常");

                    if (shouldMonitor && (DateTime.Now - lastDataAt).TotalSeconds > 5)
                    {
                        WriteAudioDiagnostic($"音频数据断流: lastDataAge={(DateTime.Now - lastDataAt).TotalSeconds:F1}s");
                        RestartAudioCapture("no-data");
                    }
                    else if (shouldMonitor && bytes > 0)
                    {
                        if (shouldLogLevel)
                            WriteAudioDiagnostic($"音频电平: peak={peak}, bytes={bytes}, silentCount={silentCount}, silentRestart=disabled");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] 监控异常: {ex.Message}");
                    WriteAudioDiagnostic($"监控异常: {ex.Message}");
                }
            }
        }

        private bool ShouldRestartAudioCapture()
        {
            lock (_audioLock)
            {
                return !_audioStopRequested && _audioTargetFormat != null && !_audioRestarting;
            }
        }

        private void RestartAudioCapture(string reason)
        {
            WasapiCapture? oldCapture = null;

            lock (_audioLock)
            {
                if (_audioStopRequested || _audioTargetFormat == null || _audioRestarting) return;
                _audioRestarting = true;
                oldCapture = _audioCapture;
                _audioCapture = null;
            }

            try { oldCapture?.StopRecording(); } catch { }
            try { oldCapture?.Dispose(); } catch { }

            try
            {
                var device = ResolveAudioEndpoint();
                if (device == null)
                {
                    Debug.WriteLine($"[Audio] 重启失败({reason}): 未找到麦克风端点");
                    WriteAudioDiagnostic($"重启失败({reason}): 未找到麦克风端点");
                    return;
                }

                var capture = CreateWasapiCapture(device);
                lock (_audioLock)
                {
                    if (_audioStopRequested || _audioTargetFormat == null)
                    {
                        try { capture.Dispose(); } catch { }
                        return;
                    }
                    var restartFormat = CreatePcm16WaveFormat(capture.WaveFormat);
                    if (restartFormat.SampleRate != _audioTargetFormat.SampleRate
                        || restartFormat.Channels != _audioTargetFormat.Channels)
                    {
                        try { capture.Dispose(); } catch { }
                        WriteAudioDiagnostic($"重启失败({reason}): 麦克风格式变化 sourceFormat={capture.WaveFormat}, targetFormat={_audioTargetFormat}");
                        return;
                    }
                    _audioCapture = capture;
                    _lastAudioDataAt = DateTime.Now;
                    _lastAudioPacketAt = DateTime.Now;
                    _audioSuppressUntil = DateTime.Now.AddMilliseconds(500);
                    _audioPeakSinceLastCheck = 0;
                    _audioBytesSinceLastCheck = 0;
                    _silentAudioCheckCount = 0;
                    _audioMonitorLogTick = 0;
                    _audioConvertFailureCount = 0;
                    _audioResamplePosition = 0;
                    _audioPreviousSourceSample = 0;
                    _audioHasPreviousSourceSample = false;
                }

                capture.StartRecording();
                Debug.WriteLine($"[Audio] 已重启录音({reason}): {device.FriendlyName}");
                WriteAudioDiagnostic($"已重启录音({reason}): device={device.FriendlyName}, sourceFormat={capture.WaveFormat}, wavFormat={CreatePcm16WaveFormat(capture.WaveFormat)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 重启异常({reason}): {ex.Message}");
                WriteAudioDiagnostic($"重启异常({reason}): {ex.Message}");
            }
            finally
            {
                lock (_audioLock)
                {
                    _audioRestarting = false;
                }
            }
        }

        private MMDevice? ResolveAudioEndpoint()
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            if (devices == null || devices.Count == 0) return null;

            MMDevice? defaultDevice = null;
            try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }

            bool hasConfiguredEndpoint = false;
            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.ID, Config.AudioDeviceMoniker))
                        return device;
                }
            }

            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceName))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.FriendlyName, Config.AudioDeviceName)
                        || AudioEndpointMatches(GetEndpointDisplayName(device), Config.AudioDeviceName))
                        return device;
                }
            }

            if (hasConfiguredEndpoint)
                return null;

            return defaultDevice ?? devices[0];
        }

        private static string GetEndpointDisplayName(MMDevice device)
        {
            try { return device.DeviceFriendlyName; } catch { return device.FriendlyName; }
        }

        private static bool AudioEndpointMatches(string endpointName, string configuredName)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || string.IsNullOrWhiteSpace(configuredName))
                return false;

            return endpointName.Equals(configuredName, StringComparison.OrdinalIgnoreCase)
                || endpointName.Contains(configuredName, StringComparison.OrdinalIgnoreCase)
                || configuredName.Contains(endpointName, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteAudioTempFile(string? audioFilePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                    File.Delete(audioFilePath);
            }
            catch { }
        }

        private static string GetEmbeddedAudioMarkerPath(string mediaPath) =>
            Path.ChangeExtension(mediaPath, ".embedded-audio");

        private static void WriteEmbeddedAudioMarker(string mediaPath, long audioBytes, int syncOffsetMs)
        {
            try
            {
                File.WriteAllText(
                    GetEmbeddedAudioMarkerPath(mediaPath),
                    $"pcmBytes={audioBytes}{Environment.NewLine}syncOffsetMs={Math.Clamp(syncOffsetMs, -5000, 5000)}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Audio", $"Failed to write embedded-audio marker file={Path.GetFileName(mediaPath)}: {ex.Message}");
            }
        }

        private static bool HasEmbeddedAudioMarker(string mediaPath) =>
            !string.IsNullOrWhiteSpace(mediaPath) && File.Exists(GetEmbeddedAudioMarkerPath(mediaPath));

        private static void DeleteEmbeddedAudioMarker(string? mediaPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(mediaPath))
                    File.Delete(GetEmbeddedAudioMarkerPath(mediaPath));
            }
            catch { }
        }

        private bool HasConfiguredAudioDevice()
        {
            return !string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker)
                || (!string.IsNullOrWhiteSpace(Config.AudioDeviceName)
                    && Config.AudioDeviceName != "未检测到麦克风");
        }

        private bool IsConfiguredAudioDevice(string name, string moniker)
        {
            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker)
                && string.Equals(moniker, Config.AudioDeviceMoniker, StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(Config.AudioDeviceName)
                && string.Equals(name, Config.AudioDeviceName, StringComparison.OrdinalIgnoreCase);
        }

        private int GetVideoCqp() => Config.VideoCqp > 0 ? Config.VideoCqp : 25;

        /// <summary>
        /// 录制完成后自动将 MKV 无损转换为 MP4（容器转换，不重新编码）
        /// </summary>
    }
}
