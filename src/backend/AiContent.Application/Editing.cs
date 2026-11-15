using System.Text.Json;

namespace AiContent.Application;

public sealed record TimedWord(double Start, double End, string Text);

public sealed record EditorialPlan(
    string Hook,
    string Message,
    string Title,
    string Caption,
    IReadOnlyList<string> Hashtags,
    string CoverHeadline,
    string Look,
    string CaptionStyle,
    string CaptionAnimation,
    string Rationale,
    bool FromModel);

public sealed record EditOperation(
    string Op,
    string? Clip = null,
    double? From = null,
    double? To = null,
    double? At = null,
    double? Start = null,
    double? End = null,
    double? Time = null,
    string? Text = null,
    string? Style = null,
    string? Position = null,
    double? Gain = null,
    int? Index = null,
    JsonElement? Options = null);

public sealed record ProposedChangeSet(
    string Scope,
    bool TouchesVideo,
    bool TouchesCover,
    string Summary,
    IReadOnlyList<EditOperation> Operations,
    string? CoverHeadline,
    string? CoverPrompt,
    string? SubtitleText,
    double? MusicGain);

public static class StudioOperations
{
    /// <summary>
    /// Exact operation names from AI Edit Studio 0.12.0 <c>OPERATIONS</c> in src/agent-tools.mjs.
    /// A test compares this set with the vendored module so the lists cannot drift silently.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "split", "delete", "delete_range", "remove_ranges", "speed_ranges", "move_block", "punch_in", "speed_ramp",
        "add_overlay", "update_overlay", "remove_overlay", "move", "swap", "trim", "trim_to", "slip", "insert", "set",
        "replace", "beat_sync", "set_options", "add_marker", "remove_marker", "add_text", "update_text", "remove_text"
    };

    public static readonly IReadOnlySet<string> ToolAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "studio_schema", "studio_list", "studio_get", "studio_timeline", "studio_frame", "studio_storyboard",
        "studio_review", "studio_highlights", "studio_beats", "studio_create", "studio_analyze", "studio_plan",
        "studio_edit", "studio_silence", "studio_update", "studio_history", "studio_diff", "studio_restore",
        "studio_style", "studio_captions_import", "studio_doctor", "studio_duplicate", "studio_auto_edit",
        "studio_transcribe", "studio_transcript", "studio_text_edit", "studio_reframe", "studio_publish_pack",
        "studio_brand", "studio_find_clips", "studio_qc", "studio_wait", "studio_queue", "studio_recipe",
        "studio_freeze", "studio_render_range", "studio_fit_duration", "studio_sections", "studio_search",
        "studio_assemble", "studio_sync", "studio_multicam", "studio_export_timeline", "studio_comments",
        "studio_proxies", "studio_lut", "studio_color_match", "studio_stabilize", "studio_music_fit",
        "studio_audio_repair", "studio_sfx", "studio_auto_zoom", "studio_auto_broll", "studio_batch",
        "studio_compare", "studio_apply", "studio_guide", "studio_music", "studio_render"
    };
}

