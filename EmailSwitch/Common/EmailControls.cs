namespace EmailSwitch.Common
{
	public sealed class EmailControls
	{
		public byte MaximumFailedAttemptsToVerify { get; init; }
		public int SessionTimeoutInSeconds { get; init; }
		public byte MaxRoundRobinAttempts { get; init; }

		/// <summary>
		/// Days a session is kept after it expires, before a MongoDB TTL index removes it. Zero or
		/// less keeps sessions indefinitely.
		/// </summary>
		public int SessionRetentionDays { get; init; }

		/// <summary>
		/// Providers in the order they should be tried, which is what makes this the priority list.
		///
		/// A <see cref="List{T}"/> rather than a <see cref="HashSet{T}"/>: a set does not promise
		/// enumeration order, and the fact that one happens to preserve insertion order while nothing
		/// is removed is an implementation detail rather than something to build failover on. It is
		/// de-duplicated when it is read from configuration instead.
		/// </summary>
		public required List<EmailProvider> Priority { get; init; }
	}
}
