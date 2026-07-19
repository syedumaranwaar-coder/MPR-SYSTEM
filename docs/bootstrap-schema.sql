-- MPR System: manual schema bootstrap, for use BEFORE `dotnet ef migrations` has been
-- run (e.g. if you want a working database today while sorting out the .NET toolchain).
-- Once real EF Core migrations exist, prefer those as the source of truth and retire
-- this script - it is a convenience fallback, not a substitute for migrations long-term.

IF DB_ID('MPRSystem') IS NULL
    CREATE DATABASE MPRSystem;
GO
USE MPRSystem;
GO

CREATE TABLE Grades (
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(20) NOT NULL,
    SortOrder INT NOT NULL,
    IsSubjectWiseOnly BIT NOT NULL DEFAULT 0
);

CREATE TABLE Subjects (
    Id INT IDENTITY PRIMARY KEY,
    GradeId INT NOT NULL REFERENCES Grades(Id),
    Name NVARCHAR(100) NOT NULL,
    Category INT NOT NULL, -- 0=RC,1=O2O,2=PW,3=TT
    O2OPairLabel NVARCHAR(200) NULL
);

CREATE TABLE Users (
    Id INT IDENTITY PRIMARY KEY,
    FullName NVARCHAR(200) NOT NULL,
    Email NVARCHAR(200) NOT NULL UNIQUE,
    UserName NVARCHAR(100) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(500) NOT NULL,
    Role INT NOT NULL, -- 0=Admin,1=Editor,2=Viewer
    CanExportOrEmail BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    MustChangePassword BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLoginAt DATETIME2 NULL
);

CREATE TABLE ReportPeriods (
    Id INT IDENTITY PRIMARY KEY,
    PeriodType INT NOT NULL, -- 3,4,5
    FromDate DATE NOT NULL,
    ToDate DATE NOT NULL,
    MonthLabel NVARCHAR(20) NOT NULL,
    Status INT NOT NULL DEFAULT 0, -- 0=Draft,1=InReview,2=Finalized
    CreatedByUserId INT NOT NULL REFERENCES Users(Id),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    FinalizedAt DATETIME2 NULL
);

CREATE TABLE PeriodWeekDates (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    WeekNumber INT NOT NULL,
    DateFamily NVARCHAR(10) NOT NULL, -- 'RC' | 'TT' | 'PW_O2O'
    WeekEndingDate DATE NOT NULL
);

CREATE TABLE UploadedFiles (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    GradeId INT NOT NULL REFERENCES Grades(Id),
    SubjectId INT NULL REFERENCES Subjects(Id),
    FileName NVARCHAR(300) NOT NULL,
    StoragePath NVARCHAR(500) NOT NULL,
    UploadedByUserId INT NOT NULL REFERENCES Users(Id),
    UploadedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Status INT NOT NULL DEFAULT 0,
    ProcessingNotes NVARCHAR(MAX) NULL
);

CREATE TABLE AttendanceRecords (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    GradeId INT NOT NULL REFERENCES Grades(Id),
    SubjectId INT NOT NULL REFERENCES Subjects(Id),
    StudentName NVARCHAR(200) NOT NULL,
    TeacherName NVARCHAR(200) NULL,
    SourceUploadedFileId INT NULL REFERENCES UploadedFiles(Id),
    Week1 INT NULL, Week2 INT NULL, Week3 INT NULL, Week4 INT NULL, Week5 INT NULL,
    IsManuallyOverridden BIT NOT NULL DEFAULT 0,
    OcrConfidence FLOAT NULL
);
CREATE INDEX IX_AttendanceRecords_Lookup ON AttendanceRecords(ReportPeriodId, GradeId, SubjectId, StudentName);

