using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Infrastructure.Persistence;

namespace MPR.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    public AuditService(AppDbContext db) => _db = db;

    public async Task LogAsync(int userId, string action, string entityType, int entityId, string? details = null)
    {
        _db.AuditLogEntries.Add(new AuditLogEntry
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details
        });
        await _db.SaveChangesAsync();
    }
}
