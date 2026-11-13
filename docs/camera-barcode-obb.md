# 增强面单识别（YOLO-OBB）

增强面单识别是可选的摄像头识别路径。开关打开后：

YOLO-OBB 默认随应用发布：主程序依赖
`ExpressPackingMonitoring.Vision.Contracts`，ONNX Runtime、DirectML、模型加载和输出解析位于
`ExpressPackingMonitoring.Vision.Obb`。默认构建、测试和发布都包含 YOLO11n OBB 模型；
只有明确需要精简包时才排除：

```powershell
pwsh -NoProfile -File tools\Publish-CleanPackage.ps1 -VisionObb exclude
```

也可以运行 `打包脚本-增量.bat <版本> 0.0.84 novision`。显式排除时设置页隐藏相关选项，
普通 ZXing 识别、倾斜码重试、扫码枪和录像流程不受影响。注意 `org.k2fsa.sherpa.onnx`
仍可能为本地语音功能携带另一份 `onnxruntime.dll`；它与 YOLO 功能包无关。

默认包含视觉功能包的本地验证：

```powershell
pwsh -NoProfile -File tools\Test-CI.ps1
pwsh -NoProfile -File tools\Test-Release.ps1 -Configuration Release
pwsh -NoProfile -File tools\Test-Release-Automated.ps1
```

显式传 `-VisionObb exclude` 时不会编译或引用
`ExpressPackingMonitoring.Vision.Obb`，OBB 专属测试会从测试工程排除；
普通扫码、透视兜底、业务规则、WPF 和 Web 测试继续执行。

1. 从摄像头完整画面中按用户设置的识别区域裁出局部图像，只把这块图像送入
   单类别 YOLO-OBB 模型
2. 模型定位面单后得到旋转框，并把检测坐标加回识别区域偏移，恢复为完整画面坐标
3. 旋转框离最近的 90° 不超过 `10°` 时直接裁轴对齐外接框并快速解码；
   超过 `10°` 时按四点透视裁正后再交给现有 ZXing 解码器
4. 小角度快速解码失败后直接放弃该候选，不使用慢速扫描或整帧兜底
5. 所有 OBB 候选都失败后，不论是否存在检测框，都先回到原始取景区域执行普通 ZXing；
   检测框不能阻断原图兜底
6. 开启增强面单识别时，原图 ZXing 也失败就直接结束，不再进入 OpenCV 轮廓兜底
7. 把旋转框画在预览画面上

开关关闭时完全不加载模型，也不执行 OBB 推理。

## 关闭增强面单时的 OpenCV 透视兜底

关闭增强面单识别时，普通引导框 ZXing 没有解出条码，每 `300ms` 最多再执行一次轻量透视兜底：

1. 只处理用户设置的识别区域，不扫描识别区域外的画面
2. 用 Canny 边缘轮廓和最小外接旋转矩形提取候选面单，不加载任何神经网络
3. 对面积最大的若干候选做四点透视裁正
4. 先直接交给现有 ZXing；失败后在候选内部用 Hough 估计主方向，并尝试估角及左右 `5°`
5. 解码结果仍要通过原单号、指令码和商品条码过滤规则，不会因为兜底绕过业务校验

该路径用于未开启增强面单的普通识别，不会替代 OBB 的连续框选和智能跟随。
开启增强面单后只使用 OBB 裁图与原取景框 ZXing；两者都失败时不执行轮廓兜底。

增强模式只处理固定识别区域，不再把整帧送入模型。它会复用原有取景框的裁剪与
旋转换算，模型输出坐标再映射回完整预览画面，避免区域外相似面单造成误识别。

识别区域本身按增强开关显示两种样式：

- 关闭增强识别：保留原来的完整虚线框
- 开启增强识别：不画完整边框，只显示四个圆角 L 角和四边中点短线
- 解锁编辑取景区域时：恢复完整可拖动框和四角把手

模型检测到的面单仍按完整旋转框绘制。检测框使用 `220ms` 三次缓出动画平滑移动到
新位置；新框淡入、消失框淡出。

检测框使用轻量级短时轨迹跟踪：按预测位置、框重叠率和尺寸做最多
`6×6` 的一对一匹配。目标短暂遮挡后，轨迹会保留 `1.5 秒` 并按速度预测位置，
重新出现时优先接回原框，避免两个面单交换身份或突然跳成新框。

同一帧的候选框先按旋转矩形 IoU 和包含率去重，而不是用外接轴对齐矩形估算。
这样两张交叉但方向不同的面单不会被误删，同一张面单产生的近似重复框仍会合并。

框颜色沿用摄像头识别状态：

