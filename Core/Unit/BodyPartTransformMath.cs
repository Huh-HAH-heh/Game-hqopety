using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Shared 3D transform and orthographic-projection math for body-part editing,
/// rendering and hit regions. Euler components are radians: X=pitch, Y=tilt, Z=top-down rotation.
/// </summary>
public static class BodyPartTransformMath
{
    public readonly record struct ProjectedShape(Vector3 Scale, float Rotation);

    public static Quaternion FromEulerRadians(Vector3 euler) =>
        Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);

    public static Quaternion Compose(Quaternion parent, Vector3 localEuler)
    {
        Quaternion result = parent * FromEulerRadians(localEuler);
        return Quaternion.Normalize(result);
    }

    public static Quaternion TopDownRotation(float radians) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);

    public static Vector3 TransformPosition(Vector3 localPosition, Quaternion parentOrientation) =>
        Vector3.Transform(localPosition, parentOrientation);

    /// <summary>
    /// Projects a 3D primitive into an orthographic view. Horizontal/vertical axes are
    /// world axes 0=X, 1=Y, 2=Z. For a rotation confined to the view plane, preserve
    /// the original primitive dimensions and exact angle (important for triangles).
    /// A tilted primitive uses the projected covariance of its 3D half-extents.
    /// </summary>
    public static ProjectedShape Project(
        Vector3 scale,
        Quaternion orientation,
        int horizontalAxis,
        int verticalAxis)
    {
        if ((uint)horizontalAxis > 2u || (uint)verticalAxis > 2u ||
            horizontalAxis == verticalAxis)
            throw new ArgumentOutOfRangeException(nameof(horizontalAxis), "Projection axes must be two distinct XYZ axes.");

        scale = Vector3.Abs(scale);
        Vector3 axisX = Vector3.Transform(Vector3.UnitX, orientation);
        Vector3 axisY = Vector3.Transform(Vector3.UnitY, orientation);
        Vector3 axisZ = Vector3.Transform(Vector3.UnitZ, orientation);
        Span<Vector3> basis = stackalloc Vector3[3] { axisX, axisY, axisZ };
        Span<float> extents = stackalloc float[3] { scale.X, scale.Y, scale.Z };

        int normalAxis = 3 - horizontalAxis - verticalAxis;
        Vector3 normalBasis = basis[normalAxis];
        if (MathF.Abs(Component(normalBasis, normalAxis)) > 0.9999f &&
            MathF.Abs(Component(normalBasis, horizontalAxis)) < 0.0001f &&
            MathF.Abs(Component(normalBasis, verticalAxis)) < 0.0001f)
        {
            Vector3 horizontalBasis = basis[horizontalAxis];
            float angle = MathF.Atan2(
                Component(horizontalBasis, verticalAxis),
                Component(horizontalBasis, horizontalAxis));
            return new ProjectedShape(
                new Vector3(extents[horizontalAxis], extents[verticalAxis], extents[normalAxis]),
                angle);
        }

        float xx = 0f;
        float yy = 0f;
        float xy = 0f;
        for (int i = 0; i < 3; i++)
        {
            float h = Component(basis[i], horizontalAxis);
            float v = Component(basis[i], verticalAxis);
            float variance = extents[i] * extents[i];
            xx += variance * h * h;
            yy += variance * v * v;
            xy += variance * h * v;
        }

        float discriminant = MathF.Sqrt(
            MathF.Max(0f, (xx - yy) * (xx - yy) + 4f * xy * xy));
        float major = MathF.Sqrt(MathF.Max(0.000001f, (xx + yy + discriminant) * 0.5f));
        float minor = MathF.Sqrt(MathF.Max(0.000001f, (xx + yy - discriminant) * 0.5f));
        float rotation = 0.5f * MathF.Atan2(2f * xy, xx - yy);
        float normalVariance = 0f;
        for (int i = 0; i < 3; i++)
        {
            float normal = Component(basis[i], normalAxis);
            normalVariance += extents[i] * extents[i] * normal * normal;
        }

        return new ProjectedShape(
            new Vector3(major, minor, MathF.Sqrt(MathF.Max(0.000001f, normalVariance))),
            rotation);
    }

    private static float Component(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };
}
