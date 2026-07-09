namespace Cirreum.Runtime.Messaging;

using Cirreum.Conductor;
using Cirreum.Messaging;

/// <summary>
/// The outbound Conductor bridge for the <see cref="DistributedMessage"/> channel.
/// Intercepts any published notification deriving from <see cref="DistributedMessage"/>
/// and forwards it to the channel's delivery engine for external distribution.
/// </summary>
/// <typeparam name="TMessage">The type of message to handle, which must derive from
/// <see cref="DistributedMessage"/>.</typeparam>
/// <param name="deliveryEngine">The channel's delivery engine.</param>
/// <remarks>
/// <para>
/// Registered as an open generic against <see cref="INotificationHandler{TNotification}"/>
/// when the channel has a configured transport, so apps publish once via
/// <c>IPublisher.PublishAsync(myMessage)</c> and Conductor fans out to all in-process
/// handlers AND to the external transport.
/// </para>
/// <para>
/// Inbound messages do not re-enter this bridge: the receiver dispatches them wrapped in
/// <see cref="DistributedMessageReceived{TMessage}"/>, a distinct notification shape this
/// handler's constraint does not catch.
/// </para>
/// </remarks>
internal sealed class DistributedMessageSender<TMessage>(
	DistributedMessageDeliveryEngine deliveryEngine
) : INotificationHandler<TMessage>
	where TMessage : notnull, DistributedMessage {

	/// <summary>
	/// Handles the message by publishing it to the external messaging system.
	/// </summary>
	/// <param name="notification">The <see cref="DistributedMessage"/> to be distributed externally.</param>
	/// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
	/// <returns>A task representing the asynchronous operation.</returns>
	public Task HandleAsync(TMessage notification, CancellationToken cancellationToken = default) =>
		deliveryEngine.PublishMessageAsync(notification, cancellationToken);

}
