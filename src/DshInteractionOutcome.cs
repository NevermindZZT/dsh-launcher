namespace DshLauncher;

/// <summary>Builds DSH Remote Event outcomes with the exact optional-field semantics expected by DSH.</summary>
internal static class DshInteractionOutcome
{
    public static object Build(DshInteractionDecision decision)
    {
        if (decision.ErrorCode != null)
        {
            return new
            {
                kind = "rejected",
                error = new
                {
                    name = "UserQuestionError",
                    message = decision.ErrorMessage ?? "interaction cancelled",
                    code = decision.ErrorCode,
                },
            };
        }

        if (decision.Answers != null)
        {
            return new
            {
                kind = "result",
                value = new { answers = decision.Answers.Select(BuildAnswer).ToArray() },
            };
        }

        return new { kind = "result", value = decision.Outcome };
    }

    private static Dictionary<string, object> BuildAnswer(DshQuestionAnswer answer)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = answer.Id,
            ["selected"] = answer.Selected,
        };
        // DSH accepts an omitted custom value, but rejects JSON null for this optional string field.
        if (answer.Custom != null) result["custom"] = answer.Custom;
        return result;
    }
}
