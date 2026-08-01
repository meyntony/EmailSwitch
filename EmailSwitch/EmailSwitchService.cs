using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Database;
using EmailSwitch.Database.DTOs;
using EmailSwitch.Services.SendGrid;
using HumanLanguages;
using Microsoft.Extensions.Logging;
using MongoDbTokenManager;
using SMSwitch.Common.DTOs;

namespace EmailSwitch
{
	public sealed class EmailSwitchService
	{
		private readonly EmailSwitchInitializer _emailSwitchInitializer;
		private readonly SendGridService _sendGridService;
		private readonly EmailSwitchDbService _emailSwitchDbService;
		private readonly AbstractTokenService _tokenService;
		private readonly ILogger<EmailSwitchService> _logger;

		public EmailSwitchService(
			EmailSwitchInitializer emailSwitchInitializer,
			SendGridService sendGridService,
			EmailSwitchDbService emailSwitchDbService,
			AbstractTokenService tokenService,
		ILogger<EmailSwitchService> logger
			)
		{
			_emailSwitchInitializer = emailSwitchInitializer;
			_sendGridService = sendGridService;
			_emailSwitchDbService = emailSwitchDbService;
			_tokenService = tokenService;
			_logger = logger;
		}

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
					return new EmailSwitchResponseSendOTP() { IsSent = false };
				}

				Queue<EmailProvider> emailProvidersQueue;
				if (session.EmailProvidersQueue?.Any() ?? false)
				{
					emailProvidersQueue = session.EmailProvidersQueue;
				}
				else
				{
					emailProvidersQueue = new();
					HashSet<EmailProvider> emailProviders = _emailSwitchInitializer.EmailControls.Priority;

					for (int i = 0; i < _emailSwitchInitializer.EmailControls.MaxRoundRobinAttempts; i++)
					{
						foreach (EmailProvider emailProvider in emailProviders)
						{
							emailProvidersQueue.Enqueue(emailProvider);
						}
					}
				}

				if (emailProvidersQueue.Count == 0)
				{
					return new EmailSwitchResponseSendOTP()
					{
						IsSent = false
					};
				}

				while (emailProvidersQueue.Any())
				{
					if (session.SentAttempts.Any())
					{
						emailProvidersQueue.Dequeue();
						if (!emailProvidersQueue.Any())
						{
							break;
						}
					}
					responseSendOTP = emailProvidersQueue.Peek() switch
					{
						EmailProvider.SendGrid => await _sendGridService.SendOTP(email, session.SendOTPEmail),
						_ => throw new NotImplementedException(),
					};

					session.SentAttempts.Add(new AttemptDetailsSendOTP(DateTime.UtcNow, emailProvidersQueue.Peek(), responseSendOTP.IsSent));
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

				session.EmailProvidersQueue = emailProvidersQueue;
				await _emailSwitchDbService.UpdateSession(session);

				if (responseSendOTP == null || !responseSendOTP.IsSent)
				{
					_logger.LogCritical("Unable to send OTP to {Email} with SessionId: {SessionId}", email, session.SessionId);
				}
			}
			catch (Exception exception)
			{
				_logger.LogCritical(exception, "Unable to send OTP to {Email} with SessionId: {SessionId}", email, session?.SessionId);
			}
			return responseSendOTP ?? new EmailSwitchResponseSendOTP() { IsSent = false };
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
					expired = false;

					// ConsumeAndValidate claims the token in a single hash-matched delete, so two
					// concurrent requests holding the same correct OTP cannot both succeed. A wrong
					// guess leaves the token in place for the legitimate holder to still use.
					verified = await _tokenService.ConsumeAndValidate(session.SessionId, token: OTP);
					if (verified)
					{
						session.SuccessfullyVerifiedTimestampUTC = DateTime.UtcNow;
					}
					else
					{
						session.FailedVerificationAttemptsUTC.Add(DateTime.UtcNow);
					}
					await _emailSwitchDbService.UpdateSession(session);
				}
			}
			catch (Exception exception)
			{
				// A stored token written by an older MongoDbTokenManager cannot be deserialized by
				// the current one, which surfaces here as a FormatException. Report a failed
				// verification rather than letting it escape to the caller as a server error.
				_logger.LogCritical(exception, "Unable to verify OTP for {Email}", email);
				verified = false;
			}

			return new EmailSwitchResponseVerifyOTP()
			{
				Verified = verified,
				Expired = !verified && expired
			};
		}
	}
}
