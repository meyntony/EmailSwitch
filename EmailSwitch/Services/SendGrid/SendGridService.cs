using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using Microsoft.Extensions.Logging;
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
				var sendEmailRequest = await _sendGridInitializer.SendGridClient.SendEmailAsync(sendGridMessage);
				return await Task.FromResult(new EmailSwitchResponseSendOTP() {
					IsSent = sendEmailRequest.IsSuccessStatusCode,
					OtpLength = _sendGridInitializer.SendGridSettings.OtpLength
				});
			}
			catch (Exception e)
			{
				_logger.LogError(e, $"SendUserInboxVerificationCode error: {e.Message}");
			}
			return await Task.FromResult(new EmailSwitchResponseSendOTP()
			{
				IsSent = false
			});
		}
	}
}
