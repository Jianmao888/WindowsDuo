using System.Numerics;
using WindowsDuo.Rendering;

int passed = 0;
Run("Zero pose is identity at all sampled pixels", () =>
{
    var f = Frame(DuoAttitude.Flat);
    Check(!DuoLensMath.IsActive(f), "zero pose must bypass the shader");
    for (int y = 0; y <= 720; y += 45)
        for (int x = 0; x <= 1280; x += 80)
            Near(DuoLensMath.ProjectScreen(new(x, y), f).SourcePx, new(x, y));
});
Run("Far top edge shrinks; near top edge grows; hinge stays fixed", () =>
{
    foreach (float deg in new[] { 5, 15, 30, 50 })
    {
        var far = Frame(new(Rad(deg), 0, 0, 0));
        var near = Frame(new(-Rad(deg), 0, 0, 0));
        Check(WidthAt(far, 0) < far.PlaneWidthPx, "far edge grew");
        Check(WidthAt(near, 0) > near.PlaneWidthPx, "near edge shrank");
        Check(Forward(far, new(640, 0)).Y > 0, "far top did not recede downwards");
        foreach (var f in new[] { far, near })
            foreach (float x in new[] { 0, 640, 1280 })
                Near(Forward(f, new(x, 720)), new(x, 720));
    }
});
Run("Perspective obeys eye / (eye - signed depth)", () =>
{
    var f = Frame(new(Rad(30), 0, 0, 0));
    float eye = f.EyeDistanceMm * f.PixelsPerMm;
    float depth = (f.ContentCenterPx + f.ContentAxisY * 360).Z;
    Near(WidthAt(f, 0) / 1280, eye / (eye - depth));
    Check(depth < 0, "far plane must have negative Z");
});
Run("Forward projection and inverse texture lookup round-trip", () =>
{
    foreach (float x in new[] { -40, -15, 0, 15, 40 })
    {
        var f = Frame(new(Rad(x), Rad(30), Rad(45), 1) { TiltZ = Rad(25) });
        foreach (var uv in new[] { new Vector2(80, 80), new(640, 360), new(1200, 640) })
        {
            var p = DuoLensMath.ProjectScreen(Forward(f, uv), f);
            Check(p.Valid, "visible point was rejected");
            Near(p.SourcePx, uv, 0.015f);
        }
    }
});
Run("Only X tilt can rotate the content plane", () =>
{
    var baseline = Frame(DuoAttitude.Flat);
    foreach (var ignored in new[]
    {
        new DuoAttitude(0, Rad(40), 0, 0),
        new DuoAttitude(0, 0, Rad(45), 1),
        new DuoAttitude(0, Rad(-35), Rad(-30), 0.7) { TiltZ = Rad(55) },
    })
    {
        var f = Frame(ignored);
        Near(f.ContentAxisX, baseline.ContentAxisX);
        Near(f.ContentAxisY, baseline.ContentAxisY);
        Near(f.ContentCenterPx, baseline.ContentCenterPx);
        Check(!DuoLensMath.IsActive(f), "an ignored axis activated the shader");
    }
});
Run("X tilt keeps every point on the bottom edge fixed", () =>
{
    foreach (float xTilt in new[] { -50f, -20f, 20f, 50f })
    {
        var f = Frame(new(Rad(xTilt), Rad(35), Rad(40), 1) { TiltZ = Rad(30) });
        foreach (float x in new[] { 0f, 320f, 640f, 960f, 1280f })
            Near(Forward(f, new(x, 720)), new(x, 720));
    }
});
Run("DPI and quality scaling preserve projection, gap and blur", () =>
{
    foreach (double dpi in new[] { 96.0, 144.0, 192.0 })
        foreach (double manualPpm in new[] { 0.0, 8.0 })
            foreach (double manualEye in new[] { 0.0, 500.0 })
            {
                var parameters = new DuoLensParameters { PixelsPerMm = manualPpm, EyeDistanceMm = manualEye };
                var pose = new DuoAttitude(Rad(20), Rad(-10), Rad(30), 1) { TiltZ = Rad(25) };
                var full = DuoLensMath.BuildFrame(parameters, pose, 1280, 720, dpi, 2);
                foreach (double scale in new[] { 0.25, 0.35, 0.5, 1.0 })
                {
                    var reduced = DuoLensMath.BuildFrame(parameters, pose, 1280, 720, dpi, 2, scale);
                    var screen = new Vector2(full.PlaneWidthPx * 0.6f, full.PlaneHeightPx * 0.4f);
                    var a = DuoLensMath.ProjectScreen(screen, full);
                    var b = DuoLensMath.ProjectScreen(screen * (float)scale, reduced);
                    Near(a.SourcePx, b.SourcePx / (float)scale, 0.02f);
                    Near(a.GapMm, b.GapMm, 0.005f);
                    Near((float)DuoLensMath.Evaluate(full).BlurRadiusPx,
                        (float)(DuoLensMath.Evaluate(reduced).BlurRadiusPx / scale), 0.005f);
                }
            }
});
Run("Back-facing, edge-on and behind-eye rays are rejected", () =>
{
    foreach (float degrees in new[] { 90, 100, 170 })
    {
        var f = Frame(new(Rad(degrees), 0, 0, 0));
        Check(!DuoLensMath.ProjectScreen(new(640, 360), f).Valid, "back face was sampled");
    }
    var behind = Frame(DuoAttitude.Flat);
    behind = behind with { ContentCenterPx = new(0, 0, behind.EyeDistanceMm * behind.PixelsPerMm * 2) };
    Check(!DuoLensMath.ProjectScreen(new(640, 360), behind).Valid, "behind-eye plane was sampled");
});
Run("Calibration switches reverse projection while non-X axes remain isolated", () =>
{
    var parameters = new DuoLensParameters();
    var pose = new DuoAttitude(Rad(20), Rad(35), Rad(45), 1) { TiltZ = Rad(30) };
    var a = DuoLensMath.BuildFrame(parameters, pose, 1280, 720, 96, 0);
    parameters.InvertTiltX = true;
    var b = DuoLensMath.BuildFrame(parameters, pose, 1280, 720, 96, 0);
    Check(WidthAt(a, 0) < 1280 && WidthAt(b, 0) > 1280, "invert tilt did not reverse near/far");
});
Console.WriteLine($"PASS: {passed} geometry regression groups.");
#if GPU_CHECKS
GpuProjectionChecks.Run();
#endif

