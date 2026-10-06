namespace Core.Unit;

public static class FactionRules
{
    public static bool ShouldAttack(
        ushort observerFaction,
        ushort targetFaction)
    {
        if (observerFaction == 0 ||
            targetFaction == 0)
        {
            return false;
        }

        return observerFaction != targetFaction;
    }
}
