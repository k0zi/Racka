// Ported from PocketFlow (https://github.com/The-Pocket/PocketFlow), Copyright (c) 2024 Zachary Huang, MIT; see THIRD_PARTY_NOTICES.md.
namespace HerdFlow;

public class ConditionalTransition
{
    public BaseNode Src { get; }
    public string Action { get; }

    public ConditionalTransition(BaseNode src, string action)
    {
        Src = src;
        Action = action;
    }

    public BaseNode Then(BaseNode tgt) => Src.Next(tgt, Action);
}