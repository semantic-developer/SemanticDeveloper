using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace SemanticDeveloper.Services;

public static class ProtoHelper
{
    public static string? LastSubmitId { get; private set; }
    public enum SubmissionShape
    {
        TopLevelInternallyTagged, // { op: "submit", id, msg, cwd }
        NestedInternallyTagged    // { op: { op: "submit", id, msg }, cwd }
    }

    public enum ContentStyle
    {
        Flattened, // fields (type,text,...) directly under op object
        MsgField   // fields nested under msg object
    }

    public static (bool Ok, string Minified, string Error) PrepareSubmission(
        string input,
        string? cwd,
        SubmissionShape shape = SubmissionShape.TopLevelInternallyTagged,
        ContentStyle style = ContentStyle.Flattened,
        string defaultType = "user_input",
        string? model = null,
        string? approvalPolicy = null,
        bool allowNetworkAccess = false)
    {
        // Build a proper Submission per protocol.rs: { id, op: { type, ... } }
        var payload = BuildSubmissionFromText(input, cwd, defaultType, model, approvalPolicy, allowNetworkAccess);
        return (true, payload, string.Empty);
    }

    public static (bool Ok, string Minified, string Error) TryMinifyJson(string input)
    {
        try
        {
            var token = JToken.Parse(input);
            var min = token.ToString(Formatting.None);
            return (true, min, string.Empty);
        }
        catch (JsonReaderException ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    // Wraps plain text into a minimal proto message envelope.
    // Default message type is "input".
    public static string WrapPlainText(string text, string? cwd, SubmissionShape shape, ContentStyle style, string type = "user_input", string? model = null, string? approvalPolicy = null, bool allowNetworkAccess = false)
        => BuildSubmissionFromText(text, cwd, type, model, approvalPolicy, allowNetworkAccess);

    private static JObject NewSubmission()
    {
        var id = System.Guid.NewGuid().ToString();
        LastSubmitId = id;
        return new JObject
        {
            ["id"] = id,
            ["op"] = new JObject()
        };
    }

    private static JObject GetOpObject(JObject submission, SubmissionShape shape)
        => (JObject)submission["op"]!;

    private static void ApplyDefaultsToExistingEnvelope(JObject env, string? cwd)
    {
        if (env["op"] is JObject inner)
        {
            if (!string.IsNullOrWhiteSpace(cwd) && inner["cwd"] == null) inner["cwd"] = cwd;
            EnsurePolicy(inner, null);
            EnsureSandboxPolicy(inner, null);
            EnsureEffort(inner);
            EnsureSummary(inner);
        }
    }

    private static void EnsurePolicy(JObject o, string? policy)
    {
        var value = string.IsNullOrWhiteSpace(policy) ? "on-request" : policy;
        if (o["approval_policy"] == null)
            o["approval_policy"] = value;
    }

    private static void EnsureSandboxPolicy(JObject o, bool? allowNetwork)
    {
        var sp = o["sandbox_policy"];
        if (sp == null)
        {
            var obj = new JObject { ["mode"] = "workspace-write" };
            if (allowNetwork.HasValue && allowNetwork.Value)
                obj["network_access"] = true;
            o["sandbox_policy"] = obj;
            return;
        }
        if (sp is JValue v && v.Type == JTokenType.String)
        {
            // Normalize string to object form expected by internally tagged enum
            var obj = new JObject { ["mode"] = v.Value<string>() ?? "workspace-write" };
            if (allowNetwork.HasValue && allowNetwork.Value)
                obj["network_access"] = true;
            o["sandbox_policy"] = obj;
            return;
        }
        if (sp is JObject so && allowNetwork.HasValue)
        {
            if (allowNetwork.Value)
                so["network_access"] = true;
        }
    }

    private static void EnsureEffort(JObject o)
    {
        if (o["effort"] == null)
            o["effort"] = "medium";
    }

    private static void EnsureSummary(JObject o)
    {
        // Externally tagged enum. Default to 'auto' since some models reject 'none'.
        if (o["summary"] == null)
        {
            o["summary"] = new JObject { ["auto"] = new JObject() };
            return;
        }

        if (o["summary"] is JObject so)
        {
            // Ensure it has at least one key; if empty, set to 'auto'
            var hasAny = false;
            foreach (var _ in so.Properties()) { hasAny = true; break; }
            if (!hasAny) o["summary"] = new JObject { ["auto"] = new JObject() };
            return;
        }

        if (o["summary"] is JValue sv && sv.Type == JTokenType.String)
        {
            var v = (sv.Value<string>() ?? "auto").Trim().ToLowerInvariant();
            // Map unsupported 'none' to 'auto' to avoid 400s from the model API
            if (v == "none") v = "auto";
            if (v is "auto" or "concise" or "detailed")
            {
                o["summary"] = new JObject { [v] = new JObject() };
            }
            else
            {
                o["summary"] = new JObject { ["auto"] = new JObject() };
            }
        }
    }

    private static readonly Regex ModelEffortSuffix = new("-(low|medium|high)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void EnsureModel(JObject o, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;

        // Normalize model like "gpt-5-high" -> model: "gpt-5", effort: "high"
        var m = ModelEffortSuffix.Match(model);
        if (m.Success)
        {
            var effort = m.Groups[1].Value.ToLowerInvariant();
            var baseModel = model.Substring(0, model.Length - (effort.Length + 1));

            // Always set normalized model, overriding only if not already set
            if (o["model"] == null)
                o["model"] = baseModel;
            else
                o["model"] = baseModel;

            // Override effort to match model suffix
            o["effort"] = effort;
            return;
        }

        if (o["model"] == null)
            o["model"] = model;
    }

    private static void TryNormalizeTypes(JObject obj)
    {
        // normalize top-level
        NormalizeTypeInPlace(obj);
        // nested op object
        if (obj["op"] is JObject inner)
            NormalizeTypeInPlace(inner);
        // nested msg object (either at top level or under op depending on variant)
        if (obj["msg"] is JObject msg1)
            NormalizeTypeInPlace(msg1);
        if (obj["op"] is JObject inner2 && inner2["msg"] is JObject msg2)
            NormalizeTypeInPlace(msg2);
    }

    private static void NormalizeTypeInPlace(JObject o)
    {
        if (o["type"] is JValue v && v.Type == JTokenType.String)
        {
            o["type"] = NormalizeType(v.Value<string>() ?? string.Empty);
        }
    }

    private static JToken NormalizeTypeToken(JToken t)
    {
        if (t is JValue v && v.Type == JTokenType.String)
        {
            return new JValue(NormalizeType(v.Value<string>() ?? string.Empty));
        }
        return t;
    }

    private static string NormalizeType(string type)
    {
        var s = (type ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "input" => "user_input",
            "message" => "user_input",
            "msg" => "user_input",
            "user" => "user_input",
            _ => string.IsNullOrEmpty(s) ? "user_input" : s
        };
    }

    public static string BuildApproval(string approvalOp, string approvedSubmissionId, string decision, SubmissionShape shape)
    {
        // Build top-level Submission with new id and op carrying the approval
        var sub = NewSubmission();
        var op = (JObject)sub["op"]!;
        op["type"] = approvalOp; // e.g., exec_approval, patch_approval
        op["id"] = approvedSubmissionId;
        op["decision"] = decision; // ReviewDecision in snake_case: approved, approved_for_session, denied, abort
        return sub.ToString(Formatting.None);
    }

    public static string BuildInterrupt()
    {
        var sub = NewSubmission();
        var op = (JObject)sub["op"]!;
        op["type"] = "interrupt";
        return sub.ToString(Formatting.None);
    }

    public static string BuildListMcpTools()
    {
        var sub = NewSubmission();
        var op = (JObject)sub["op"]!;
        op["type"] = "list_mcp_tools";
        return sub.ToString(Formatting.None);
    }

    private static string BuildSubmissionFromText(string text, string? cwd, string type, string? model, string? approvalPolicy, bool allowNetwork)
    {
        var sub = NewSubmission();
        var op = (JObject)sub["op"]!;
        var norm = NormalizeType(type);
        if (norm == "user_turn")
        {
            op["type"] = "user_turn";
            op["items"] = new JArray(new JObject { ["type"] = "text", ["text"] = text });
            if (!string.IsNullOrWhiteSpace(cwd)) op["cwd"] = cwd;
            EnsurePolicy(op, approvalPolicy);
            EnsureSandboxPolicy(op, allowNetwork);
            EnsureEffort(op);
            EnsureSummary(op);
            EnsureModel(op, model);
        }
        else
        {
            op["type"] = "user_input";
            op["items"] = new JArray(new JObject { ["type"] = "text", ["text"] = text });
        }
        return sub.ToString(Formatting.None);
    }
}
