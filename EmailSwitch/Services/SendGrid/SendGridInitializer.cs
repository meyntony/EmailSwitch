using EmailSwitch.Common;
using Microsoft.Extensions.Logging;
using SendGrid;

namespace EmailSwitch.Services.SendGrid
{
	/// <summary>
	/// Composes <see cref="EmailSwitchGeneralInitializer"/> rather than deriving from it. Deriving
	/// meant the two were either two singletons reading the signature logo from disk twice, or one
	/// registration forwarded to the other - and the forwarding version made every consumer of the
	/// general settings depend on SendGrid credentials being present, which left no way to run on
	/// the DevConsole provider alone.
	/// </summary>
	public sealed class SendGridInitializer
	{
		internal readonly SendGridSettings SendGridSettings;
		public readonly SendGridClient SendGridClient;

		public SendGridInitializer(
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			ILogger<SendGridInitializer> logger)
		{
			var sendGridConfig = emailSwitchGeneralInitializer.EmailSwitchSettings.GetSection(EmailProvider.SendGrid.ToString());

			var from = sendGridConfig["From"];
			var apiKey = sendGridConfig["Password"];

			// Missing configuration used to be logged and swallowed, which left SendGridSettings null
			// and turned every later send into a caught NullReferenceException - so email silently
			// never went out behind a single startup log line. Fail the startup instead.
			var settingsPath = $"{ConstantStrings.EmailSwitchSettingsName}:{EmailProvider.SendGrid}";

			if (string.IsNullOrWhiteSpace(from))
			{
				throw new ArgumentException($"{settingsPath}:From is missing. SendGrid cannot send without a sender address.", nameof(emailSwitchGeneralInitializer));
			}

			if (string.IsNullOrWhiteSpace(apiKey))
			{
				throw new ArgumentException($"{settingsPath}:Password (the SendGrid API key) is missing.", nameof(emailSwitchGeneralInitializer));
			}

			SendGridSettings = new SendGridSettings()
			{
				OtpLength = emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength,
				SendGridPrivateSettings = new SendGridPrivateSettings()
				{
					From = from,
					Password = apiKey
				}
			};
			SendGridClient = new SendGridClient(apiKey);

			logger.LogInformation("SendGrid initialised for sender {From}.", from);
		}
	}
}