void Run(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
static float Rad(float degree) => degree * MathF.PI / 180;
static DuoLensFrame Frame(DuoAttitude pose) => DuoLensMath.BuildFrame(new(), pose, 1280, 720, 96, 0);
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Near<T>(T actual, T expected, float tolerance = 0.003f) where T : struct
{
    float distance = actual switch
    {
        float a => Math.Abs(a - (float)(object)expected),
        Vector2 a => Vector2.Distance(a, (Vector2)(object)expected),
        Vector3 a => Vector3.Distance(a, (Vector3)(object)expected),
        _ => throw new NotSupportedException(),
    };
    Check(distance <= tolerance, $"Expected {expected}, got {actual} (error {distance})");
}
// Independent forward pinhole projection of known 3D vertices; not the inverse shader formula.
static Vector2 Forward(DuoLensFrame f, Vector2 source)
{
    Vector3 point = f.ContentCenterPx + f.ContentAxisX * (source.X - f.PlaneWidthPx * 0.5f)
        + f.ContentAxisY * (f.PlaneHeightPx * 0.5f - source.Y);
    float eye = f.EyeDistanceMm * f.PixelsPerMm;
    float scale = eye / (eye - point.Z);
    return new(f.PlaneWidthPx * 0.5f + point.X * scale, f.PlaneHeightPx * 0.5f - point.Y * scale);
}
static float WidthAt(DuoLensFrame f, float sourceY)
    => Vector2.Distance(Forward(f, new(0, sourceY)), Forward(f, new(f.PlaneWidthPx, sourceY)));
