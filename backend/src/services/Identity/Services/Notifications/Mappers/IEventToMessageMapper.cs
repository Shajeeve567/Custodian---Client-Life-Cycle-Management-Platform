using Custodian.Shared.Messaging;

namespace Custodian.Identity.Services.Notifications.Mappers;

public interface IEventToMessageMapper
{
    ClientSafeMessageResult MapToClientSafeMessage(KafkaEnvelope envelope);
}
