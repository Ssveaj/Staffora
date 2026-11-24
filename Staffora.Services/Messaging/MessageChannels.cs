using Staffora.Core.Entities.Messaging;

namespace Staffora.Services.Messaging;

public enum MessageChannel
{
    InApp = 0,
}

public interface IChannelSender
{
    MessageChannel Channel { get; }

    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

public sealed class MessageChannelRouter
{
    private readonly IReadOnlyList<IChannelSender> senders;

    public MessageChannelRouter(IEnumerable<IChannelSender> senders)
    {
        this.senders = [.. senders];
    }

    public static IReadOnlyList<MessageChannel> ChannelsOf(OutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return [MessageChannel.InApp];
    }

    public IReadOnlyList<IChannelSender> SendersOf(OutgoingMessage message) =>
        [.. ChannelsOf(message).Select(channel => this.senders.FirstOrDefault(sender => sender.Channel == channel)
            ?? throw new InvalidOperationException($"No sender is registered for the {channel} channel."))];
}
