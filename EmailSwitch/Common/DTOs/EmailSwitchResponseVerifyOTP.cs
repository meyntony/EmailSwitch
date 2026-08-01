namespace EmailSwitch.Common.DTOs
{
	public sealed class EmailSwitchResponseVerifyOTP
	{
		public bool Verified { get; init; }

		/// <summary>
		/// True when there was no live session to check the code against - it timed out, ran out of
		/// verification attempts, was already used, or could not be read. The caller should ask the
		/// user to request a new code rather than retry this one.
		/// </summary>
		public bool Expired { get; init; }
	}
}
