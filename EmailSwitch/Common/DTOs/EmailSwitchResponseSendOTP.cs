namespace EmailSwitch.Common.DTOs
{
	public sealed class EmailSwitchResponseSendOTP
	{
		/// <summary>
		/// Whether a provider <em>accepted</em> the message, which is not the same as delivering it.
		/// Every provider here accepts synchronously and delivers later, so a bounce, a suppression
		/// list or an unauthenticated sender all report true here and fail afterwards. Delivery
		/// failures arrive over the provider's webhook instead - see <c>Webhooks/</c>.
		/// </summary>
		public bool IsSent { get; set; }
		public byte OtpLength { get; set; }
		public DateTimeOffset ExpiryDateTimeOffset { get; set; }

		/// <summary>
		/// The provider's own id for the accepted message, when it returned one. This is what a later
		/// delivery event is correlated back to a session by, so a provider that does not report one
		/// cannot participate in delivery failover.
		///
		/// Null on a failed send, and on DevConsole, which has nothing to correlate.
		/// </summary>
		public string? ProviderMessageId { get; set; }
	}
}
