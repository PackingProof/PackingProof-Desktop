# PackingProof 扩展接口 v1

扩展接口供 ERP、油猴脚本、称重程序等局域网适配器使用。第三方程序只能通过 HTTP API 交换受限结构化数据，不得直接访问 PackingProof 的数据库、配置文件或执行目录。本文件是已经实现的 v1 协议，不包含未落地的路线图草案。

v1 同时保留两类接口：

- 生产扩展协议：用户明确授权后，扩展使用独立凭据签名，领取扫码任务并提交订单、退款或测量结果
- 兼容数据接口：旧脚本继续使用录像网页访问密钥主动推送订单或当前录像水印字段

扩展 API 总开关默认关闭。关闭时不创建扩展凭据，不初始化任务代理、结果处理器或扩展后台定时器；旧版订单广播接口仍可继续工作。

## 快速开始

1. 在 PackingProof 的“设置 → 扩展与联动”中开启“启用扩展 API”
2. 扩展读取 `/api/extensions/v1/capabilities`，确认 `extensionApiEnabled=true` 和所需 feature
3. 扩展向 `/api/extensions/v1/enroll` 发起授权，用户在电脑端确认名称、来源、权限、能力和绑定工位
4. 扩展仅保存本次返回的独立凭据，后续请求使用 v1 签名，不使用录像网页访问密钥
5. 用户可在“已授权扩展”中查看在线状态或撤销授权；需要更换凭据时，撤销后由扩展重新发起授权

油猴脚本还可以从“安装自定义扩展”导入 `.user.js` 或 `.ppext`，再通过“安装订单联动”选择安装。导入和安装只负责维护脚本文件、地址及更新链接，不会绕过扩展授权。

PackingProof 不执行导入的脚本，也不会把脚本加载为桌面端插件。脚本只会被保存、检查、注入当前设备地址后提供给浏览器脚本管理器安装。

扩展市场还支持 `external-adapter` PPEXT。PackingProof 只验证签名市场索引、SHA-256、`packingproof-extension` 格式标识和包结构，再安全解包到用户数据目录；不会自动执行其中的程序。外部适配器是普通 Windows 程序，不受 PackingProof 沙箱限制，市场详情和安装确认页会明确提示其源码状态与系统访问风险。

## 两种鉴权方式

### 扩展签名鉴权（推荐）

扫码任务、确认、结果提交和扩展心跳必须使用扩展授权返回的独立凭据签名。该凭据与录像网页访问密钥、手机备份令牌互相独立，撤销或轮换不会改变录像网页链接。

签名请求头：

```http
X-PackingProof-Extension-Version: 1
X-PackingProof-Extension-Id: <extensionInstanceId>
X-PackingProof-Extension-Credential-Generation: <credentialGeneration>
X-PackingProof-Extension-Timestamp: <Unix 秒>
X-PackingProof-Extension-Nonce: <至少 16 字节随机数的十六进制>
X-PackingProof-Extension-Content-SHA256: <请求正文 SHA-256>
X-PackingProof-Extension-Signature: <HMAC-SHA256>
```

签名原文严格使用 UTF-8 和换行符 `\n`：

```text
packingproof-extension-request-v1
1
<credentialGeneration>
<大写 HTTP 方法>
<路径和查询字符串>
<Unix 秒>
<小写 nonce>
<小写正文 SHA-256>
<extensionInstanceId>
```

GET 请求的正文是零字节，不是 `{}`。每个请求必须使用新的 nonce；时间与主机相差不得超过 5 分钟。

### 录像网页访问密钥（兼容）

`orders`、`recordings/active` 和录像字段接口继续兼容网页访问密钥：

```http
X-EPM-Access-Key: <PackingProof 访问密钥>
Content-Type: application/json
```

也兼容在 URL 中使用 `?key=...`，但不建议这样做，因为 URL 可能进入浏览器历史、代理或日志。不要把任何凭据写入公开仓库、脚本元数据或页面文本。

扩展 API 已启用时，这些兼容接口也接受扩展凭据签名。签名调用必须分别获得 `orders.write`、`recordings.active.read` 或 `recording-fields.write` 权限；录像相关接口还会检查当前录像节点是否在授权范围内。签名请求中的 `providerId` 必须与授权身份一致，不能冒用其他数据来源。

成功响应为 JSON；失败响应包含 `error`，扩展接口还会尽量提供稳定的 `errorCode`。常见状态码：

