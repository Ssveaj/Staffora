using System.Text.RegularExpressions;

namespace Staffora.Core.Messaging;

public sealed record MessageTemplate(string Subject, string Text, string? Link);

public sealed record RenderedMessage(string Subject, string Text, string? Link);

public static partial class MessageCatalogue
{
    public const int MaxSubjectLength = 200;

    public const int MaxTextLength = 2000;

    public const int MaxLinkLength = 500;

    public static IReadOnlyDictionary<MessageType, MessageTemplate> Templates { get; } = new Dictionary<MessageType, MessageTemplate>
    {
        [MessageTypes.UserReactivated] = new(
            Subject: "Your account is active again",
            Text: "Hello {{FirstName}}, an administrator of your organization reactivated your account. You can sign in again, and your roles grant their permissions.",
            Link: "/me/overview"),
        [MessageTypes.AbsenceLeaveRequested] = new(
            Subject: "Leave requested by {{Staff}}",
            Text: "{{Staff}} asks for planned leave {{Period}}. It blocks them from staffing only once an approver approves it.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceSickReported] = new(
            Subject: "Sick leave reported for {{Staff}}",
            Text: "{{Staff}} is reported sick {{Period}}. They are not staffed for that time, and an approver confirms or rejects the report.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceRegistered] = new(
            Subject: "Absence registered for you",
            Text: "Hello {{FirstName}}, a coordinator registered {{Kind}} for you {{Period}}. {{Status}}",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceApproved] = new(
            Subject: "Your {{Kind}} is approved",
            Text: "Hello {{FirstName}}, an approver approved your {{Kind}} {{Period}}. You are not staffed for that time.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceRejected] = new(
            Subject: "Your {{Kind}} was rejected",
            Text: "Hello {{FirstName}}, an approver rejected your {{Kind}} {{Period}}. It does not keep you from being staffed. The reason is written on the absence.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceCanceled] = new(
            Subject: "Your {{Kind}} was canceled",
            Text: "Hello {{FirstName}}, your {{Kind}} {{Period}} was canceled on your behalf. It no longer keeps you from being staffed.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceQuestionToStaff] = new(
            Subject: "New question about your absence",
            Text: "Hello {{FirstName}}, a coordinator wrote to you about your {{Kind}} {{Period}}.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceAnswerToStaff] = new(
            Subject: "New answer about your absence",
            Text: "Hello {{FirstName}}, a coordinator answered your question about your {{Kind}} {{Period}}. It waits for an approver again.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceQuestionFromStaff] = new(
            Subject: "New question about an absence",
            Text: "{{Staff}} has a question about their {{Kind}} {{Period}}.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceAnswerFromStaff] = new(
            Subject: "New answer on an absence",
            Text: "{{Staff}} answered the question about their {{Kind}} {{Period}}. It waits for an approver again.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AbsenceCanceledByStaff] = new(
            Subject: "An absence was canceled",
            Text: "{{Staff}} canceled their {{Kind}} {{Period}}. It no longer keeps them from being staffed.",
            Link: "?dlg=absence-{{AbsenceId}}"),
        [MessageTypes.AssignmentRequested] = new(
            Subject: "You are asked to work {{Job}}",
            Text: "Hello {{FirstName}}, a coordinator asks you to work {{Job}} as {{Slot}}, {{Period}}. Open the request to accept or decline it.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentAccepted] = new(
            Subject: "{{Staff}} accepted {{Job}}",
            Text: "{{Staff}} said yes to working {{Job}} as {{Slot}}. Assign them, or another staff member who said yes.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentDeclined] = new(
            Subject: "{{Staff}} declined {{Job}}",
            Text: "{{Staff}} declined working {{Job}} as {{Slot}}. Their reason is in the assignment's conversation.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentWithdrawn] = new(
            Subject: "{{Staff}} took back their yes for {{Job}}",
            Text: "{{Staff}} no longer says yes to working {{Job}} as {{Slot}}. Their reason is in the assignment's conversation.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentAssigned] = new(
            Subject: "You are assigned to {{Job}}",
            Text: "Hello {{FirstName}}, you are assigned to work {{Job}} as {{Slot}}, {{Period}}.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentNotSelected] = new(
            Subject: "Another staff member was picked for {{Job}}",
            Text: "Hello {{FirstName}}, another staff member was assigned to work {{Job}} as {{Slot}}, so your request is closed. Thank you for answering.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentRequestedInRound] = new(
            Subject: "You are asked to work on {{Job}}",
            Text: "Hello {{FirstName}}, a coordinator asks you to work on {{Job}} as {{Slot}}, {{Period}}. {{Rule}} Open the request to accept or decline it.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentTaken] = new(
            Subject: "{{Staff}} took a slot on {{Job}}",
            Text: "{{Staff}} said yes to {{Job}} as {{Slot}} and took an open slot of the request round.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentQuestionFromStaff] = new(
            Subject: "New question about a request",
            Text: "{{Staff}} has a question about working {{Job}} as {{Slot}} before answering the request.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentAnswerToStaff] = new(
            Subject: "New answer about your request",
            Text: "Hello {{FirstName}}, a coordinator answered your question about working {{Job}} as {{Slot}}. Open the request to accept or decline it.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.RoundFilled] = new(
            Subject: "Round for {{Slot}} on {{Job}} is filled",
            Text: "Round for {{Slot}} on {{Job}} is filled: {{Names}}.",
            Link: "?dlg=round-{{RoundId}}"),
        [MessageTypes.AssignmentExpired] = new(
            Subject: "{{Staff}} did not answer for {{Job}}",
            Text: "{{Staff}} did not answer the request to work {{Job}} as {{Slot}} in time, so it expired. Ask another staff member, or ask them again.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.RoundExpired] = new(
            Subject: "Round for {{Slot}} on {{Job}} expired",
            Text: "The time to answer the round for {{Slot}} on {{Job}} passed, so the requests nobody answered expired: {{Names}}. Ask other staff members for the open slots.",
            Link: "?dlg=round-{{RoundId}}"),
        [MessageTypes.AssignmentCanceled] = new(
            Subject: "{{Job}} is canceled for you",
            Text: "Hello {{FirstName}}, your request or assignment to work {{Job}} as {{Slot}} was canceled. You no longer work or are asked for it.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.AssignmentTakenOff] = new(
            Subject: "You are taken off {{Job}}",
            Text: "Hello {{FirstName}}, because of your {{Absence}} you no longer work {{Job}} as {{Slot}}. The coordinator asks someone else to work it.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.JobRequested] = new(
            Subject: "{{Customer}} ordered {{Job}}",
            Text: "{{Customer}} sent an order through its portal: {{Job}}, {{Period}}. The job waits for coordination: complete its timetable and slots, then start staffing it.",
            Link: "?dlg=job-{{JobId}}"),
        [MessageTypes.OrderConfirmed] = new(
            Subject: "{{Job}} is confirmed",
            Text: "Your staffing company confirmed {{Job}}, {{Period}}. Open the order to see its staff members.",
            Link: "?dlg=order-{{OrderId}}"),
        [MessageTypes.OrderCanceledBySupplier] = new(
            Subject: "{{Job}} was canceled by the staffing company",
            Text: "Your staffing company canceled {{Job}}, {{Period}}, after confirming it. The order is kept for the record.",
            Link: "?dlg=order-{{OrderId}}"),
        [MessageTypes.OrderDeclined] = new(
            Subject: "{{Job}} was declined",
            Text: "Your staffing company declined {{Job}}, {{Period}}, before confirming it. The order is kept for the record.",
            Link: "?dlg=order-{{OrderId}}"),
        [MessageTypes.OrderCompleted] = new(
            Subject: "{{Job}} is completed",
            Text: "The period of {{Job}}, {{Period}}, is over, and the job is completed. The order is kept for the record.",
            Link: "?dlg=order-{{OrderId}}"),
        [MessageTypes.JobCanceledInPortal] = new(
            Subject: "{{Customer}} canceled {{Job}}",
            Text: "{{Customer}} canceled its order through its portal, so {{Job}}, {{Period}}, is canceled by the customer. Its slots and every staff member asked for or assigned to it are canceled, and each staff member is told.",
            Link: "?dlg=job-{{JobId}}"),
        [MessageTypes.AssignmentStaffWrote] = new(
            Subject: "{{Staff}} wrote about {{Job}}",
            Text: "{{Staff}} wrote in the conversation about working {{Job}} as {{Slot}}. Open it to read and reply.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
        [MessageTypes.JobCustomerWrote] = new(
            Subject: "{{Customer}} wrote about {{Job}}",
            Text: "{{Author}} of {{Customer}} wrote in the customer thread of {{Job}}. Open the job to read and reply.",
            Link: "?dlg=job-{{JobId}}"),
        [MessageTypes.OrderSupplierWrote] = new(
            Subject: "{{Supplier}} wrote about {{Job}}",
            Text: "{{Supplier}} wrote in the messages about the order for {{Job}}. Open the order to read and reply.",
            Link: "?dlg=order-{{OrderId}}"),
        [MessageTypes.AssignmentCoordinatorWrote] = new(
            Subject: "{{Author}} wrote about {{Job}}",
            Text: "Hello {{FirstName}}, {{Author}} wrote in your conversation about working {{Job}} as {{Slot}}. Open it to read and reply.",
            Link: "?dlg=assignment-{{AssignmentId}}"),
    };

    public static MessageTemplate TemplateFor(MessageType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Templates.TryGetValue(type, out var template)
            ? template
            : throw new InvalidOperationException($"The message catalogue has no template for \"{type.Key}\".");
    }

    public static RenderedMessage Render(MessageType type, IReadOnlyDictionary<string, string?> values) =>
        Render(type, TemplateFor(type), values);

    public static RenderedMessage Render(MessageType type, MessageTemplate template, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (ProblemsOf(type, template) is [var problem, ..])
        {
            throw new InvalidOperationException(problem);
        }

        if (values.Keys.FirstOrDefault(name => !type.Placeholders.Contains(name, StringComparer.Ordinal)) is { } unknown)
        {
            throw new InvalidOperationException($"The message type \"{type.Key}\" has no placeholder \"{unknown}\".");
        }

        var link = template.Link is null ? null : Fill(template.Link, values, Uri.EscapeDataString);
        if (link is not null && (!IsLocalLink(link) || link.Length > MaxLinkLength))
        {
            throw new InvalidOperationException($"The link of \"{type.Key}\" is not a local address of the app once filled.");
        }

        return new RenderedMessage(Cut(Fill(template.Subject, values, value => value), MaxSubjectLength), Cut(Fill(template.Text, values, value => value), MaxTextLength), link);
    }

    public static RenderedMessage WithWords(RenderedMessage message, string? words)
    {
        ArgumentNullException.ThrowIfNull(message);
        return string.IsNullOrWhiteSpace(words) ? message : message with { Text = Cut($"{message.Text}\n\n{words.Trim()}", MaxTextLength) };
    }

    public static IReadOnlyList<string> PlaceholdersIn(string? text) =>
        text is null ? [] : [.. PlaceholderPattern().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyList<string> ProblemsOf(MessageType type, MessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(template.Subject) || string.IsNullOrWhiteSpace(template.Text))
        {
            problems.Add($"The template of \"{type.Key}\" has a blank subject or text.");
        }

        foreach (var (part, text) in new[] { ("subject", template.Subject), ("text", template.Text), ("link", template.Link) })
        {
            foreach (var name in PlaceholdersIn(text).Where(name => !type.Placeholders.Contains(name, StringComparer.Ordinal)))
            {
                problems.Add($"The {part} of \"{type.Key}\" uses {{{{{name}}}}}, which is not one of its own placeholders ({string.Join(", ", type.Placeholders)}).");
            }

            if (text is not null && PlaceholderPattern().Replace(text, string.Empty) is var rest && (rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal)))
            {
                problems.Add($"The {part} of \"{type.Key}\" has braces that are not a placeholder.");
            }
        }

        if (template.Link is { } link && !IsLocalLink(PlaceholderPattern().Replace(link, "x")))
        {
            problems.Add($"The link of \"{type.Key}\" is not a local address of the app.");
        }

        return problems;
    }

    public static bool IsLocalLink(string? link) =>
        link is { Length: > 0 }
        && !link.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
        && ((link[0] == '/' && !link.StartsWith("//", StringComparison.Ordinal)) || link.StartsWith("?dlg=", StringComparison.Ordinal));

    private static string Fill(string text, IReadOnlyDictionary<string, string?> values, Func<string, string> encode) =>
        PlaceholderPattern().Replace(text, match => values.TryGetValue(match.Groups[1].Value, out var value) && value is not null ? encode(value.Trim()) : string.Empty);

    private static string Cut(string text, int length) => text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex PlaceholderPattern();
}
