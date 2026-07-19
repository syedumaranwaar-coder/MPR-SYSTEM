using Microsoft.AspNetCore.Identity;
using MPR.Domain.Entities;
using MPR.Domain.Enums;

namespace MPR.Infrastructure.Persistence;

/// <summary>
/// Run once against a freshly-migrated database:
///   await DbSeeder.SeedAsync(app.Services);
/// in Program.cs after `app.Build()`, or invoke from a one-off console entry point.
/// Idempotent - safe to call on every startup, it checks for existing rows first.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(); // remove once real migrations are in place

        if (!db.Grades.Any())
            SeedGradesAndSubjects(db);

        if (!db.Users.Any())
            SeedAdminUser(db);

        await db.SaveChangesAsync();
    }

    private static void SeedGradesAndSubjects(AppDbContext db)
    {
        var rcSubjects = new[] { "Eng", "Math", "Reasoning", "Science" };

        for (int g = 1; g <= 10; g++)
        {
            var grade = new Grade { Name = g.ToString(), SortOrder = g, IsSubjectWiseOnly = false };
            db.Grades.Add(grade);

            foreach (var s in rcSubjects)
                grade.Subjects.Add(new Subject { Name = s, Category = SubjectCategory.RC });

            grade.Subjects.Add(new Subject { Name = "Power Writing", Category = SubjectCategory.PW });
            grade.Subjects.Add(new Subject { Name = "O2O", Category = SubjectCategory.O2O });
        }

        // 8TT is tracked separately per the sample sheets (Yr-8 TT Class Attendance)
        var grade8tt = new Grade { Name = "8TT", SortOrder = 11, IsSubjectWiseOnly = false };
        grade8tt.Subjects.Add(new Subject { Name = "English", Category = SubjectCategory.TT });
        grade8tt.Subjects.Add(new Subject { Name = "Maths", Category = SubjectCategory.TT });
        db.Grades.Add(grade8tt);

        // Grades 11 & 12: subject-wise blocks, not merged into a single RC "No. Of Std Present"
        var subjectWiseSubjects = new[] { "Biology", "Chemistry", "FM/MM", "English", "GM" };
        foreach (var g in new[] { 11, 12 })
        {
            var grade = new Grade { Name = g.ToString(), SortOrder = 20 + g, IsSubjectWiseOnly = true };
            foreach (var s in subjectWiseSubjects)
                grade.Subjects.Add(new Subject { Name = s, Category = SubjectCategory.RC });
            db.Grades.Add(grade);
        }
    }

    private static void SeedAdminUser(AppDbContext db)
    {
        var hasher = new PasswordHasher<AppUser>();
        var admin = new AppUser
        {
            FullName = "System Administrator",
            Email = "admin@yourdomain.com",
            UserName = "admin",
            Role = AppRole.Admin,
            CanExportOrEmail = true,
            MustChangePassword = true
        };
        // CHANGE THIS before running against anything but a local dev database.
        admin.PasswordHash = hasher.HashPassword(admin, "ChangeMe!2026");
        db.Users.Add(admin);
    }
}