| 状态码 | 含义 |
| --- | --- |
| `200` | 请求已处理 |
| `400` | JSON、字段或长度无效 |
| `401` | 缺少或无效的访问密钥 |
| `403` | 扩展未启用、权限不足、授权被拒绝或凭据已撤销 |
| `409` | 当前状态不允许操作，例如录像已结束 |
| `410` | 扫码任务已经过期 |
| `426` | 请求的扩展接口版本不受支持 |
| `429` | 请求频率、连接数或防重放容量达到上限 |
| `500` | 主机暂时无法处理请求 |

## 第三方油猴脚本开发规范

第三方脚本保留自己的 `@name`、`@namespace` 和作者信息，不得冒用官方脚本名称。源版本必须使用两段格式 `X.Y`，例如 `1.4`；安装向导下载时，桌面端会根据设备配置生成最终版本 `X.Y.Z`，其中 `Z` 是设备配置修订号。脚本不应把 `X.Y.Z` 写死，也不应自行修改设备修订号。

建议的元数据头：

```javascript
// ==UserScript==
// @name         我的 PackingProof 订单适配器
// @namespace    https://example.com/my-packingproof-adapter
// @version      1.0
// @description  将第三方系统订单发送到 PackingProof
// @author       Example
// @match        https://example.example/*
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// ==/UserScript==
```

为了让 PackingProof 自动维护设备地址、跨源权限和更新地址，脚本中应保留以下占位符及对应的数据声明：

```javascript
// PACKING_PROOF_CONNECT_TARGETS
// PACKING_PROOF_UPDATE_URLS

const PACKING_PROOF_RECORDERS = [];
const PACKING_PROOF_HOST = null;
```

安装向导会替换这些位置：

- `PACKING_PROOF_RECORDERS`：当前已配置的录像设备地址、设备 ID 和名称
- `PACKING_PROOF_HOST`：当前保存主机的地址和节点信息
- `PACKING_PROOF_CONNECT_TARGETS`：精确的 `@connect` 地址
- `PACKING_PROOF_UPDATE_URLS`：当前脚本的 `@updateURL` 和 `@downloadURL`
- `@version`：在保留 `X.Y` 的前提下追加当前设备配置修订号

缺少占位符的脚本仍可以导入和安装，但安装向导会显示警告，设备地址、`@connect` 或自动更新地址无法由桌面端自动维护。脚本可以自行实现地址配置，但必须仍然通过本接口发送数据。

兼容订单推送的最小示例：

```javascript
function pushOrders(host, accessKey, orders) {
  return new Promise((resolve, reject) => {
    GM_xmlhttpRequest({
      method: 'POST',
      url: `${host}/api/extensions/v1/orders`,
      headers: {
        'Content-Type': 'application/json',
        'X-EPM-Access-Key': accessKey
      },
      data: JSON.stringify({
        apiVersion: 'v1',
        providerId: 'example.adapter',
        orders
      }),
      onload: response => response.status === 200
        ? resolve(JSON.parse(response.responseText))
        : reject(new Error(`PackingProof HTTP ${response.status}`)),
      onerror: reject,
      timeout: 10000
    });
  });
}
```

订单字段中的商品名称、备注、留言和第三方编号都按普通数据处理。不要拼接 SQL、Shell、PowerShell、FFmpeg 参数或本地文件路径，也不要把外部输入拼接成脚本代码。

## 自定义脚本导入与发布流程

用户导入脚本时，PackingProof 会读取元数据、校验 `.user.js` 文件格式、检查 `X.Y` 版本和维护占位符，并对包含命令行、数据库或 FFmpeg 等高风险文本的脚本显示警告。警告不会自动执行脚本；只有用户明确确认后才会保存。

脚本保存在用户数据目录的 `userscripts` 子目录中，注册信息保存在同目录的 `registry.json`。导入相同 SHA-256 的脚本不会重复注册。脚本文件名、名称和命名空间会原样保留；官方“PackingProof 快递助手订单联动”也通过扩展市场安装，不随 Desktop 发布包内置。

从内置脚本迁移时，Desktop 仅识别两个历史下载入口和正在运行的官方脚本心跳。带版本号的官方心跳会触发 `packingproof.kdzs` 的静默市场登记；历史下载入口会返回已验签市场中的迁移版本，并改写为新的通用脚本下载地址。没有版本号的早期脚本无法可靠确认油猴身份，用户安装新版后必须删除“快递助手 → 打包监控联动”或“订单备注播报插件”，避免两份脚本重复推送。安装目录中的旧脚本文件可能只是 AppPatch 残留，不能作为已安装或正在使用的依据。

