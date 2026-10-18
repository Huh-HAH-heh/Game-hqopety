using Core.AI.Behaviac;

namespace behaviac;

/// <summary>
/// Small generated-style behavior tree. Keep decisions deterministic and
/// side-effect-free apart from assigning the resulting intent.
/// </summary>
public static class bt_rimclone_unit_decision
{
    private enum PredicateKind
    {
        Dead,
        SeekCover,
        FlankVisibleTarget,
        EngageVisibleTarget,
        InvestigateLastKnownTarget
    }

    private sealed class IntentCondition : Condition
    {
        private readonly PredicateKind _kind;

        public IntentCondition(PredicateKind kind) => _kind = kind;

        protected override EBTStatus update_impl(Agent pAgent, EBTStatus childStatus)
        {
            var agent = (BehaviacUnitDecisionAgent)pAgent;
            bool satisfied = _kind switch
            {
                PredicateKind.Dead => agent.ShouldReturnDead(),
                PredicateKind.SeekCover => agent.ShouldSeekCover(),
                PredicateKind.FlankVisibleTarget => agent.ShouldFlankVisibleTarget(),
                PredicateKind.EngageVisibleTarget => agent.ShouldEngageVisibleTarget(),
                PredicateKind.InvestigateLastKnownTarget => agent.ShouldInvestigateLastKnownTarget(),
                _ => false
            };

            return satisfied ? EBTStatus.BT_SUCCESS : EBTStatus.BT_FAILURE;
        }
    }

    private sealed class IntentAction : Action
    {
        private readonly BehaviacUnitIntent _intent;

        public IntentAction(BehaviacUnitIntent intent)
        {
            _intent = intent;
            m_resultOption = EBTStatus.BT_INVALID;
        }

        protected override EBTStatus update_impl(Agent pAgent, EBTStatus childStatus) =>
            ((BehaviacUnitDecisionAgent)pAgent).SelectIntent(_intent);
    }

    public static bool build_behavior_tree(BehaviorTree bt)
    {
        bt.SetClassNameString("BehaviorTree");
        bt.SetId(-1);
        bt.SetName("rimclone_unit_decision");
        bt.IsFSM = false;

        var root = new Selector();
        ConfigureNode(root, "Selector", 0);
        bt.AddChild(root);

        AddBranch(root, 1, PredicateKind.Dead, BehaviacUnitIntent.Dead);
        AddBranch(root, 4, PredicateKind.SeekCover, BehaviacUnitIntent.SeekCover);
        AddBranch(root, 7, PredicateKind.FlankVisibleTarget, BehaviacUnitIntent.FlankTarget);
        AddBranch(root, 10, PredicateKind.EngageVisibleTarget, BehaviacUnitIntent.EngageTarget);
        AddBranch(root, 13, PredicateKind.InvestigateLastKnownTarget, BehaviacUnitIntent.InvestigateLastKnownTarget);

        var patrol = new IntentAction(BehaviacUnitIntent.Patrol);
        ConfigureNode(patrol, "Action", 16);
        root.AddChild(patrol);

        return true;
    }

    private static void AddBranch(
        Selector root,
        int id,
        PredicateKind predicate,
        BehaviacUnitIntent intent)
    {
        var sequence = new Sequence();
        ConfigureNode(sequence, "Sequence", id);
        root.AddChild(sequence);

        var condition = new IntentCondition(predicate);
        ConfigureNode(condition, "Condition", id + 1);
        sequence.AddChild(condition);

        var action = new IntentAction(intent);
        ConfigureNode(action, "Action", id + 2);
        sequence.AddChild(action);
    }

    private static void ConfigureNode(BehaviorNode node, string className, int id)
    {
        node.SetClassNameString(className);
        node.SetId(id);
    }
}
