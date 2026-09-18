using BPM.Domain.Entities;
using BPM.EmailDelivery;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.2 — pure, DB-free coverage of EmailTemplates: subject selection per NotificationType,
// body rendering, HTML-encoding of user-influenced content, and the "no sensitive data" guarantee.
public class EmailTemplatesTests
{
    [Theory]
    [InlineData(NotificationType.TaskAssigned, "BPM Task Assigned")]
    [InlineData(NotificationType.ApprovalRequired, "BPM Approval Required")]
    [InlineData(NotificationType.ApprovalReturned, "BPM Approval Returned")]
    [InlineData(NotificationType.TaskCompleted, "BPM Task Completed")]
    [InlineData(NotificationType.ApprovalCompleted, "BPM Approval Completed")]
    [InlineData(NotificationType.ProcessCompleted, "BPM Process Completed")]
    [InlineData(NotificationType.ProcessRejected, "BPM Process Rejected")]
    [InlineData(NotificationType.ProcessReturned, "BPM Process Returned")]
    public void Render_SelectsCorrectSubject_ForEachNotificationType(NotificationType type, string expectedSubject)
    {
        var (subject, _, _) = EmailTemplates.Render(type, "Title", "Message");
        Assert.Equal(expectedSubject, subject);
    }

    [Fact]
    public void Render_IncludesTitleAndMessage_InBothHtmlAndTextBodies()
    {
        var (_, html, text) = EmailTemplates.Render(NotificationType.TaskAssigned, "Task Assigned", "You have been assigned the task \"Review\".");

        Assert.Contains("Task Assigned", html);
        Assert.Contains("You have been assigned the task", html);
        Assert.Contains("Task Assigned", text);
        Assert.Contains("You have been assigned the task", text);
    }

    [Fact]
    public void Render_HtmlEncodesMessageContent_NoRawMarkupInjection()
    {
        var (_, html, _) = EmailTemplates.Render(NotificationType.TaskAssigned, "Title", "<script>alert('xss')</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_DoesNotIncludeUnrelatedSensitiveContent()
    {
        var (_, html, text) = EmailTemplates.Render(NotificationType.ApprovalRequired, "Approval Required", "Your approval is required for \"Manager Approval\".");

        Assert.DoesNotContain("password", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }
}
