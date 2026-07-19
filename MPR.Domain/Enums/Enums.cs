namespace MPR.Domain.Enums;

public enum PeriodType
{
    ThreeWeeks = 3,
    FourWeeks = 4,
    FiveWeeks = 5
}

public enum ReportStatus
{
    Draft = 0,
    InReview = 1,
    Finalized = 2
}

public enum ProcessStatus
{
    Uploaded = 0,
    Extracting = 1,
    NeedsReview = 2,
    Reviewed = 3,
    Failed = 4
}

public enum MarkCode
{
    None = 0,
    Present = 1,   // v
    Absent = 2,    // A
    Late = 3,      // L
    StruckOut = 4  // row crossed out for this week -> counts as 0
}

public enum HomeworkCode
{
    None = 0,
    FullyDone = 1,   // H
    NotDone = 2,     // N
    PartiallyDone = 3 // P
}

public enum SubjectCategory
{
    RC = 0,   // regular class (Eng/Math/Reasoning/Science etc.)
    O2O = 1,  // one-to-one
    PW = 2,   // power writing
    TT = 3    // 8TT special class type
}

public enum MprStatus
{
    NA = 0,
    Paid = 1,
    Unpaid = 2
}

public enum AppRole
{
    Admin = 0,
    Editor = 1,
    Viewer = 2
}
