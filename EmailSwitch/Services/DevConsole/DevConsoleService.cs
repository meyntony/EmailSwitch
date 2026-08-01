using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Services.DevConsole
{
	/// <summary>
	/// A provider for local development that never sends a real email: the rendered message, which
	/// already contains the verification code, is written to the log instead. No SendGrid account
	/// and no credentials are needed, and because the code is minted and verified through
	/// MongoDbTokenManager before any provider is involved, the full SendOTP/VerifyOTP flow works
	/// end to end.
	///
	/// Refuses to operate in the Production environment.
	/// </summary>
	public sealed class DevConsoleService : IServiceEmails
	{
		private readonly EmailSwitchGeneralInitializer _emailSwitchGeneralInitializer;
		private readonly IHostEnvironment _hostEnvironment;
		private readonly ILogger<DevConsoleService> _logger;

		public DevConsoleService(
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			IHostEnvironment hostEnvironment,
			ILogger<DevConsoleService> logger)
		{
			_emailSwitchGeneralInitializer = emailSwitchGeneralInitializer;
			_hostEnvironment = hostEnvironment;
			_logger = logger;
		}

		public Task<EmailSwitchResponseSendOTP> SendOTP(EmailIdentifier emailPendingVerification, EmailContent emailContent)
		{
			if (_hostEnvironment.IsProduction())
			{
				// Reported as a failed send rather than thrown, so the provider queue falls through
				// to a real provider if one is configured after this.
				_logger.LogCritical(
					"The DevConsole email provider must never be used in Production: refusing to send to {Email}. Configure a real provider in {SettingsName}:Controls:Priority.",
					emailPendingVerification,
					ConstantStrings.EmailSwitchSettingsName);

				// OtpLength reported even on the refusal, so a caller sizing its input field off the
				// response does not get zero just because this provider declined.
				return Task.FromResult(new EmailSwitchResponseSendOTP()
				{
					IsSent = false,
					OtpLength = _emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength
				});
			}

			// Warning rather than Information so it survives a default log filter - the whole point
			// is that a developer can read the code off the console.
			_logger.LogWarning(
				"DevConsole email to {Email}. Subject: {Subject}\n{PlainTextContent}",
				emailPendingVerification.GetRawValue(),
				emailContent.Subject,
				emailContent.PlainTextContent);

			return Task.FromResult(new EmailSwitchResponseSendOTP()
			{
				IsSent = true,
				OtpLength = _emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength
			});
		}
	}
}
