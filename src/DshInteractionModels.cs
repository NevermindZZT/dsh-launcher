namespace DshLauncher;

internal enum DshInteractionKind
{
    Approval,
    Question,
}

internal sealed record DshQuestionOption(string Label, string? Description);

internal sealed record DshUserQuestion(
    string Id,
    string Question,
    string? Header,
    string? Detail,
    IReadOnlyList<DshQuestionOption> Options,
    bool MultiSelect);

internal interface IDshInteractionResponder
{
    Task ReplyAsync(DshPendingInteraction interaction, DshInteractionDecision decision);
}

internal sealed record DshPendingInteraction(
    string SourceKey,
    string SourceName,
    string EventId,
    string ClientId,
    string AgentId,
    DshInteractionKind Kind,
    string? ToolName,
    string? Reason,
    IReadOnlyList<DshUserQuestion> Questions,
    IDshInteractionResponder Owner);

internal sealed record DshQuestionAnswer(string Id, IReadOnlyList<string> Selected, string? Custom);

internal sealed record DshInteractionDecision(
    string Outcome,
    IReadOnlyList<DshQuestionAnswer>? Answers = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static DshInteractionDecision AllowOnce() => new("allowed-once");
    public static DshInteractionDecision Reject() => new("rejected");
    public static DshInteractionDecision CancelApproval() => new("cancelled");
    public static DshInteractionDecision CancelQuestion() => new("rejected", null, "ASK_CANCELLED", "the user cancelled ask_user_question");
    public static DshInteractionDecision Answer(IReadOnlyList<DshQuestionAnswer> answers) => new("answers", answers);
}
