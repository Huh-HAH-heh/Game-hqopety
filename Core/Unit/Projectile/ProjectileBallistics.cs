using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct BallisticSolution
{
    public Vector3 Direction { get; }
    public float TimeOfFlight { get; }
    public float ImpactVelocity { get; }
    public float ImpactEnergy { get; }

    public BallisticSolution(
        Vector3 direction,
        float timeOfFlight,
        float impactVelocity,
        float impactEnergy)
    {
        Direction = direction;
        TimeOfFlight = timeOfFlight;
        ImpactVelocity = impactVelocity;
        ImpactEnergy = impactEnergy;
    }
}

public static class ProjectileBallistics
{
    public const float Gravity = 9.81f;
    public const float AirDensity = 1.225f;

    public static bool TrySolve(
        Vector3 start,
        Vector3 target,
        float massKg,
        float diameterM,
        float muzzleVelocity,
        float dragCoefficient,
        float ballisticCoefficient,
        out BallisticSolution solution)
    {
        Vector3 delta = target - start;

        float horizontalDistance = MathF.Sqrt(
            delta.X * delta.X +
            delta.Y * delta.Y);

        float speed = MathF.Max(0.001f, muzzleVelocity);
        float mass = MathF.Max(0.000001f, massKg);
        float diameter = MathF.Max(0.0001f, diameterM);
        float drag = MathF.Max(0f, dragCoefficient);
        float bc = MathF.Max(0.01f, ballisticCoefficient);

        if (horizontalDistance < 0.0001f)
        {
            Vector3 direction = Normalize(delta);
            float time = MathF.Abs(delta.Z) / speed;

            solution = new BallisticSolution(
                direction,
                time,
                speed,
                0.5f * mass * speed * speed);

            return true;
        }

        Vector3 horizontal = new Vector3(
            delta.X,
            delta.Y,
            0f) / horizontalDistance;

        const float minAngle = -0.35f;
        const float maxAngle = 1.30f;
        const int samples = 18;

        float bestAngle = minAngle;
        float bestError = float.PositiveInfinity;
        float bestTime = 0f;
        float bestVelocity = speed;

        float previousAngle = minAngle;

        SimulateAtAngle(
            start,
            target.Z,
            horizontal,
            horizontalDistance,
            previousAngle,
            mass,
            diameter,
            speed,
            drag,
            bc,
            out float previousError,
            out float previousTime,
            out float previousVelocity);

        UpdateBest(
            previousError,
            previousAngle,
            previousTime,
            previousVelocity,
            ref bestError,
            ref bestAngle,
            ref bestTime,
            ref bestVelocity);

        bool foundBracket = false;
        float bracketLow = minAngle;
        float bracketHigh = minAngle;
        float bracketLowError = previousError;

        for (int i = 1; i <= samples; i++)
        {
            float angle = minAngle +
                (maxAngle - minAngle) * i / samples;

            SimulateAtAngle(
                start,
                target.Z,
                horizontal,
                horizontalDistance,
                angle,
                mass,
                diameter,
                speed,
                drag,
                bc,
                out float error,
                out float time,
                out float velocity);

            UpdateBest(
                error,
                angle,
                time,
                velocity,
                ref bestError,
                ref bestAngle,
                ref bestTime,
                ref bestVelocity);

            if ((previousError <= 0f && error >= 0f) ||
                (previousError >= 0f && error <= 0f))
            {
                bracketLow = previousAngle;
                bracketHigh = angle;
                bracketLowError = previousError;
                foundBracket = true;
                break;
            }

            previousAngle = angle;
            previousError = error;
        }

        if (foundBracket)
        {
            for (int i = 0; i < 10; i++)
            {
                float mid =
                    (bracketLow + bracketHigh) * 0.5f;

                SimulateAtAngle(
                    start,
                    target.Z,
                    horizontal,
                    horizontalDistance,
                    mid,
                    mass,
                    diameter,
                    speed,
                    drag,
                    bc,
                    out float error,
                    out float time,
                    out float velocity);

                UpdateBest(
                    error,
                    mid,
                    time,
                    velocity,
                    ref bestError,
                    ref bestAngle,
                    ref bestTime,
                    ref bestVelocity);

                if ((bracketLowError <= 0f && error >= 0f) ||
                    (bracketLowError >= 0f && error <= 0f))
                {
                    bracketHigh = mid;
                }
                else
                {
                    bracketLow = mid;
                    bracketLowError = error;
                }
            }
        }

        Vector3 directionResult = Normalize(
            horizontal * MathF.Cos(bestAngle) +
            new Vector3(
                0f,
                0f,
                MathF.Sin(bestAngle)));

        bool valid =
            bestError <=
            MathF.Max(
                0.25f,
                horizontalDistance * 0.01f);

        solution = new BallisticSolution(
            directionResult,
            MathF.Max(0f, bestTime),
            MathF.Max(0f, bestVelocity),
            0.5f * mass * bestVelocity * bestVelocity);

        return valid;
    }

