using EmailSwitch.Common.DTOs;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// EmailIdentifier is the key sessions are stored under, so its normalisation decides whether two
	/// spellings of one inbox share a session or get two.
	/// </summary>
	public sealed class EmailIdentifierTests
	{
		[Theory]
		[InlineData("user@example.com", "user@example.com")]
		[InlineData("User@Example.COM", "user@example.com")]
		[InlineData("  user@example.com", "user@example.com")]
		public void Addresses_are_lowercased(string input, string expectedId)
		{
			Assert.Equal(expectedId, new EmailIdentifier(input).ToString());
		}

		/// <summary>Gmail ignores dots in the local part, so two spellings are one inbox.</summary>
		[Theory]
		[InlineData("j.o.h.n@gmail.com", "john@gmail.com")]
		[InlineData("john@gmail.com", "john@gmail.com")]
		public void Gmail_dots_are_collapsed(string input, string expectedId)
		{
			Assert.Equal(expectedId, new EmailIdentifier(input).ToString());
		}

		/// <summary>Other providers treat dots as significant, so they must survive.</summary>
		[Fact]
		public void Dots_are_preserved_outside_gmail()
		{
			Assert.Equal("j.o.h.n@example.com", new EmailIdentifier("j.o.h.n@example.com").ToString());
		}

		[Theory]
		[InlineData("john+newsletter@gmail.com", "john@gmail.com")]
		[InlineData("user+tag@example.com", "user@example.com")]
		public void Plus_addressing_is_stripped(string input, string expectedId)
		{
			Assert.Equal(expectedId, new EmailIdentifier(input).ToString());
		}

		/// <summary>
		/// The raw value is what gets emailed, so it must survive normalisation untouched.
		/// </summary>
		[Fact]
		public void The_raw_value_is_preserved_verbatim()
		{
			const string input = "John+Tag@Example.COM";

			Assert.Equal(input, new EmailIdentifier(input).GetRawValue());
		}

		[Fact]
		public void Two_spellings_of_one_gmail_inbox_are_equal()
		{
			EmailIdentifier a = "j.o.h.n+promo@gmail.com";
			EmailIdentifier b = "John@Gmail.com";

			Assert.Equal(a, b);
			Assert.True(a == b);
			Assert.Equal(a.GetHashCode(), b.GetHashCode());
		}

		[Fact]
		public void Different_inboxes_are_not_equal()
		{
			EmailIdentifier a = "one@example.com";
			EmailIdentifier b = "two@example.com";

			Assert.NotEqual(a, b);
			Assert.True(a != b);
		}

		[Theory]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData("not-an-email")]
		public void Junk_is_rejected(string input)
		{
			Assert.ThrowsAny<Exception>(() => new EmailIdentifier(input));
		}

		[Fact]
		public void A_mismatched_email_id_is_rejected()
		{
			Assert.ThrowsAny<Exception>(() => new EmailIdentifier("user@example.com", "someone.else@example.com"));
		}

		[Fact]
		public void A_matching_email_id_is_accepted()
		{
			var identifier = new EmailIdentifier("John+tag@Example.com", "john@example.com");

			Assert.Equal("john@example.com", identifier.ToString());
		}
	}
}
