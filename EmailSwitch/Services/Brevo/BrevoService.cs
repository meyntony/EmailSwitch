using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmailSwitch.Services.Brevo
{
	public sealed class BrevoService : IServiceEmails
	{
		private const string SendEndpoint = "smtp/email";
		private const string ApiKeyHeaderName = "api-key";

		private readonly BrevoInitializer _brevoInitializer;
		private readonly ILogger<BrevoService> _logger;

		public BrevoService(
			BrevoInitializer brevoInitializer,
			ILogger<BrevoService> logger)
		{
			_brevoInitializer = brevoInitializer;
			_logger = logger;
		}

		public async Task<EmailSwitchResponseSendOTP> SendOTP(EmailIdentifier emailPendingVerification, EmailContent emailContent)
		{
			// Reported whatever happens: a caller sizing its code input off this should not get zero
			// just because the send failed.
			var otpLength = _brevoInitializer.BrevoSettings.OtpLength;

			try
			{
				var from = new BrevoAddress() { Email = _brevoInitializer.BrevoSettings.BrevoPrivateSettings.From };

				using var httpClient = _brevoInitializer.CreateClient();

				using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
				{
					Content = JsonContent.Create(new BrevoSendEmailRequest()
					{
						Sender = from,
						// The address exactly as supplied, not the normalised session key: plus-addressing
						// and gmail dots are collapsed for keying, and mailing the collapsed form would
						// deliver somewhere the caller never asked for.
						To = [new BrevoAddress() { Email = emailPendingVerification.GetRawValue() }],
						Subject = emailContent.Subject,
						HtmlContent = emailContent.HtmlContent,
						TextContent = emailContent.PlainTextContent,
						ReplyTo = from
					})
				};
				request.Headers.Add(ApiKeyHeaderName, _brevoInitializer.ApiKey);

				using var sendEmailResponse = await httpClient.SendAsync(request);

				// Brevo answers 201 on an immediate send and 202 on a scheduled one, so this stays a
				// success-range check rather than an equality test against 200.
				if (!sendEmailResponse.IsSuccessStatusCode)
				{
					// Without the body a rejection is unattributable: 401 is a bad or revoked key, 400 a
					// rejected parameter such as an unverified sender, and 429 the rate limit or the free
					// plan's daily allowance. Brevo names which one it is in the body.
					_logger.LogError(
						"Brevo rejected the OTP email with {StatusCode}. Response body: {BrevoResponseBody}",
						sendEmailResponse.StatusCode,
						await HttpResponseLogging.ReadBodyForLogging(sendEmailResponse));
				}

				return new EmailSwitchResponseSendOTP()
				{
					IsSent = sendEmailResponse.IsSuccessStatusCode,
					OtpLength = otpLength,
					ProviderMessageId = sendEmailResponse.IsSuccessStatusCode
						? await ReadMessageId(sendEmailResponse)
						: null
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Unable to send the OTP email through Brevo.");
			}

			return new EmailSwitchResponseSendOTP()
			{
				IsSent = false,
				OtpLength = otpLength
			};
		}

		/// <summary>
		/// The id a later delivery webhook is correlated back to this session by. Never allowed to fail
		/// the send: the email has already been accepted by the time this runs, so reporting a failure
		/// because the id could not be read would invite the caller to send a second one. Losing the id
		/// costs delivery failover for this message, nothing more.
		/// </summary>
		private async Task<string?> ReadMessageId(HttpResponseMessage response)
		{
			try
			{
				var body = await response.Content.ReadFromJsonAsync<BrevoSendEmailResponse>();

				return string.IsNullOrWhiteSpace(body?.MessageId) ? null : body.MessageId;
			}
			catch (Exception exception)
			{
				_logger.LogWarning(exception, "Brevo accepted the OTP email but its message id could not be read; delivery failover cannot cover this message.");
				return null;
			}
		}

		private sealed record BrevoSendEmailResponse
		{
			[JsonPropertyName("messageId")]
			public string? MessageId { get; init; }
		}

		/// <summary>
		/// Only the fields this provider sends. Brevo nests its addresses, where Resend takes plain
		/// strings, so the payload types differ even though the send does the same thing.
		/// </summary>
		private sealed record BrevoSendEmailRequest
		{
			[JsonPropertyName("sender")]
			public required BrevoAddress Sender { get; init; }

			[JsonPropertyName("to")]
			public required BrevoAddress[] To { get; init; }

			[JsonPropertyName("subject")]
			public required string Subject { get; init; }

			[JsonPropertyName("htmlContent")]
			public required string HtmlContent { get; init; }

			[JsonPropertyName("textContent")]
			public required string TextContent { get; init; }

			[JsonPropertyName("replyTo")]
			public required BrevoAddress ReplyTo { get; init; }
		}

		private sealed record BrevoAddress
		{
			[JsonPropertyName("email")]
			public required string Email { get; init; }
		}
	}
}
