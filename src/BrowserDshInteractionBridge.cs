using System.Text.Json;

namespace DshLauncher;

internal sealed record BrowserDshInteraction(string Type, string EventId, string ClientId, string AgentId, DshInteractionKind? Kind, string? ToolName, string? Reason, IReadOnlyList<DshUserQuestion> Questions);
internal sealed record BrowserDshInteractionReply(string RequestId, bool Accepted, string? Error);

internal sealed class BrowserDshInteractionResponder : IDshInteractionResponder
{
    private readonly Func<string, string, DshInteractionDecision, Task> _reply;
    public BrowserDshInteractionResponder(Func<string, string, DshInteractionDecision, Task> reply) => _reply = reply;
    public Task ReplyAsync(DshPendingInteraction interaction, DshInteractionDecision decision) => _reply(interaction.EventId, interaction.ClientId, decision);
}

internal static class BrowserDshInteractionBridge
{
    public static bool TryParse(string raw, out BrowserDshInteraction interaction)
    {
        interaction = null!;
        try
        {
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "dsh-interaction") return false;
            var action = Text(root,"action"); var eventId = Text(root,"eventId");
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(eventId)) return false;
            if (action == "cancel") { interaction = new(action,eventId,"","",null,null,null,Array.Empty<DshUserQuestion>()); return true; }
            var kind = Text(root,"event") == "approval/request" ? DshInteractionKind.Approval : Text(root,"event") == "user-questions/request" ? DshInteractionKind.Question : (DshInteractionKind?)null;
            if (kind == null || string.IsNullOrWhiteSpace(Text(root,"clientId"))) return false;
            var request = root.TryGetProperty("request",out var value) ? value : default;
            if (request.ValueKind != JsonValueKind.Object) return false;
            var questions = kind == DshInteractionKind.Question ? Questions(request) : Array.Empty<DshUserQuestion>();
            if (kind == DshInteractionKind.Question && questions.Count == 0) return false;
            interaction = new(action,eventId,Text(root,"clientId")!,Text(root,"agentId") ?? "",kind,Text(request,"toolName"),Text(request,"reason"),questions); return true;
        }
        catch { return false; }
    }
    public static bool TryParseReply(string raw, out BrowserDshInteractionReply reply)
    {
        reply = null!;
        try
        {
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            if (Text(root, "type") != "dsh-interaction-result") return false;
            var requestId = Text(root, "requestId");
            if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            reply = new BrowserDshInteractionReply(requestId, accepted.GetBoolean(), Text(root, "error"));
            return true;
        }
        catch { return false; }
    }

    private static string? Text(JsonElement value,string name) => value.TryGetProperty(name,out var item) && item.ValueKind==JsonValueKind.String ? item.GetString() : null;
    private static IReadOnlyList<DshUserQuestion> Questions(JsonElement request)
    {
        if(!request.TryGetProperty("questions",out var list)||list.ValueKind!=JsonValueKind.Array) return Array.Empty<DshUserQuestion>(); var result=new List<DshUserQuestion>();
        foreach(var item in list.EnumerateArray()) { var id=Text(item,"id"); var question=Text(item,"question"); if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(question)) continue; var options=new List<DshQuestionOption>(); if(item.TryGetProperty("options",out var raw)&&raw.ValueKind==JsonValueKind.Array) foreach(var option in raw.EnumerateArray()){var label=Text(option,"label");if(!string.IsNullOrWhiteSpace(label))options.Add(new(label!,Text(option,"description")));} result.Add(new(id!,question!,Text(item,"header"),Text(item,"detail"),options,item.TryGetProperty("multiSelect",out var multi)&&multi.ValueKind==JsonValueKind.True)); }
        return result;
    }
}
