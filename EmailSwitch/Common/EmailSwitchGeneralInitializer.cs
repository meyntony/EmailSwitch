using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Common
{
	public class EmailSwitchGeneralInitializer
	{
		private const string FallbackLogoContentType = "application/octet-stream";

		public readonly EmailSwitchGeneralSettings EmailSwitchGeneralSettings;
		public readonly IConfigurationSection EmailSwitchSettings;
		public EmailSwitchGeneralInitializer(IConfiguration configuration, ILogger<EmailSwitchGeneralInitializer> logger)
		{
			EmailSwitchSettings = configuration.GetSection(ConstantStrings.EmailSwitchSettingsName);

			byte defaultLength = 6;
			var otpLength = byte.TryParse(EmailSwitchSettings["OtpLength"], out byte l) ? l : defaultLength;

			var signatureLogoPath = EmailSwitchSettings["SignatureLogoPath"];

			if (string.IsNullOrWhiteSpace(signatureLogoPath))
			{
				throw new ArgumentException($"{ConstantStrings.EmailSwitchSettingsName}:SignatureLogoPath is missing.", nameof(configuration));
			}
			byte[] signatureLogoInBytes = [];
			try {
				signatureLogoInBytes = File.ReadAllBytes(signatureLogoPath);
			} catch (Exception ex) {
				// Not fatal: the logo endpoint turns empty bytes into a 404 rather than a broken
				// image, and an OTP email without a signature logo is still a working OTP email.
				logger.LogCritical(ex, "Unable to read the signature logo from {SignatureLogoPath}; emails will be sent without it.", signatureLogoPath);
			}


			EmailSwitchGeneralSettings = new EmailSwitchGeneralSettings()
			{
				OtpLength = otpLength,
				SignatureLogoBytes = signatureLogoInBytes,
				SignatureLogoContentType = ContentTypeFor(signatureLogoPath, logger)
			};
		}

		private static string ContentTypeFor(string signatureLogoPath, ILogger<EmailSwitchGeneralInitializer> logger)
		{
			var contentType = Path.GetExtension(signatureLogoPath).ToLowerInvariant() switch
			{
				".png" => "image/png",
				".jpg" or ".jpeg" => "image/jpeg",
				".gif" => "image/gif",
				".webp" => "image/webp",
				".svg" => "image/svg+xml",
				_ => FallbackLogoContentType
			};

			if (contentType == FallbackLogoContentType)
			{
				logger.LogWarning("Unrecognised signature logo extension on {SignatureLogoPath}; serving it as {ContentType}, which email clients may not render.", signatureLogoPath, FallbackLogoContentType);
			}

			return contentType;
		}
	}
}