- 蓝色：仅检测到面单位置和角度
- 黄色：当前选中并识别出待确认单号的那一个框
- 绿色：最终确认单号所对应的那一个框

其他同时存在的面单框保持蓝色，不会跟着一起变色。

## 多张面单的候选选择

同一帧里解出多个有效单号时，不按模型输出顺序直接取第一个，而是为每个候选计算：

| 因素 | 权重 | 归一化方式 |
| --- | ---: | --- |
| 旋转框面积 | 45% | 当前框面积 / 候选中的最大框面积 |
| 模型置信度 | 35% | `(分数 - 0.35) / (1 - 0.35)` |
| 中心接近度 | 20% | `1 - 框中心到识别框中心的距离 / 识别框中心到角的距离` |

同一订单的裸单号与包裹码先归为一组，组内选最高分；不同订单之间再按总分选择最高者。
总分相同时才用原始模型置信度兜底。

## 运行时

安装包随附 `Microsoft.ML.OnnxRuntime.DirectML` 1.23.0 和 DirectML 1.15.4：

- Windows 上优先使用 DirectML 调用 NVIDIA、AMD 或 Intel 显卡
- DirectML Session 初始化失败时自动回退 CPU
- 不要求用户安装 CUDA 或 cuDNN

发布包不包含 `DirectML.Debug.dll` 和 PDB。

## 权重

模型二进制不提交到 Desktop Git 仓库，而是由独立的 `PackingProof-Vision` 训练仓库
随其 Release 发布，并由 `tools\vision-model-baseline.json` 锁定版本、仓库地址和哈希。
Desktop 的 Release 不承载模型二进制。模型资产和运行时统一使用角色文件名
`vision-obb.onnx`，训练批次只记录在 Release 标签、锁定清单和 ONNX 元数据中。
基线清单同时锁定训练批次、文件大小、SHA256 和下载地址；
`tools\Prepare-PinnedVisionModel.ps1` 在 CI、发布和本地准备阶段下载并校验，
模型缺失或哈希不一致会直接阻断构建。工程和用户替换统一使用稳定的角色文件名，
不把训练批次编进路径。程序优先加载：

```
<安装目录>\app\models\vision-obb.onnx
```

开发环境或没有内置模型的包会回退到：

```
%LOCALAPPDATA%\ExpressPackingMonitoring\models\vision-obb.onnx
```

普通设置页不再暴露模型路径。新安装默认不先开启增强识别，而是在主界面加载后运行一次
本机 benchmark：

- 同一批空画面、单码、双码、六码、二维码、倾斜面单、假框和裸面单场景分别测量：
  - 完全不走 OBB：普通引导框 ZXing，失败后执行 OpenCV 透视兜底
  - 开启 OBB：模型检测、旋转裁图与 ZXing 解码
- 两组都取所有场景中的 P95 最大值，日志明确分别输出 `noObbP95` 和 `enhancedP95`
- benchmark 按每秒识别 4 次计算，每帧预算 `250ms`；保留 30% 余量后，墙钟延迟
  p95 必须不超过 `175ms`
- DirectML 路径达到延迟门槛即可自动开启；CPU 路径还必须让每帧累计 CPU 时间不超过
  `175ms`，也就是最多约占一个 CPU 核心的 70%
- 未达到自动开启条件时保持关闭，用户仍可在设置中手动开启
- 结果和模型身份写入配置；同一模型版本不会重复自动检测，之后以用户手动开关为准

OBB 候选进入 ZXing 前同时检查绝对像素和画幅占比：

- 以 1080p 为参考，候选框对角线至少为 `300px`
- 1080p 完整画面对角线为 `sqrt(1920² + 1080²) = 2202.91px`，
  因此统一阈值为 `300 / 2202.91 = 13.62%`
- 同一比例在 1280×720 下约为 `200px`，在 2560×1440 下约为 `400px`
- 一维条码和二维码继续保留各自的最低可解码像素要求，同时都必须达到上述画幅占比
- 百分比始终用候选框对角线除以**完整摄像头画面对角线**，不是除以取景框对角线
- 采样单独写入 `%LOCALAPPDATA%\ExpressPackingMonitoring\log\vision_obb_diagnostics.log`，
  不混入 `runtime.log`；每行记录候选对角线像素、占完整画面对角线百分比、置信度、
  是否通过阈值以及是否解码成功；相同候选在 1.5 秒内不重复记录

识别触发录像后的结果单独写入
`%LOCALAPPDATA%\ExpressPackingMonitoring\log\vision_obb_trigger_events.log`：