用户可在安装向导中分别安装官方脚本或任意已导入的第三方脚本。每个脚本都有独立下载地址：

```text
/api/userscripts/{scriptId}/download
```

下载响应是已经注入当前设备配置的 JavaScript，不是用户导入的原始文件。第三方脚本不得依赖固定的 `scriptId`；脚本 ID 由 PackingProof 为每个导入文件生成。

## 能力查询

```http
GET /api/extensions/v1/capabilities
```

返回当前主机支持的扩展能力和请求限制。生产扩展应检查：

```json
{
  "apiVersion": "v1",
  "extensionApiEnabled": true,
  "features": {
    "signedScanTasks": true,
    "recordingSearch": true,
    "recordingDownload": true,
    "recordingDelivery": true
  }
}
```

总开关关闭时能力查询仍可返回，但授权和签名接口返回 `extension_disabled`。

扩展 API 的启用状态、能力查询、服务启动方式和签名鉴权失败原因会默认写入自动轮转的 `runtime.log`，导出反馈信息时会一并收集，不需要用户提前打开“详细调试日志”。设置中的详细调试日志可能包含订单内容和文件路径，只应在需要复现业务数据或媒体处理问题时临时开启。

## 注册与授权

```http
POST /api/extensions/v1/enroll
Content-Type: application/json
```

```json
{
  "requestId": "enroll-0123456789abcdef01234567",
  "requestSecret": "64 位随机十六进制",
  "extensionInstanceId": "scale-device-0001",
  "providerId": "scale.example",
  "displayName": "示例称重设备",
  "version": "1.0",
  "source": "https://example.com/packingproof-adapter",
  "requestedPermissions": [
    "scan-tasks.read",
    "scan-results.write",
    "recording-fields.write"
  ],
  "requestedCapabilities": ["measurement.capture"]
}
```

`requestId`、`requestSecret` 和 `extensionInstanceId` 必须由扩展生成；不要使用机器名、IP、手机号或账号等个人信息。相同请求可在短时间内安全重试，但同一 `requestId + requestSecret` 不得改动申请内容。

成功响应中的 `credential` 只返回一次：

```json
{
  "apiVersion": "v1",
  "extensionInstanceId": "scale-device-0001",
  "credential": "...",
  "credentialGeneration": 1,
  "permissions": ["scan-tasks.read", "scan-results.write", "recording-fields.write"],
  "capabilities": ["measurement.capture"],
  "routingScope": "selected_recording_nodes",
  "boundOriginNodeIds": ["recording-node-001"]
}
```

扩展必须核对实际批准的权限、能力和绑定节点。用户拒绝时不要高频重复弹窗；建议至少等待 10 分钟或由用户主动重试。

完整的最小 JavaScript 客户端见 [`examples/extension-v1-minimal.js`](examples/extension-v1-minimal.js)。示例只包含注册、签名、心跳、任务领取、确认和结果提交，不包含任何 ERP 页面解析或业务系统代码，也不会自动保存凭据。

需要检索录像的机器人申请 `recordings.search` 和 `recordings.download`。需要由主机生成超限交付副本时，还应申请 `recordings.delivery`；该权限必须与录像查询、下载权限同时批准。用户批准后凭据由机器人保存在自己的受保护状态中，不应要求用户复制网页访问密钥，也不得把扩展凭据写进源码。

### 串口电子秤适配器示例

完整参考见 [`examples/extension-v1-serial-scale.js`](examples/extension-v1-serial-scale.js)。这是由第三方独立运行的 Node.js 程序，不会被 PackingProof 加载或执行。示例使用 `serialport` 连接电子秤，复用最小客户端完成授权、签名、扫码任务领取和重量提交：

```bash
cd docs/examples
npm install
set PACKINGPROOF_BASE_URL=http://127.0.0.1:5280
set PACKINGPROOF_SCALE_PORT=COM3
node extension-v1-serial-scale.js
```

示例采用常见英展 ASCII 连续输出格式：波特率 9600、数据位 8、停止位 1、无校验。每帧共 18 字节，最后两个字节为 `0D 0A`：

```text
ST,NT,+32.1000kg\r\n
```

| 字节位置 | 含义 |
|---|---|
| 1～2 | `ST` 表示稳定，`US` 表示不稳定 |
| 3、6 | ASCII 逗号 `2C` |
| 4～5 | `NT` 表示净重，`TR` 表示去皮状态 |
| 7～14 | 固定 8 字节重量文本，第 7 字节为正负号 |
| 15～16 | 单位，千克为 `kg`（`6B 67`） |
| 17～18 | 回车换行 `0D 0A` |

