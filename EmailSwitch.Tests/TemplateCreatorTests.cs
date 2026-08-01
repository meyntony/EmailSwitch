using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates;
using EmailSwitch.EmailTemplates.DTOs;
using HumanLanguages;
using SMSwitch.Common.DTOs;
using System.Globalization;

namespace EmailSwitch.Tests
{
	public sealed class TemplateCreatorTests
	{
		private static readonly Uri SignatureLogoUri = new("https://api.example.com/emailswitch/logo/abc123");

		private static MobileNumber MobileNumber(string countryPhoneCode = "45", string phoneNumber = "12345678") =>
			new()
			{
				CountryIsoCodeString = "DK",
				CountryPhoneCode = countryPhoneCode,
				PhoneNumber = phoneNumber
			};

		private static EmailContent Create(
			string emailPendingVerification = "user@example.com",
			HashSet<LanguageIsoCode>? preferredLanguages = null,
			EmailIdentifier[]? verifiedEmails = null,
			DateTimeOffset? expiry = null,
			string generatedCode = "123456") =>
			TemplateCreator.CreateSendOTPEmail(
				emailPendingVerification: emailPendingVerification,
				verifiedMobileNumbers: [MobileNumber()],
				verifiedEmails: verifiedEmails ?? [],
				preferredLanguageIsoCodeList: preferredLanguages ?? [new LanguageIsoCode(LanguageId.en)],
				userAgent: UserAgent.WebBrowser,
				generatedCode: generatedCode,
				expiryDateTimeOffset: expiry ?? new DateTimeOffset(2026, 7, 31, 14, 5, 9, TimeSpan.Zero),
				signatureLogoUri: SignatureLogoUri);

		// ---------------------------------------------------------------- translations

		[Fact]
		public void English_gets_the_English_subject()
		{
			Assert.Equal("Email verification", Create().Subject);
		}

		[Fact]
		public void Danish_gets_the_Danish_subject()
		{
			var content = Create(preferredLanguages: [new LanguageIsoCode(LanguageId.da)]);

			Assert.Equal("E-mailbekræftelse", content.Subject);
		}

		/// <summary>
		/// A locale variation with no translation of its own must fall back to the language default
		/// rather than come back empty.
		/// </summary>
		[Fact]
		public void An_untranslated_locale_variation_falls_back_to_the_language_default()
		{
			var content = Create(preferredLanguages: [new LanguageIsoCode(LanguageId.da, LanguageLocaleVariationCode.DK)]);

			Assert.Equal("E-mailbekræftelse", content.Subject);
		}

		/// <summary>
		/// HumanLanguages 11.0.0 carries 240 LanguageId values and only two are translated here.
		/// This used to be First() over an empty array, so any other language threw
		/// InvalidOperationException instead of sending an email.
		/// </summary>
		[Theory]
		[InlineData(LanguageId.zu)]
		[InlineData(LanguageId.fr)]
		[InlineData(LanguageId.ja)]
		public void An_untranslated_language_falls_back_to_English_instead_of_throwing(LanguageId languageId)
		{
			var content = Create(preferredLanguages: [new LanguageIsoCode(languageId)]);

			Assert.Equal("Email verification", content.Subject);
		}

		/// <summary>Also previously a First() on an empty sequence.</summary>
		[Fact]
		public void An_empty_preferred_language_list_falls_back_to_English_instead_of_throwing()
		{
			var content = Create(preferredLanguages: []);

			Assert.Equal("Email verification", content.Subject);
		}

		// ---------------------------------------------------------------- HTML encoding

		/// <summary>
		/// MailAddress accepts a quoted local part containing '&lt;', and EmailIdentifier keeps the
		/// raw input verbatim, so an unencoded template let that reach the email body as markup.
		/// </summary>
		[Fact]
		public void The_pending_address_is_html_encoded()
		{
			const string hostileAddress = "\"quoted<tag\"@example.com";

			var content = Create(emailPendingVerification: hostileAddress);

			Assert.DoesNotContain("quoted<tag", content.HtmlContent);
			Assert.Contains("quoted&lt;tag", content.HtmlContent);
		}

		[Fact]
		public void Verified_addresses_are_html_encoded()
		{
			var content = Create(verifiedEmails: [new EmailIdentifier("\"a<b\"@example.com")]);

			Assert.DoesNotContain("a<b", content.HtmlContent);
			Assert.Contains("a&lt;b", content.HtmlContent);
		}

