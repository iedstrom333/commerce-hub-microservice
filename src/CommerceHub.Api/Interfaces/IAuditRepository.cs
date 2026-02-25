using CommerceHub.Api.Models;

namespace CommerceHub.Api.Interfaces;

public interface IAuditRepository
{
    Task LogAsync(AuditLog entry, CancellationToken ct = default);
}
