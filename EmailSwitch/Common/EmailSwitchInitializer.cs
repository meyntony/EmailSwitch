using Microsoft.Extensions.Configuration;

namespace EmailSwitch.Common
{
	public sealed class EmailSwitchInitializer
	{
		public readonly EmailControls EmailControls;

		public EmailSwitchInitializer(IConfiguration configuration)
		{
			var emailControlsConfig = configuration.GetSection("EmailSwitchSettings:Controls");
			EmailControls = new EmailControls()
			{
				MaximumFailedAttemptsToVerify = byte.TryParse(emailControlsConfig["MaximumFailedAttemptsToVerify"], out byte maximumFailedAttemptsToVerify) ? maximumFailedAttemptsToVerify : (byte)3,
				SessionTimeoutInSeconds = int.TryParse(emailControlsConfig["SessionTimeoutInSeconds"], out int sessionTimeoutInSeconds) ? sessionTimeoutInSeconds : 240,
				MaxRoundRobinAttempts = byte.TryParse(emailControlsConfig["MaxRoundRobinAttempts"], out byte maxRoundRobinAttempts) ? maxRoundRobinAttempts : (byte)1,
				Priority = getPriority(emailControlsConfig.GetRequiredSection("Priority").Get<string[]>() ?? [])
			};
		}

		private HashSet<EmailProvider> getPriority(string[] value)
		{
			var valuesFromConfig = value.Where(p => Enum.TryParse(p, out EmailProvider _)).Select(p => Enum.Parse<EmailProvider>(p)).ToHashSet();
			if (valuesFromConfig.Count() < 1)
			{
				throw new Exception("Priority list missing!!");
			}
			return valuesFromConfig;
		}
	}
}
