using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace EmailSwitch.Services.SendGrid
{
	public sealed class SendGridService : IServiceEmails
	{
		private readonly SendGridInitializer _sendGridInitializer;
		private readonly ILogger<SendGridService> _logger;


		public SendGridService(
			SendGridInitializer sendGridInitializer,
			ILogger<SendGridService> logger)
		{
			_sendGridInitializer = sendGridInitializer;
			_logger = logger;
		}

		public async Task<EmailSwitchResponseSendOTP> SendOTP(EmailIdentifier emailPendingVerification, EmailContent emailContent)
		{
			// Reported whatever happens: a caller sizing its code input off this should not get zero
			// just because the send failed.
			var otpLength = _sendGridInitializer.SendGridSettings.OtpLength;

			try
			{
				var fromEmail = new EmailAddress(_sendGridInitializer.SendGridSettings.SendGridPrivateSettings.From);

				var sendGridMessage = MailHelper.CreateSingleEmail(
							   from: fromEmail,
							   to: new EmailAddress(emailPendingVerification.GetRawValue()),
							   subject: emailContent.Subject,
							   plainTextContent: emailContent.PlainTextContent,
							   htmlContent: emailContent.HtmlContent
						   );
				sendGridMessage.SetReplyTo(fromEmail);

				var sendEmailResponse = await _sendGridInitializer.SendGridClient.SendEmailAsync(sendGridMessage);

				if (!sendEmailResponse.IsSuccessStatusCode)
				{
					// A rejection used to produce no log at all, so an unverified sender, a revoked
					// key or a suppressed recipient was indistinguishable from any other failed send.
					// SendGrid puts the reason in the body.
					_logger.LogError(
						"SendGrid rejected the OTP email with {StatusCode}. Response body: {SendGridResponseBody}",
						sendEmailResponse.StatusCode,
						await ReadBodyForLogging(sendEmailResponse));
				}

				return new EmailSwitchResponseSendOTP()
				{
					IsSent = sendEmailResponse.IsSuccessStatusCode,
					OtpLength = otpLength
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Unable to send the OTP email through SendGrid.");
			}

			return new EmailSwitchResponseSendOTP()
			{
				IsSent = false,
				OtpLength = otpLength
			};
		}

		/// <summary>
		/// Diagnostics only, so a body that cannot be read must not turn a failed send into a thrown
		/// one - the caller is already on its unhappy path.
		/// </summary>
		private static async Task<string> ReadBodyForLogging(Response response)
		{
			try
			{
				return await response.Body.ReadAsStringAsync();
			}
			catch (Exception)
			{
				return "<could not be read>";
			}
		}
	}
}
