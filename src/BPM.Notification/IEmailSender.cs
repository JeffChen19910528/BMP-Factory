namespace BPM.EmailDelivery;

// Phase 6.2 — the one seam between the delivery processor and "how an email actually leaves this
// process." Mirrors BPM.Application.Forms.IAttachmentStorage's own role (an external-system
// abstraction with a real production implementation and a swappable one for local dev/tests) —
// throw on failure rather than return a bool, matching IAttachmentStorage's own style; the
// processor's catch block is what turns a thrown exception into a recorded, bounded LastError.
public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default);
}
