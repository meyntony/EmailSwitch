using EmailSwitch.Translations;
using HumanLanguages;

namespace EmailSwitch.Tests
{
	public sealed class TranslationServiceTests
	{
		[Fact]
		public void A_translated_language_resolves()
		{
			var translation = TranslationKey.SendOTPEmailSubject.GetTranslation(new LanguageIsoCode(LanguageId.en));

			Assert.Equal(["Email verification"], translation);
		}

		[Fact]
		public void A_locale_variation_with_no_entry_of_its_own_falls_back_to_the_language_default()
		{
			var translation = TranslationKey.SendOTPEmailSubject.GetTranslation(
				new LanguageIsoCode(LanguageId.en, LanguageLocaleVariationCode.GB));

			Assert.Equal(["Email verification"], translation);
		}

		/// <summary>
		/// An unmapped language returns empty rather than null, so callers can fall back without a
		/// null check. TemplateCreator depends on this.
		/// </summary>
		[Theory]
		[InlineData(LanguageId.zu)]
		[InlineData(LanguageId.fr)]
		public void An_unmapped_language_returns_an_empty_array(LanguageId languageId)
		{
			var translation = TranslationKey.SendOTPEmailSubject.GetTranslation(new LanguageIsoCode(languageId));

			Assert.NotNull(translation);
			Assert.Empty(translation);
		}

		/// <summary>
		/// LanguageIsoCode defaults to English, which is what makes it usable as TemplateCreator's
		/// fallback. HumanLanguages 11.0.0 pinned the enum values explicitly, so this is worth
		/// pinning here too.
		/// </summary>
		[Fact]
		public void The_default_language_iso_code_is_english()
		{
			var isoCode = new LanguageIsoCode();

			Assert.Equal(LanguageId.en, isoCode.LanguageId);
			Assert.Equal(LanguageLocaleVariationCode.Default, isoCode.LanguageLocaleVariationCode);
			Assert.Equal(["Email verification"], TranslationKey.SendOTPEmailSubject.GetTranslation(isoCode));
		}
	}
}
