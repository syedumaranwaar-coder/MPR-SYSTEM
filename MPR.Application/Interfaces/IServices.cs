using MPR.Domain.Entities;
using MPR.Domain.Enums;

namespace MPR.Application.Interfaces;

public interface IPdfExtractionService
{
    /// <summary>
    /// Runs OCR over the uploaded attendance PDF and produces draft AttendanceRecord
    /// rows (unsaved) with per-cell confidence scores. Struck-out rows/columns are
    /// detected and their week value forced to 0 with IsManuallyOverridden = false
    /// (still shown to the reviewer, just pre-resolved).
    /// </summary>
    Task<List<AttendanceRecord>> ExtractAsync(string pdfStoragePath, int reportPeriodId, int gradeId, int? subjectId, CancellationToken ct = default);
}

public interface IMprCalculationService
{
    /// <summary>
    /// Recomputes MPRDetailResult rows for a report period from AttendanceRecord data,
    /// applying the template's MAX (per week, across subjects in the same category)
    /// -> AVERAGE (across populated weeks) -> ROUNDUP (to 0 decimals) formula chain.
    /// </summary>
    Task RecalculateDetailAsync(int reportPeriodId, CancellationToken ct = default);

    /// <summary>
    /// Rolls up MPRDetailResult into MPRSummary (RC/O2O/PW per grade) and applies the
    /// Paid (>70) / Unpaid (<=70) rule on the row total.
    /// </summary>
    Task RecalculateSummaryAsync(int reportPeriodId, CancellationToken ct = default);
}

public interface IExcelExportService
{
    /// <summary>Produces a workbook stream matching the original MPR template layout exactly.</summary>
    Task<Stream> ExportAsync(int reportPeriodId, CancellationToken ct = default);
}

public interface IEmailService
{
    Task<bool> SendReportAsync(int reportPeriodId, List<string> recipients, string subject, string? message, Stream attachment, string attachmentFileName, CancellationToken ct = default);
}

public interface IAuditService
{
    Task LogAsync(int userId, string action, string entityType, int entityId, string? details = null);
}
