using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Database;
using EmailSwitch.Database.DTOs;
using EmailSwitch.Services.SendGrid;
using HumanLanguages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDbTokenManager;
using SMSwitch.Common.DTOs;

namespace EmailSwitch
{
	public sealed class EmailSwitchService
	{
		private readonly EmailSwitchInitializer _emailSwitchInitializer;
		private readonly EmailSwitchGeneralInitializer _emailSwitchGeneralInitializer;
		private readonly IServiceProvider _serviceProvider;
		private readonly EmailSwitchDbService _emailSwitchDbService;
		private readonly AbstractTokenService _tokenService;
		private readonly ILogger<EmailSwitchService> _logger;

		public EmailSwitchService(
			EmailSwitchInitializer emailSwitchInitializer,
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			IServiceProvider serviceProvider,
			EmailSwitchDbService emailSwitchDbService,
			AbstractTokenService tokenService,
			ILogger<EmailSwitchService> logger
			)
		{
			_emailSwitchInitializer = emailSwitchInitializer;
			_emailSwitchGeneralInitializer = emailSwitchGeneralInitializer;
			_serviceProvider = serviceProvider;
			_emailSwitchDbService = emailSwitchDbService;
			_tokenService = tokenService;
			_logger = logger;
		}

		/// <summary>
		/// Reported on every response, successful or not. A caller sizes its code input off this, and
		/// a failed send used to hand back zero - SendGridService already went out of its way to
		/// populate it on its own failure paths, so the ones here were simply inconsistent.
		/// </summary>
		private EmailSwitchResponseSendOTP FailedSend(DateTimeOffset? expiry = null) =>
			new()
			{
				IsSent = false,
				OtpLength = _emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength,
				ExpiryDateTimeOffset = expiry ?? default
			};

		/// <summary>
		/// Resolved per provider rather than injected, so a provider whose configuration is absent is
		/// never constructed - that is what lets a DevConsole-only setup run without SendGrid
		/// credentials. Adding a provider is a registration change in ServiceCollectionExtensions,
		/// not an edit here.
		/// </summary>
		private IServiceEmails ProviderFor(EmailProvider emailProvider) =>
			_serviceProvider.GetRequiredKeyedService<IServiceEmails>(emailProvider);

		public async Task<EmailSwitchResponseSendOTP> SendOTP(EmailIdentifier email, MobileNumber[] verifiedMobileNumbers, EmailIdentifier[] verifiedEmails, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent)
		{
			EmailSwitchResponseSendOTP? responseSendOTP = null;
			EmailSwitchSession? session = null;
			try
			{
				session = await _emailSwitchDbService.GetOrCreateAndGetLatestSession(email, verifiedMobileNumbers, verifiedEmails, preferredLanguageIsoCodeList, userAgent);

				// Without a session there is no templated email to send, so bail out rather than
				// relying on the catch below to mop up a NullReferenceException further in.
				if (session is null)
				{
					_logger.LogCritical("Unable to create or load a session to send an OTP to {Email}", email);
					return FailedSend();
				}

				// Only a null queue means "not started yet". An empty one means this session has
				// spent its send budget and must not be silently refilled.
				Queue<EmailProvider> emailProvidersQueue =
					session.EmailProvidersQueue ?? BuildProviderQueue(_emailSwitchInitializer.EmailControls);

				if (emailProvidersQueue.Count == 0)
				{
					_logger.LogWarning("Send budget already spent for {Email} with SessionId: {SessionId}; not sending again.", email, session.SessionId);
					return FailedSend(session.ExpiryTimeUTC);
				}

				// The rendered email is retired once the code is verified or the budget is spent, so a
				// live session with slots left should always still carry one. Checked rather than
				// assumed: the field is nullable precisely because the database can have dropped it,
				// and dereferencing it here would fail mid-send rather than reporting a failed send.
				if (session.SendOTPEmail is null)
				{
					_logger.LogCritical("Session {SessionId} has no rendered email left to send to {Email}", session.SessionId, email);
					return FailedSend(session.ExpiryTimeUTC);
				}

				// Accumulated in memory and written once at the end. The session document is never
				// replaced wholesale, so attempts have to arrive as a $push rather than as a field of
				// a document read before the provider call.
				var sentAttempts = new List<AttemptDetailsSendOTP>();

				while (emailProvidersQueue.Any())
				{
					var emailProvider = emailProvidersQueue.Peek();
					responseSendOTP = await ProviderFor(emailProvider).SendOTP(email, session.SendOTPEmail);

					sentAttempts.Add(new AttemptDetailsSendOTP(DateTime.UtcNow, emailProvider, responseSendOTP.IsSent));

					// Every attempt spends one slot, success included, so a caller cannot mail the
					// same address indefinitely by resending. Only a failure falls through to the
					// next provider.
					emailProvidersQueue.Dequeue();

					if (responseSendOTP.IsSent)
					{
						break;
					}
				}

				// The session owns the deadline, and the caller needs it to show a countdown. Without
				// this the property went back as default(DateTimeOffset) on every successful send.
				if (responseSendOTP is not null)
				{
					responseSendOTP.ExpiryDateTimeOffset = session.ExpiryTimeUTC;
				}

				try
				{
					await _emailSwitchDbService.RegisterSendAttempts(session.SessionId, emailProvidersQueue, sentAttempts);
				}
				catch (Exception exception)
				{
					// Contained separately, and deliberately does not change IsSent: the email really
					// did go out, and reporting otherwise would invite the caller to resend and mail
					// the recipient twice. What is lost is the record of the slot being spent, so the
					// budget still shows room - which is a distinct failure from being unable to send,
					// and is worth being able to tell apart in the logs.
					_logger.LogCritical(exception, "Sent the OTP to {Email} but could not record it against SessionId: {SessionId}; the send budget was not decremented.", email, session.SessionId);
				}

				if (responseSendOTP == null || !responseSendOTP.IsSent)
				{
					_logger.LogCritical("Unable to send OTP to {Email} with SessionId: {SessionId}", email, session.SessionId);
				}
			}
			catch (Exception exception)
			{
				_logger.LogCritical(exception, "Unable to send OTP to {Email} with SessionId: {SessionId}", email, session?.SessionId);
			}
			return responseSendOTP ?? FailedSend(session?.ExpiryTimeUTC);
		}