- 同一次触发使用相同事件 ID，记录识别触发、录像真正开始、停止请求和录像结束时间
- 停止来源区分 `manual_button`、同码停录、停止命令和扫码切换
- 同时记录实际录像时长、含预录的文件时长、配置的最短录像时长，以及是否属于短录像
- 手动停止标记和短录像标记用于统计“识别触发后很快被人工停掉”的误触嫌疑

实际训练版本写在 ONNX 的 `metadata_props` 中，由训练脚本自动维护：

- `packingproof.model`：模型名称、任务和基础模型
- `packingproof.training`：训练批次、轮次和关键参数
- `packingproof.dataset`：训练数据包名、数据指纹和样本统计
- `packingproof.evaluation_summary`：当前模型在独立测试和留出集上的对比指标
- `packingproof.independent_evaluation`：旧版单模型独立测试元数据，保持向后兼容

“导出诊断日志”同时兼容新旧评估元数据，在 `feedback-info.txt` 中显示实际模型名、
训练批次、数据指纹和独立测试指标。用户自行替换的模型如果没有 PackingProof 元数据，
会明确标记，不会把旧版本信息误当成当前模型。

模型要求：

- 默认随包权重为单类别 `Waybill`
- 输入 `1×3×640×640`
- 输出 `1×6×8400`
- 每通道依次为 `cx, cy, w, h, classScore, angle`
- letterbox 填充值 `114`，缩放使用最近邻

实验模型可通过 ONNX 元数据 `packingproof.output_layout=yolox_obb` 声明 YOLOX-OBB 契约：

- 输出 `1×8400×7`
- 每行依次为 `cx, cy, w, h, angle, objectness, classScore`
- 最终置信度为 `objectness × classScore`
- `angle` 转换到 C# 图像坐标的旋转方向后再用于预览、裁图和旋转 NMS
- 预处理按 YOLOX 训练参数使用左上角对齐、双线性缩放、`114` 填充和 `0–255` 像素值
- 可选 `packingproof.score_threshold`、`packingproof.nms_threshold` 覆盖运行时默认阈值

未声明该元数据时继续使用上面的 Ultralytics 默认契约，现有基线模型行为不变。

RTMDet-R 的旋转头可通过 `packingproof.output_layout=rtmdet_obb` 接入：

- 导出时将 `bboxes` 与 `scores` 合并为 `1×8400×6`
- 每行依次为 `cx, cy, w, h, angle, score`
- 预处理使用左上角对齐、双线性缩放、`114` 填充，
  并按 BGR 通道执行 RTMDet 的均值/标准差归一化
- 未内置的 mmdeploy 自定义 NMS 不进入 ONNX，由运行时统一执行旋转框去重

解析层保存每个检测的类别 ID，不把类别固定成 `Waybill`。替换成多类别模型时，至少要保持
上述输入输出张量契约；当前面单链路是第一个消费方，以后商品识别可以直接复用同一份 OBB
检测结果。

模型缺失或加载失败时只记录日志，并回退到引导框识别；全部失败后再进入受
`300ms` 节流限制的无模型透视兜底。兜底只扫描引导区域，不会扫描整帧。这样在空针、
遮挡或模型漏检时仍有有限次数的倾斜面单重试，同时避免每帧都重复执行高成本轮廓与 Hough。
显式使用 `-VisionObb include` 时，正式 Setup、Store MSIX 和 AppPatch 都会校验
`models\vision-obb.onnx`、`onnxruntime.dll` 与 `DirectML.dll` 是否进入实际应用负载；
默认的无功能包构建则要求这些文件不存在。
GitHub 发布工作流默认不下载模型；只有手动触发并勾选 `vision_obb` 时才按同一规则包含功能包。

## 训练来源

训练代码、数据准备、评估和权重发布由独立的 `PackingProof-Vision` 仓库维护。当前训练
底座从 Ultralytics YOLO11n-OBB 基础权重开始迭代，支持 AnyLabeling JSON 与 YOLO TXT
标注、按来源录像分组切分、可选负样本比例限制，以及训练和评估元数据写入模型。
Desktop 只下载并校验锁定后的 Release 资产，不包含训练流程或样本数据。
Ultralytics 工具链采用 AGPL-3.0，模型正式分发前仍需按发布方式复核许可证义务。

## 后续扩展边界

- 自定义模型：替换 `models\vision-obb.onnx`，不需要改检测器和坐标转换代码，但必须保持
  相同的输入输出张量契约
- 商品识别：建立独立模型配置，读取同一套带类别 ID 的旋转框结果
- 装箱核对：订单期望商品与识别结果做独立匹配，不把映射规则塞进相机识别服务
