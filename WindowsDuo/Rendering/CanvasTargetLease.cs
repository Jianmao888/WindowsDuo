using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace WindowsDuo.Rendering;

/// <summary>
/// 复用离屏画布。每帧新建一个全屏 <see cref="CanvasRenderTarget"/> 会不断申请/释放 GPU
/// 表面，既浪费又容易造成卡顿，所以只在尺寸 / DPI / 设备变化时才重建。
/// </summary>
internal sealed class CanvasTargetLease : IDisposable
{
    private CanvasRenderTarget? _target;
    private CanvasDevice? _device;
    private float _width;
    private float _height;
    private float _dpi;

    /// <summary>按画布当前的尺寸与 DPI 借出一块离屏画布（尺寸以 DIP 计）。</summary>
    public CanvasRenderTarget Rent(CanvasControl sender, float width, float height)
        => Rent(sender.Device, width, height, sender.Dpi);

    /// <summary>
    /// 按指定的 DPI 借出离屏画布。显式传 DPI 是为了让叠加层能在比屏幕更低的分辨率上
    /// 渲染整套级联，同时保持 DIP 尺寸不变（上采样由最后的 DrawImage 完成）。
    /// </summary>
    public CanvasRenderTarget Rent(CanvasDevice device, float width, float height, float dpi)
    {
        if (_target is null
            || !ReferenceEquals(_device, device)
            || Math.Abs(_width - width) > 0.5f
            || Math.Abs(_height - height) > 0.5f
            || Math.Abs(_dpi - dpi) > 0.5f)
        {
            _target?.Dispose();

            // 必须按目标 DPI 创建：用 96 DPI 建出来的是 1x 像素图，在高缩放屏幕上会被
            // 放大，文字在进入着色器之前就已经糊了一遍，模糊效果也就无从分辨。
            _target = new CanvasRenderTarget(device, width, height, dpi);
            _device = device;
            _width = width;
            _height = height;
            _dpi = dpi;
        }

        return _target;
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
        _device = null;
    }
}
