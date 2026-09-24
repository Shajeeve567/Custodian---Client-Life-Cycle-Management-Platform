using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.HashChain;

namespace Custodian.Audit.Services;

public class AuditEventService : IAuditEventService
{
    private readonly IAuditEventRepository _repository;
    private readonly IHashChainService _hashChain;

    public AuditEventService(IAuditEventRepository repository, IHashChainService hashChain)
    {
        _repository = repository;
        _hashChain = hashChain;
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

        string validPayload = request.Payload;
        try
        {
            using var doc = JsonDocument.Parse(validPayload);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Payload must be a valid JSON string.", nameof(request.Payload));
        }

        if (request.EventId.HasValue)
        {
            var existing = await _repository.GetByIdAsync(request.EventId.Value, effectiveTenantId);
            if (existing != null)
            {
                return MapToResponse(existing);
            }
        }

        var utcNow = DateTime.UtcNow;
        // MySQL datetime(6) stores microsecond precision; DateTime.Ticks is 100ns.
        // Truncate to microseconds before hashing so the value we hash is byte-for-byte
        // the value MySQL stores on round-trip.
        utcNow = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        var eventId = request.EventId ?? Guid.NewGuid();

        // Previous hash: the engagement's latest event, or genesis for the first.
        // Chains are scoped per (tenant, engagement) — not per tenant.
        var latest = await _repository.GetLatestForEngagementAsync(effectiveTenantId, request.EngagementId);
        var previousHash = latest?.Hash ?? _hashChain.GenesisHash;

        var hashInput = new EventHashInput
        {
            EventId = eventId,
            EngagementId = request.EngagementId,
            TenantId = effectiveTenantId,
            Actor = request.Actor,
            Type = request.Type,
            Timestamp = utcNow,
            Payload = validPayload,
            PreviousHash = previousHash,
        };

        var computedHash = _hashChain.ComputeEventHash(hashInput);

        var auditEvent = new AuditEvent
        {
            EventId = eventId,
            EngagementId = request.EngagementId,
            TenantId = effectiveTenantId,
            Actor = request.Actor,
            Type = request.Type,
            Timestamp = utcNow,
            Payload = validPayload,
            Hash = computedHash,
            PreviousHash = previousHash,
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

    public async Task<ChainVerificationResult> VerifyChainAsync(Guid effectiveTenantId, Guid engagementId)
    {
        var events = await _repository.GetByEngagementIdInChainOrderAsync(effectiveTenantId, engagementId);

        if (events.Count == 0)
        {
            return new ChainVerificationResult
            {
                EngagementId = engagementId,
                IsVerified = true,
                Count = 0,
            };
        }

        var expectedPrevious = _hashChain.GenesisHash;

        foreach (var e in events)
        {
            if (!string.Equals(e.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new ChainVerificationResult
                {
                    EngagementId = engagementId,
                    IsVerified = false,
                    Count = events.Count,
                    BrokenAtEventId = e.EventId,
                    Reason = "previous_hash does not match the prior event's hash",
                };
            }

            var input = new EventHashInput
            {
                EventId = e.EventId,
                EngagementId = e.EngagementId,
                TenantId = e.TenantId,
                Actor = e.Actor,
                Type = e.Type,
                Timestamp = e.Timestamp,
                Payload = e.Payload,
                PreviousHash = e.PreviousHash ?? string.Empty,
            };

            if (!_hashChain.VerifyEventHash(input, e.Hash ?? string.Empty))
            {
                return new ChainVerificationResult
                {
                    EngagementId = engagementId,
                    IsVerified = false,
                    Count = events.Count,
                    BrokenAtEventId = e.EventId,
                    Reason = "stored hash does not match recomputed hash",
                };
            }

            expectedPrevious = e.Hash!;
        }

        return new ChainVerificationResult
        {
            EngagementId = engagementId,
            IsVerified = true,
            Count = events.Count,
        };
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
            Hash = entity.Hash,
            PreviousHash = entity.PreviousHash,
        };
    }
}