电子秤会连续输出，适配器只在内存中保留最近的稳定且大于零的重量，不会把每一帧都上传。PackingProof 扫码后主动发布 `measurement.capture` 任务；适配器确认任务，等待与本次扫码时间相邻的新鲜稳定值，再提交测量结果。超时仍没有稳定读数时提交 `timeout`，不会把上一件包裹的旧重量绑定到新单号。

测量结果中的 `deliveryId + taskId` 已经与 `trackingNumber`、`originNodeId` 和 `recordingSessionId` 绑定。测量任务的 `orders` 必须保持为空，第三方不能重复填写或替换快递单号；服务端会把重量写入扫码任务对应的订单和录像会话。

不同品牌电子秤通常只需替换 `parseYingzhanFrame`。串口号、主机地址和授权凭据均保存在运行环境或本机状态文件中，不应写死到源码或提交仓库。

## 心跳与在线状态

```http
POST /api/extensions/v1/heartbeat
```

此接口使用签名鉴权。正文示例：

```json
{
  "version": "1.2",
  "capabilities": ["order.lookup", "refund.lookup"],
  "lastSuccessfulActivityAt": "2026-08-23T06:20:18Z",
  "dataCount": 3
}
```

建议每 15 秒发送一次；45 秒没有心跳后管理界面显示离线。心跳只用于在线状态和运行版本，不会增加“收到 N 条数据”；业务活动以主机真正接受的结果为准。

## 扫码任务闭环

扩展使用签名长轮询领取任务：

```http
GET /api/extensions/v1/scan-tasks/next?waitSeconds=20
```

没有任务时返回 `204`。有任务时返回：

```json
{
  "deliveryId": "...",
  "taskId": "...",
  "originNodeId": "recording-node-001",
  "recordingSessionId": "...",
  "trackingNumber": "YT123456",
  "recordingMode": "发货",
  "capability": "order.lookup",
  "occurredAt": "2026-08-23T06:20:00Z",
  "softDeadline": "2026-08-23T06:20:08Z",
  "expiresAt": "2026-08-23T06:20:30Z",
  "deliveryAttempt": 1
}
```

先确认投递：

```http
POST /api/extensions/v1/scan-tasks/{deliveryId}/ack

{"taskId":"..."}
```

再提交结构化结果：

```http
POST /api/extensions/v1/scan-results
```

```json
{
  "deliveryId": "...",
  "taskId": "...",
  "providerId": "erp.example",
  "resultId": "result-0123456789abcdef",
  "revision": 1,
  "status": "found",
  "observedAt": "2026-08-23T06:20:05Z",
  "orders": [
    {
      "trackingNumber": "YT123456",
      "orderId": "ORDER-1",
      "buyerMessage": "请轻放",
      "sellerMemo": "",
      "totalItemCount": 3,
      "products": [
        {"name":"商品 A","sku":"A-1","merchantSku":"","quantity":3}
      ],
      "refundState": "none",
      "refundReason": ""
    }
  ],
  "measurements": []
}
```

可用能力：`order.lookup`、`refund.lookup`、`measurement.capture`。结果状态包括 `in_progress`、`found`、`not_found`、`completed`、`unavailable`、`provider_auth_required`、`rate_limited`、`timeout` 和 `invalid_request`。

`resultId + revision` 用于幂等和修订；重试相同内容不会重复应用，不得用同一修订号发送不同内容。晚于硬超时的任务返回 `410`，乱序或冲突修订返回 `409`。多台工位的任务必须按 `originNodeId` 和 `recordingSessionId` 保持隔离。

## 聊天机器人录像检索

聊天机器人应通过 PackingProof 查询录像，不能读取 `videos.db`，也不能获取本地或 NAS 真实路径。v1 支持机器人与 PackingProof 运行在同一台电脑或同一局域网；公网机器人需要另行部署安全中继，不在本接口范围内。

创建单号查询。主机先精确匹配订单标识；没有结果时，才在订单号、快递单号和来源订单号中按包含关系回退匹配。不会搜索文件名、备注、商品或其他非订单字段：

```http
POST /api/extensions/v1/recording-queries
Content-Type: application/json

{"trackingNumber":"YT123456"}
```

接口返回 `202` 和 `queryId`。机器人根据响应的 `Retry-After` 轮询：

