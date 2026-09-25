public abstract class Attachment
{
    public string Name;
    public abstract float ApplyRangeModifier(float currentRange);
    public abstract float ApplyAccuracyModifier(float currentAccuracy);
}

// Пример 1: Оптический прицел (Увеличивает эффективную дальность на 40%)
public class ScopeAttachment : Attachment
{
    public float RangeMultiplier = 1.4f; // +40% к дальности

    public override float ApplyRangeModifier(float currentRange) => currentRange * RangeMultiplier;
    public override float ApplyAccuracyModifier(float currentAccuracy) => currentAccuracy * 0.9f; // Немного улучшает точность
}

// Пример 2: Глушитель (Уменьшает дальность на 10%, но снижает отдачу)
public class SilencerAttachment : Attachment
{
    public override float ApplyRangeModifier(float currentRange) => currentRange * 0.9f; // -10% дальности
    public override float ApplyAccuracyModifier(float currentAccuracy) => currentAccuracy;
}
