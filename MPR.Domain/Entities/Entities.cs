using MPR.Domain.Enums;

namespace MPR.Domain.Entities;

public class AppUser
{
    public int Id { get; set; }
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string UserName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public AppRole Role { get; set; }
    public bool CanExportOrEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public class Grade
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;      // "1".."10","11","12","8TT"
    public int SortOrder { get; set; }
    public bool IsSubjectWiseOnly { get; set; }     // true for 11 & 12
    public ICollection<Subject> Subjects { get; set; } = new List<Subject>();
}

public class Subject
{
    public int Id { get; set; }
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public string Name { get; set; } = null!;       // Eng, Math, Reasoning, Science, Power Writing, Biology...
    public SubjectCategory Category { get; set; }
    public string? O2OPairLabel { get; set; }       // e.g. "Amir - Gabi" when Category = O2O
}

public class ReportPeriod
{
    public int Id { get; set; }
    public PeriodType PeriodType { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string MonthLabel { get; set; } = null!; // "2026-05"
    public ReportStatus Status { get; set; } = ReportStatus.Draft;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; set; }

    // CG reference dates differ by subject family, mirroring the template's
    // "CG Date" (RC) vs "CG Date TT" vs "CG Date PW & O2O" rows.
    public ICollection<PeriodWeekDate> WeekDates { get; set; } = new List<PeriodWeekDate>();
    public ICollection<UploadedFile> UploadedFiles { get; set; } = new List<UploadedFile>();
    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
}

public class PeriodWeekDate
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public ReportPeriod ReportPeriod { get; set; } = null!;
    public int WeekNumber { get; set; }             // 1..5
    public string DateFamily { get; set; } = null!;  // "RC" | "TT" | "PW_O2O"
    public DateTime WeekEndingDate { get; set; }
}

public class UploadedFile
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public ReportPeriod ReportPeriod { get; set; } = null!;
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public int? SubjectId { get; set; }
    public Subject? Subject { get; set; }
    public string FileName { get; set; } = null!;
    public string StoragePath { get; set; } = null!;
    public int UploadedByUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public ProcessStatus Status { get; set; } = ProcessStatus.Uploaded;
    public string? ProcessingNotes { get; set; }
}

/// <summary>
/// One row per student per subject per report period. WeekN holds the resolved
/// mark (Present/Absent/Late/StruckOut) already collapsed to a 0/1 attendance
/// value by the extraction + review pipeline (v or L => 1, A or struck-out => 0).
/// </summary>
public class AttendanceRecord
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public ReportPeriod ReportPeriod { get; set; } = null!;
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public int SubjectId { get; set; }
    public Subject Subject { get; set; } = null!;
    public string StudentName { get; set; } = null!;
    public string? TeacherName { get; set; }
    public int? SourceUploadedFileId { get; set; }

    public int? Week1 { get; set; } // 0 or 1, null = no data for that week
    public int? Week2 { get; set; }
    public int? Week3 { get; set; }
    public int? Week4 { get; set; }
    public int? Week5 { get; set; }

    public bool IsManuallyOverridden { get; set; }
    public double? OcrConfidence { get; set; } // lowest confidence among the row's marks, 0-1

    /// <summary>
    /// Transient, not mapped to the database: PNG bytes of each week's cropped cell
    /// image, populated by IPdfExtractionService during extraction and consumed once
    /// by the wizard to persist ExtractionCellSample rows. Kept off the main table so
    /// AttendanceRecord itself stays small; only cells a reviewer actually corrects
    /// need their image retained long-term (see ExtractionCellSample).
    /// </summary>
    public Dictionary<int, byte[]>? WeekCellImages { get; set; }
}

/// <summary>
/// Computed "No. Of Std Present" row per grade+subject-category block,
/// mirroring MAX(week columns across subjects)/AVERAGE/ROUNDUP in the template.
/// </summary>
public class MPRDetailResult
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public ReportPeriod ReportPeriod { get; set; } = null!;
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public SubjectCategory Category { get; set; }
    public string? SubjectNameForGrade1112 { get; set; } // only set when Grade IsSubjectWiseOnly

    public int Week1Present { get; set; }
    public int Week2Present { get; set; }
    public int Week3Present { get; set; }
    public int? Week4Present { get; set; }
    public int? Week5Present { get; set; }

    public double Total { get; set; }   // AVERAGE of populated week columns
    public int Mpr { get; set; }        // ROUNDUP(Total, 0)
}

/// <summary>
/// A retained crop of one week's attendance cell, created when that cell needs (or
/// received) manual review. Correcting a sample's Label here is what feeds
/// ITemplateLibrary.AddExemplar, closing the loop so the classifier gets better at
/// this organization's actual handwriting over successive reports.
/// </summary>
public class ExtractionCellSample
{
    public int Id { get; set; }
    public int AttendanceRecordId { get; set; }
    public AttendanceRecord AttendanceRecord { get; set; } = null!;
    public int WeekNumber { get; set; }
    public byte[] ImageBytes { get; set; } = null!;
    public string? PredictedLabel { get; set; }
    public double? PredictedConfidence { get; set; }
    public string? CorrectedLabel { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MPRSummary
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public ReportPeriod ReportPeriod { get; set; } = null!;
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;

    public int RC { get; set; }
    public int O2O { get; set; }
    public int PW { get; set; }

    public int RowTotal => RC + O2O + PW;
    public MprStatus Status => RowTotal > 70 ? MprStatus.Paid : MprStatus.Unpaid;
}

public class AuditLogEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Action { get; set; } = null!;      // "Created", "Edited", "Finalized", "Exported", "Emailed"
    public string EntityType { get; set; } = null!;  // "ReportPeriod", "AttendanceRecord", "User"
    public int EntityId { get; set; }
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class EmailLogEntry
{
    public int Id { get; set; }
    public int ReportPeriodId { get; set; }
    public int SentByUserId { get; set; }
    public string Recipients { get; set; } = null!;  // comma separated
    public string Subject { get; set; } = null!;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One conversation thread with the MPR chat agent. Kept separate from ReportPeriod
/// because a session may span browsing history, asking questions, or starting a
/// report before one exists yet - LinkedReportPeriodId is set once the agent creates
/// or opens one during the conversation.
/// </summary>
public class ChatSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? LinkedReportPeriodId { get; set; }
    public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public class ChatMessage
{
    public int Id { get; set; }
    public int ChatSessionId { get; set; }
    public ChatSession ChatSession { get; set; } = null!;
    public string Role { get; set; } = null!; // "user" | "assistant" | "tool"
    public string Content { get; set; } = "";
    /// <summary>JSON-serialized tool calls the assistant made on this turn, if any - kept for audit/replay.</summary>
    public string? ToolCallsJson { get; set; }
    public string? ToolName { get; set; } // set on Role="tool" messages, which tool produced this result
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
