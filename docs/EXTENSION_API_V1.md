# PackingProof 扩展接口 v1

扩展接口供 ERP、油猴脚本、称重程序等局域网适配器使用。第三方程序只能通过 HTTP API 提交数据，不得直接访问 PackingProof 的数据库、配置文件或执行目录。

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
