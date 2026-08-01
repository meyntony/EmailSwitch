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

		/// <summary>Moves a session's deadline into the past, as the clock eventually would.</summary>
		private static Task ExpireSession(EmailSwitchIntegrationFixture fixture, string sessionId) =>
			fixture.Database
				.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession))
				.UpdateOneAsync(
					Builders<EmailSwitchSession>.Filter.Eq(session => session.SessionId, sessionId),
					Builders<EmailSwitchSession>.Update.Set(session => session.ExpiryTimeUTC, DateTime.UtcNow.AddMinutes(-1)));

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

		// --------------------------------------------------------- one live session per address

		/// <summary>
		/// Find-then-insert with no uniqueness constraint: two concurrent first sends both found
		/// nothing, both minted a code and both inserted. The user got two emails with two different
		/// codes, and only the one GetLatestSession happened to return would ever verify.
		/// </summary>
		[Fact]
		public async Task Concurrent_first_sends_open_exactly_one_session()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			// The claim index is created on first use, and this test is about what happens without a
			// prior read, so make sure it exists before racing.
			await fixture.DbService.GetLatestSession(Email);

			var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => CreateSession(fixture)));

			Assert.All(sessions, session => Assert.NotNull(session));
			Assert.Single(sessions.Select(session => session!.SessionId).Distinct());
		}

		/// <summary>
		/// The successor a user is entitled to once the previous session times out. A unique index
		/// cannot express liveness, so the claim is released explicitly - and a version that forgot to
		/// would lock the address out until the retention TTL removed the old session.
		/// </summary>
		[Fact]
		public async Task A_new_session_can_be_opened_once_the_previous_one_expires()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var first = await CreateSession(fixture);

			// Time it out where it stands, which is what the clock would have done.
			await ExpireSession(fixture, first!.SessionId);

			var second = await CreateSession(fixture);

			Assert.NotNull(second);
			Assert.NotEqual(first.SessionId, second!.SessionId);
		}

		/// <summary>A verified session is finished, so the next send gets a session of its own.</summary>
		[Fact]
		public async Task A_new_session_can_be_opened_once_the_previous_one_is_verified()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var first = await CreateSession(fixture);

			await fixture.DbService.RegisterSuccessfulVerification(first!.SessionId);
			var second = await CreateSession(fixture);

			Assert.NotNull(second);
			Assert.NotEqual(first.SessionId, second!.SessionId);
		}

		/// <summary>
		/// Burning the attempts must still leave a way to ask for a new code, or a mistyped digit
		/// would lock the address out until the old session timed out.
		/// </summary>
		[Fact]
		public async Task A_new_session_can_be_opened_once_the_attempts_are_spent()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 3);
			var first = await CreateSession(fixture);

			for (var attempt = 0; attempt < 3; attempt++)
			{
				await fixture.DbService.TryReserveVerificationAttempt(first!.SessionId, 3);
			}

			var second = await CreateSession(fixture);

			Assert.NotNull(second);
			Assert.NotEqual(first!.SessionId, second!.SessionId);
		}

		/// <summary>Two different addresses are unrelated and must not contend for one claim.</summary>
		[Fact]
		public async Task Different_addresses_each_get_their_own_live_session()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var first = await CreateSession(fixture);
			var second = await fixture.DbService.GetOrCreateAndGetLatestSession(
				"someone.else@example.com", [], [], [new LanguageIsoCode()], UserAgent.WebBrowser);

			Assert.NotNull(second);
			Assert.NotEqual(first!.SessionId, second!.SessionId);
		}

		/// <summary>
		/// Released claims must not collide with one another. The index is unique, so if releasing
		/// wrote a null instead of removing the field, the second address to release one would be
		/// rejected and could never open another session.
		/// </summary>
		[Fact]
		public async Task Many_released_claims_can_coexist()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			string[] addresses = ["a@example.com", "b@example.com", "c@example.com"];

			foreach (var address in addresses)
			{
				var session = await fixture.DbService.GetOrCreateAndGetLatestSession(
					address, [], [], [new LanguageIsoCode()], UserAgent.WebBrowser);

				// Releases the claim.
				await fixture.DbService.RegisterSuccessfulVerification(session!.SessionId);
			}

			// Every address can still open a fresh session afterwards.
			foreach (var address in addresses)
			{
				Assert.NotNull(await fixture.DbService.GetOrCreateAndGetLatestSession(
					address, [], [], [new LanguageIsoCode()], UserAgent.WebBrowser));
			}
		}

		// ------------------------------------------------------------ retiring the cleartext code

		/// <summary>
		/// MongoDbTokenManager stores only a hash of the code. Keeping the rendered email - which
		/// carries the code in cleartext, plus the recipient's verified numbers and emails - defeated
		/// that, and the retention TTL kept it for 90 days after a four minute session.
		/// </summary>
		[Fact]
		public async Task Verifying_a_session_retires_the_rendered_email()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);
			Assert.NotNull((await Reload(fixture, session!.SessionId)).SendOTPEmail);

			await fixture.DbService.RegisterSuccessfulVerification(session.SessionId);

			var reloaded = await Reload(fixture, session.SessionId);
			Assert.Null(reloaded.SendOTPEmail);
			// The rest of the audit record survives.
			Assert.NotNull(reloaded.SuccessfullyVerifiedTimestampUTC);
			Assert.Equal(session.SessionId, reloaded.SessionId);
			Assert.Equal(session.EmailId, reloaded.EmailId);
		}

		/// <summary>Nothing can send it again, so the code has no further use.</summary>
		[Fact]
		public async Task Spending_the_send_budget_retires_the_rendered_email()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterSendAttempts(
				session!.SessionId,
				new Queue<EmailProvider>(),
				[new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)]);

			var reloaded = await Reload(fixture, session.SessionId);
			Assert.Null(reloaded.SendOTPEmail);
			Assert.Single(reloaded.SentAttempts);
		}

		/// <summary>
		/// A budget with slots left means a resend is still possible, and a resend reuses the rendered
		/// email - so it must survive until the budget is actually spent.
		/// </summary>
		[Fact]
		public async Task A_send_that_leaves_budget_keeps_the_rendered_email()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterSendAttempts(
				session!.SessionId,
				new Queue<EmailProvider>([EmailProvider.SendGrid]),
				[new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)]);

			Assert.NotNull((await Reload(fixture, session.SessionId)).SendOTPEmail);
		}

		/// <summary>
		/// A retired session must still deserialise. The element is gone from the document entirely,
		/// and `required` would not have stopped the driver handing back null through a non-nullable
		/// property - it is a compile-time construct the BSON layer does not enforce.
		/// </summary>
		[Fact]
		public async Task A_session_whose_email_was_retired_still_loads()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);
			await fixture.DbService.RegisterSendAttempts(session!.SessionId, new Queue<EmailProvider>(), []);

			// Still returned for verification - that goes through the token, not the body.
			var reloaded = await fixture.DbService.GetLatestSession(Email);

			Assert.NotNull(reloaded);
			Assert.Null(reloaded!.SendOTPEmail);
		}

		// ------------------------------------------------------------------ the attempt cap

		/// <summary>
		/// The guard that matters most. Reading the session, testing HasNotExpired and counting the
		/// failure afterwards is check-then-act: guesses issued in parallel all passed the test before
		/// any of them had been recorded, so the cap held sequentially and not at all under
		/// concurrency. Since MongoDbTokenManager 10.2.0 dropped its own limit this is the only guard
		/// on a six digit code.
		/// </summary>
		[Theory]
		[InlineData(1)]
		[InlineData(3)]
		[InlineData(5)]
		public async Task Concurrent_guesses_cannot_claim_more_attempts_than_the_cap(byte maximumFailedAttemptsToVerify)
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify);
			var session = await CreateSession(fixture);

			const int concurrentGuesses = 60;
			var reservations = await Task.WhenAll(Enumerable
				.Range(0, concurrentGuesses)
				.Select(_ => fixture.DbService.TryReserveVerificationAttempt(session!.SessionId, maximumFailedAttemptsToVerify)));

			Assert.Equal(maximumFailedAttemptsToVerify, reservations.Count(reservation => reservation is not null));
		}

		[Fact]
		public async Task Attempts_are_refused_once_the_cap_is_reached()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 3);
			var session = await CreateSession(fixture);

			for (var attempt = 0; attempt < 3; attempt++)
			{
				Assert.NotNull(await fixture.DbService.TryReserveVerificationAttempt(session!.SessionId, 3));
			}

			Assert.Null(await fixture.DbService.TryReserveVerificationAttempt(session!.SessionId, 3));
		}

		/// <summary>
		/// The upgrade path. A session written before VerificationAttemptsCount existed carries its
		/// attempts only in the audit list, and deserialises the counter to zero - so a cap read from
		/// the counter alone would hand every in-flight session a fresh set of guesses.
		/// </summary>
		[Fact]
		public async Task A_session_predating_the_counter_is_still_capped_by_its_audit_list()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(maximumFailedAttemptsToVerify: 3);
			var session = await CreateSession(fixture);

			// Exactly the shape of a pre-upgrade session: failures recorded, counter never written.
			for (var attempt = 0; attempt < 3; attempt++)
			{
				await fixture.DbService.RegisterFailedVerificationAttempt(session!.SessionId);
			}
			Assert.Equal(0, (await Reload(fixture, session!.SessionId)).VerificationAttemptsCount);

			Assert.Null(await fixture.DbService.TryReserveVerificationAttempt(session.SessionId, 3));
		}

		/// <summary>A correct guess claims a slot too, but must not be recorded as a failure.</summary>
		[Fact]
		public async Task Claiming_an_attempt_does_not_write_to_the_failure_audit_trail()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.TryReserveVerificationAttempt(session!.SessionId, 3);

			var reloaded = await Reload(fixture, session.SessionId);
			Assert.Equal(1, reloaded.VerificationAttemptsCount);
			Assert.Empty(reloaded.FailedVerificationAttemptsUTC);
		}

		[Fact]
		public async Task A_verified_session_refuses_further_attempts()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			await fixture.DbService.RegisterSuccessfulVerification(session!.SessionId);

			Assert.Null(await fixture.DbService.TryReserveVerificationAttempt(session.SessionId, 3));
		}

		/// <summary>A cap of zero permits nothing, and must not index into the array at -1.</summary>
		[Fact]
		public async Task A_cap_of_zero_refuses_every_attempt()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();
			var session = await CreateSession(fixture);

			Assert.Null(await fixture.DbService.TryReserveVerificationAttempt(session!.SessionId, 0));
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
			Assert.NotNull(session.SendOTPEmail);
			Assert.Contains("Verification Code", session.SendOTPEmail!.PlainTextContent);
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
