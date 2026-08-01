using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using EmailSwitch.Translations;
using HumanLanguages;
using SMSwitch.Common.DTOs;
using System.Globalization;
using System.Text.Encodings.Web;

namespace EmailSwitch.EmailTemplates
{
	public static class TemplateCreator
	{
		private const string FallbackSubject = "Email verification";

		internal static EmailContent CreateSendOTPEmail(EmailIdentifier emailPendingVerification, MobileNumber[] verifiedMobileNumbers, EmailIdentifier[] verifiedEmails, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, string generatedCode, DateTimeOffset expiryDateTimeOffset, Uri signatureLogoUri)
		{
			var verifiedMobileNumberStrings = verifiedMobileNumbers.Select(x => $"+{x.CountryPhoneCode} {x.PhoneNumberAsNumericString}").ToList();
			var verifiedEmailStrings = verifiedEmails.Select(x => x.GetRawValue()).ToList();

			// Only a handful of languages are translated, so an unlisted one must fall back rather
			// than throw. The parameterless LanguageIsoCode is English with the default locale.
			var preferredLanguageIsoCode = preferredLanguageIsoCodeList.FirstOrDefault() ?? new LanguageIsoCode();

			// Formatted invariantly and in 24-hour time: the culture-dependent alternative rendered
			// an empty AM/PM designator in several cultures, leaving an ambiguous hour. The value is
			// UTC, so say so rather than leaving the reader to guess the zone.
			var expiryTime = expiryDateTimeOffset.ToUniversalTime().ToString("dd-MM-yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

			return new EmailContent
			{
				Subject = TranslationKey.SendOTPEmailSubject.GetTranslation(preferredLanguageIsoCode).FirstOrDefault()
						  ?? TranslationKey.SendOTPEmailSubject.GetTranslation(new LanguageIsoCode()).FirstOrDefault()
						  ?? FallbackSubject,
				// The verified lists are omitted when empty rather than rendered as a bare heading with
				// nothing after it. Mobile numbers used to be unconditional, unlike emails right below.
				PlainTextContent = $"Verification Code: {generatedCode}\n" +
								   $"Expiry Time: {expiryTime}\n" +
								   (verifiedMobileNumberStrings.Any() ? $"Verified Mobile Numbers: {string.Join(", ", verifiedMobileNumberStrings)}\n" : "") +
								   (verifiedEmailStrings.Any() ? $"Verified Emails: {string.Join(", ", verifiedEmailStrings)}" : ""),
				// Every interpolated value is encoded: the addresses originate from user input, and
				// MailAddress validation is not an HTML sanitiser - a quoted local part or display
				// name can legitimately carry '<' or '"'.
				HtmlContent = $"<h1>Verification Code for {Encode(emailPendingVerification.GetRawValue())}: {Encode(generatedCode)}</h1>" +
							  $"<p>Expiry Time: {Encode(expiryTime)}</p>" +
							  (verifiedMobileNumberStrings.Any() ? $"<p>Verified Mobile Numbers: {Encode(string.Join(", ", verifiedMobileNumberStrings))}</p>" : "") +
							  (verifiedEmailStrings.Any() ? $"<p>Verified Emails: {Encode(string.Join(", ", verifiedEmailStrings))}</p>" : "") +
							  // alt text, so a client that blocks images - which many do by default for
							  // an unknown sender - shows something rather than an empty box. No
							  // dimensions: the logo should render at its natural size.
							  $"<img src=\"{Encode(signatureLogoUri.ToString())}\" alt=\"Signature logo\">"
			};
		}

		private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
	}
}
