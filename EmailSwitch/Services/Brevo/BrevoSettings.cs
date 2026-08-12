using EmailSwitch.Common;

namespace EmailSwitch.Services.Brevo
{
	public sealed class BrevoSettings : EmailSwitchGeneralSettings
	{
		public required BrevoPrivateSettings BrevoPrivateSettings { get; init; }
	}

	public sealed class BrevoPrivateSettings
	{
		public required string From { get; init; }

		/// <summary>
		/// `ApiKey`, matching Resend rather than SendGrid's `Password`. See the note on
		/// <c>ResendPrivateSettings.ApiKey</c>: SendGrid's name is wrong and stays wrong because a
		/// configuration key cannot be renamed without breaking existing consumers on upgrade.
		/// </summary>
		public required string ApiKey { get; init; }
	}
}
