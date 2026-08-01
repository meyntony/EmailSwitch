namespace EmailSwitch.Common
{
	public class EmailSwitchGeneralSettings
	{
		public byte OtpLength { get; init; }

		/// <summary>
		/// Read once at startup and then shared by reference with every response that serves it, so
		/// the reference at least cannot be swapped after construction.
		/// </summary>
		public byte[] SignatureLogoBytes { get; init; } = [];

		/// <summary>
		/// Media type served for <see cref="SignatureLogoBytes"/>, derived from the configured
		/// SignatureLogoPath extension. Email clients need this to render the image.
		/// </summary>
		public string SignatureLogoContentType { get; init; } = "application/octet-stream";
	}
}
