using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using EmailSwitch.Services.DevConsole;
using EmailSwitch.Services.Resend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// The enum's numbers are stored data, not an implementation detail: they are what sits inside
	/// every <c>EmailSwitchSession.EmailProvidersQueue</c> already in a consumer's database. Pinned
	/// here so an insert or a renumber fails a test rather than quietly reinterpreting live sessions.
	/// </summary>
	public sealed class EmailProviderTests
	{
		[Fact]
		public void The_persisted_provider_numbers_are_unchanged()
		{
			Assert.Equal(0, (int)EmailProvider.SendGrid);
			Assert.Equal(1, (int)EmailProvider.DevConsole);
		}

		[Fact]
		public void Resend_is_appended_at_the_next_free_value()
		{
			Assert.Equal(2, (int)EmailProvider.Resend);

			// Catches a member added anywhere but the end: a new one inserted above Resend would shift
			// it without changing either assertion above.
			Assert.Equal(3, Enum.GetValues<EmailProvider>().Length);
		}
	}

	public sealed class ResendInitializerTests
	{
		private static readonly IHttpClientFactory HttpClientFactory =
			new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

		private static ResendInitializer Create(string? from = "noreply@example.com", string? apiKey = "re_fake-api-key")
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:OtpLength"] = "6",
				["EmailSwitchSettings:Resend:From"] = from,
				["EmailSwitchSettings:Resend:ApiKey"] = apiKey
			};

			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			return new ResendInitializer(
				new EmailSwitchGeneralInitializer(configuration, NullLogger<EmailSwitchGeneralInitializer>.Instance),
				HttpClientFactory,
				NullLogger<ResendInitializer>.Instance);
		}

		/// <summary>
		/// Missing credentials must fail startup rather than being logged and swallowed - a swallowed
		/// failure leaves the settings null and turns every later send into a caught
		/// NullReferenceException, so email silently never goes out.
		/// </summary>
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("  ")]
		public void A_missing_sender_address_fails_startup(string? from)
		{
			var exception = Assert.Throws<ArgumentException>(() => Create(from: from));

			Assert.Contains("From", exception.Message);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("  ")]
		public void A_missing_api_key_fails_startup(string? apiKey)
		{
			var exception = Assert.Throws<ArgumentException>(() => Create(apiKey: apiKey));

			Assert.Contains("ApiKey", exception.Message);
		}

		/// <summary>The failure message must name the setting without quoting its value.</summary>
		[Fact]
		public void The_failure_message_does_not_leak_the_api_key()
		{
			const string apiKey = "re_super-secret-value";

			var exception = Assert.Throws<ArgumentException>(() => Create(from: null, apiKey: apiKey));

			Assert.DoesNotContain(apiKey, exception.Message);
		}

		[Fact]
		public void A_complete_configuration_initializes()
		{
			var initializer = Create();

			Assert.Equal("noreply@example.com", initializer.ResendSettings.ResendPrivateSettings.From);
			Assert.Equal(6, initializer.ResendSettings.OtpLength);
		}

		/// <summary>
		/// The key is called ApiKey here and Password on SendGrid. SendGrid's name is wrong and stays
		/// wrong because renaming a configuration key breaks every existing consumer on upgrade, so
		/// the inconsistency is deliberate - and a "tidy-up" that unified them would break Resend
		/// hosts instead.
		/// </summary>
		[Fact]
		public void The_sendgrid_key_name_is_not_accepted_as_an_alias()
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:Resend:From"] = "noreply@example.com",
				["EmailSwitchSettings:Resend:Password"] = "re_fake-api-key"
			};

			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			Assert.Throws<ArgumentException>(() => new ResendInitializer(
				new EmailSwitchGeneralInitializer(configuration, NullLogger<EmailSwitchGeneralInitializer>.Instance),
				HttpClientFactory,
				NullLogger<ResendInitializer>.Instance));
		}
	}

	public sealed class ResendTests
	{
		private static EmailContent Content() => new()
		{
			Subject = "Email verification",
			PlainTextContent = "Verification Code: 123456",
			HtmlContent = "<h1>Verification Code: 123456</h1>"
		};

		// ------------------------------------------------------------------ registration

		[Fact]
		public void Resend_resolves_to_its_own_implementation()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithResend().WithPriority("Resend"));

			Assert.IsType<ResendService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Resend));
		}

		[Fact]
		public void A_resend_only_priority_builds_the_whole_graph()
		{
			var settings = TestHost.BaseSettings().WithResend().WithPriority("Resend");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("SendGrid"));

			using var provider = TestHost.Build(settings);

			Assert.NotNull(provider.GetRequiredService<EmailSwitchService>());
			Assert.NotNull(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Resend));
		}

		/// <summary>
		/// The credential-free local development property, now that there are two real providers.
		/// ResendInitializer fails fast on missing credentials, so anything depending on it eagerly -
		/// including the named HttpClient registration - would make a DevConsole-only run impossible.
		/// </summary>
		[Fact]
		public void The_whole_graph_resolves_with_no_resend_configuration_at_all()
		{
			var settings = TestHost.BaseSettings().WithPriority("DevConsole");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("Resend"));

			using var provider = TestHost.Build(settings);

			Assert.NotNull(provider.GetRequiredService<EmailSwitchService>());
			Assert.NotNull(provider.GetRequiredService<EmailSwitchGeneralInitializer>());
			Assert.IsType<DevConsoleService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.DevConsole));
		}

		/// <summary>
		/// The other half: Resend must still refuse to start without credentials. Resolving it is what
		/// triggers that, which is exactly why nothing may depend on it eagerly.
		/// </summary>
		[Fact]
		public void Resolving_resend_without_credentials_still_fails_fast()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithPriority("DevConsole"));

			Assert.ThrowsAny<Exception>(() => provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Resend));
		}

		// ------------------------------------------------------------------ the send itself

		[Fact]
		public async Task An_accepted_send_reports_success()
		{
			var handler = new StubHandler(HttpStatusCode.OK, """{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""");

			var response = await Send(handler);

			Assert.True(response.IsSent);
			Assert.Equal(6, response.OtpLength);
		}

		/// <summary>
		/// A rejection must come back as a failed send rather than an exception, and it must be
		/// attributable: 401 revoked key, 403 unverified sending domain, 422 malformed address, 429
		/// rate limit or exhausted quota all arrive as the same status-free failure otherwise.
		/// </summary>
		[Fact]
		public async Task A_rejected_send_reports_a_failure_and_logs_the_response_body()
		{
			const string errorBody = """{"statusCode":401,"name":"validation_error","message":"API key is invalid"}""";

			var log = new TestHost.LogCapture();
			var handler = new StubHandler(HttpStatusCode.Unauthorized, errorBody);

			var response = await Send(handler, log);

			Assert.False(response.IsSent);
			// Still reported, so a caller sizing its input field off the response does not get zero
			// just because the provider declined.
			Assert.Equal(6, response.OtpLength);
			Assert.Contains(log.Messages, message => message.Contains("Resend rejected") && message.Contains("API key is invalid"));
		}

		/// <summary>
		/// A transport failure - DNS, TLS, the ten second timeout - is a failed send, not an exception
		/// escaping into EmailSwitchService's own catch, where the provider it came from is lost.
		/// </summary>
		[Fact]
		public async Task A_transport_failure_reports_a_failed_send()
		{
			var response = await Send(new ThrowingHandler());

			Assert.False(response.IsSent);
			Assert.Equal(6, response.OtpLength);
		}

		/// <summary>
		/// Pins the wire contract. The Idempotency-Key assertion is the load-bearing one: Resend
		/// deduplicates on that header for 24 hours and returns the original id without sending, so a
		/// resend - which is meant to deliver the same code again - would report success while nothing
		/// left Resend.
		/// </summary>
		[Fact]
		public async Task The_send_request_matches_the_resend_contract()
		{
			var handler = new StubHandler(HttpStatusCode.OK, """{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""");

			await Send(handler);

			Assert.Equal("https://api.resend.com/emails", handler.RequestUri);
			Assert.Equal("Bearer re_fake-api-key", handler.AuthorizationHeader);
			Assert.False(handler.HadIdempotencyKey);

			var payload = Payload(handler);

			Assert.Equal("noreply@example.com", payload.GetProperty("from").GetString());
			Assert.Equal("noreply@example.com", payload.GetProperty("reply_to").GetString());
			Assert.Equal("Email verification", payload.GetProperty("subject").GetString());
			Assert.Equal(Content().HtmlContent, payload.GetProperty("html").GetString());
			Assert.Equal(Content().PlainTextContent, payload.GetProperty("text").GetString());
			Assert.Equal(["user@example.com"], Recipients(payload));
		}

		/// <summary>
		/// The verbatim address is what gets emailed, not the normalised session key - EmailIdentifier
		/// strips plus-addressing and collapses gmail dots for keying, and mailing the collapsed form
		/// would deliver somewhere the caller never asked for.
		///
		/// Read back through a JSON parse rather than off the raw body: System.Text.Json's default
		/// encoder writes the plus as +, which is the same string once decoded but would make
		/// this assertion about the encoder instead of about the recipient.
		/// </summary>
		[Fact]
		public async Task The_recipient_is_the_address_as_supplied_not_the_session_key()
		{
			var handler = new StubHandler(HttpStatusCode.OK, """{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""");

			await Send(handler, email: "J.o.h.n+promo@Gmail.com");

			Assert.Equal(["J.o.h.n+promo@Gmail.com"], Recipients(Payload(handler)));
		}

		private static JsonElement Payload(StubHandler handler)
		{
			using var document = JsonDocument.Parse(handler.RequestBody);
			return document.RootElement.Clone();
		}

		private static string[] Recipients(JsonElement payload) =>
			[.. payload.GetProperty("to").EnumerateArray().Select(recipient => recipient.GetString() ?? string.Empty)];

		/// <summary>
		/// Kept out of the test methods themselves: the awaited call takes a CancellationToken, which
		/// xUnit1051 makes an error under -warnaserror when it appears directly in a test.
		/// </summary>
		private static async Task<EmailSwitchResponseSendOTP> Send(
			HttpMessageHandler handler,
			TestHost.LogCapture? log = null,
			string email = "user@example.com")
		{
			using var provider = TestHost.Build(
				TestHost.BaseSettings().WithResend().WithPriority("Resend"),
				loggerProvider: log,
				resendHandler: handler);

			return await provider.GetRequiredService<ResendService>().SendOTP(email, Content());
		}

		/// <summary>
		/// Answers with one canned response and records what it was asked, so the send path is
		/// exercised with no network and no credential. The request is read eagerly because HttpClient
		/// disposes it as soon as SendAsync returns.
		/// </summary>
		private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
		{
			internal string? AuthorizationHeader { get; private set; }
			internal string? RequestUri { get; private set; }
			internal string RequestBody { get; private set; } = string.Empty;
			internal bool HadIdempotencyKey { get; private set; }

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				AuthorizationHeader = request.Headers.Authorization?.ToString();
				RequestUri = request.RequestUri?.ToString();
				HadIdempotencyKey = request.Headers.Contains("Idempotency-Key");
				RequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

				return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
			}
		}

		private sealed class ThrowingHandler : HttpMessageHandler
		{
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
				throw new HttpRequestException("The remote name could not be resolved.");
		}
	}
}
