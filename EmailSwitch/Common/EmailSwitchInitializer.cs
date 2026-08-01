using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Common
{
	public sealed class EmailSwitchInitializer
	{
		/// <summary>
		/// The send template advertises an expiry ten seconds early, and an OTP email needs time to
		/// arrive, so anything shorter than this is a misconfiguration rather than a tight window.
		/// </summary>
		internal const int MinimumSessionTimeoutInSeconds = 30;

		private const string ControlsSection = "EmailSwitchSettings:Controls";

		public readonly EmailControls EmailControls;

		public EmailSwitchInitializer(IConfiguration configuration, ILogger<EmailSwitchInitializer> logger)
		{
			var emailControlsConfig = configuration.GetSection(ControlsSection);

			var sessionTimeoutInSeconds = int.TryParse(emailControlsConfig["SessionTimeoutInSeconds"], out int configuredTimeout) ? configuredTimeout : 240;

			// Previously unvalidated: zero or less made every Generate throw ArgumentOutOfRangeException
			// into a swallowed IsSent = false, and anything under the display leeway made the email
			// advertise an expiry already in the past.
			if (sessionTimeoutInSeconds < MinimumSessionTimeoutInSeconds)
			{
				throw new ArgumentException(
					$"{ControlsSection}:SessionTimeoutInSeconds is {sessionTimeoutInSeconds}, which is below the minimum of {MinimumSessionTimeoutInSeconds}.",
					nameof(configuration));
			}

			EmailControls = new EmailControls()
			{
				MaximumFailedAttemptsToVerify = byte.TryParse(emailControlsConfig["MaximumFailedAttemptsToVerify"], out byte maximumFailedAttemptsToVerify) ? maximumFailedAttemptsToVerify : (byte)3,
				SessionTimeoutInSeconds = sessionTimeoutInSeconds,
				MaxRoundRobinAttempts = byte.TryParse(emailControlsConfig["MaxRoundRobinAttempts"], out byte maxRoundRobinAttempts) ? maxRoundRobinAttempts : (byte)1,
				Priority = GetPriority(emailControlsConfig.GetRequiredSection("Priority").Get<string[]>() ?? [], logger)
			};
		}

		private static HashSet<EmailProvider> GetPriority(string[] configuredProviders, ILogger logger)
		{
			var knownProviders = string.Join(", ", Enum.GetNames<EmailProvider>());
			var priority = new HashSet<EmailProvider>();

			foreach (var configuredProvider in configuredProviders)
			{
				// Case-insensitive, because a lowercase "sendgrid" in configuration used to be dropped
				// without a word. Numeric strings are rejected outright: Enum.TryParse would happily
				// read "0" as the first provider, which is never what a config file meant.
				if (!int.TryParse(configuredProvider, out _)
					&& Enum.TryParse(configuredProvider, ignoreCase: true, out EmailProvider emailProvider))
				{
					priority.Add(emailProvider);
				}
				else
				{
					logger.LogError(
						"Ignoring unrecognised email provider {ConfiguredProvider} in {ControlsSection}:Priority. Known providers: {KnownProviders}.",
						configuredProvider,
						ControlsSection,
						knownProviders);
				}
			}

			if (priority.Count < 1)
			{
				throw new ArgumentException(
					$"{ControlsSection}:Priority names no recognised email provider. Known providers: {knownProviders}.",
					nameof(configuredProviders));
			}

			return priority;
		}
	}
}