		[Fact]
		public void The_signature_logo_uri_is_html_encoded_in_its_attribute()
		{
			var content = Create();

			Assert.Contains($"<img src=\"{SignatureLogoUri}\" alt=\"Signature logo\">", content.HtmlContent);
		}

		/// <summary>
		/// Many clients block images from an unknown sender by default, so the signature needs alt
		/// text rather than leaving an empty box. It must not carry dimensions - that would render the
		/// logo as a one pixel tracking dot instead of a logo.
		/// </summary>
		[Fact]
		public void The_signature_logo_has_alt_text_and_no_forced_dimensions()
		{
			var content = Create();

			Assert.Contains("alt=\"Signature logo\"", content.HtmlContent);
			Assert.DoesNotContain("width=", content.HtmlContent);
			Assert.DoesNotContain("height=", content.HtmlContent);
		}

		// ---------------------------------------------------------------- empty verified lists

		/// <summary>
		/// Mobile numbers used to be rendered unconditionally, so an empty list produced a bare
		/// "Verified Mobile Numbers:" heading with nothing after it - unlike verified emails, which
		/// were already conditional.
		/// </summary>
		[Fact]
		public void An_empty_verified_mobile_number_list_is_omitted_entirely()
		{
			var content = TemplateCreator.CreateSendOTPEmail(
				emailPendingVerification: "user@example.com",
				verifiedMobileNumbers: [],
				verifiedEmails: [],
				preferredLanguageIsoCodeList: [new LanguageIsoCode(LanguageId.en)],
				userAgent: UserAgent.WebBrowser,
				generatedCode: "123456",
				expiryDateTimeOffset: DateTimeOffset.UtcNow.AddMinutes(4),
				signatureLogoUri: SignatureLogoUri);

			Assert.DoesNotContain("Verified Mobile Numbers", content.PlainTextContent);
			Assert.DoesNotContain("Verified Mobile Numbers", content.HtmlContent);
			Assert.DoesNotContain("Verified Emails", content.PlainTextContent);
		}

		[Fact]
		public void A_populated_verified_mobile_number_list_is_still_rendered()
		{
			var content = Create();

			Assert.Contains("Verified Mobile Numbers: +45 12345678", content.PlainTextContent);
			// The HTML body is encoded, and HtmlEncoder.Default escapes '+' to &#x2B;, so the number
			// is asserted in its encoded form rather than as it reads in plain text.
			Assert.Contains("Verified Mobile Numbers: &#x2B;45 12345678", content.HtmlContent);
		}

		/// <summary>Encoding belongs to the HTML body only - plain text must stay literal.</summary>
		[Fact]
		public void The_plain_text_body_is_not_html_encoded()
		{
			var content = Create(emailPendingVerification: "\"quoted<tag\"@example.com");

			Assert.DoesNotContain("&lt;", content.PlainTextContent);
			Assert.DoesNotContain("&quot;", content.PlainTextContent);
		}

		[Fact]
		public void Both_bodies_carry_the_verification_code()
		{
			var content = Create(generatedCode: "987654");

			Assert.Contains("987654", content.PlainTextContent);
			Assert.Contains("987654", content.HtmlContent);
		}

		// ---------------------------------------------------------------- expiry timestamp

		/// <summary>
		/// The old format string used a 12 hour clock with no culture, so the AM/PM designator came
		/// back empty in cultures such as da-DK and the hour became ambiguous. Under a non-Gregorian
		/// calendar culture the date itself changed.
		/// </summary>
		[Theory]
		[InlineData("en-US")]
		[InlineData("da-DK")]
		[InlineData("ar-SA")]
		[InlineData("th-TH")]
		public void The_expiry_timestamp_is_culture_independent(string culture)
		{
			var previousCulture = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo(culture);

				var content = Create(expiry: new DateTimeOffset(2026, 7, 31, 14, 5, 9, TimeSpan.Zero));

				Assert.Contains("31-07-2026 14:05:09 UTC", content.PlainTextContent);
			}
			finally
			{
				CultureInfo.CurrentCulture = previousCulture;
			}
		}

		/// <summary>The rendered time must be the UTC instant, whatever offset it arrives with.</summary>
		[Fact]
		public void The_expiry_timestamp_is_normalised_to_utc()
		{
			var content = Create(expiry: new DateTimeOffset(2026, 7, 31, 16, 5, 9, TimeSpan.FromHours(2)));

			Assert.Contains("31-07-2026 14:05:09 UTC", content.PlainTextContent);
		}
	}
}
