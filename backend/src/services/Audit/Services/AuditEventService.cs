using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;

namespace Custodian.Audit.Services;

public class AuditEventService : IAuditEventService
{
    private readonly IAuditEventRepository _repository;

    public AuditEventService(IAuditEventRepository repository)
    {
        _repository = repository;
    }

    public async Task<AuditEventResponse> RecordEventAsync(CreateAuditEventRequest request, Guid effectiveTenantId)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.EngagementId == Guid.Empty)
        {
            throw new ArgumentException("EngagementId is required.", nameof(request.EngagementId));
        }

        if (string.IsNullOrWhiteSpace(request.Actor))
        {
            throw new ArgumentException("Actor is required.", nameof(request.Actor));
        }

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            throw new ArgumentException("Event Type is required.", nameof(request.Type));
        }

        // Validate JSON payload
        string validPayload = request.Payload;
        try
        {
            using var doc = JsonDocument.Parse(validPayload);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Payload must be a valid JSON string.", nameof(request.Payload));
        }

        var utcNow = DateTime.UtcNow;
        var eventId = Guid.NewGuid();

        // Calculate tamper-evident hash
        string hashInput = $"{eventId}:{request.EngagementId}:{effectiveTenantId}:{request.Actor}:{request.Type}:{utcNow:O}:{validPayload}";
        string computedHash = ComputeSha256Hash(hashInput);

        var auditEvent = new AuditEvent
        {
            EventId = eventId,
            EngagementId = request.EngagementId,
            TenantId = effectiveTenantId,
            Actor = request.Actor,
            Type = request.Type,
            Timestamp = utcNow,
            Payload = validPayload,
            Hash = computedHash
        };

        var createdEvent = await _repository.AddAsync(auditEvent);
        return MapToResponse(createdEvent);
    }

    public async Task<IEnumerable<AuditEventResponse>> GetEventsByEngagementAsync(Guid engagementId, Guid effectiveTenantId)
    {
        var events = await _repository.GetByEngagementIdAsync(engagementId, effectiveTenantId);
        return events.Select(MapToResponse);
    }

    public async Task<IEnumerable<AuditEventResponse>> GetEventsByTenantAsync(Guid effectiveTenantId)
    {
        var events = await _repository.GetByTenantIdAsync(effectiveTenantId);
        return events.Select(MapToResponse);
    }

    public async Task<AuditEventResponse?> GetEventByIdAsync(Guid eventId, Guid effectiveTenantId)
    {
        var auditEvent = await _repository.GetByIdAsync(eventId, effectiveTenantId);
        return auditEvent != null ? MapToResponse(auditEvent) : null;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        var builder = new StringBuilder();
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    private static AuditEventResponse MapToResponse(AuditEvent entity)
    {
        return new AuditEventResponse
        {
            EventId = entity.EventId,
            EngagementId = entity.EngagementId,
            TenantId = entity.TenantId,
            Actor = entity.Actor,
            Type = entity.Type,
            Timestamp = entity.Timestamp,
            Payload = entity.Payload,
            SequenceNumber = entity.SequenceNumber,
            Hash = entity.Hash
        };
    }
}
