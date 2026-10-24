using System;
using System.Diagnostics;
using Core.Unit;

namespace Core.AI.Perception;

public sealed class VisualPerceptionSense : PerceptionSenseBase
{
    private const float PublishIntervalSeconds = 0.20f;

    private readonly VisionSystem _vision;
    private long _lastPublishedVisionUpdateCount;
    private float _lastPublishedAtSeconds = float.NegativeInfinity;

    public double LastPublishMilliseconds { get; private set; }
    public int LastObservationsPublished { get; private set; }

    public override string Id => "visual";
    public override PerceptionSense Channel => PerceptionSense.Visual;

    public VisualPerceptionSense(VisionSystem vision)
    {
        _vision = vision ?? throw new ArgumentNullException(nameof(vision));
        _lastPublishedVisionUpdateCount = _vision.UpdateCount;
    }

    public override bool Enabled
    {
        get => base.Enabled;
        set
        {
            if (base.Enabled == value)
                return;
            base.Enabled = value;
            LastPublishMilliseconds = 0d;
            LastObservationsPublished = 0;
            _lastPublishedAtSeconds = float.NegativeInfinity;
            if (!value)
            {
                _vision.ClearAll();
                _lastPublishedVisionUpdateCount = _vision.UpdateCount;
            }
        }
    }

    protected override void UpdateCore(in PerceptionSenseContext context)
    {
        LastPublishMilliseconds = 0d;
        LastObservationsPublished = 0;
        _vision.Update(context.Units, context.WorldMap, context.DeltaTime, context.Health, context.Bodies);
        if (_vision.UpdateCount == _lastPublishedVisionUpdateCount)
            return;

        _lastPublishedVisionUpdateCount = _vision.UpdateCount;
        float nowSeconds = context.SimulationTimeSeconds;
        if (nowSeconds - _lastPublishedAtSeconds < PublishIntervalSeconds)
            return;

        _lastPublishedAtSeconds = nowSeconds;
        long publishStarted = Stopwatch.GetTimestamp();
        try
        {
            var units = context.Units;
            var health = context.Health;
            ReadOnlySpan<int> active = units.ActiveIndices;
            for (int i = 0; i < active.Length; i++)
            {
                int observer = active[i];
                if (health.OverallHitPoints[observer] <= 0f)
                    continue;
                ReadOnlySpan<int> targets = _vision.GetVisibleTargets(observer);
                for (int j = 0; j < targets.Length; j++)
                {
                    int target = targets[j];
                    if ((uint)target >= (uint)units.Capacity ||
                        target == observer ||
                        health.OverallHitPoints[target] <= 0f ||
                        !_vision.WasVisibleOnLatestUpdate(observer, target))
                        continue;

                    context.Publish(
                        Channel, observer,
                        new PerceptionObservation(
                            Channel,
                            TacticalStimulusType.VisualContact,
                            units.Position[target],
                            positionErrorRadius: 0.25f,
                            intensity: 1f,
                            detectionConfidence: 1f,
                            identificationConfidence: 1f,
                            detectionLevel: PerceptionDetectionLevel.Detected,
                            identificationLevel: PerceptionIdentificationLevel.Identified,
                            occurredAtSeconds: nowSeconds,
                            receivedAtSeconds: nowSeconds,
                            expiresAtSeconds: nowSeconds + 8f,
                            identifiedSubject: units.GetId(target)));
                    LastObservationsPublished++;
                }
            }
        }
        finally
        {
            LastPublishMilliseconds = Stopwatch.GetElapsedTime(publishStarted).TotalMilliseconds;
        }
    }

    public override void ClearUnit(int unitIndex) => _vision.ClearUnit(unitIndex);
}
