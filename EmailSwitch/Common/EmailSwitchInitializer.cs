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

		/// <summary>
		/// Days a session is kept after expiry when nothing is configured. Sessions carry the
		/// verified address, so an audit trail that grows without bound is a storage-limitation
		/// problem, not just a disk one.
		/// </summary>
		internal const int DefaultSessionRetentionDays = 90;

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
				SessionRetentionDays = ReadSessionRetentionDays(emailControlsConfig, logger),
				Priority = GetPriority(emailControlsConfig.GetRequiredSection("Priority").Get<string[]>() ?? [], logger)
			};
		}

		/// <summary>
		/// Unlike the other controls this never fails startup. Zero or less is a legitimate operator
		/// choice - keep the audit trail indefinitely and prune it some other way - rather than a
		/// misconfiguration, so it is logged and honoured.
		/// </summary>
		private static int ReadSessionRetentionDays(IConfigurationSection emailControlsConfig, ILogger logger)
		{
			var configured = emailControlsConfig["SessionRetentionDays"];

			if (string.IsNullOrWhiteSpace(configured))
			{
				return DefaultSessionRetentionDays;
			}

			if (!int.TryParse(configured, out var sessionRetentionDays))
			{
				logger.LogWarning(
					"{ControlsSection}:SessionRetentionDays is not a number, falling back to {DefaultSessionRetentionDays} days.",
					ControlsSection,
					DefaultSessionRetentionDays);

				return DefaultSessionRetentionDays;
			}

			if (sessionRetentionDays <= 0)
			{
				logger.LogWarning(
					"{ControlsSection}:SessionRetentionDays is {SessionRetentionDays}, so sessions are kept indefinitely and the collection will grow without bound.",
					ControlsSection,
					sessionRetentionDays);
			}

			return sessionRetentionDays;
		}

		private static List<EmailProvider> GetPriority(string[] configuredProviders, ILogger logger)
		{
			var knownProviders = string.Join(", ", Enum.GetNames<EmailProvider>());
			var priority = new List<EmailProvider>();

			foreach (var configuredProvider in configuredProviders)
			{
				// Case-insensitive, because a lowercase "sendgrid" in configuration used to be dropped
				// without a word. Numeric strings are rejected outright: Enum.TryParse would happily
				// read "0" as the first provider, which is never what a config file meant.
				if (!int.TryParse(configuredProvider, out _)
					&& Enum.TryParse(configuredProvider, ignoreCase: true, out EmailProvider emailProvider))
				{
					// De-duplicated here rather than by using a set, so the configured order is the
					// order providers are tried. Listing one twice would otherwise double its share of
					// the send budget, which is not what repeating a name in a priority list means.
					if (!priority.Contains(emailProvider))
					{
						priority.Add(emailProvider);
					}
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
