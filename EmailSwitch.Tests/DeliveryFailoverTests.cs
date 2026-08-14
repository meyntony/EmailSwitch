using EmailSwitch.Common;
using EmailSwitch.Database.DTOs;
using EmailSwitch.Webhooks;
using EmailSwitch.Webhooks.Brevo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Brevo's vocabulary, mapped onto the one decision that matters: will this message ever arrive?
	/// </summary>
	public sealed class BrevoDeliveryEventParserTests
	{
		private static string Payload(string eventName, string messageId = "<abc@smtp-relay.mailin.fr>") =>
			$$"""
			{"event":"{{eventName}}","email":"user@example.com","message-id":"{{messageId}}","reason":"Sender not authorised","ts_event":1786000000}
			""";

		[Theory]
		[InlineData("hardBounce")]
		[InlineData("blocked")]
		[InlineData("invalid")]
		[InlineData("error")]
		public void Terminal_failures_are_recognised(string eventName)
		{
			var deliveryEvent = BrevoDeliveryEventParser.Parse(Payload(eventName));

			Assert.NotNull(deliveryEvent);
			Assert.True(deliveryEvent.IsTerminalFailure);
			Assert.Equal(EmailProvider.Brevo, deliveryEvent.EmailProvider);
			Assert.Equal("<abc@smtp-relay.mailin.fr>", deliveryEvent.ProviderMessageId);
			Assert.Equal("Sender not authorised", deliveryEvent.Reason);
		}

		/// <summary>
		/// Brevo retries deferrals and soft bounces itself, so acting on one would put a second copy of
		/// the same code in the inbox alongside the one still in flight. <c>spam</c> is not a delivery
		/// failure at all - the recipient marked a message they received, and resending is the last
		/// thing they want.
		/// </summary>
		[Theory]
		[InlineData("delivered")]
		[InlineData("deferred")]
		[InlineData("softBounce")]
		[InlineData("spam")]
		[InlineData("opened")]
		[InlineData("unsubscribed")]
		public void Everything_else_is_not_a_terminal_failure(string eventName)
		{
			var deliveryEvent = BrevoDeliveryEventParser.Parse(Payload(eventName));

			Assert.NotNull(deliveryEvent);
			Assert.False(deliveryEvent.IsTerminalFailure);
		}

		[Fact]
		public void Event_names_are_matched_case_insensitively()
		{
			var deliveryEvent = BrevoDeliveryEventParser.Parse(Payload("HARDBOUNCE"));

			Assert.NotNull(deliveryEvent);
			Assert.True(deliveryEvent.IsTerminalFailure);
		}

		/// <summary>
		/// An unrecognised payload is ignored rather than throwing: the endpoint answers 200 either way,
		/// because a 4xx would earn a redelivery of something that will never parse.
		/// </summary>
		[Theory]
		[InlineData("")]
		[InlineData("not json at all")]
		[InlineData("[]")]
		[InlineData("""{"event":"hardBounce"}""")]
		[InlineData("""{"message-id":"<abc@example.com>"}""")]
		public void An_unusable_payload_yields_nothing(string body)
		{
			Assert.Null(BrevoDeliveryEventParser.Parse(body));
		}

		/// <summary>The reason is optional; its absence must not lose the event.</summary>
		[Fact]
		public void A_payload_with_no_reason_still_parses()
		{
			var deliveryEvent = BrevoDeliveryEventParser.Parse("""{"event":"blocked","message-id":"<abc@example.com>"}""");

			Assert.NotNull(deliveryEvent);
			Assert.True(deliveryEvent.IsTerminalFailure);
			Assert.Null(deliveryEvent.Reason);
		}
	}

	/// <summary>
	/// The idempotent claim, against a real server. Webhooks retry, and a redelivered bounce that
	/// claimed a second budget slot would mail the recipient twice - so this is measured rather than
	/// argued, as the verification cap was.
	/// </summary>
	public sealed class DeliveryEventClaimIntegrationTests
	{
		[Fact]
		public async Task An_event_can_only_be_claimed_once()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var session = await InsertSession(fixture);

			Assert.True(await fixture.DbService.TryClaimDeliveryEvent(session, "Brevo:<abc@example.com>:hardBounce"));
			Assert.False(await fixture.DbService.TryClaimDeliveryEvent(session, "Brevo:<abc@example.com>:hardBounce"));
		}

		/// <summary>
		/// The property that matters. Ten redeliveries arriving together must yield exactly one claim;
		/// testing membership on a loaded session and writing afterwards is check-then-act, which is how
		/// the verification cap once admitted sixteen guesses against a limit of three.
		/// </summary>
		[Fact]
		public async Task Concurrent_redeliveries_yield_exactly_one_claim()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var session = await InsertSession(fixture);

			var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
				fixture.DbService.TryClaimDeliveryEvent(session, "Brevo:<abc@example.com>:hardBounce")));

			Assert.Equal(1, claims.Count(claimed => claimed));
		}

		/// <summary>
		/// Keyed on the event as well as the message id, so a message that legitimately produces two
		/// different terminal events is not silently reduced to one.
		/// </summary>
		[Fact]
		public async Task A_different_event_for_the_same_message_is_claimable()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var session = await InsertSession(fixture);

			Assert.True(await fixture.DbService.TryClaimDeliveryEvent(session, "Brevo:<abc@example.com>:blocked"));
			Assert.True(await fixture.DbService.TryClaimDeliveryEvent(session, "Brevo:<abc@example.com>:hardBounce"));
		}

		[Fact]
		public async Task A_session_that_is_gone_cannot_be_claimed_against()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			Assert.False(await fixture.DbService.TryClaimDeliveryEvent(Guid.NewGuid().ToString(), "Brevo:<abc@example.com>:hardBounce"));
		}

		private static async Task<string> InsertSession(EmailSwitchIntegrationFixture fixture)
		{
			var sessionId = Guid.NewGuid().ToString();
			var startTimeUTC = DateTime.UtcNow;

			await fixture.Database.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession)).InsertOneAsync(
				new EmailSwitchSession()
				{
					SessionId = sessionId,
					EmailId = $"user-{sessionId}@example.com",
					LiveClaimKey = $"user-{sessionId}@example.com",
					StartTimeUTC = startTimeUTC,
					ExpiryTimeUTC = startTimeUTC.AddSeconds(240)
				});

			return sessionId;
		}
	}

	/// <summary>
	/// The whole feature, end to end against a real MongoDB: Brevo accepts a send, later reports that
	/// it will never arrive, and the same code goes out through Resend instead.
	///
	/// This is the test that would have caught the incident that prompted the feature.
	/// </summary>
	public sealed class DeliveryFailoverEndToEndTests
	{
		private const string BrevoMessageId = "<abc@smtp-relay.mailin.fr>";

		[Fact]
		public async Task A_terminal_bounce_resends_the_same_code_through_the_next_provider()
		{
			await using var host = new FailoverHost();

			await host.SendInitialOtp();

			// Brevo took it, so nothing has reached Resend yet.
			Assert.Equal(1, host.BrevoHandler.RequestCount);
			Assert.Equal(0, host.ResendHandler.RequestCount);

			var outcome = await host.Handle(TerminalEvent());

			Assert.Equal(DeliveryFailoverOutcome.Resent, outcome);
			Assert.Equal(1, host.ResendHandler.RequestCount);

			// The point of reusing the stored body rather than rendering a new one: a second, different
			// code in the inbox would leave the recipient guessing which one the session accepts. The
			// two payloads are not comparable directly - Brevo nests its addresses and Resend does not -
			// so the code itself is what gets compared, read out of what each provider was actually
			// asked to send.
			var codeSentByBrevo = SixDigitCodeIn(host.BrevoHandler.LastBody);

			Assert.Equal(codeSentByBrevo, SixDigitCodeIn(host.ResendHandler.LastBody));
		}

		/// <summary>Webhook redeliveries are normal, and must not mail the recipient twice.</summary>
		[Fact]
		public async Task A_redelivered_event_does_not_send_again()
		{
			await using var host = new FailoverHost();

			await host.SendInitialOtp();

			Assert.Equal(DeliveryFailoverOutcome.Resent, await host.Handle(TerminalEvent()));
			Assert.Equal(DeliveryFailoverOutcome.AlreadyHandled, await host.Handle(TerminalEvent()));

			Assert.Equal(1, host.ResendHandler.RequestCount);
		}

		[Fact]
		public async Task A_non_terminal_event_sends_nothing()
		{
			await using var host = new FailoverHost();

			await host.SendInitialOtp();

			var deliveryEvent = BrevoDeliveryEventParser.Parse(
				$$"""{"event":"deferred","message-id":"{{BrevoMessageId}}"}""");

			Assert.NotNull(deliveryEvent);
			Assert.Equal(DeliveryFailoverOutcome.NotAFailure, await host.Handle(deliveryEvent));
			Assert.Equal(0, host.ResendHandler.RequestCount);
		}

		[Fact]
		public async Task An_event_for_an_unknown_message_sends_nothing()
		{
			await using var host = new FailoverHost();

			await host.SendInitialOtp();

			var deliveryEvent = BrevoDeliveryEventParser.Parse(
				"""{"event":"hardBounce","message-id":"<never-sent@example.com>"}""");

			Assert.NotNull(deliveryEvent);
			Assert.Equal(DeliveryFailoverOutcome.NoLiveSession, await host.Handle(deliveryEvent));
			Assert.Equal(0, host.ResendHandler.RequestCount);
		}

		/// <summary>
		/// A single-provider Priority spends its only slot on the send that just failed, and the
		/// rendered email is retired with it - so there is nothing left to resend and nowhere to send
		/// it. Delivery failover needs a second provider, exactly as rejection failover always did.
		/// </summary>
		[Fact]
		public async Task A_spent_budget_cannot_be_recovered()
		{
			await using var host = new FailoverHost(brevoOnly: true);

			await host.SendInitialOtp();

			Assert.Equal(DeliveryFailoverOutcome.NoBudgetLeft, await host.Handle(TerminalEvent()));
			Assert.Equal(0, host.ResendHandler.RequestCount);
		}

		/// <summary>
		/// The verification code as it appears in the payload a provider was handed. Asserted non-empty
		/// here rather than at the call site, so a payload that somehow carried no code fails as a
		/// missing code rather than as two empty strings comparing equal.
		/// </summary>
		private static string SixDigitCodeIn(string? providerPayload)
		{
			var match = Regex.Match(providerPayload ?? string.Empty, @"Verification Code: (\d{6})");

			Assert.True(match.Success, "The provider payload carried no verification code.");

			return match.Groups[1].Value;
		}

		private static DeliveryEvent TerminalEvent()
		{
			var deliveryEvent = BrevoDeliveryEventParser.Parse(
				$$"""{"event":"hardBounce","message-id":"{{BrevoMessageId}}","reason":"Sender not authorised"}""");

			Assert.NotNull(deliveryEvent);
			return deliveryEvent;
		}

		/// <summary>
		/// A real container over a real MongoDB with both HTTP providers stubbed, so the only things
		/// faked are the two sockets.
		/// </summary>
		private sealed class FailoverHost : IAsyncDisposable
		{
			private readonly ServiceProvider _provider;
			private readonly string _databaseName;
			private readonly string _connectionString;
			private readonly TestHost.LogCapture _log = new();

			internal CountingHandler BrevoHandler { get; } =
				new(System.Net.HttpStatusCode.Created, $$"""{"messageId":"{{BrevoMessageId}}"}""");

			internal CountingHandler ResendHandler { get; } =
				new(System.Net.HttpStatusCode.OK, """{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""");

			internal string CapturedOtp => _log.CapturedOtp;

			internal FailoverHost(bool brevoOnly = false)
			{
				_connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING") ?? "mongodb://localhost:27017";
				_databaseName = "EmailSwitchFailover_" + Guid.NewGuid();

				var settings = TestHost.BaseSettings(_connectionString).WithBrevo().WithResend();
				settings["MongoDbSettings:DatabaseName"] = _databaseName;
				settings = brevoOnly
					? settings.WithPriority("Brevo")
					: settings.WithPriority("Brevo", "Resend");

				_provider = TestHost.Build(
					settings,
					loggerProvider: _log,
					resendHandler: ResendHandler,
					brevoHandler: BrevoHandler);
			}

			internal async Task SendInitialOtp()
			{
				var response = await _provider.GetRequiredService<EmailSwitchService>()
					.SendOTP("user@example.com", [], [], [], SMSwitch.Common.DTOs.UserAgent.WebBrowser);

				Assert.True(response.IsSent);
			}

			internal async Task<DeliveryFailoverOutcome> Handle(DeliveryEvent deliveryEvent) =>
				await _provider.GetRequiredService<DeliveryFailoverService>().Handle(deliveryEvent);

			public async ValueTask DisposeAsync()
			{
				_provider.Dispose();

				// Deliberately unconditional: cleanup must still run when a test is cancelled or times
				// out, otherwise the database is left behind.
				await new MongoClient(_connectionString).DropDatabaseAsync(_databaseName);
			}
		}

		private sealed class CountingHandler(System.Net.HttpStatusCode statusCode, string body) : HttpMessageHandler
		{
			private int _requestCount;

			internal int RequestCount => _requestCount;
			internal string? LastBody { get; private set; }

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref _requestCount);
				LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

				return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
			}
		}
	}

	/// <summary>
	/// Correlating a delivery event back to the session that produced it, against a real server. The
	/// liveness rules have to mirror GetLatestSession exactly, or a bounce resurrects a session a
	/// reader would have refused.
	/// </summary>
	public sealed class DeliveryEventLookupIntegrationTests
	{
		[Fact]
		public async Task A_live_session_is_found_by_its_provider_message_id()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var sessionId = await InsertSession(fixture, messageId: "<abc@smtp-relay.mailin.fr>");

			var found = await fixture.DbService.GetLiveSessionByProviderMessageId("<abc@smtp-relay.mailin.fr>");

			Assert.NotNull(found);
			Assert.Equal(sessionId, found.SessionId);
		}

		[Fact]
		public async Task An_unknown_message_id_finds_nothing()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			await InsertSession(fixture, messageId: "<abc@smtp-relay.mailin.fr>");

			Assert.Null(await fixture.DbService.GetLiveSessionByProviderMessageId("<never-sent@example.com>"));
		}

		[Fact]
		public async Task An_expired_session_is_not_returned()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			await InsertSession(fixture, messageId: "<expired@example.com>", sessionTimeoutInSeconds: -60);

			Assert.Null(await fixture.DbService.GetLiveSessionByProviderMessageId("<expired@example.com>"));
		}

		[Fact]
		public async Task A_verified_session_is_not_returned()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			await InsertSession(fixture, messageId: "<verified@example.com>", verified: true);

			Assert.Null(await fixture.DbService.GetLiveSessionByProviderMessageId("<verified@example.com>"));
		}

		/// <summary>
		/// The case that stops a superseded code being resent. Once the user gives up and requests a new
		/// code the old session hands back its claim, and a late bounce for it must not put a code in
		/// their inbox that the live session will refuse.
		/// </summary>
		[Fact]
		public async Task A_session_that_has_handed_back_its_claim_is_not_returned()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			await InsertSession(fixture, messageId: "<superseded@example.com>", holdsClaim: false);

			Assert.Null(await fixture.DbService.GetLiveSessionByProviderMessageId("<superseded@example.com>"));
		}

		private static async Task<string> InsertSession(
			EmailSwitchIntegrationFixture fixture,
			string messageId,
			int sessionTimeoutInSeconds = 240,
			bool verified = false,
			bool holdsClaim = true)
		{
			var sessionId = Guid.NewGuid().ToString();
			var startTimeUTC = DateTime.UtcNow;
			var emailId = $"user-{sessionId}@example.com";

			await fixture.Database.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession)).InsertOneAsync(
				new EmailSwitchSession()
				{
					SessionId = sessionId,
					EmailId = emailId,
					LiveClaimKey = holdsClaim ? emailId : null,
					StartTimeUTC = startTimeUTC,
					ExpiryTimeUTC = startTimeUTC.AddSeconds(sessionTimeoutInSeconds),
					SuccessfullyVerifiedTimestampUTC = verified ? DateTime.UtcNow : null,
					SentAttempts =
					[
						new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.Brevo, true, messageId)
					]
				});

			return sessionId;
		}
	}
}
