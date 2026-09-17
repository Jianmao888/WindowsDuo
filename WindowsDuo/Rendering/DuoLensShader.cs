using System;
using System.IO;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace WindowsDuo.Rendering;

/// <summary>
/// 对 <see cref="PixelShaderEffect"/> 的薄封装：负责加载 <c>Shaders\DuoLens.bin</c>（由构建期的
/// <c>DuoCompileShaders</c> 目标用 fxc 生成）并把 <see cref="DuoLensFrame"/> 写进常量缓冲。
/// </summary>
/// <remarks>
/// 关键约定（与 DuoLens.hlsl 强耦合）：
/// <list type="bullet">
/// <item><c>Properties</c> 的键名必须与 HLSL 全局变量名逐字相同。</item>
/// <item>着色器把采样点搬到任意位置（透视重投影 + 磁盘模糊），因此
/// <c>Source1Mapping</c> 必须是 <see cref="SamplerCoordinateMapping.Unknown"/>。</item>
/// <item>自定义像素着色器工作在同一"场景像素"空间，所有长度都以输出像素为单位。</item>
/// </list>
/// </remarks>
internal sealed class DuoLensShader : IDisposable
{
    private const string ShaderFileName = "DuoLens.bin";

    private static byte[]? _cachedBytecode;

    private readonly PixelShaderEffect _effect;
    private ICanvasImage? _source;

    public DuoLensShader(ICanvasResourceCreator device)
    {
        ArgumentNullException.ThrowIfNull(device);

        _effect = new PixelShaderEffect(LoadShaderBytecode())
        {
            Name = "DuoLens",
            Source1Mapping = SamplerCoordinateMapping.Unknown,
            Source1BorderMode = EffectBorderMode.Hard,
            Source1Interpolation = CanvasImageInterpolation.Linear,
            CacheOutput = false,
        };

        // 在创建阶段拒绝旧字节码，让调用方既有的降级逻辑接住错误。
        string[] required = ["eyeDistanceMm", "pixelsPerMm", "planeOriginPx", "planeSizePx",
            "contentAxisX", "contentAxisY", "contentCenterPx", "blurScale", "maxBlurPx",
            "darkenScale", "maxDarken", "depthBiasMm", "edgeFeatherPx", "projectionEnabled"];
        foreach (string name in required)
        {
            if (_effect.Properties.ContainsKey(name))
                continue;
            _effect.Dispose();
            throw new InvalidOperationException($"着色器缺少属性 {name}，请重新构建 DuoLens.bin。");
        }
    }

    /// <summary>可以直接交给 <c>CanvasDrawingSession.DrawImage</c> 的效果图。</summary>
    public ICanvasImage Effect => _effect;

    /// <summary>界面内容（被"钉"在界面平面上的那一层）。</summary>
    public ICanvasImage? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value))
                return;

            _source = value;
            _effect.Source1 = value;
        }
    }

    /// <summary>把一帧的解析结果写入 HLSL 常量缓冲。</summary>
    public void Apply(in DuoLensFrame frame)
    {
        Set("eyeDistanceMm", frame.EyeDistanceMm);
        Set("pixelsPerMm", frame.PixelsPerMm);
        Set("planeOriginPx", new System.Numerics.Vector2(0f, 0f));
        Set("planeSizePx", new System.Numerics.Vector2(frame.PlaneWidthPx, frame.PlaneHeightPx));
        Set("contentAxisX", frame.ContentAxisX);
        Set("contentAxisY", frame.ContentAxisY);
        Set("contentCenterPx", frame.ContentCenterPx);
        Set("blurScale", frame.BlurScale);
        Set("maxBlurPx", frame.MaxBlurPx);
        Set("darkenScale", frame.DarkenScale);
        Set("maxDarken", frame.MaxDarken);
        Set("depthBiasMm", frame.DepthBiasMm);
        Set("edgeFeatherPx", frame.EdgeFeatherPx);
        Set("projectionEnabled", frame.ProjectionEnabled);
    }

    private void Set(string name, object value)
    {
        _effect.Properties[name] = value;
    }

    private static byte[] LoadShaderBytecode()
    {
        if (_cachedBytecode is { } cached)
            return cached;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Shaders", ShaderFileName),
            Path.Combine(AppContext.BaseDirectory, ShaderFileName),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return _cachedBytecode = File.ReadAllBytes(candidate);
        }

        throw new FileNotFoundException(
            $"找不到已编译的着色器字节码 {ShaderFileName}（已尝试：{string.Join("、", candidates)}）。" +
            "请在 Visual Studio 或命令行完整构建一次，让 DuoCompileShaders 目标用 fxc 生成它。");
    }

    public void Dispose() => _effect.Dispose();
}
