using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Infrastructure.Persistence;
using MPR.Web.Services.Chat;

namespace MPR.Web.Controllers;

public record SendMessageRequest(int? SessionId, string Message);
public record SendMessageResponse(int SessionId, string Reply, List<string> ToolsUsed);

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly OllamaChatClient _ollama;
    private readonly IMprCalculationService _calc;
    private readonly IExcelExportService _excel;
    private readonly IEmailService _email;

    private const string SystemPrompt = """
        You are the MPR Report Assistant, embedded in a monthly progress report (MPR)
        system for a tutoring organization. You help staff generate, review, and send
        MPR reports by calling the tools available to you - you cannot directly read
        PDFs or edit numbers yourself, only through the tools.

        Guidelines:
        - Report periods are 3, 4, or 5 weeks long. Always confirm the period length
          and start date with the user before calling create_report_period if either
          is ambiguous.
        - PDF uploads happen through the chat UI's upload button, not through you -
          when the user says they've uploaded files, use list_uploaded_files to see
          what's there and check processing status.
        - Never call finalize_report or email_report without an explicit, unambiguous
          confirmation from the user in this conversation. If in doubt, ask first.
        - Before presenting final numbers, check get_extraction_review_summary - if
          there are low-confidence rows, tell the user those need manual review
          before the report can be trusted, and don't imply the numbers are final.
        - When you show MPR totals, state the Paid/Unpaid status per grade plainly
          (>70 total = Paid, otherwise Unpaid) since that's what the user cares about.
        - Keep replies concise and concrete - state what you did and what the numbers
          are, don't pad with generic pleasantries.
        """;

    public ChatController(AppDbContext db, OllamaChatClient ollama, IMprCalculationService calc, IExcelExportService excel, IEmailService email)
    {
        _db = db;
        _ollama = ollama;
        _calc = calc;
        _excel = excel;
        _email = email;
    }

    [HttpPost("message")]
    public async Task<ActionResult<SendMessageResponse>> SendMessage(SendMessageRequest req, CancellationToken ct)
    {
        int userId = CurrentUserId();

        var session = req.SessionId.HasValue
            ? await _db.ChatSessions.Include(s => s.Messages).FirstOrDefaultAsync(s => s.Id == req.SessionId && s.UserId == userId, ct)
            : null;

        if (session is null)
        {
            session = new ChatSession { UserId = userId, Title = Truncate(req.Message, 60) };
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync(ct);
        }

        _db.ChatMessages.Add(new ChatMessage { ChatSessionId = session.Id, Role = "user", Content = req.Message });
        await _db.SaveChangesAsync(ct);

        var history = new List<ChatTurn> { new("system", SystemPrompt) };
        foreach (var m in session.Messages.OrderBy(m => m.CreatedAt))
            history.Add(new ChatTurn(m.Role, m.Content, ToolName: m.ToolName));

        var executor = new MprToolExecutor(_db, _calc, _excel, _email, userId);
        var toolsUsed = new List<string>();

        // Tool-calling loop: keep feeding tool results back to the model until it
        // produces a plain text reply with no further tool calls, capped to avoid a
        // runaway loop if the model keeps calling tools indefinitely.
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var result = await _ollama.ChatAsync(history, MprToolCatalog.All, ct);

            if (result.ToolCalls.Count == 0)
            {
                string reply = result.Content ?? "I wasn't able to generate a response - please try rephrasing.";
                _db.ChatMessages.Add(new ChatMessage { ChatSessionId = session.Id, Role = "assistant", Content = reply });
                session.LastMessageAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Ok(new SendMessageResponse(session.Id, reply, toolsUsed));
            }

            history.Add(new ChatTurn("assistant", result.Content));

            foreach (var call in result.ToolCalls)
            {
                toolsUsed.Add(call.FunctionName);
                string toolResult = await executor.ExecuteAsync(call.FunctionName, call.Arguments, ct);

                _db.ChatMessages.Add(new ChatMessage
                {
                    ChatSessionId = session.Id,
                    Role = "tool",
                    Content = toolResult,
                    ToolName = call.FunctionName,
                    ToolCallsJson = call.Arguments.GetRawText()
                });

                history.Add(new ChatTurn("tool", toolResult, ToolCallId: call.Id, ToolName: call.FunctionName));
            }
            await _db.SaveChangesAsync(ct);
        }

        const string fallback = "I made several tool calls but couldn't reach a final answer - please check the report period status directly or try a more specific request.";
        _db.ChatMessages.Add(new ChatMessage { ChatSessionId = session.Id, Role = "assistant", Content = fallback });
        await _db.SaveChangesAsync(ct);
        return Ok(new SendMessageResponse(session.Id, fallback, toolsUsed));
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions()
    {
        var sessions = await _db.ChatSessions
            .Where(s => s.UserId == CurrentUserId())
            .OrderByDescending(s => s.LastMessageAt)
            .Select(s => new { s.Id, s.Title, s.LastMessageAt, s.LinkedReportPeriodId })
            .ToListAsync();
        return Ok(sessions);
    }

    [HttpGet("sessions/{id}")]
    public async Task<IActionResult> GetSession(int id)
    {
        var session = await _db.ChatSessions
            .Include(s => s.Messages)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == CurrentUserId());
        if (session is null) return NotFound();

        var visible = session.Messages
            .Where(m => m.Role is "user" or "assistant")
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Content, m.CreatedAt });
        return Ok(visible);
    }

    private int CurrentUserId() => int.Parse(User.FindFirst("sub")?.Value ?? "0");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
