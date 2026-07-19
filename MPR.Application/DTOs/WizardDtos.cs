using MPR.Domain.Enums;

namespace MPR.Application.DTOs;

public record CreateReportPeriodRequest(
    PeriodType PeriodType,
    DateTime FromDate,
    string MonthLabel);

public record WeekDateDto(int WeekNumber, string DateFamily, DateTime WeekEndingDate);

public record UploadedFileDto(
    int Id,
    string FileName,
    int GradeId,
    string GradeName,
    int? SubjectId,
    string? SubjectName,
    ProcessStatus Status);

/// <summary>One extracted cell awaiting human confirmation in the review step.</summary>
public record ExtractionReviewRowDto(
    int AttendanceRecordId,
    string StudentName,
    string? TeacherName,
    int SubjectId,
    string SubjectName,
    int? Week1,
    int? Week2,
    int? Week3,
    int? Week4,
    int? Week5,
    double? OcrConfidence,
    bool IsLowConfidence,
    bool IsStruckOut);

public record UpdateAttendanceCellRequest(
    int AttendanceRecordId,
    int WeekNumber,
    int Value); // 0 or 1

public record CorrectMarkRequest(int WeekNumber, string CorrectLabel); // CorrectLabel: "v" | "A" | "L"

public record MPRDetailRowDto(
    int GradeId,
    string GradeName,
    SubjectCategory Category,
    string? SubjectNameForGrade1112,
    int[] WeeklyPresent,
    double Total,
    int Mpr);

public record MPRSummaryRowDto(
    int GradeId,
    string GradeName,
    int RC,
    int O2O,
    int PW,
    int RowTotal,
    MprStatus Status);

public record FinalizeReportRequest(int ReportPeriodId);

public record ExportReportRequest(int ReportPeriodId, bool AsExcel, bool AsPdf);

public record EmailReportRequest(int ReportPeriodId, List<string> Recipients, string Subject, string? Message);
