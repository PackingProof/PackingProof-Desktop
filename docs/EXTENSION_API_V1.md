# PackingProof 扩展接口 v1

扩展接口供 ERP、油猴脚本、称重程序等局域网适配器使用。第三方程序只能通过 HTTP API 提交数据，不得直接访问 PackingProof 的数据库、配置文件或执行目录。本文件同时约定第三方油猴脚本的导入、安装和维护规则。

后续的扩展授权、通用扫码任务、多扩展响应和结构化商品规划见 [第三方扩展 API 路线图](EXTENSION_API_ROADMAP.md)。路线图中的草案接口尚未全部实现，当前开发必须以本文件列出的 v1 接口为准。

## 快速开始

1. 在 PackingProof 的“设置 → 局域网查看”中点击“导入自定义脚本”，选择第三方的 `.user.js` 文件
2. 导入完成后打开“安装订单联动”，在脚本列表中选择该脚本安装
3. 脚本从安装向导提供的主机地址调用本接口，并在每次请求中发送访问密钥

PackingProof 不执行导入的脚本，也不会把脚本加载为桌面端插件。脚本只会被保存、检查、注入当前设备地址后提供给浏览器脚本管理器安装。

## 鉴权与请求约定

扩展接口的所有路径都纳入 Web 访问保护；当主机启用 Web 访问保护时，能力查询也需要鉴权。未启用访问保护的本机开发环境可以直接调用。推荐使用请求头传递密钥：

```http
X-EPM-Access-Key: <PackingProof 访问密钥>
Content-Type: application/json
```

也兼容在 URL 中使用 `?key=...`，但不建议这样做，因为 URL 可能出现在浏览器历史、代理或日志中。油猴脚本应使用 `GM_xmlhttpRequest` 并只请求安装向导写入的主机地址，不要把密钥写入公开仓库或页面文本。

成功响应为 JSON；失败响应包含 `error`，扩展接口还会尽量提供稳定的 `errorCode`。常见状态码：

| 状态码 | 含义 |
| --- | --- |
| `200` | 请求已处理 |
| `400` | JSON、字段或长度无效 |
| `401` | 缺少或无效的访问密钥 |
| `409` | 当前状态不允许操作，例如录像已结束 |
| `426` | 请求的扩展接口版本不受支持 |
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

最小的订单推送示例：

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

脚本保存在用户数据目录的 `userscripts` 子目录中，注册信息保存在同目录的 `registry.json`。导入相同 SHA-256 的脚本不会重复注册。第三方脚本的文件名、名称和命名空间会原样保留，官方脚本使用文件名 `PackingProof-Order-Integration-KDZS.user.js`，显示名为“PackingProof 快递助手订单联动”。

用户可在安装向导中分别安装官方脚本或任意已导入的第三方脚本。每个脚本都有独立下载地址：

```text
/api/userscripts/{scriptId}/download
```

下载响应是已经注入当前设备配置的 JavaScript，不是用户导入的原始文件。第三方脚本不得依赖固定的 `scriptId`；脚本 ID 由 PackingProof 为每个导入文件生成。

## 能力查询

```http
GET /api/extensions/v1/capabilities
```

返回当前主机支持的扩展能力和请求限制。启用 Web 访问保护时，扩展接口也必须携带现有访问密钥。

## 推送订单

```http
POST /api/extensions/v1/orders
Content-Type: application/json
```

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

向指定的活跃会话提交扩展字段：

```http
POST /api/extensions/v1/recordings/{recordingSessionId}/data
Content-Type: application/json
```

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

扩展字段限制为单次最多 32 个，命名空间和字段名仅允许字母、数字、`.`、`_`、`-`，每个值最多 1000 个字符。接口继续使用 Web 访问密钥保护。

## 输入限制

- 单次最多 200 条订单
- 单次请求最多 1 MB
- 商品信息最长 4000 个字符
- 买家留言和卖家备注最长 2000 个字符
- 总件数范围为 0 到 100000
- 聚合订单数量范围为 0 到 200

请求失败只拒绝当前请求，不会停止录像或删除已有订单数据。

## 安全约束

第三方字段只能作为数据进入订单缓存和快照：

- 使用参数化数据库写入
- 不接受 SQL、Shell、PowerShell、FFmpeg 参数或文件路径
- Web 展示必须进行 HTML 转义
- 不允许第三方直接写 SQLite 或加载主程序插件代码
