using System.Net;
using BPM.Domain.Entities;

namespace BPM.EmailDelivery;

// Phase 6.2 — code/configuration-based templates (Part K: "Do NOT build a full Template Designer
// UI... Do NOT add a database template management system"), selected purely by NotificationType.
// Deliberately lives in BPM.Notification, not inside WorkflowEngine/ApprovalEngine or
// NotificationService — those only ever produce a Notification's Title/Message; this is the one
// place that knows how to turn a NotificationType into an email Subject, and it reuses the
// Notification's own already-safe, already-backend-authored Title/Message as the body content
// rather than re-deriving process/task names a second way (Part L: don't dump FormData, don't
// duplicate business text). If a NotificationType is ever added without a case here, the switch's
// default keeps working rather than throwing — a missing template must never break delivery.
public static class EmailTemplates
{
    public static (string Subject, string HtmlBody, string TextBody) Render(NotificationType type, string title, string message)
    {
        var subject = SubjectFor(type);
        var safeMessage = WebUtility.HtmlEncode(message);
        var safeTitle = WebUtility.HtmlEncode(title);

        var html = $"""
            <p><strong>{safeTitle}</strong></p>
            <p>{safeMessage}</p>
            <p style="color:#888;font-size:12px;">This is an automated message from the BPM Platform. Please do not reply to this email.</p>
            """;
        var text = $"{title}\n\n{message}\n\n— BPM Platform (automated message, please do not reply)";

        return (subject, html, text);
    }

    private static string SubjectFor(NotificationType type) => type switch
    {
        NotificationType.TaskAssigned => "BPM Task Assigned",
        NotificationType.ApprovalRequired => "BPM Approval Required",
        NotificationType.ApprovalReturned => "BPM Approval Returned",
        NotificationType.TaskCompleted => "BPM Task Completed",
        NotificationType.ApprovalCompleted => "BPM Approval Completed",
        NotificationType.ProcessCompleted => "BPM Process Completed",
        NotificationType.ProcessRejected => "BPM Process Rejected",
        NotificationType.ProcessReturned => "BPM Process Returned",
        NotificationType.SlaWarning => "BPM SLA Warning",
        NotificationType.SlaOverdue => "BPM SLA Overdue",
        NotificationType.SlaEscalated => "BPM SLA Escalated",
        _ => "BPM Notification",
    };
}
