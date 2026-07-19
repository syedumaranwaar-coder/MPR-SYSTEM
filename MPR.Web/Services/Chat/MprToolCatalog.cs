namespace MPR.Web.Services.Chat;

/// <summary>
/// The set of MPR operations exposed to the chat agent as callable tools. Each maps
/// to a real backend operation already built for the wizard (ReportWizardController /
/// ReportsAnalyticsController / ExportController) - the chat agent is a conversational
/// front-end over the same logic, not a separate implementation.
///
/// File uploads are deliberately NOT a tool here: LLM tool-calling protocols pass
/// JSON arguments, not binary blobs, so PDF upload stays a normal HTTP multipart
/// request from the chat UI (see wwwroot/chat.html) straight to the existing
/// /api/wizard/periods/{id}/upload endpoint. After an upload completes, the UI tells
/// the agent about it in plain text ("I've uploaded 3 files for Grade 5"), and the
/// agent uses list_uploaded_files to see and act on them.
/// </summary>
public static class MprToolCatalog
{
    public static List<ToolDefinition> All => new()
    {
        new ToolDefinition(
            "list_report_periods",
            "List existing MPR report periods (historical and in-progress), most recent first.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),

        new ToolDefinition(
            "create_report_period",
            "Start a new MPR report for a given period length and start date.",
            new
            {
                type = "object",
                properties = new
                {
                    periodWeeks = new { type = "integer", @enum = new[] { 3, 4, 5 }, description = "Report length in weeks" },
                    fromDate = new { type = "string", format = "date", description = "First day of the report period, YYYY-MM-DD" },
                    monthLabel = new { type = "string", description = "Label for the report, e.g. '2026-05'" }
                },
                required = new[] { "periodWeeks", "fromDate", "monthLabel" }
            }),

        new ToolDefinition(
            "list_uploaded_files",
            "List the PDF files uploaded so far for a report period, with their processing status.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "get_extraction_review_summary",
            "Get a summary of how many attendance rows need manual review (low OCR confidence) for a report period, so the user knows what still needs checking before the report can be trusted.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "recalculate_mpr",
            "Recompute the MPR detail and summary numbers for a report period from the current attendance data. Call this after uploads finish processing or after any manual corrections.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "get_mpr_preview",
            "Get the current MPR detail rows (per grade/subject) and summary rows (RC/O2O/PW totals, Paid/Unpaid status) for a report period.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "finalize_report",
            "Lock a report period as finalized once the user confirms the numbers are correct. This cannot be easily undone, so only call it after explicit user confirmation.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "get_excel_download_link",
            "Get the download URL for the report period's Excel export in the MPR template format.",
            new
            {
                type = "object",
                properties = new { reportPeriodId = new { type = "integer" } },
                required = new[] { "reportPeriodId" }
            }),

        new ToolDefinition(
            "email_report",
            "Email the Excel MPR report to one or more recipients. Only call this after the user has explicitly confirmed the recipient list.",
            new
            {
                type = "object",
                properties = new
                {
                    reportPeriodId = new { type = "integer" },
                    recipients = new { type = "array", items = new { type = "string" }, description = "Recipient email addresses" },
                    subject = new { type = "string" }
                },
                required = new[] { "reportPeriodId", "recipients", "subject" }
            }),

        new ToolDefinition(
            "get_low_attendance_students",
            "List students below an attendance percentage threshold for a report period - useful when the user asks about at-risk or frequently-absent students.",
            new
            {
                type = "object",
                properties = new
                {
                    reportPeriodId = new { type = "integer" },
                    thresholdPercent = new { type = "number", description = "Defaults to 75 if not specified" }
                },
                required = new[] { "reportPeriodId" }
            })
    };
}