		/// <summary>
		/// One slot per provider per round-robin attempt. This queue is the cap on how many emails a
		/// single session can send: it is built once when the session starts, and every send attempt
		/// spends a slot. Internal so the budget can be tested without a datastore.
		/// </summary>
		internal static Queue<EmailProvider> BuildProviderQueue(EmailControls emailControls)
		{
			var emailProvidersQueue = new Queue<EmailProvider>();

			for (int i = 0; i < emailControls.MaxRoundRobinAttempts; i++)
			{
				foreach (EmailProvider emailProvider in emailControls.Priority)
				{
					emailProvidersQueue.Enqueue(emailProvider);
				}
			}

			return emailProvidersQueue;
		}

		public async Task<EmailSwitchResponseVerifyOTP> VerifyOTP(EmailIdentifier email, string OTP)
		{
			bool verified = false;

			// GetLatestSession filters out sessions that have expired or exhausted their verification
			// attempts, so a null session means there is no live OTP to check against and the caller
			// needs to request a new code.
			bool expired = true;

			try
			{
				var session = await _emailSwitchDbService.GetLatestSession(email);

				if (session is null)
				{
					_logger.LogInformation("Session not found: Unable to verify OTP for {Email}", email);
				}
				else
				{
					// GetLatestSession only discovers which session to guess against; the value it
					// returns is already stale by the time it arrives. The slot is claimed server side
					// instead, before the guess is checked, so concurrent guesses cannot all pass a cap
					// that none of them has yet been counted against.
					var reserved = await _emailSwitchDbService.TryReserveVerificationAttempt(
						session.SessionId,
						_emailSwitchInitializer.EmailControls.MaximumFailedAttemptsToVerify);

					if (reserved is null)
					{
						// Out of attempts, or the session was verified or expired between the two
						// reads. Either way there is nothing left to guess against, so the token is
						// never touched.
						_logger.LogInformation("No verification attempt left: Unable to verify OTP for {Email} with SessionId: {SessionId}", email, session.SessionId);
					}
					else
					{
						expired = false;

						// ConsumeAndValidate claims the token in a single hash-matched delete, so two
						// concurrent requests holding the same correct OTP cannot both succeed. A wrong
						// guess leaves the token in place for the legitimate holder to still use.
						verified = await _tokenService.ConsumeAndValidate(session.SessionId, token: OTP);
						if (verified)
						{
							await _emailSwitchDbService.RegisterSuccessfulVerification(session.SessionId);
						}
						else
						{
							// Audit only - the attempt itself was already claimed above. Kept as an
							// atomic $push so parallel failures are each recorded.
							await _emailSwitchDbService.RegisterFailedVerificationAttempt(session.SessionId);
						}
					}
				}
			}
			catch (Exception exception)
			{
				// A stored token written by an older MongoDbTokenManager cannot be deserialized by
				// the current one, which surfaces here as a FormatException. Report a failed
				// verification rather than letting it escape to the caller as a server error.
				//
				// Deliberately not resetting verified: ConsumeAndValidate has already deleted the
				// token by the time the session write runs, so failing that write must not report a
				// correct code as wrong - the caller could never retry it. Every path that can throw
				// before the guess is checked leaves verified false anyway.
				_logger.LogCritical(exception, "Unable to verify OTP for {Email}", email);

				// Reported as expired whenever the guess could not actually be checked. A token
				// written by MongoDbTokenManager 10.0.0 can never be read by the current one, so
				// leaving this false told the caller "that code is not correct" and invited them to
				// retype it forever. Asking for a new code is the one thing that does work.
				if (!verified)
				{
					expired = true;
				}
			}

			return new EmailSwitchResponseVerifyOTP()
			{
				Verified = verified,
				Expired = !verified && expired
			};
		}
	}
}
