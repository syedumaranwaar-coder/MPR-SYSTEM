using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Infrastructure.Persistence;

namespace MPR.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;

    public EmailService(IConfiguration config, AppDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<bool> SendReportAsync(int reportPeriodId, List<string> recipients, string subject, string? message, Stream attachment, string attachmentFileName, CancellationToken ct = default)
    {
        var smtp = _config.GetSection("Smtp");
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(smtp["FromDisplayName"], smtp["User"]));
        foreach (var r in recipients) msg.To.Add(MailboxAddress.Parse(r));
        msg.Subject = subject;

        var body = new BodyBuilder { TextBody = message ?? "Please find the attached MPR report." };
        attachment.Position = 0;
        body.Attachments.Add(attachmentFileName, attachment);
        msg.Body = body.ToMessageBody();

        var log = new EmailLogEntry
        {
            ReportPeriodId = reportPeriodId,
            Recipients = string.Join(",", recipients),
            Subject = subject,
            SentByUserId = 0 // set by caller/controller from the authenticated user claim in production
        };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(smtp["Host"], int.Parse(smtp["Port"]!), MailKit.Security.SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(smtp["User"], smtp["Password"], ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);

            log.Success = true;
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.ErrorMessage = ex.Message;
        }

        _db.EmailLogEntries.Add(log);
        await _db.SaveChangesAsync(ct);
        return log.Success;
    }
}
