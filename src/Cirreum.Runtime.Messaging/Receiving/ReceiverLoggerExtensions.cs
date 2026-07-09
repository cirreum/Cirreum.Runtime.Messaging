namespace Cirreum.Runtime.Messaging.Receiving;

using Microsoft.Extensions.Logging;
using System;

/// <summary>
/// Source-generated logger messages for <see cref="DistributedMessageReceiver"/>.
/// </summary>
internal static partial class ReceiverLoggerExtensions {

	[LoggerMessage(
		EventId = 2001,
		Level = LogLevel.Information,
		Message = "Receiver: starting. NodeId={nodeId}; QueueSource={queueName}; TopicSource={topicName}/{subscriptionName}; MaxConcurrency={maxConcurrency}")]
	public static partial void ReceiverStarting(
		this ILogger logger,
		string nodeId,
		string? queueName,
		string? topicName,
		string? subscriptionName,
		int maxConcurrency);

	[LoggerMessage(
		EventId = 2002,
		Level = LogLevel.Information,
		Message = "Receiver: stopping. Awaiting in-flight handlers (up to {timeout}).")]
	public static partial void ReceiverStopping(this ILogger logger, TimeSpan timeout);

	[LoggerMessage(
		EventId = 2003,
		Level = LogLevel.Warning,
		Message = "Receiver: shutdown timeout exceeded; in-flight handlers cancelled.")]
	public static partial void ReceiverShutdownTimeout(this ILogger logger);

	[LoggerMessage(
		EventId = 2004,
		Level = LogLevel.Error,
		Message = "Receiver: consumer loop for source {source} failed unexpectedly.")]
	public static partial void ConsumerLoopFailed(this ILogger logger, Exception ex, string source);

	[LoggerMessage(
		EventId = 2005,
		Level = LogLevel.Debug,
		Message = "Receiver: self-echo skipped (cirreum.node={nodeId}).")]
	public static partial void SelfEchoSkipped(this ILogger logger, string nodeId);

	[LoggerMessage(
		EventId = 2006,
		Level = LogLevel.Error,
		Message = "Receiver: failed to deserialize envelope for message on {source}. Dead-lettering.")]
	public static partial void EnvelopeDeserializationFailed(this ILogger logger, Exception ex, string source);

	[LoggerMessage(
		EventId = 2007,
		Level = LogLevel.Warning,
		Message = "Receiver: subscription received a message with unregistered identity (identifier={identifier}, version={version}; wire type hint '{messageType}'). Completing without dispatch — a fan-out subscription normally delivers family members this consumer does not handle.")]
	public static partial void UnknownMessageType(
		this ILogger logger,
		string messageType,
		string identifier,
		string version);

	[LoggerMessage(
		EventId = 2011,
		Level = LogLevel.Error,
		Message = "Receiver: queue message has unregistered identity (identifier={identifier}, version={version}; wire type hint '{messageType}'). Dead-lettering for triage; verify the message type's assembly is deployed on this consumer.")]
	public static partial void UnknownMessageTypeDeadLettered(
		this ILogger logger,
		string messageType,
		string identifier,
		string version);

	[LoggerMessage(
		EventId = 2008,
		Level = LogLevel.Error,
		Message = "Receiver: failed to deserialize payload for message type '{messageType}' (identifier={identifier}, version={version}). Dead-lettering.")]
	public static partial void PayloadDeserializationFailed(
		this ILogger logger,
		Exception ex,
		string messageType,
		string identifier,
		string version);

	[LoggerMessage(
		EventId = 2009,
		Level = LogLevel.Warning,
		Message = "Receiver: handler dispatch returned failure for {identifier} v{version}. Abandoning for broker retry.")]
	public static partial void HandlerFailure(
		this ILogger logger,
		string identifier,
		string version);

	[LoggerMessage(
		EventId = 2010,
		Level = LogLevel.Error,
		Message = "Receiver: handler dispatch threw for {identifier} v{version}. Abandoning for broker retry.")]
	public static partial void HandlerException(
		this ILogger logger,
		Exception ex,
		string identifier,
		string version);

}
