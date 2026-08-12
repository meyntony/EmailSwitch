using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmailSwitch.Services.Resend
{
	public sealed class ResendService : IServiceEmails
	{
		private const string SendEndpoint = "emails";

		private readonly ResendInitializer _resendInitializer;
		private readonly ILogger<ResendService> _logger;

		public ResendService(
			ResendInitializer resendInitializer,
			ILogger<ResendService> logger)
		{
			_resendInitializer = resendInitializer;
			_logger = logger;
		}

		public async Task<EmailSwitchResponseSendOTP> SendOTP(EmailIdentifier emailPendingVerification, EmailContent emailContent)
		{
			// Reported whatever happens: a caller sizing its code input off this should not get zero
			// just because the send failed.
			var otpLength = _resendInitializer.ResendSettings.OtpLength;

			try
			{
				var from = _resendInitializer.ResendSettings.ResendPrivateSettings.From;

				using var httpClient = _resendInitializer.CreateClient();

				// Deliberately no Idempotency-Key header. Resend deduplicates on it for 24 hours and
				// hands back the original id without sending anything, and a resend here is meant to
				// deliver the same code again - so a stable per-session key would report success while
				// nothing actually left Resend.
				using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
				{
					Content = JsonContent.Create(new ResendSendEmailRequest()
					{
						From = from,
						// The address exactly as supplied, not the normalised session key: plus-addressing
						// and gmail dots are collapsed for keying, and mailing the collapsed form would
						// deliver somewhere the caller never asked for.
						To = [emailPendingVerification.GetRawValue()],
						Subject = emailContent.Subject,
						Html = emailContent.HtmlContent,
						Text = emailContent.PlainTextContent,
						ReplyTo = from
					})
				};
				request.Headers.Authorization = _resendInitializer.AuthorizationHeader;

				using var sendEmailResponse = await httpClient.SendAsync(request);

				if (!sendEmailResponse.IsSuccessStatusCode)
				{
					// Without the body a rejection is unattributable: 401 is a revoked key, 403 a sending
					// domain that was never verified, 422 a malformed address and 429 either the rate
					// limit or the free tier's daily quota. Resend names which one it is in the body.
					_logger.LogError(
						"Resend rejected the OTP email with {StatusCode}. Response body: {ResendResponseBody}",
						sendEmailResponse.StatusCode,
						await HttpResponseLogging.ReadBodyForLogging(sendEmailResponse));
				}

				return new EmailSwitchResponseSendOTP()
				{
					IsSent = sendEmailResponse.IsSuccessStatusCode,
					OtpLength = otpLength
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Unable to send the OTP email through Resend.");
			}

			return new EmailSwitchResponseSendOTP()
			{
				IsSent = false,
				OtpLength = otpLength
			};
		}

		/// <summary>
		/// Only the fields this provider sends. The contract here is one OTP email, not Resend's API,
		/// so attachments, tags, scheduling and the rest are deliberately absent.
		/// </summary>
		private sealed record ResendSendEmailRequest
		{
			[JsonPropertyName("from")]
			public required string From { get; init; }

			[JsonPropertyName("to")]
			public required string[] To { get; init; }

			[JsonPropertyName("subject")]
			public required string Subject { get; init; }

			[JsonPropertyName("html")]
			public required string Html { get; init; }

			[JsonPropertyName("text")]
			public required string Text { get; init; }

			[JsonPropertyName("reply_to")]
			public required string ReplyTo { get; init; }
		}
	}
}
