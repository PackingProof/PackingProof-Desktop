# 面单定向（YOLO-OBB，可选）

摄像头识别面单时，如果面单是歪着放的，一维码的条纹方向和 ZXing 的水平扫描线
对不上，条码就认不出来。程序内置了两条处理路径：

| 路径 | 依赖 | 适用 |
| --- | --- | --- |
| 梯度方向估计（默认，始终可用） | 无，纯 OpenCV | 画面里只有一个面单、背景相对干净 |
| **YOLO-OBB 面单定向**（本文档） | 模型 + ONNX Runtime，均需自备 | 画面里有多个方向不同的面单，或背景有规则斜纹 |

两条路径都只在常规解码失败后才触发，正常识别不增加开销。YOLO 不可用时自动退回梯度法，
不会报错，也不会让原本能识别的画面变差。

## 为什么模型和运行时都不随包

本项目的可选能力（例如 Kokoro TTS）一贯是「代码随包、模型与原生运行时由使用方自备」：
模型体积、训练数据的来源与许可、运行时版本都由部署方决定，不该固化进安装包。
`ExpressPackingMonitoring.csproj` 里的 `RemoveUnusedDlls` / `RemoveUnusedFromPublish`
会排除 `onnxruntime` 原生库，同理。

结果是：**装上就能用的功能不需要它们；需要它们的功能要自己准备。**

## 准备模型

程序期望一个 **YOLO-OBB 单类别**模型，输入固定 `1×3×640×640`，输出 `1×6×8400`：

```
通道 0..3 : cx, cy, w, h        （网络输入坐标系，像素）
通道 4    : 面单类别分数
通道 5    : 旋转角               （弧度）
```

这种布局是 ultralytics 的 OBB 头默认导出结果（`cat([preds, angle])`），
角度排在最后一个通道。

训练与导出的最小流程（ultralytics 版本自选，导出后按上面的布局核对一遍）：

```bash
# 1. 标注：每个面单一个旋转框，单类别（名字随意，程序只认"分数最高的框"）
#    数据集按 ultralytics OBB 格式：每行 "cls x1 y1 x2 y2 x3 y3 x4 y4"（归一化）

# 2. 训练
yolo obb train model=yolo11n-obb.pt data=label.yaml imgsz=640 epochs=100

# 3. 导出
yolo export model=runs/obb/train/weights/best.pt format=onnx imgsz=640 opset=17 simplify=True
```

导出后确认形状：

```python
import onnx
m = onnx.load("best.onnx")
print([d.dim_value for d in m.graph.input[0].type.tensor_type.shape.dim])   # [1, 3, 640, 640]
print([d.dim_value for d in m.graph.output[0].type.tensor_type.shape.dim])  # [1, 6, 8400]
```

> ⚠️ 评估模型请用 `model.val(...)`，**不要用 `model.predict(...)` 判断效果**：
> 实测 ultralytics 对 OBB 权重的 `predict` 会恒返回 0 个框（`conf=0.001` 也一样），
> 而同一份权重 `val` 出来的 mAP50 是正常的。

## 准备运行时

程序按文件名加载原生库，需要 `onnxruntime.dll` 与托管包
`Microsoft.ML.OnnxRuntime.dll` 版本匹配。最省事的做法：

```powershell
dotnet add package Microsoft.ML.OnnxRuntime --version 1.23.0
# 然后把 NuGet 缓存里的原生库拷到程序目录：
#   %USERPROFILE%\.nuget\packages\microsoft.ml.onnxruntime\1.23.0\runtimes\win-x64\native\onnxruntime.dll
```

运行时缺失时，`YoloObbDetector.TryLoad` 会捕获加载失败并返回 null，
识别链路退回梯度法 —— 日志里能看到一条 `面单检测模型加载失败`。

## 配置

设置 → 摄像头 → 识别面单 → 高级设置：

- **面单定向（可选）**：打开开关
- **面单定向模型**：选上面导出的 `.onnx`

两项都存在且模型能加载时才会生效；路径写错或文件被删会立刻退回梯度法，不需要重启。

## 实现位置

| 文件 | 职责 |
| --- | --- |
| `Services/YoloObbDetector.cs` | 模型加载、letterbox、推理、输出解析、坐标还原 |
| `Services/CameraBarcodeObbPolicy.cs` | 角度归一化、旋转框 IoU、重叠抑制（纯几何，可单测） |
| `Services/CameraBarcodeEnhancedDecoder.cs` | 兜底解码编排：优先用模型给的角度，拿不到就用梯度法 |
| `Services/CameraBarcodeRecognitionService.cs` | 按配置懒加载模型；路径变化时重载 |

letterbox 的口径必须和训练时一致，改这里之前先看 `YoloObbDetector` 里的注释：
填充值是 **114 灰**（不是黑色）、缩放用**最近邻**、填张量必须是 **NCHW 三平面**
（写成像素交错会让模型看到错乱的图，分类分直接掉到 0.00x）。
