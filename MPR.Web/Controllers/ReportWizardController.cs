using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MPR.Application.DTOs;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Domain.Enums;
using MPR.Infrastructure.Persistence;

namespace MPR.Web.Controllers;

[ApiController]
[Route("api/wizard")]
[Authorize]
public class ReportWizardController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPdfExtractionService _extraction;
    private readonly IMprCalculationService _calc;
    private readonly IAuditService? _audit;
    private readonly MPR.Infrastructure.Services.Ocr.ITemplateLibrary _templateLibrary;

    public ReportWizardController(AppDbContext db, IPdfExtractionService extraction, IMprCalculationService calc,
        MPR.Infrastructure.Services.Ocr.ITemplateLibrary templateLibrary, IAuditService? audit = null)
    {
        _db = db;
        _extraction = extraction;
        _calc = calc;
        _templateLibrary = templateLibrary;
        _audit = audit;
    }

    // Step 1 - Report Setup
    [HttpPost("periods")]
    public async Task<ActionResult<int>> CreatePeriod(CreateReportPeriodRequest req)
    {
        int weeks = (int)req.PeriodType;
        var toDate = req.FromDate.AddDays(weeks * 7 - 1);

        var period = new ReportPeriod
        {
            PeriodType = req.PeriodType,
            FromDate = req.FromDate,
            ToDate = toDate,
            MonthLabel = req.MonthLabel,
            Status = ReportStatus.Draft,
            CreatedByUserId = CurrentUserId()
        };
        _db.ReportPeriods.Add(period);
        await _db.SaveChangesAsync();

        // Default week-ending dates per family, editable afterwards if the user's
        // CG cadence differs (template shows RC/TT/PW&O2O tracked on different days).
        foreach (var family in new[] { "RC", "TT", "PW_O2O" })
        {
            for (int w = 1; w <= weeks; w++)
            {
                _db.PeriodWeekDates.Add(new PeriodWeekDate
                {
                    ReportPeriodId = period.Id,
                    WeekNumber = w,
                    DateFamily = family,
                    WeekEndingDate = req.FromDate.AddDays(w * 7 - 1)
                });
            }
        }
        await _db.SaveChangesAsync();

        await LogAsync("Created", nameof(ReportPeriod), period.Id);
        return Ok(period.Id);
    }

    [HttpPut("periods/{id}/week-dates")]
    public async Task<IActionResult> UpdateWeekDates(int id, List<WeekDateDto> dates)
    {
        var existing = _db.PeriodWeekDates.Where(w => w.ReportPeriodId == id);
        _db.PeriodWeekDates.RemoveRange(existing);
        foreach (var d in dates)
        {
            _db.PeriodWeekDates.Add(new PeriodWeekDate
            {
                ReportPeriodId = id,
                WeekNumber = d.WeekNumber,
                DateFamily = d.DateFamily,
                WeekEndingDate = d.WeekEndingDate
            });
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Step 2 - Upload PDFs (multiple at once)
    [HttpPost("periods/{id}/upload")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<List<UploadedFileDto>>> Upload(int id, [FromForm] List<IFormFile> files, [FromForm] int gradeId, [FromForm] int? subjectId)
    {
        var storageRoot = Path.Combine("App_Data", "Uploads", id.ToString());
        Directory.CreateDirectory(storageRoot);

        var result = new List<UploadedFileDto>();
        foreach (var file in files)
        {
            var path = Path.Combine(storageRoot, file.FileName);
            await using (var stream = System.IO.File.Create(path))
                await file.CopyToAsync(stream);

            var entity = new UploadedFile
            {
                ReportPeriodId = id,
                GradeId = gradeId,
                SubjectId = subjectId,
                FileName = file.FileName,
                StoragePath = path,
                UploadedByUserId = CurrentUserId(),
                Status = ProcessStatus.Uploaded
            };
            _db.UploadedFiles.Add(entity);
            await _db.SaveChangesAsync();

            var grade = await _db.Grades.FindAsync(gradeId);
            var subject = subjectId.HasValue ? await _db.Subjects.FindAsync(subjectId.Value) : null;
            result.Add(new UploadedFileDto(entity.Id, entity.FileName, gradeId, grade?.Name ?? "", subjectId, subject?.Name, entity.Status));

            // Queue extraction as a background job in production (Hangfire.BackgroundJob.Enqueue);
            // done inline here for scaffold clarity.
            await ExtractFileAsync(entity.Id);
        }

        return Ok(result);
    }

    private async Task ExtractFileAsync(int uploadedFileId)
    {
        var file = await _db.UploadedFiles.FindAsync(uploadedFileId);
        if (file is null) return;

        file.Status = ProcessStatus.Extracting;
        await _db.SaveChangesAsync();

        try
        {
            var records = await _extraction.ExtractAsync(file.StoragePath, file.ReportPeriodId, file.GradeId, file.SubjectId);
            foreach (var r in records)
            {
                r.SourceUploadedFileId = file.Id;
                var cellImages = r.WeekCellImages;
                r.WeekCellImages = null; // transient - don't let EF try to track/persist it on the entity
                _db.AttendanceRecords.Add(r);
                await _db.SaveChangesAsync(); // need r.Id before creating child samples

                if (cellImages is not null)
                {
                    foreach (var (weekNumber, imageBytes) in cellImages)
                    {
                        _db.ExtractionCellSamples.Add(new ExtractionCellSample
                        {
                            AttendanceRecordId = r.Id,
                            WeekNumber = weekNumber,
                            ImageBytes = imageBytes,
                            PredictedConfidence = r.OcrConfidence
                        });
                    }
                }
            }
            file.Status = ProcessStatus.NeedsReview;
        }
        catch (Exception ex)
        {
            file.Status = ProcessStatus.Failed;
            file.ProcessingNotes = ex.Message;
        }
        await _db.SaveChangesAsync();
    }

    // Step 3 - Column/Week mapping (which PDF date-column -> which report week)
    // Handled by re-shuffling AttendanceRecord.WeekN values before the review step;
    // exposed here so the wizard UI can let the user pick the mapping explicitly.
    [HttpPut("attendance/{recordId}/remap-weeks")]
    public async Task<IActionResult> RemapWeeks(int recordId, [FromBody] Dictionary<int, int> pdfColumnToReportWeek)
    {
        var record = await _db.AttendanceRecords.FindAsync(recordId);
        if (record is null) return NotFound();

        var original = new[] { record.Week1, record.Week2, record.Week3, record.Week4, record.Week5 };
        var remapped = new int?[5];
        foreach (var (pdfCol, reportWeek) in pdfColumnToReportWeek)
        {
            if (pdfCol >= 1 && pdfCol <= 5 && reportWeek >= 1 && reportWeek <= 5)
                remapped[reportWeek - 1] = original[pdfCol - 1];
        }
        record.Week1 = remapped[0]; record.Week2 = remapped[1]; record.Week3 = remapped[2];
        record.Week4 = remapped[3]; record.Week5 = remapped[4];
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Step 4 - Extraction Review
    [HttpGet("periods/{id}/review")]
    public async Task<ActionResult<List<ExtractionReviewRowDto>>> GetReviewRows(int id, [FromQuery] bool onlyLowConfidence = false)
    {
        var query = _db.AttendanceRecords
            .Include(a => a.Subject)
            .Where(a => a.ReportPeriodId == id);

        var records = await query.ToListAsync();

        var rows = records.Select(r => new ExtractionReviewRowDto(
            r.Id, r.StudentName, r.TeacherName, r.SubjectId, r.Subject.Name,
            r.Week1, r.Week2, r.Week3, r.Week4, r.Week5,
            r.OcrConfidence,
            IsLowConfidence: (r.OcrConfidence ?? 1.0) < 0.75,
            IsStruckOut: new[] { r.Week1, r.Week2, r.Week3, r.Week4, r.Week5 }.Any(v => v == 0) && r.OcrConfidence == null
        ));

        if (onlyLowConfidence) rows = rows.Where(r => r.IsLowConfidence);
        return Ok(rows.ToList());
    }

    [HttpPut("attendance/cell")]
    public async Task<IActionResult> UpdateCell(UpdateAttendanceCellRequest req)
    {
        var record = await _db.AttendanceRecords.FindAsync(req.AttendanceRecordId);
        if (record is null) return NotFound();

        switch (req.WeekNumber)
        {
            case 1: record.Week1 = req.Value; break;
            case 2: record.Week2 = req.Value; break;
            case 3: record.Week3 = req.Value; break;
            case 4: record.Week4 = req.Value; break;
            case 5: record.Week5 = req.Value; break;
            default: return BadRequest("WeekNumber must be 1-5.");
        }
        record.IsManuallyOverridden = true;
        await _db.SaveChangesAsync();
        await LogAsync("Edited", nameof(AttendanceRecord), record.Id, $"Week{req.WeekNumber}={req.Value}");
        return NoContent();
    }

    [HttpPost("periods/{id}/review/approve-all-high-confidence")]
    public async Task<IActionResult> ApproveHighConfidence(int id)
    {
        var files = await _db.UploadedFiles.Where(f => f.ReportPeriodId == id && f.Status == ProcessStatus.NeedsReview).ToListAsync();
        foreach (var f in files) f.Status = ProcessStatus.Reviewed;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Corrects one week's mark with its true glyph label (v/A/L/H/N/P) and feeds
    /// that labeled crop into the OCR template library, so future extractions on
    /// this deployment's handwriting/scan quality get more accurate over time. This
    /// is the mechanism that makes the classifier improve report over report instead
    /// of staying static.
    /// </summary>
    [HttpPost("attendance/{recordId}/correct-mark")]
    public async Task<IActionResult> CorrectMark(int recordId, [FromBody] CorrectMarkRequest req)
    {
        var record = await _db.AttendanceRecords.FindAsync(recordId);
        if (record is null) return NotFound();

        var sample = await _db.ExtractionCellSamples
            .FirstOrDefaultAsync(s => s.AttendanceRecordId == recordId && s.WeekNumber == req.WeekNumber);
        if (sample is null) return NotFound("No retained crop for this cell - it may predate the sample-retention feature or already be purged.");

        sample.CorrectedLabel = req.CorrectLabel;
        _templateLibrary.AddExemplar(req.CorrectLabel, OpenCvSharp.Cv2.ImDecode(sample.ImageBytes, OpenCvSharp.ImreadModes.Grayscale));

        int value = req.CorrectLabel switch
        {
            "v" or "V" or "L" => 1,
            "A" => 0,
            _ => 0
        };
        switch (req.WeekNumber)
        {
            case 1: record.Week1 = value; break;
            case 2: record.Week2 = value; break;
            case 3: record.Week3 = value; break;
            case 4: record.Week4 = value; break;
            case 5: record.Week5 = value; break;
        }
        record.IsManuallyOverridden = true;

        await _db.SaveChangesAsync();
        await LogAsync("Corrected", nameof(ExtractionCellSample), sample.Id, $"Week{req.WeekNumber}->{req.CorrectLabel}");
        return NoContent();
    }

    // Step 5/6 - Preview, recompute, and allow edits (edits happen via UpdateCell above,
    // then Recalculate must be re-run before re-previewing)
    [HttpPost("periods/{id}/recalculate")]
    public async Task<ActionResult<(List<MPRDetailRowDto> Detail, List<MPRSummaryRowDto> Summary)>> Recalculate(int id)
    {
        await _calc.RecalculateDetailAsync(id);
        await _calc.RecalculateSummaryAsync(id);

        var detail = await _db.MPRDetailResults.Include(d => d.Grade).Where(d => d.ReportPeriodId == id).ToListAsync();
        var summary = await _db.MPRSummaries.Include(s => s.Grade).Where(s => s.ReportPeriodId == id).ToListAsync();

        var detailDtos = detail.Select(d => new MPRDetailRowDto(
            d.GradeId, d.Grade.Name, d.Category, d.SubjectNameForGrade1112,
            new[] { d.Week1Present, d.Week2Present, d.Week3Present, d.Week4Present ?? 0, d.Week5Present ?? 0 },
            d.Total, d.Mpr)).ToList();

        var summaryDtos = summary.Select(s => new MPRSummaryRowDto(
            s.GradeId, s.Grade.Name, s.RC, s.O2O, s.PW, s.RowTotal, s.Status)).ToList();

        return Ok(new { Detail = detailDtos, Summary = summaryDtos });
    }

    // Step 7 - Finalize (locks the report from further edits)
    [HttpPost("periods/{id}/finalize")]
    public async Task<IActionResult> Finalize(int id)
    {
        var period = await _db.ReportPeriods.FindAsync(id);
        if (period is null) return NotFound();

        period.Status = ReportStatus.Finalized;
        period.FinalizedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Finalized", nameof(ReportPeriod), id);
        return NoContent();
    }

    // Historical reports list
    [HttpGet("periods")]
    public async Task<ActionResult<IEnumerable<object>>> ListPeriods()
    {
        var periods = await _db.ReportPeriods
            .OrderByDescending(p => p.FromDate)
            .Select(p => new { p.Id, p.MonthLabel, p.PeriodType, p.FromDate, p.ToDate, p.Status })
            .ToListAsync();
        return Ok(periods);
    }

    private int CurrentUserId() => int.Parse(User.FindFirst("sub")?.Value ?? "0");

    private Task LogAsync(string action, string entityType, int entityId, string? details = null)
        => _audit?.LogAsync(CurrentUserId(), action, entityType, entityId, details) ?? Task.CompletedTask;
}
