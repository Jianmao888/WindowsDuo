# WindowsDuo

> 一个受 iPhone Duo 动画启发的 Windows 桌面空间透视实验。

WindowsDuo 将 Windows 桌面处理成具有空间深度的透视画面。当 Surface 平板转动时，屏幕内容仿佛固定在空间中的一个平面上：远离观察者的一侧缩小，靠近观察者的一侧放大，距离屏幕底边越远的区域虚化和变暗越明显。

项目基于 WinUI 3、Win2D、Windows.Graphics.Capture 和自定义 Direct2D 像素着色器实现。

> **由 AI 协作构建。** 本项目的设计、实现、调试和验证均经过 AI 辅助完成，实际发布前仍建议进行人工审查和真实设备调校。

## 功能特性

- 捕获主显示器画面并进行实时处理。
- 使用透视投影模拟画面固定在空间中的效果。
- 旋转轴固定为屏幕底边，只有俯仰 X 轴驱动画面变换。
- 左右旋转、滚转和铰链开合读数不会改变画面几何。
- 根据玻璃平面与内容平面的距离施加磁盘模糊和变暗效果。
- 提供 WinUI 参数面板，可调整视点距离、模糊、变暗、边缘羽化和渲染倍率。
- 支持点击穿透、置顶的整屏叠加层。
- 提供 `Ctrl+Alt+D` 全局快捷键，用于显示或隐藏叠加层。
- 没有可用姿态传感器时，自动使用内置演示动画。

## 工作原理

程序将桌面画面视为固定在参考平面上的内容。着色器从一个虚拟观察点向每个输出像素发射射线，并计算射线与旋转后内容平面的交点，以得到正确的纹理采样位置。

透视缩放关系为：

```text
透视缩放 = 视点距离 / (视点距离 - 有符号深度)
```

内容平面只绕屏幕底边这条水平轴旋转。完成透视采样后，着色器根据平面距离计算模糊半径，并同步降低亮度，从而模拟玻璃层逐渐离开界面的效果。

CPU 侧的投影模型位于 `Rendering/DuoLensMath.cs`，与 HLSL 着色器使用同一套射线求交公式，确保诊断数据和实际画面保持一致。

## 运行要求

- Windows 11，系统版本 22621 或更高
- Visual Studio 2022，安装 .NET 桌面开发工作负载
- Windows SDK，包含 `fxc.exe` 和 `d2d1effecthelpers.hlsli`
- 建议使用带姿态传感器的 Surface 或其他 Windows 设备进行实时体验

没有姿态传感器时仍然可以使用演示动画查看效果。

## 构建和运行

使用 Visual Studio 打开 `WindowsDuo.slnx`，选择 `x64` 配置进行构建。构建过程会将 `Shaders/DuoLens.hlsl` 编译为 `Shaders/DuoLens.bin`，源文件和编译后的着色器都属于应用运行所需的资源。

也可以使用命令行构建：

```powershell
msbuild WindowsDuo/WindowsDuo.csproj /t:Build /p:Configuration=Debug /p:Platform=x64
```

然后从 Visual Studio 启动，或运行生成的 `WindowsDuo.exe`。

程序默认启动整屏叠加层。可用的启动参数如下：

```text
--no-overlay              只启动控制面板，不显示整屏叠加层
--scale 0.5               使用 50% 分辨率渲染叠加层
--passthrough             绕过着色器，仅验证捕获和合成通路
--selfcheck               启动后自动运行叠加层通路自检
--no-capture-exclusion    诊断模式，允许叠加层出现在自身捕获画面中
```

当桌面被叠加层覆盖时，可以使用 `Ctrl+Alt+D` 显示或隐藏叠加层。

## 校准和调参

1. 关闭“演示动画”。
2. 将 Surface 放在希望作为中性姿态的位置。
3. 点击“将当前姿态设为空间参考”。
4. 让设备围绕屏幕底边进行小幅俯仰。

控制面板提供视点距离、每毫米像素数、模糊扩散系数、最大模糊半径、变暗系数、边缘羽化和叠加层渲染倍率等参数。如果设备上的运动方向与画面方向相反，可以启用“翻转俯仰方向（X）”。

为了检查几何效果，可以暂时将模糊和变暗参数设为 0。此时画面底边应保持固定，另一侧的尺寸会根据有符号深度发生变化。

## 测试

项目包含一个不依赖额外测试框架的几何测试项目，以及一个可选的、基于 WARP 的 Direct2D 着色器测试。

运行几何回归测试：

```powershell
dotnet run --project Tests/WindowsDuo.GeometryTests.csproj --configuration Release
```

先构建主项目，然后运行实际着色器验证：

```powershell
dotnet run --project Tests/WindowsDuo.GeometryTests.csproj `
  --configuration Release --property:GpuChecks=true
```

测试覆盖远近缩放、底边固定支点、逆向投影、非 X 轴隔离、无效交点、DPI 和渲染倍率一致性，以及两遍模糊流程。

## 项目结构

```text
WindowsDuo/
├─ Rendering/     投影模型、着色器封装和帧参数
├─ Shaders/       DuoLens HLSL 源码与编译后的着色器字节码
├─ Sensors/       姿态传感器、加速度计、铰链和演示动画
├─ Capture/       Windows.Graphics.Capture 屏幕捕获
├─ Overlay/       整屏点击穿透叠加窗口
├─ Tests/         几何测试和可选着色器回归测试
└─ MainWindow.*   WinUI 预览窗口和调参面板
```

## 当前限制

- 当前效果只补偿设备旋转，不跟踪观察者的头部或眼睛位置。
- 当前不跟踪设备平移，因此它是视觉透视效果，并不是完整的 AR 空间锚定。
- 只有加速度计时，可以恢复相对于重力的倾斜，但无法单独观测绕重力方向的旋转。
- Windows.Graphics.Capture 和无边框捕获权限受系统设置及应用清单能力影响。
- 叠加层存在少量捕获与合成延迟，因为 Windows 没有提供直接修改桌面合成结果的公开接口。

## 许可证

本仓库目前尚未选择许可证。如果计划分发项目或接受外部贡献，请先添加合适的开源许可证。