```http
GET /api/extensions/v1/recording-queries/{queryId}
```

任务状态包括：

| 状态 | 含义 |
| --- | --- |
| `queued` | 已接收请求 |
| `searching` | 正在查询数据库 |
| `preparing` | 正在把仅存于 NAS 的录像准备到临时缓存 |
| `ready` | 至少一段录像可以下载 |
| `downloading` | 机器人正在下载 |
| `completed` | 所有可用录像下载完成 |
| `not_found` | 没有匹配的录像 |
| `failed` | 查询或文件准备失败 |
| `expired` | 临时任务已经过期 |

同一单号可能返回多段录像，每段录像有独立的 `status`、`progress`、`durationSeconds`、`fileSizeBytes`、`videoCodec`、`fileName` 和 `downloadUrl`。单次最多返回 20 段；超过时 `truncated=true` 并通过 `totalMatches` 告知完整数量。本地录像不会复制；仅存在于已确认 NAS 归档中的录像会先复制到 Web cache。某一段准备失败不会阻止其他已经就绪的录像下载。

```json
{
  "queryId": "0123456789abcdef0123456789abcdef",
  "trackingNumber": "YT123456",
  "status": "ready",
  "message": "录像可以下载",
  "totalMatches": 1,
  "truncated": false,
  "recordings": [
    {
      "recordingId": 42,
      "status": "ready",
      "progress": 100,
      "videoCodec": "h265",
      "fileSizeBytes": 12345678,
      "downloadUrl": "/api/extensions/v1/recording-queries/0123456789abcdef0123456789abcdef/recordings/42/download"
    }
  ],
  "expiresAt": "2026-08-23T08:00:00Z"
}
```

下载地址仍需使用 `recordings.download` 权限签名，不能把密钥放进 URL：

```http
GET /api/extensions/v1/recording-queries/{queryId}/recordings/{recordingId}/download
```

服务端返回原始录像，不自动转码。机器人可根据 `videoCodec` 决定聊天平台是否接受；下载中断后任务恢复为 `ready`，可以重新签名下载。查询和临时缓存默认保留一小时，真实文件路径、UNC 地址和 NAS 凭据永远不会出现在响应中。

### 超限录像交付副本

当原始录像超过聊天平台限制时，机器人可以根据自己的设置请求主机生成副本。机器人必须先用查询响应中的 `fileSizeBytes` 和 `durationSeconds` 判断是否需要请求；未超限时应继续下载原片。主机不接受 FFmpeg 命令行、码率或 CRF 等自由参数，只接受固定预设和目标大小：

```http
POST /api/extensions/v1/recording-queries/{queryId}/recordings/{recordingId}/deliveries
Content-Type: application/json

{"profile":"source_codec_target_size","maxFileSizeMb":190}
```

预设 `source_codec_target_size` 保持源视频编码并降低码率；`h265_target_size` 明确要求 H.265。目标大小范围为 1 到 200 MB。主机按实际时长和目标字节数计算本次转码码率，使用双遍编码并校验最终文件大小；无法压入限制时返回 `delivery_size_limit_unreachable`，不会切割录像或修改原片。

创建接口返回 `202` 和 `deliveryId`。机器人根据 `Retry-After` 轮询：

```http
GET /api/extensions/v1/recording-queries/{queryId}/recordings/{recordingId}/deliveries/{deliveryId}
```

状态为 `queued`、`transcoding`、`ready`、`downloading`、`completed`、`failed` 或 `expired`。`ready` 响应包含最终 `fileSizeBytes`、`durationSeconds`、`videoCodec`、`fileName` 和 `downloadUrl`。下载仍需签名：

```http
GET /api/extensions/v1/recording-queries/{queryId}/recordings/{recordingId}/deliveries/{deliveryId}/download
```

副本位于主机现有转码缓存目录的查询专用子目录，文件名为原文件名加 `_转码.mp4`，不使用哈希名。副本使用 `.partial` 原子发布并随查询任务在最长一小时后清理。

NAS 临时副本受 Web cache 容量限制；空间不足或单个录像超过限制时，该录像返回 `archive_cache_limit_exceeded`，不会删除原始录像或已确认归档。

## 推送订单

```http
POST /api/extensions/v1/orders
Content-Type: application/json
```

签名调用需要 `orders.write` 权限；使用网页访问密钥的旧脚本保持兼容。

请求示例：

