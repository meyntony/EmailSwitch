namespace EmailSwitch.Common
{
	public sealed class EmailControls
	{
		public byte MaximumFailedAttemptsToVerify { get; init; }
		public int SessionTimeoutInSeconds { get; init; }
		public byte MaxRoundRobinAttempts { get; set; }

		/// <summary>
		/// Days a session is kept after it expires, before a MongoDB TTL index removes it. Zero or
		/// less keeps sessions indefinitely.
		/// </summary>
		public int SessionRetentionDays { get; init; }

		public required HashSet<EmailProvider> Priority { get; set; }
	}
}
