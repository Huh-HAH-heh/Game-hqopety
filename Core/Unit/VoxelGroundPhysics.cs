using System;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

/// <summary>
/// Vertical support and gravity integration for pawns inside edited voxel
/// columns. Horizontal movement remains controlled by UnitMovementSystem.
/// </summary>
public static class VoxelGroundPhysics
{
    private const float GravityMetersPerSecondSquared = 9.81f;
    private const float MaximumFallSpeedMetersPerSecond = 55f;
    private const float SupportSnapToleranceMeters = 0.18f;
    private const float CollisionSkinMeters = 0.015f;

    public static void Step(
        WorldMap worldMap,
        ref Vector3 position,
        ref float verticalVelocity,
        float deltaTime,
        float radiusMeters,
        float bodyHeightMeters)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f ||
            !float.IsFinite(radiusMeters) || radiusMeters <= 0f ||
            !float.IsFinite(bodyHeightMeters) ||
            bodyHeightMeters <= radiusMeters * 2f ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            verticalVelocity = 0f;
            return;
        }

        if (!worldMap.HasVoxelEditsAt(position.X, position.Y))
        {
            verticalVelocity = 0f;
            return;
        }

        // A grounded body stays on the nearest support directly below it.
        // The narrow tolerance prevents snapping a falling body to a distant floor.
        if (worldMap.TryFindVoxelWalkableSurface(
                position.X, position.Y, position.Z,
                SupportSnapToleranceMeters, SupportSnapToleranceMeters,
                bodyHeightMeters,
                out SparseVoxelField.WalkableSurface grounded) &&
            MathF.Abs(grounded.HeightMeters - position.Z) <= SupportSnapToleranceMeters &&
            worldMap.IsVoxelCapsuleClear(
                position.X, grounded.HeightMeters, position.Y,
                radiusMeters, bodyHeightMeters))
        {
            position.Z = grounded.HeightMeters;
            verticalVelocity = 0f;
            return;
        }

        float step = MathF.Min(deltaTime, 0.1f);
        float nextVelocity = MathF.Max(
            -MaximumFallSpeedMetersPerSecond,
            verticalVelocity - GravityMetersPerSecondSquared * step);
        float candidateHeight = position.Z + nextVelocity * step;
        float travelledDown = MathF.Max(0f, position.Z - candidateHeight);

        // Search the swept vertical interval for a floor. This prevents a fast
        // fall from tunnelling through a thin voxel floor between fixed steps.
        float supportSearchAbove = travelledDown + CollisionSkinMeters + 0.02f;
        if (worldMap.TryFindVoxelWalkableSurface(
                position.X, position.Y, candidateHeight,
                CollisionSkinMeters, supportSearchAbove,
                bodyHeightMeters,
                out SparseVoxelField.WalkableSurface landing) &&
            landing.HeightMeters <= position.Z + CollisionSkinMeters &&
            candidateHeight <= landing.HeightMeters + CollisionSkinMeters)
        {
            position.Z = landing.HeightMeters;
            verticalVelocity = 0f;
            return;
        }

        if (worldMap.IsVoxelCapsuleClear(
                position.X, candidateHeight, position.Y,
                radiusMeters, bodyHeightMeters))
        {
            position.Z = candidateHeight;
            verticalVelocity = nextVelocity;
            return;
        }

        // A ceiling or wall has blocked the capsule. No momentum is retained
        // into the solid volume; the next grounded query resolves the support.
        verticalVelocity = 0f;
    }
}
