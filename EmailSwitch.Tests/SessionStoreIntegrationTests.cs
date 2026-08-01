using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Database.DTOs;
using HumanLanguages;
using MongoDB.Driver;
using SMSwitch.Common.DTOs;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Runs against a real MongoDB, because the behaviour under test is the server's: the
	/// GetLatestSession filter, and whether a concurrent counter actually counts. Neither can be
	/// established by reasoning over the C#, and both guard findings that reached main.
	/// </summary>
	public sealed class SessionStoreIntegrationTests
	{
		private static readonly EmailIdentifier Email = "user@example.com";

		private static Task<EmailSwitchSession?> CreateSession(EmailSwitchIntegrationFixture fixture) =>
			fixture.DbService.GetOrCreateAndGetLatestSession(
				Email, [], [], [new LanguageIsoCode()], UserAgent.WebBrowser);

		private static Task<EmailSwitchSession> Reload(EmailSwitchIntegrationFixture fixture, string sessionId) =>
			fixture.Database
				.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession))
				.Find(Builders<EmailSwitchSession>.Filter.Eq(session => session.SessionId, sessionId))
				.FirstOrDefaultAsync();

		/// <summary>
		/// The lockout. The send budget draining used to count as expiry, so once a resend spent the
		/// last slot the code already in the user's inbox stopped verifying.
		/// </summary>
		[Fact]
		public async Task A_session_whose_send_budget_is_spent_is_still_returned_for_verification()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);
			Assert.NotNull(session);

			// Exactly what SendOTP persists once the final slot is spent.
			await fixture.DbService.RegisterSendAttempts(
				session!.SessionId,
				new Queue<EmailProvider>(),
				[new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)]);

			var reloaded = await fixture.DbService.GetLatestSession(Email);

			Assert.NotNull(reloaded);
			Assert.Equal(session.SessionId, reloaded!.SessionId);
		}

		/// <summary>
		/// The brute-force cap. Read-modify-replace lost concurrent failures, and since
		/// MongoDbTokenManager 10.2.0 dropped its own limit this count is the only guard left.
		/// </summary>
		[Fact]
		public async Task Concurrent_failed_attempts_are_every_one_of_them_counted()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 250);
			var session = await CreateSession(fixture);
			Assert.NotNull(session);

			const int concurrentGuesses = 25;
			await Task.WhenAll(Enumerable
				.Range(0, concurrentGuesses)
				.Select(_ => fixture.DbService.RegisterFailedVerificationAttempt(session!.SessionId)));

			var reloaded = await Reload(fixture, session!.SessionId);

			Assert.Equal(concurrentGuesses, reloaded.FailedVerificationAttemptsUTC.Count);
		}

		/// <summary>
		/// The send path used to replace the whole session document, reading it before a provider call
		/// and writing it back after. Anything the server recorded in that window was reverted - most
		/// damagingly the brute-force counter, which meant a resend racing a guess handed the guesser
		/// its attempts back.
		/// </summary>
		[Fact]
		public async Task A_send_write_does_not_revert_what_the_server_recorded_while_it_was_in_flight()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 250);
			var session = await CreateSession(fixture);

			// Stands in for SendOTP having loaded the session before the provider call.
			var budgetAsRead = session!.EmailProvidersQueue;

			// Recorded server side while that send is notionally still awaiting its provider.
			await fixture.DbService.RegisterFailedVerificationAttempt(session.SessionId);
			await fixture.DbService.RegisterFailedVerificationAttempt(session.SessionId);
			await fixture.DbService.RegisterRenderRequest(session.SessionId);
			await fixture.DbService.RegisterSuccessfulVerification(session.SessionId);

			// The send now completes and writes what it knows.
			await fixture.DbService.RegisterSendAttempts(
				session.SessionId,
				new Queue<EmailProvider>(budgetAsRead ?? new Queue<EmailProvider>()),
				[new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)]);

			var reloaded = await Reload(fixture, session.SessionId);

			Assert.Equal(2, reloaded.FailedVerificationAttemptsUTC.Count);
			Assert.Single(reloaded.LogoRenderedAttemptsUTC);
			Assert.NotNull(reloaded.SuccessfullyVerifiedTimestampUTC);
			// And the send's own field did land.
			Assert.Single(reloaded.SentAttempts);
		}

		/// <summary>Two sends racing must both leave a record, not overwrite one another.</summary>
		[Fact]
		public async Task Concurrent_send_writes_each_leave_their_attempt()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			const int concurrentSends = 10;
			await Task.WhenAll(Enumerable.Range(0, concurrentSends).Select(_ =>
				fixture.DbService.RegisterSendAttempts(
					session!.SessionId,
					new Queue<EmailProvider>(),
					[new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)])));

			var reloaded = await Reload(fixture, session!.SessionId);

			Assert.Equal(concurrentSends, reloaded.SentAttempts.Count);
		}

		[Fact]
		public async Task A_session_that_has_used_up_its_attempts_is_no_longer_returned()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 3);
			var session = await CreateSession(fixture);

			for (var attempt = 0; attempt < 3; attempt++)
			{
				await fixture.DbService.RegisterFailedVerificationAttempt(session!.SessionId);
			}

			Assert.Null(await fixture.DbService.GetLatestSession(Email));
		}

		[Fact]
		public async Task The_attempt_before_the_cap_still_finds_the_session()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 3);
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterFailedVerificationAttempt(session!.SessionId);
			await fixture.DbService.RegisterFailedVerificationAttempt(session.SessionId);

			Assert.NotNull(await fixture.DbService.GetLatestSession(Email));
		}

		/// <summary>
		/// Covers the server-side Eq(SuccessfullyVerifiedTimestampUTC, null) predicate, which no unit
		/// test can reach - a null equality has to match missing fields too.
		/// </summary>
		[Fact]
		public async Task A_verified_session_is_no_longer_returned()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterSuccessfulVerification(session!.SessionId);

			Assert.Null(await fixture.DbService.GetLatestSession(Email));
		}

		[Fact]
		public async Task Registering_a_success_stamps_the_session()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterSuccessfulVerification(session!.SessionId);

			var reloaded = await Reload(fixture, session.SessionId);
			Assert.NotNull(reloaded.SuccessfullyVerifiedTimestampUTC);
			Assert.Equal(DateTimeKind.Utc, reloaded.SuccessfullyVerifiedTimestampUTC!.Value.Kind);
		}

		/// <summary>A live session is reused, so a resend does not mint a second code.</summary>
		[Fact]
		public async Task An_existing_live_session_is_reused_rather_than_duplicated()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var first = await CreateSession(fixture);
			var second = await CreateSession(fixture);

			Assert.NotNull(first);
			Assert.NotNull(second);
			Assert.Equal(first!.SessionId, second!.SessionId);
		}

		[Fact]
		public async Task A_new_session_carries_a_rendered_email_and_no_queue_yet()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var session = await CreateSession(fixture);

			Assert.NotNull(session);
			// Null rather than empty: SendOTP reads that as "budget not built yet".
			Assert.Null(session!.EmailProvidersQueue);
			Assert.Contains("Verification Code", session.SendOTPEmail.PlainTextContent);
		}

		[Fact]
		public async Task Registering_a_render_request_is_recorded()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterRenderRequest(session!.SessionId);
			await fixture.DbService.RegisterRenderRequest(session.SessionId);

			var reloaded = await Reload(fixture, session.SessionId);
			Assert.Equal(2, reloaded.LogoRenderedAttemptsUTC.Count);
		}
	}
}
