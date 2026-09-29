using Custodian.Shared.Messaging;

namespace Custodian.Identity.Services.Notifications.Mappers;

public interface IEventToMessageMapper
{
    /// <returns>The client message, or null when the event is not client-facing and must not be sent.</returns>
    ClientSafeMessageResult? MapToClientSafeMessage(KafkaEnvelope envelope);
}
