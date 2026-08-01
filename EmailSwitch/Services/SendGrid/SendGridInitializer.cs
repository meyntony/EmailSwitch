using EmailSwitch.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendGrid;

namespace EmailSwitch.Services.SendGrid
{
	public sealed class SendGridInitializer: EmailSwitchGeneralInitializer
	{
		internal readonly SendGridSettings SendGridSettings;
		public readonly SendGridClient SendGridClient;
		public SendGridInitializer(
			IConfiguration configuration,
			ILogger<SendGridInitializer> logger) : base(configuration, logger)
		{
			var sendGridConfig = EmailSwitchSettings.GetSection(EmailProvider.SendGrid.ToString());

			var from = sendGridConfig["From"];
			var apiKey = sendGridConfig["Password"];

			// Missing configuration used to be logged and swallowed, which left SendGridSettings null
			// and turned every later send into a caught NullReferenceException - so email silently
			// never went out behind a single startup log line. Fail the startup instead.
			var settingsPath = $"{ConstantStrings.EmailSwitchSettingsName}:{EmailProvider.SendGrid}";

			if (string.IsNullOrWhiteSpace(from))
			{
				throw new ArgumentException($"{settingsPath}:From is missing. SendGrid cannot send without a sender address.", nameof(configuration));
			}

			if (string.IsNullOrWhiteSpace(apiKey))
			{
				throw new ArgumentException($"{settingsPath}:Password (the SendGrid API key) is missing.", nameof(configuration));
			}

			SendGridSettings = new SendGridSettings()
			{
				OtpLength = EmailSwitchGeneralSettings.OtpLength,
				SendGridPrivateSettings = new SendGridPrivateSettings()
				{
					From = from,
					Password = apiKey
				}
			};
			SendGridClient = new SendGridClient(apiKey);
		}
	}
}
