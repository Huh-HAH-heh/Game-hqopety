namespace Core.Unit.Components.AiComponents;

public enum AiOpCode : byte
{
    Idle,
    Wander,
    MoveToTarget,
    TakeCover,
    CombatEngage,
    InvestigateSound
}

public enum AiCommandInsertMode : byte
{
    ReplaceAll,
    Append
}

public struct AiCommand
{
    public AiOpCode OpCode;
    public int TargetX;
    public int TargetY;
    public int TargetZ;
    public float Timer;
}