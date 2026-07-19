using Microsoft.EntityFrameworkCore;
using MPR.Domain.Entities;

namespace MPR.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<ReportPeriod> ReportPeriods => Set<ReportPeriod>();
    public DbSet<PeriodWeekDate> PeriodWeekDates => Set<PeriodWeekDate>();
    public DbSet<UploadedFile> UploadedFiles => Set<UploadedFile>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<ExtractionCellSample> ExtractionCellSamples => Set<ExtractionCellSample>();
    public DbSet<MPRDetailResult> MPRDetailResults => Set<MPRDetailResult>();
    public DbSet<MPRSummary> MPRSummaries => Set<MPRSummary>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<EmailLogEntry> EmailLogEntries => Set<EmailLogEntry>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.UserName).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Subject>()
            .HasOne(s => s.Grade)
            .WithMany(g => g.Subjects)
            .HasForeignKey(s => s.GradeId);

        b.Entity<PeriodWeekDate>()
            .HasOne(w => w.ReportPeriod)
            .WithMany(p => p.WeekDates)
            .HasForeignKey(w => w.ReportPeriodId);

        b.Entity<UploadedFile>()
            .HasOne(f => f.ReportPeriod)
            .WithMany(p => p.UploadedFiles)
            .HasForeignKey(f => f.ReportPeriodId);

        b.Entity<AttendanceRecord>(e =>
        {
            e.HasOne(a => a.ReportPeriod).WithMany(p => p.AttendanceRecords).HasForeignKey(a => a.ReportPeriodId);
            e.HasOne(a => a.Grade).WithMany().HasForeignKey(a => a.GradeId);
            e.HasOne(a => a.Subject).WithMany().HasForeignKey(a => a.SubjectId);
            e.HasIndex(a => new { a.ReportPeriodId, a.GradeId, a.SubjectId, a.StudentName });
            e.Ignore(a => a.WeekCellImages); // transient extraction-time-only data, not persisted on the row
        });

        b.Entity<ExtractionCellSample>()
            .HasOne(s => s.AttendanceRecord).WithMany().HasForeignKey(s => s.AttendanceRecordId);

        b.Entity<ChatMessage>()
            .HasOne(m => m.ChatSession).WithMany(s => s.Messages).HasForeignKey(m => m.ChatSessionId);

        b.Entity<MPRDetailResult>()
            .HasOne(d => d.ReportPeriod).WithMany().HasForeignKey(d => d.ReportPeriodId);
        b.Entity<MPRDetailResult>()
            .HasOne(d => d.Grade).WithMany().HasForeignKey(d => d.GradeId);

        b.Entity<MPRSummary>()
            .HasOne(s => s.ReportPeriod).WithMany().HasForeignKey(s => s.ReportPeriodId);
        b.Entity<MPRSummary>()
            .HasOne(s => s.Grade).WithMany().HasForeignKey(s => s.GradeId);
        // RowTotal / Status are computed in code (not mapped) - see MPRSummary entity.
        b.Entity<MPRSummary>().Ignore(s => s.RowTotal);
        b.Entity<MPRSummary>().Ignore(s => s.Status);
    }
}
