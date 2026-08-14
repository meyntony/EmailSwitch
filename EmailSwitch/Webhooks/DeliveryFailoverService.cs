using EmailSwitch.Database;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Webhooks
{
	/// <summary>
	/// Turns a provider delivery event into a resend through the next provider in the session's budget.
	///
	/// This exists because a synchronous send cannot tell delivery from acceptance. Every provider here
	/// answers 2xx the moment it takes responsibility for a message, so an unauthenticated sender, a
	/// suppression-list hit or a hard bounce all report success and fail minutes later. Provider
	/// failover in <c>EmailSwitchService.SendOTP</c> only ever covered rejection; this covers the rest,
	/// late, which is the only way it can be covered at all.
	///
	/// Provider-neutral on purpose: each provider's webhook parses its own vocabulary into a
	/// <see cref="DeliveryEvent"/> and the decision below is made once.
	/// </summary>
	internal sealed class DeliveryFailoverService
	{
		private readonly EmailSwitchDbService _emailSwitchDbService;
		private readonly EmailSwitchService _emailSwitchService;
		private readonly ILogger<DeliveryFailoverService> _logger;

		public DeliveryFailoverService(
			EmailSwitchDbService emailSwitchDbService,
			EmailSwitchService emailSwitchService,
			ILogger<DeliveryFailoverService> logger)
		{
			_emailSwitchDbService = emailSwitchDbService;
			_emailSwitchService = emailSwitchService;
			_logger = logger;
		}

		internal async Task<DeliveryFailoverOutcome> Handle(DeliveryEvent deliveryEvent)
		{
			if (!deliveryEvent.IsTerminalFailure)
			{
				// Deferrals and soft bounces are retried by the provider itself, so resending on one
				// would put a second copy of the same code in the inbox alongside the one still in
				// flight.
				return DeliveryFailoverOutcome.NotAFailure;
			}

			// Liveness, and still holding the claim, are both enforced by the lookup. A bounce for a
			// session the user has already replaced must not resend its superseded code.
			var session = await _emailSwitchDbService.GetLiveSessionByProviderMessageId(deliveryEvent.ProviderMessageId);

			if (session is null)
			{
				_logger.LogInformation(
					"{EmailProvider} reported {EventName} for a message with no live session; nothing to fail over. Reason: {Reason}",
					deliveryEvent.EmailProvider,
					deliveryEvent.EventName,
					deliveryEvent.Reason);

				return DeliveryFailoverOutcome.NoLiveSession;
			}

			// Claimed before the send rather than recorded after it. Webhooks retry, and two
			// redeliveries arriving together would otherwise both pass a membership test and both send.
			var claimed = await _emailSwitchDbService.TryClaimDeliveryEvent(session.SessionId, KeyFor(deliveryEvent));

			if (!claimed)
			{
				_logger.LogInformation(
					"{EmailProvider} redelivered {EventName} for SessionId: {SessionId}, which was already acted on.",
					deliveryEvent.EmailProvider,
					deliveryEvent.EventName,
					session.SessionId);

				return DeliveryFailoverOutcome.AlreadyHandled;
			}

			_logger.LogWarning(
				"{EmailProvider} reported {EventName} for SessionId: {SessionId}; the code never arrived. Reason: {Reason}. Trying the next provider.",
				deliveryEvent.EmailProvider,
				deliveryEvent.EventName,
				session.SessionId,
				deliveryEvent.Reason);

			if (session.EmailProvidersQueue is null || session.EmailProvidersQueue.Count == 0 || session.SendOTPEmail is null)
			{
				// Expected whenever Priority names a single provider: its one slot was spent on the send
				// that just failed, and the rendered email was retired with it. Delivery failover needs
				// a second provider to fail over to, exactly as rejection failover always did.
				_logger.LogWarning(
					"No send budget left for SessionId: {SessionId}, so the delivery failure cannot be recovered. Configure more than one provider in Priority to make delivery failover possible.",
					session.SessionId);

				return DeliveryFailoverOutcome.NoBudgetLeft;
			}

			var resent = await _emailSwitchService.ResendThroughNextProvider(session);

			if (resent)
			{
				_logger.LogInformation("Resent the code for SessionId: {SessionId} through the next provider.", session.SessionId);

				return DeliveryFailoverOutcome.Resent;
			}

			_logger.LogCritical(
				"Could not resend the code for SessionId: {SessionId} through any remaining provider after {EmailProvider} reported {EventName}.",
				session.SessionId,
				deliveryEvent.EmailProvider,
				deliveryEvent.EventName);

			return DeliveryFailoverOutcome.ResendFailed;
		}

		/// <summary>
		/// Includes the event name, not just the message id: a message can legitimately produce more
		/// than one terminal event, and keying on the id alone would silently discard the second.
		/// </summary>
		private static string KeyFor(DeliveryEvent deliveryEvent) =>
			$"{deliveryEvent.EmailProvider}:{deliveryEvent.ProviderMessageId}:{deliveryEvent.EventName}";
	}
}
