namespace EmailSwitch.Common
{
	public class EmailSwitchGeneralSettings
	{
		public byte OtpLength { get; set; }
		public byte[] SignatureLogoBytes { get; set; } = [];

		/// <summary>
		/// Media type served for <see cref="SignatureLogoBytes"/>, derived from the configured
		/// SignatureLogoPath extension. Email clients need this to render the image.
		/// </summary>
		public string SignatureLogoContentType { get; set; } = "application/octet-stream";
	}
}
