using EmailSwitch.Common;

namespace EmailSwitch.Webhooks
{
	/// <summary>
	/// A provider's delivery event, reduced to the only three things the failover decision needs.
	/// Each provider's parser produces one of these, so <see cref="DeliveryFailoverService"/> stays
	/// free of provider vocabulary.
	/// </summary>
	/// <param name="EmailProvider">Which provider reported it, for the audit trail and the log.</param>
	/// <param name="ProviderMessageId">
	/// The provider's id for the message, which is what correlates the event back to a session.
	/// </param>
	/// <param name="EventName">
	/// The provider's own name for the event, carried verbatim so the log says what actually arrived
	/// rather than this library's interpretation of it.
	/// </param>
	/// <param name="IsTerminalFailure">
	/// Whether the message is now known never to arrive. Only terminal failures trigger a resend:
	/// a deferral or a soft bounce is usually retried by the provider itself, so acting on one would
	/// deliver the code twice.
	/// </param>
	/// <param name="Reason">The provider's stated reason, when it gave one. Logged, never parsed.</param>
	internal sealed record DeliveryEvent(
		EmailProvider EmailProvider,
		string ProviderMessageId,
		string EventName,
		bool IsTerminalFailure,
		string? Reason);

	/// <summary>
	/// What became of a delivery event. Distinct values rather than a bool because the difference
	/// between "nothing to do" and "tried and could not" is exactly what a support question about a
	/// missing OTP turns on, and every one of these is a normal outcome rather than an error.
	/// </summary>
	internal enum DeliveryFailoverOutcome
	{
		/// <summary>Not a terminal failure - delivered, opened, deferred, soft bounced.</summary>
		NotAFailure,

		/// <summary>No live session carries that message id: already verified, timed out, or superseded.</summary>
		NoLiveSession,

		/// <summary>This exact event was already acted on. Webhook redeliveries land here.</summary>
		AlreadyHandled,

		/// <summary>Terminal, and the budget had nothing left - or the rendered email was already retired.</summary>
		NoBudgetLeft,

		/// <summary>Terminal, and another provider accepted the same code.</summary>
		Resent,

		/// <summary>Terminal, budget was spent trying, and no remaining provider accepted it either.</summary>
		ResendFailed
	}
}
