using System;
using System.Numerics;

namespace Core.Unit;

public static class ProjectileBallistics
{
    public const float Gravity = 9.81f;
    public const float AirDensity = 1.225f;

    public static bool TrySolveDirection(
        Vector3 start,
        Vector3 target,
        float massKg,
        float diameterM,
        float muzzleVelocity,
        float dragCoefficient,
        out Vector3 direction)
    {
        Vector3 delta =
            target -
            start;

        float horizontalDistance =
            MathF.Sqrt(
                delta.X * delta.X +
                delta.Y * delta.Y);

        if (horizontalDistance < 0.0001f)
        {
            direction =
                Normalize(delta);

            return true;
        }

        Vector3 horizontal =
            new Vector3(
                delta.X,
                delta.Y,
                0f) /
            horizontalDistance;

        float mass =
            MathF.Max(
                0.000001f,
                massKg);

        float diameter =
            MathF.Max(
                0.0001f,
                diameterM);

        float speed =
            MathF.Max(
                0.001f,
                muzzleVelocity);

        float drag =
            MathF.Max(
                0f,
                dragCoefficient);

        const float minAngle =
            -0.20f;

        const float maxAngle =
            1.00f;

        const int Samples =
            24;

        float bestAngle =
            minAngle;

        float bestError =
            float.PositiveInfinity;

        float previousAngle =
            minAngle;

        float previousError =
            HeightError(
                start,
                target.Z,
                horizontal,
                horizontalDistance,
                previousAngle,
                mass,
                diameter,
                speed,
                drag);

        for (int i = 1;
             i <= Samples;
             i++)
        {
            float angle =
                minAngle +
                (maxAngle - minAngle) *
                i /
                Samples;

            float error =
                HeightError(
                    start,
                    target.Z,
                    horizontal,
                    horizontalDistance,
                    angle,
                    mass,
                    diameter,
                    speed,
                    drag);

            float absoluteError =
                MathF.Abs(error);

            if (absoluteError <
                bestError)
            {
                bestError =
                    absoluteError;

                bestAngle =
                    angle;
            }

            if ((previousError <= 0f &&
                 error >= 0f) ||
                (previousError >= 0f &&
                 error <= 0f))
            {
                float low =
                    previousAngle;

                float high =
                    angle;

                float lowError =
                    previousError;

                for (int iteration = 0;
                     iteration < 14;
                     iteration++)
                {
                    float mid =
                        (low + high) *
                        0.5f;

                    float midError =
                        HeightError(
                            start,
                            target.Z,
                            horizontal,
                            horizontalDistance,
                            mid,
                            mass,
                            diameter,
                            speed,
                            drag);

                    if ((lowError <= 0f &&
                         midError >= 0f) ||
                        (lowError >= 0f &&
                         midError <= 0f))
                    {
                        high =
                            mid;
                    }
                    else
                    {
                        low =
                            mid;

                        lowError =
                            midError;
                    }
                }

                bestAngle =
                    (low + high) *
                    0.5f;

                break;
            }

            previousAngle =
                angle;

            previousError =
                error;
        }

        direction =
            Normalize(
                horizontal *
                MathF.Cos(bestAngle) +
                new Vector3(
                    0f,
                    0f,
                    MathF.Sin(bestAngle)));

        return bestError <
               10f;
    }

    private static float HeightError(
        Vector3 start,
        float targetZ,
        Vector3 horizontal,
        float horizontalDistance,
        float angle,
        float mass,
        float diameter,
        float speed,
        float drag)
    {
        Vector3 velocity =
            horizontal *
            (MathF.Cos(angle) * speed) +
            new Vector3(
                0f,
                0f,
                MathF.Sin(angle) * speed);

        Vector3 position =
            start;

        float travelled =
            0f;

        const float dt =
            1f / 240f;

        for (int step = 0;
             step < 2000;
             step++)
        {
            Vector3 oldPosition =
                position;

            Integrate(
                ref velocity,
                mass,
                diameter,
                drag,
                dt);

            position +=
                velocity *
                dt;

            float newTravelled =
                MathF.Sqrt(
                    (position.X - start.X) *
                    (position.X - start.X) +
                    (position.Y - start.Y) *
                    (position.Y - start.Y));

            if (newTravelled >=
                horizontalDistance)
            {
                float span =
                    newTravelled -
                    travelled;

                float t =
                    span <= 0.000001f
                        ? 0f
                        : (horizontalDistance -
                           travelled) /
                          span;

                float z =
                    oldPosition.Z +
                    (position.Z -
                     oldPosition.Z) *
                    t;

                return z -
                    targetZ;
            }

            travelled =
                newTravelled;

            if (position.Z < -10f)
                break;
        }

        return position.Z -
            targetZ;
    }

    public static void Integrate(
        ref Vector3 velocity,
        float mass,
        float diameter,
        float drag,
        float deltaTime)
    {
        velocity.Z -=
            Gravity *
            deltaTime;

        float speedSquared =
            velocity.LengthSquared();

        if (speedSquared <
            0.000001f)
        {
            return;
        }

        float speed =
            MathF.Sqrt(
                speedSquared);

        float radius =
            diameter *
            0.5f;

        float area =
            MathF.PI *
            radius *
            radius;

        float dragAcceleration =
            0.5f *
            AirDensity *
            drag *
            area *
            speedSquared /
            mass;

        float deltaSpeed =
            dragAcceleration *
            deltaTime;

        if (deltaSpeed >= speed)
        {
            velocity =
                Vector3.Zero;

            return;
        }

        float newSpeed =
            speed -
            deltaSpeed;

        velocity *=
            newSpeed /
            speed;
    }

    private static Vector3 Normalize(
        Vector3 value)
    {
        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared <
            0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
