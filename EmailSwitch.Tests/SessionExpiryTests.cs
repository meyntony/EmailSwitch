using EmailSwitch.Common;
using EmailSwitch.Database.DTOs;
using EmailSwitch.EmailTemplates.DTOs;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// HasNotExpired is what stands between a caller and unlimited OTP guesses, and since
	/// MongoDbTokenManager 10.2.0 removed its own MAXIMUM_ATTEMPTS = 5 counter from Validate it is
	/// now the <em>only</em> brute-force guard on a six digit code. Each of its four conditions is
	/// pinned down separately so a future refactor cannot quietly reopen that door.
	/// </summary>
	public sealed class SessionExpiryTests
	{
		private const byte MaximumFailedAttemptsToVerify = 3;

		private static EmailSwitchSession CreateSession(
			Queue<EmailProvider>? emailProvidersQueue = null,
			int failedVerificationAttempts = 0,
			DateTime? successfullyVerifiedTimestampUTC = null,
			DateTime? expiryTimeUTC = null) =>
			new()
			{
				SessionId = Guid.NewGuid().ToString(),
				EmailId = "user@example.com",
				StartTimeUTC = DateTime.UtcNow,
				ExpiryTimeUTC = expiryTimeUTC ?? DateTime.UtcNow.AddMinutes(4),
				EmailProvidersQueue = emailProvidersQueue,
				SuccessfullyVerifiedTimestampUTC = successfullyVerifiedTimestampUTC,
				SendOTPEmail = new EmailContent
				{
					Subject = "Email verification",
					PlainTextContent = "Verification Code: 123456",
					HtmlContent = "<h1>Verification Code: 123456</h1>"
				},
				FailedVerificationAttemptsUTC = Enumerable
					.Range(0, failedVerificationAttempts)
					.Select(_ => DateTime.UtcNow)
					.ToList()
			};

		[Fact]
		public void A_fresh_session_with_no_queue_yet_has_not_expired()
		{
			Assert.True(CreateSession().HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void A_session_with_providers_left_has_not_expired()
		{
			var session = CreateSession(new Queue<EmailProvider>([EmailProvider.SendGrid]));

			Assert.True(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>An empty queue means every provider has been tried and failed.</summary>
		[Fact]
		public void A_session_whose_provider_queue_is_exhausted_has_expired()
		{
			var session = CreateSession(new Queue<EmailProvider>());

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>
		/// The attempt cap. Because GetLatestSession filters on HasNotExpired, the attempt that tips
		/// the count to the maximum is the last one that can ever find a session - every later guess
		/// finds nothing to check against.
		/// </summary>
		[Theory]
		[InlineData(0, true)]
		[InlineData(1, true)]
		[InlineData(2, true)]
		[InlineData(3, false)]
		[InlineData(4, false)]
		[InlineData(50, false)]
		public void Failed_verification_attempts_are_capped(int failedAttempts, bool expectedHasNotExpired)
		{
			var session = CreateSession(failedVerificationAttempts: failedAttempts);

			Assert.Equal(expectedHasNotExpired, session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>An OTP must not be verifiable twice.</summary>
		[Fact]
		public void An_already_verified_session_has_expired()
		{
			var session = CreateSession(successfullyVerifiedTimestampUTC: DateTime.UtcNow);

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void A_session_past_its_expiry_time_has_expired()
		{
			var session = CreateSession(expiryTimeUTC: DateTime.UtcNow.AddSeconds(-1));

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>
		/// Any single failing condition must be enough, regardless of how healthy the rest look.
		/// </summary>
		[Fact]
		public void One_failing_condition_is_enough_to_expire_an_otherwise_live_session()
		{
			var session = CreateSession(
				emailProvidersQueue: new Queue<EmailProvider>([EmailProvider.SendGrid]),
				failedVerificationAttempts: MaximumFailedAttemptsToVerify,
				expiryTimeUTC: DateTime.UtcNow.AddHours(1));

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}
	}
}