    public static bool TrySolveDirection(
        Vector3 start,
        Vector3 target,
        float massKg,
        float diameterM,
        float muzzleVelocity,
        float dragCoefficient,
        out Vector3 direction)
    {
        if (TrySolve(
                start,
                target,
                massKg,
                diameterM,
                muzzleVelocity,
                dragCoefficient,
                1f,
                out BallisticSolution solution))
        {
            direction = solution.Direction;
            return true;
        }

        direction = Normalize(target - start);
        return false;
    }

    private static void SimulateAtAngle(
        Vector3 start,
        float targetZ,
        Vector3 horizontal,
        float horizontalDistance,
        float angle,
        float mass,
        float diameter,
        float speed,
        float drag,
        float ballisticCoefficient,
        out float error,
        out float time,
        out float impactVelocity)
    {
        Vector3 velocity =
            horizontal * (MathF.Cos(angle) * speed) +
            new Vector3(
                0f,
                0f,
                MathF.Sin(angle) * speed);

        Vector3 position = start;
        float travelled = 0f;
        time = 0f;

        const float dt = 1f / 240f;
        const float maxTime = 8f;

        while (time < maxTime)
        {
            Vector3 oldPosition = position;

            Integrate(
                ref velocity,
                mass,
                diameter,
                drag,
                ballisticCoefficient,
                dt);

            position += velocity * dt;
            time += dt;

            float newTravelled = MathF.Sqrt(
                (position.X - start.X) *
                (position.X - start.X) +
                (position.Y - start.Y) *
                (position.Y - start.Y));

            if (newTravelled >= horizontalDistance)
            {
                float span = newTravelled - travelled;

                float t = span <= 0.000001f
                    ? 0f
                    : (horizontalDistance - travelled) / span;

                float z = oldPosition.Z +
                    (position.Z - oldPosition.Z) * t;

                error = z - targetZ;
                impactVelocity = velocity.Length();
                return;
            }

            travelled = newTravelled;

            if (position.Z < -20f)
                break;
        }

        error = position.Z - targetZ;
        impactVelocity = velocity.Length();
    }

    private static void UpdateBest(
        float error,
        float angle,
        float time,
        float velocity,
        ref float bestError,
        ref float bestAngle,
        ref float bestTime,
        ref float bestVelocity)
    {
        if (MathF.Abs(error) >= MathF.Abs(bestError))
            return;

        bestError = error;
        bestAngle = angle;
        bestTime = time;
        bestVelocity = velocity;
    }

    public static void Integrate(
        ref Vector3 velocity,
        float mass,
        float diameter,
        float drag,
        float deltaTime)
    {
        Integrate(
            ref velocity,
            mass,
            diameter,
            drag,
            1f,
            deltaTime);
    }

    public static void Integrate(
        ref Vector3 velocity,
        float mass,
        float diameter,
        float drag,
        float ballisticCoefficient,
        float deltaTime)
    {
        velocity.Z -= Gravity * deltaTime;

        float speedSquared = velocity.LengthSquared();

        if (speedSquared < 0.000001f)
            return;

        float speed = MathF.Sqrt(speedSquared);
        float radius = diameter * 0.5f;
        float area = MathF.PI * radius * radius;
        float bc = MathF.Max(0.01f, ballisticCoefficient);

        float dragAcceleration =
            0.5f *
            AirDensity *
            MathF.Max(0f, drag) *
            area *
            speedSquared /
            MathF.Max(0.000001f, mass) /
            bc;

        float deltaSpeed =
            dragAcceleration * deltaTime;

        if (deltaSpeed >= speed)
        {
            velocity = Vector3.Zero;
            return;
        }

        float newSpeed = speed - deltaSpeed;
        velocity *= newSpeed / speed;
    }

    private static Vector3 Normalize(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();

        if (lengthSquared < 0.000001f)
        {
            return new Vector3(1f, 0f, 0f);
        }

        return value / MathF.Sqrt(lengthSquared);
    }
}
