#if GPU_CHECKS
using Microsoft.Graphics.Canvas;
using Windows.UI;
using WindowsDuo.Rendering;

internal static class GpuProjectionChecks
{
    public static void Run()
    {
        // WARP executes the actual Direct2D shader even on a machine without a usable GPU.
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var shader = new DuoLensShader(device);
        foreach (float dpi in new[] { 96f, 192f })
        foreach (float quality in new[] { 1f, 0.35f, 0.25f })
        foreach (float degrees in new[] { -30f, 0f, 30f })
        {
            float effectiveDpi = dpi * quality;
            using var source = new CanvasRenderTarget(device, 1280, 720, effectiveDpi);
            using (var drawing = source.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(255, 0, 0, 0));
                drawing.FillRectangle(100, 80, 1080, 60, Color.FromArgb(255, 255, 0, 0));
                drawing.FillRectangle(100, 580, 1080, 60, Color.FromArgb(255, 0, 100, 255));
            }
            var parameters = new DuoLensParameters { BlurScale = 0, DarkenScale = 0, DepthBiasPerTiltMm = 0 };
            var frame = DuoLensMath.BuildFrame(parameters, new(degrees * Math.PI / 180, 0, 0, 0),
                1280, 720, dpi, 0, quality);
            using var target = new CanvasRenderTarget(device, 1280, 720, effectiveDpi);
            shader.Source = source;
            shader.Apply(frame);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(255, 0, 0, 0));
                drawing.DrawImage(shader.Effect);
            }
            // Independent analytic forward projection of the red stripe's midline.
            float pixelScale = effectiveDpi / 96;
            float angle = -degrees * MathF.PI / 180;
            float x = -540 * pixelScale;
            float y = (-360 + 610 * MathF.Cos(angle)) * pixelScale;
            float z = 610 * MathF.Sin(angle) * pixelScale;
            float eye = frame.EyeDistanceMm * frame.PixelsPerMm;
            float perspective = eye / (eye - z);
            int row = (int)MathF.Round(360 * pixelScale - y * perspective);
            float expectedWidth = -2 * x * perspective;
            Color[] pixels = target.GetPixelColors();
            int width = (int)target.SizeInPixels.Width;
            int left = width, right = -1;
            for (int col = 0; col < width; col++)
            {
                Color color = pixels[row * width + col];
                if (color.R > 180 && color.G < 40 && color.B < 40)
                {
                    left = Math.Min(left, col);
                    right = col;
                }
            }
            if (right < left || Math.Abs(right - left + 1 - expectedWidth) > 4)
                throw new Exception($"Shader perspective mismatch: dpi={dpi}, quality={quality}, angle={degrees}; "
                    + $"expected stripe width {expectedWidth:F2}, got {right - left + 1}");

            // Second pass with zero blur must not project the already transformed scene again.
            using var second = new CanvasRenderTarget(device, 1280, 720, effectiveDpi);
            shader.Source = target;
            shader.Apply(frame with { ProjectionEnabled = 0 });
            using (var drawing = second.CreateDrawingSession())
                drawing.DrawImage(shader.Effect);
            var secondPixels = second.GetPixelColors();
            for (int col = left + 2; col < right - 2; col++)
                if (secondPixels[row * width + col].R < 180)
                    throw new Exception("Composite pass reapplied projection.");
            Console.WriteLine($"PASS Direct2D shader dpi={dpi}, quality={quality}, angle={degrees}, stripe={right - left + 1}px");
        }
    }
}
#endif