public static class ChangeSetValidator
{
    public static void Validate(ProposedChangeSet change, Contracts.SelectionDto selection)
    {
        if (string.IsNullOrWhiteSpace(change.Summary))
            throw new Domain.StudioException("invalid_change", "The change set needs a summary.");

        var coverOnly = selection.Kind.Equals("cover", StringComparison.OrdinalIgnoreCase) || change.Scope.Equals("cover", StringComparison.OrdinalIgnoreCase);
        if (coverOnly)
        {
            if (change.TouchesVideo || change.Operations.Count > 0)
                throw new Domain.StudioException("scope_violation", "A cover correction cannot include timeline operations.");
            if (!change.TouchesCover)
                throw new Domain.StudioException("scope_violation", "A cover correction must touch the cover.");
            return;
        }

        if (change.Operations.Count == 0 && !change.TouchesCover)
            throw new Domain.StudioException("invalid_change", "The change set has no operations.");

        foreach (var op in change.Operations)
        {
            if (!StudioOperations.All.Contains(op.Op))
                throw new Domain.StudioException("op_not_allowed", $"Operation '{op.Op}' is not an AI Edit Studio edit operation.");
            if (selection.Kind.Equals("range", StringComparison.OrdinalIgnoreCase) || selection.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase) || selection.Kind.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
            {
                if (selection.Start is null || selection.End is null || selection.End <= selection.Start)
                    throw new Domain.StudioException("invalid_selection", "A range selection needs a start and end.");
                EnsureInside(op, selection.Start.Value, selection.End.Value);
            }
            if (selection.Kind.Equals("clip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(selection.ClipId))
            {
                if (op.Clip is not null && !op.Clip.Equals(selection.ClipId, StringComparison.Ordinal) && op.Clip != "all")
                    throw new Domain.StudioException("scope_violation", "The operation targets a different clip than the selection.");
            }
        }
    }

    private static void EnsureInside(EditOperation op, double start, double end)
    {
        if (op.Op == "set_options")
            return;
        var times = new[] { op.From, op.To, op.At, op.Start, op.End, op.Time }.Where(t => t is not null).Select(t => t!.Value).ToArray();
        if (times.Length == 0)
            throw new Domain.StudioException("scope_violation", $"Operation '{op.Op}' is not limited to the selected range.");
        const double slack = 0.05;
        if (times.Any(t => t < start - slack || t > end + slack))
            throw new Domain.StudioException("scope_violation", $"Operation '{op.Op}' falls outside {start:0.00}–{end:0.00}.");
    }
}

public static class FixturePlanner
{
    public static EditorialPlan Plan(string title, string subject, string goal, string tone, string language, string? cta)
    {
        var topic = string.IsNullOrWhiteSpace(subject) ? title : subject;
        var hook = string.IsNullOrWhiteSpace(goal) ? topic : goal.Trim();
        if (hook.Length > 90) hook = hook[..90].Trim();
        var tags = topic.Split([' ', '،', ',', '؛'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2)
            .Take(4)
            .Select(w => "#" + new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tags.Length == 0) tags = ["#contentstudio"];
        var fa = language.Equals("fa", StringComparison.OrdinalIgnoreCase) || language.Equals("persian", StringComparison.OrdinalIgnoreCase);
        var headline = fa ? topic : topic;
        return new EditorialPlan(
            hook,
            string.IsNullOrWhiteSpace(goal) ? topic : goal,
            string.IsNullOrWhiteSpace(title) ? topic : title,
            (fa ? $"{topic}. {cta}" : $"{topic}. {cta}").Trim().TrimEnd('.'),
            tags,
            headline,
            tone.Equals("cinematic", StringComparison.OrdinalIgnoreCase) ? "cinema" : "vivid",
            "bold",
            "highlight",
            "Deterministic plan from the brief. No model call was made.",
            false);
    }

    public static ProposedChangeSet Correct(string instruction, Contracts.SelectionDto selection)
    {
        var text = instruction.Trim();
        var cover = selection.Kind.Equals("cover", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || text.Contains("جلد", StringComparison.Ordinal)
            || text.Contains("کاور", StringComparison.Ordinal);
        if (cover && !text.Contains("video", StringComparison.OrdinalIgnoreCase) && !text.Contains("ویدیو", StringComparison.Ordinal) || selection.Kind.Equals("cover", StringComparison.OrdinalIgnoreCase))
        {
            if (selection.Kind.Equals("cover", StringComparison.OrdinalIgnoreCase) || (cover && selection.Kind is "project" or "cover"))
            {
                return new ProposedChangeSet("cover", false, true, "Regenerate only the cover.", [], text, text, null, null);
            }
        }

        var range = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2}:\d{2}(?::\d{2})?)\s*[–\-to]+\s*(\d{1,2}:\d{2}(?::\d{2})?)");
        double? from = selection.Start;
        double? to = selection.End;
        if (range.Success)
        {
            from = ParseClock(range.Groups[1].Value);
            to = ParseClock(range.Groups[2].Value);
        }
        if (from is null || to is null)
            throw new Domain.StudioException("needs_model", "The offline planner could not map this note to a typed edit. Select a time range or configure a model.");

        if (text.Contains("music", StringComparison.OrdinalIgnoreCase) || text.Contains("موسیقی", StringComparison.Ordinal) || text.Contains("lower the music", StringComparison.OrdinalIgnoreCase))
        {
            return new ProposedChangeSet(
                "audio",
                true,
                false,
                "Lower the music. AI Edit Studio 0.12 ducks music for the whole programme, so the selected range is marked and musicGain is reduced globally.",
                [
                    new EditOperation("add_marker", Time: from, Text: "music-duck"),
                    new EditOperation("set_options")
                ],
                null, null, null, 0.15);
        }

        if (text.Contains("subtitle", StringComparison.OrdinalIgnoreCase) || text.Contains("زیرنویس", StringComparison.Ordinal) || selection.Kind.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return new ProposedChangeSet("subtitle", true, false, "Update the subtitle in the selected range.", [
                new EditOperation("add_text", Start: from, End: to, Text: text, Style: "lower", Position: "low")
            ], null, null, text, null);
        }

        if (text.Contains("color", StringComparison.OrdinalIgnoreCase) || text.Contains("رنگ", StringComparison.Ordinal) || text.Contains("title", StringComparison.OrdinalIgnoreCase))
        {
            return new ProposedChangeSet("text", true, false, "Restyle on-screen text in the selected range.", [
                new EditOperation("add_text", Start: from, End: to ?? from + 2, Text: text, Style: "title", Position: "high")
            ], null, null, null, null);
        }

        return new ProposedChangeSet("range", true, false, "Remove the selected range.", [
            new EditOperation("delete_range", From: from, To: to)
        ], null, null, null, null);
    }

    public static double ParseClock(string value)
    {
        var parts = value.Split(':').Select(double.Parse).ToArray();
        return parts.Length switch
        {
            2 => parts[0] * 60 + parts[1],
            3 => parts[0] * 3600 + parts[1] * 60 + parts[2],
            _ => parts[0]
        };
    }
}
