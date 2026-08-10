using EmailSwitch.Common;

namespace EmailSwitch.Services.Resend
{
	public sealed class ResendSettings : EmailSwitchGeneralSettings
	{
		public required ResendPrivateSettings ResendPrivateSettings { get; init; }
	}

	public sealed class ResendPrivateSettings
	{
		public required string From { get; init; }

		/// <summary>
		/// Named ApiKey, where SendGrid's equivalent is called Password. SendGrid's name is wrong and
		/// stays wrong on purpose: a configuration key cannot be renamed without breaking every
		/// existing consumer on a package upgrade. New providers use the accurate name rather than
		/// inheriting the mistake, so the two are deliberately inconsistent.
		/// </summary>
		public required string ApiKey { get; init; }
	}
}