```json
{
  "apiVersion": "v1",
  "providerId": "scale.example",
  "orders": [
    {
      "trackingNumber": "YT123456",
      "orderId": "ORDER-1",
      "productInfo": "商品 A",
      "totalItemCount": 3,
      "mergedOrderCount": 1,
      "buyerMessage": "请轻放"
    }
  ]
}
```

字段说明：

- `providerId`：第三方来源标识，必填，最长 128 个字符
- `totalItemCount`：订单内所有商品的总件数，缺失时按 0 处理
- `mergedOrderCount`：同一快递单号聚合的订单数量，缺失时按 0 处理
- `productInfo`、留言和备注只作为普通文本处理，不会作为命令执行

服务端会复用现有订单缓存、订单广播和录像订单快照逻辑。旧的 `/api/orderinfo` 接口继续保留，旧客户端无需升级。

## 录像动态水印数据

先查询正在录制的会话：

```http
GET /api/extensions/v1/recordings/active
```

签名调用需要 `recordings.active.read` 权限和当前录像节点授权。

向指定的活跃会话提交扩展字段：

```http
POST /api/extensions/v1/recordings/{recordingSessionId}/data
Content-Type: application/json
```

签名写入需要 `recording-fields.write` 权限和当前录像节点授权；签名读取已保存字段需要 `recordings.active.read` 权限。

```json
{
  "namespace": "scale.example",
  "providerId": "scale.example",
  "fields": {
    "weight": "1.25 kg",
    "length": "30 cm"
  }
}
```

同一会话、命名空间和字段名重复提交时，以最后一次提交的值为准。录像过程中到达的数据从后续帧开始显示，已经编码的画面不会回写。字段只会以固定文本行显示在水印中，第三方不能指定坐标、字体、绘制指令或 FFmpeg 参数。录像结束后仍可通过 `GET /api/extensions/v1/recordings/{recordingSessionId}/data` 读取已保存的字段，但不能继续写入。

扩展字段限制为单次最多 32 个，命名空间和字段名仅允许字母、数字、`.`、`_`、`-`，每个值最多 1000 个字符。接口同时支持旧版 Web 访问密钥和具备对应权限的扩展签名。

## 输入限制

- 单次最多 200 条订单
- 单次请求最多 1 MB
- 商品信息最长 4000 个字符
- 买家留言和卖家备注最长 2000 个字符
- 总件数范围为 0 到 100000
- 聚合订单数量范围为 0 到 200
- 单个扫码结果最多 50 条订单，每条最多 100 个结构化商品
- 单个扫码结果最多 8 个测量值
- 扩展标识、任务标识和结果标识只允许协议规定的字母、数字及有限分隔符

请求失败只拒绝当前请求，不会停止录像或删除已有订单数据。

## 稳定错误码

签名和授权相关错误至少包括：

- `extension_disabled`
- `extension_auth_required`
- `extension_auth_version_unsupported`
- `extension_auth_timestamp_stale`
- `extension_auth_revoked`
- `extension_auth_generation_mismatch`
- `extension_auth_content_hash_mismatch`
- `extension_auth_signature_invalid`
- `extension_auth_replay_detected`
- `extension_permission_denied`
- `extension_delivery_not_found`
- `extension_delivery_expired`
- `extension_result_conflict`
- `tracking_number_invalid`
- `recording_query_not_found`
- `recording_download_not_ready`
- `recording_delivery_not_found`
- `recording_delivery_not_ready`
- `recording_delivery_invalid`
- `delivery_duration_unavailable`
- `delivery_profile_unsupported`
- `delivery_ffmpeg_unavailable`
- `delivery_cache_limit_exceeded`
- `delivery_transcode_failed`
- `delivery_size_limit_unreachable`
- `archive_cache_limit_exceeded`

收到 `extension_auth_revoked` 或 `extension_auth_generation_mismatch` 后应停止使用旧凭据，由用户重新授权或录入轮换后的凭据；不要无限重试。

## 安全约束

第三方字段只能作为数据进入订单缓存和快照：

- 使用参数化数据库写入
- 不接受 SQL、Shell、PowerShell、FFmpeg 参数或文件路径
- Web 展示必须进行 HTML 转义
- 不允许第三方直接写 SQLite 或加载主程序插件代码
- 扩展返回的所有字符串都按数据处理，不会解释为 SQL、HTML、Shell、PowerShell、FFmpeg 或绘制指令
- 录像水印只使用主机固定布局和字体，第三方不能传入坐标、模板或可执行表达式
- 每个扩展只能领取符合已批准能力和绑定工位的任务；两台工位并发时按来源节点和录像会话隔离
