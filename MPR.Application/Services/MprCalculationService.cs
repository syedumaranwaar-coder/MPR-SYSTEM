using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MPR.Application.Services;

/// <summary>
/// Re-implements, cell-for-cell, the formula chain found in the "MPR LEVEL 1" block
/// of the source template so it applies identically to every grade/category block:
///
///   No. Of Std Present (per week) = MAX(subject rows for that week)
///   Total                          = AVERAGE(weekly "No. Of Std Present" values)
///   MPR                            = ROUNDUP(Total, 0)
///
/// For O2O blocks the template additionally sums tutor-pair rows per week
/// (=SUM(pairRow1:pairRowN)) - that pair-total table is reproduced by
/// RecalculateO2OPairTotalsAsync in the same service.
/// </summary>
public class MprCalculationService : IMprCalculationService
{
    private readonly DbContext _db; // supply the concrete AppDbContext via DI in Infrastructure

    public MprCalculationService(DbContext db)
    {
        _db = db;
    }

    public async Task RecalculateDetailAsync(int reportPeriodId, CancellationToken ct = default)
    {
        var period = await _db.Set<ReportPeriod>()
            .Include(p => p.AttendanceRecords)
                .ThenInclude(a => a.Subject)
            .FirstAsync(p => p.Id == reportPeriodId, ct);

        int weekCount = (int)period.PeriodType; // 3, 4 or 5

        var existingDetail = _db.Set<MPRDetailResult>().Where(d => d.ReportPeriodId == reportPeriodId);
        _db.Set<MPRDetailResult>().RemoveRange(existingDetail);

        var recordsByGrade = period.AttendanceRecords.GroupBy(a => a.GradeId);

        foreach (var gradeGroup in recordsByGrade)
        {
            var grade = await _db.Set<Grade>().FirstAsync(g => g.Id == gradeGroup.Key, ct);

            if (grade.IsSubjectWiseOnly)
            {
                // Grades 11 & 12: one MPRDetailResult per subject (no MAX-across-subjects step,
                // since each subject stands alone rather than being merged into "No. Of Std Present").
                foreach (var subjectGroup in gradeGroup.GroupBy(a => a.SubjectId))
                {
                    var subject = subjectGroup.First().Subject;
                    var weekly = ComputeWeeklyMax(subjectGroup.ToList(), weekCount);
                    AddDetailResult(reportPeriodId, grade.Id, subject.Category, subject.Name, weekly);
                }
            }
            else
            {
                // Grades 1-10 / 8TT: MAX across all RC subjects (Eng/Math/Reasoning/Science) per
                // week feeds "No. Of Std Present" for the RC block; O2O and PW are their own blocks.
                foreach (var categoryGroup in gradeGroup.GroupBy(a => a.Subject.Category))
                {
                    var weekly = ComputeWeeklyMax(categoryGroup.ToList(), weekCount);
                    AddDetailResult(reportPeriodId, grade.Id, categoryGroup.Key, null, weekly);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private void AddDetailResult(int reportPeriodId, int gradeId, SubjectCategory category, string? subjectName, int[] weekly)
    {
        double total = Average(weekly);
        int mpr = RoundUp(total);

        _db.Set<MPRDetailResult>().Add(new MPRDetailResult
        {
            ReportPeriodId = reportPeriodId,
            GradeId = gradeId,
            Category = category,
            SubjectNameForGrade1112 = subjectName,
            Week1Present = weekly[0],
            Week2Present = weekly[1],
            Week3Present = weekly.Length > 2 ? weekly[2] : 0,
            Week4Present = weekly.Length > 3 ? weekly[3] : null,
            Week5Present = weekly.Length > 4 ? weekly[4] : null,
            Total = total,
            Mpr = mpr
        });
    }

    /// <summary>
    /// For each week column, takes MAX across all attendance rows in the group
    /// (a student counted once per week even if present in multiple subject rows
    /// that week - matching =MAX(D10:D12) in the template).
    /// </summary>
    private static int[] ComputeWeeklyMax(List<AttendanceRecord> records, int weekCount)
    {
        var result = new int[weekCount];
        for (int w = 0; w < weekCount; w++)
        {
            int max = 0;
            foreach (var r in records)
            {
                int? val = w switch
                {
                    0 => r.Week1,
                    1 => r.Week2,
                    2 => r.Week3,
                    3 => r.Week4,
                    4 => r.Week5,
                    _ => null
                };
                if (val.HasValue && val.Value > max) max = val.Value;
            }
            result[w] = max;
        }
        return result;
    }

    /// <summary>Excel AVERAGE() ignores blanks but not zeros - same semantics here.</summary>
    private static double Average(int[] values) => values.Length == 0 ? 0 : values.Average();

    /// <summary>Excel ROUNDUP(x, 0): always rounds away from zero, never down, for positive x.</summary>
    private static int RoundUp(double value) => (int)Math.Ceiling(value);

    public async Task RecalculateSummaryAsync(int reportPeriodId, CancellationToken ct = default)
    {
        var details = await _db.Set<MPRDetailResult>()
            .Where(d => d.ReportPeriodId == reportPeriodId)
            .ToListAsync(ct);

        var existing = _db.Set<MPRSummary>().Where(s => s.ReportPeriodId == reportPeriodId);
        _db.Set<MPRSummary>().RemoveRange(existing);

        foreach (var gradeGroup in details.GroupBy(d => d.GradeId))
        {
            int rc = gradeGroup.Where(d => d.Category == SubjectCategory.RC || d.Category == SubjectCategory.TT).Sum(d => d.Mpr);
            int o2o = gradeGroup.Where(d => d.Category == SubjectCategory.O2O).Sum(d => d.Mpr);
            int pw = gradeGroup.Where(d => d.Category == SubjectCategory.PW).Sum(d => d.Mpr);

            _db.Set<MPRSummary>().Add(new MPRSummary
            {
                ReportPeriodId = reportPeriodId,
                GradeId = gradeGroup.Key,
                RC = rc,
                O2O = o2o,
                PW = pw
                // RowTotal and Status are computed properties (RowTotal>70 => Paid), see MPRSummary entity.
            });
        }

        await _db.SaveChangesAsync(ct);
    }
}
