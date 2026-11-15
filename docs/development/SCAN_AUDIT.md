# 扫码审计数据规范

扫码审计用于回答“输入是否到达程序、程序做了什么、最终有没有录像”。它不是逐帧视觉日志，也不能放在 UI
线程执行数据库、文件快照或刷盘。

## 数据边界

- 业务输入：扫码枪、摄像头已确认条码、人工输入统一写入
  `%LOCALAPPDATA%\ExpressPackingMonitoring\scan-events.db` 的 `ScanEvents` 表。
- 录像实体仍只写 `videos.db` 的 `VideoRecords`。扫码事件通过 `StartedRecordId` 和
  `SourceSessionId` 关联录像，不重复保存完整的录像元数据。
- OBB 模型候选、阈值和耗时不进入扫码审计，继续写
  `vision_obb_diagnostics.log` 与 `vision_obb_timing.log`。
- 原来的 `vision_obb_trigger_events.log` 已由 `ScanEvents` 取代，不再维护第二份业务触发日志。

## 事件粒度

- 扫码枪每个完整输入、人工每次提交、摄像头每次通过稳定确认并提交业务的事件创建一个事件。
- 摄像头持续看到同一面单时，先由现有确认和重新触发策略去重，不能按摄像头帧或 OBB 候选写库。
- 2 秒内同来源、同标准化单号、同动作、同原因、同结果、同前序录像的“忽略”事件合并为一行，
  使用 `RepeatCount`、`LastOccurredAtUtc` 记录重复次数和最后时间。
- 开始、切换、停止、录像失败等不同业务结果不得合并。
- `StopOrigin` 只写稳定原因码（如 `manual_button`、`same_code`、`app_exit`、`static_timeout`），
  本地化停止文案只用于录像记录和详情，不能写进这个字段。

## 未完成事件与保留期

- `received`（输入已记录、决策未写入）、`blocked`（当前不能处理）、`queued`（冷却窗口内排队）、
  `start_requested`/`stop_requested`（等待录像开始/停止）、`started`/`stopping`（录像生命周期中）
  属于非终态。
- 非终态事件超过 1 小时、且不是当前活动事件、待开始事件或最近去重事件时，后台标记为
  `abandoned` 并落库，随后从内存移除，避免 20k 上限被历史未完成事件占满。
- `ScanEvents` 保留 365 天，`ScanEventStore` 启动时清理更早的记录；`Diagnose-MissingVideos.ps1`
  的 `-ScanEventDays` 只能导出保留期内的数据。

## UI 线程约束

- 扫码入口只能创建或更新内存事件、标记脏数据和唤醒后台任务，不得打开 SQLite 连接。
- 表创建、事务、批量 `UPSERT`、反馈包快照全部在后台任务执行。
- 后台任务事件驱动：第一条事件到达后最多等待 2 秒，脏事件达到 20 条立即写入；没有事件时不轮询。
- 写入失败时保留脏事件，等待下一次输入或正常退出时重试，不得阻塞扫码、录像或 UI。
- 正常退出时执行一次有超时上限的最终刷盘。强制结束进程时允许丢失最后不足 2 秒的内存事件。

## 售后导出

`tools/Diagnose-MissingVideos.ps1` 会读取最近 30 天扫码事件并生成
`scan-events-<时间>.csv`。需要更长时间范围时传 `-ScanEventDays`，传 `0` 表示导出全部。
CSV 是按需生成的报表，不是第二份主数据。