CREATE TABLE ExtractionCellSamples (
    Id INT IDENTITY PRIMARY KEY,
    AttendanceRecordId INT NOT NULL REFERENCES AttendanceRecords(Id),
    WeekNumber INT NOT NULL,
    ImageBytes VARBINARY(MAX) NOT NULL,
    PredictedLabel NVARCHAR(10) NULL,
    PredictedConfidence FLOAT NULL,
    CorrectedLabel NVARCHAR(10) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE MPRDetailResults (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    GradeId INT NOT NULL REFERENCES Grades(Id),
    Category INT NOT NULL,
    SubjectNameForGrade1112 NVARCHAR(100) NULL,
    Week1Present INT NOT NULL, Week2Present INT NOT NULL, Week3Present INT NOT NULL,
    Week4Present INT NULL, Week5Present INT NULL,
    Total FLOAT NOT NULL,
    Mpr INT NOT NULL
);

CREATE TABLE MPRSummaries (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    GradeId INT NOT NULL REFERENCES Grades(Id),
    RC INT NOT NULL DEFAULT 0,
    O2O INT NOT NULL DEFAULT 0,
    PW INT NOT NULL DEFAULT 0
);

CREATE TABLE AuditLogEntries (
    Id INT IDENTITY PRIMARY KEY,
    UserId INT NOT NULL,
    Action NVARCHAR(50) NOT NULL,
    EntityType NVARCHAR(100) NOT NULL,
    EntityId INT NOT NULL,
    Details NVARCHAR(MAX) NULL,
    Timestamp DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE EmailLogEntries (
    Id INT IDENTITY PRIMARY KEY,
    ReportPeriodId INT NOT NULL REFERENCES ReportPeriods(Id),
    SentByUserId INT NOT NULL,
    Recipients NVARCHAR(1000) NOT NULL,
    Subject NVARCHAR(300) NOT NULL,
    Success BIT NOT NULL,
    ErrorMessage NVARCHAR(MAX) NULL,
    SentAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Seed grades 1-10 + RC subjects + PW + O2O
DECLARE @g INT = 1;
WHILE @g <= 10
BEGIN
    INSERT INTO Grades (Name, SortOrder, IsSubjectWiseOnly) VALUES (CAST(@g AS NVARCHAR), @g, 0);
    DECLARE @gradeId INT = SCOPE_IDENTITY();
    INSERT INTO Subjects (GradeId, Name, Category) VALUES
        (@gradeId, 'Eng', 0), (@gradeId, 'Math', 0), (@gradeId, 'Reasoning', 0), (@gradeId, 'Science', 0),
        (@gradeId, 'Power Writing', 2), (@gradeId, 'O2O', 1);
    SET @g += 1;
END

INSERT INTO Grades (Name, SortOrder, IsSubjectWiseOnly) VALUES ('8TT', 11, 0);
DECLARE @tt INT = SCOPE_IDENTITY();
INSERT INTO Subjects (GradeId, Name, Category) VALUES (@tt, 'English', 3), (@tt, 'Maths', 3);

INSERT INTO Grades (Name, SortOrder, IsSubjectWiseOnly) VALUES ('11', 31, 1);
DECLARE @g11 INT = SCOPE_IDENTITY();
INSERT INTO Subjects (GradeId, Name, Category) VALUES
    (@g11,'Biology',0),(@g11,'Chemistry',0),(@g11,'FM/MM',0),(@g11,'English',0),(@g11,'GM',0);

INSERT INTO Grades (Name, SortOrder, IsSubjectWiseOnly) VALUES ('12', 32, 1);
DECLARE @g12 INT = SCOPE_IDENTITY();
INSERT INTO Subjects (GradeId, Name, Category) VALUES
    (@g12,'Biology',0),(@g12,'Chemistry',0),(@g12,'FM/MM',0),(@g12,'English',0),(@g12,'GM',0);

-- First admin user. Password hash below corresponds to a placeholder - DO NOT use this
-- literal hash in production; generate a real one via ASP.NET Core's PasswordHasher
-- (see MPR.Infrastructure/Persistence/DbSeeder.cs) as soon as the app runs, then log in
-- and change it immediately. Left as NULL here deliberately so a raw-SQL-only bootstrap
-- doesn't leave a guessable credential sitting in a database.
INSERT INTO Users (FullName, Email, UserName, PasswordHash, Role, CanExportOrEmail, MustChangePassword)
VALUES ('System Administrator', 'admin@yourdomain.com', 'admin', NULL, 0, 1, 1);
-- Set PasswordHash via the application's admin-reset-password path, or run DbSeeder.SeedAsync
-- once instead of this script if you'd rather have the hash generated correctly the first time.
GO
