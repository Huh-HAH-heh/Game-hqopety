using System;
using System.Numerics;
using Raylib_cs;

namespace RimClone.Render.Raylib;

/// <summary>
/// Conservative camera-frustum test for region bounding spheres.
/// Uses the active camera basis and viewport aspect ratio; the far range
/// is supplied by the viewer so it stays in sync with the user render-distance setting.
/// </summary>
internal static class TerrainFrustum
{
    public static bool IsSphereVisible(
        Camera3D camera,
        Vector3 center,
        float radius,
        int viewportWidth,
        int viewportHeight,
        float maximumViewDistanceMeters)
    {
        radius = Math.Max(0f, radius);
        viewportWidth = Math.Max(1, viewportWidth);
        viewportHeight = Math.Max(1, viewportHeight);
        maximumViewDistanceMeters = Math.Max(1f, maximumViewDistanceMeters);

        Vector3 forward = camera.Target - camera.Position;
        if (forward.LengthSquared() < 0.000001f)
            return true;

        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Cross(forward, camera.Up);
        if (right.LengthSquared() < 0.000001f)
            return true;

        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        Vector3 toCenter = center - camera.Position;

        float depth = Vector3.Dot(toCenter, forward);
        float distanceFromFocus = Vector3.Distance(center, camera.Target);
        if (depth + radius <= 0.01f ||
            distanceFromFocus - radius > maximumViewDistanceMeters)
        {
            return false;
        }

        float halfFovRadians =
            camera.FovY * (MathF.PI / 360f);
        float tanVertical = MathF.Tan(halfFovRadians);
        float aspect = viewportWidth / (float)viewportHeight;
        float tanHorizontal = tanVertical * aspect;

        // Expand each side plane by the sphere's projected radius.
        // The sqrt term keeps the result conservative at oblique angles.
        float verticalExtent =
            Math.Max(0f, depth) * tanVertical +
            radius * MathF.Sqrt(1f + tanVertical * tanVertical);
        float horizontalExtent =
            Math.Max(0f, depth) * tanHorizontal +
            radius * MathF.Sqrt(1f + tanHorizontal * tanHorizontal);

        float horizontalOffset = MathF.Abs(Vector3.Dot(toCenter, right));
        if (horizontalOffset > horizontalExtent)
            return false;

        float verticalOffset = MathF.Abs(Vector3.Dot(toCenter, up));
        return verticalOffset <= verticalExtent;
    }
}
