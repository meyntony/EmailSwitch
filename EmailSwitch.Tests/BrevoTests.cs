using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using EmailSwitch.Services.Brevo;
using EmailSwitch.Services.DevConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace EmailSwitch.Tests
{
	public sealed class BrevoInitializerTests
	{
		private static readonly IHttpClientFactory HttpClientFactory =
			new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

		private static BrevoInitializer Create(string? from = "noreply@example.com", string? apiKey = "xkeysib-fake-api-key")
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:OtpLength"] = "6",
				["EmailSwitchSettings:Brevo:From"] = from,
				["EmailSwitchSettings:Brevo:ApiKey"] = apiKey
			};

			return Create(values);
		}

		private static BrevoInitializer Create(Dictionary<string, string?> values)
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			return new BrevoInitializer(
				new EmailSwitchGeneralInitializer(configuration, NullLogger<EmailSwitchGeneralInitializer>.Instance),
				HttpClientFactory,
				NullLogger<BrevoInitializer>.Instance);
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
			const string apiKey = "xkeysib-super-secret-value";

			var exception = Assert.Throws<ArgumentException>(() => Create(from: null, apiKey: apiKey));

			Assert.DoesNotContain(apiKey, exception.Message);
		}

		[Fact]
		public void A_complete_configuration_initializes()
		{
			var initializer = Create();

			Assert.Equal("noreply@example.com", initializer.BrevoSettings.BrevoPrivateSettings.From);
			Assert.Equal(6, initializer.BrevoSettings.OtpLength);
		}

		/// <summary>
		/// The key is called ApiKey here and on Resend, and Password on SendGrid. SendGrid's name is
		/// wrong and stays wrong because renaming a configuration key breaks every existing consumer
		/// on upgrade, so the inconsistency is deliberate and must not be papered over with an alias.
		/// </summary>
		[Fact]
		public void The_sendgrid_key_name_is_not_accepted_as_an_alias()
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:Brevo:From"] = "noreply@example.com",
				["EmailSwitchSettings:Brevo:Password"] = "xkeysib-fake-api-key"
			};

			Assert.Throws<ArgumentException>(() => Create(values));
		}
	}

	public sealed class BrevoTests
	{
		private static EmailContent Content() => new()
		{
			Subject = "Email verification",
			PlainTextContent = "Verification Code: 123456",
			HtmlContent = "<h1>Verification Code: 123456</h1>"
		};

		// ------------------------------------------------------------------ registration

		[Fact]
		public void Brevo_resolves_to_its_own_implementation()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithBrevo().WithPriority("Brevo"));

			Assert.IsType<BrevoService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Brevo));
		}

		[Fact]
		public void A_brevo_only_priority_builds_the_whole_graph()
		{
			var settings = TestHost.BaseSettings().WithBrevo().WithPriority("Brevo");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("SendGrid"));
			Assert.DoesNotContain(settings.Keys, key => key.Contains("Resend"));

			using var provider = TestHost.Build(settings);

			Assert.NotNull(provider.GetRequiredService<EmailSwitchService>());
			Assert.NotNull(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Brevo));
		}

		/// <summary>
		/// The credential-free local development property, now at a third provider. BrevoInitializer
		/// fails fast on missing credentials, so anything depending on it eagerly - including the
		/// named HttpClient registration - would make a DevConsole-only run impossible.
		/// </summary>
		[Fact]
		public void The_whole_graph_resolves_with_no_brevo_configuration_at_all()
		{
			var settings = TestHost.BaseSettings().WithPriority("DevConsole");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("Brevo"));

			using var provider = TestHost.Build(settings);

			Assert.NotNull(provider.GetRequiredService<EmailSwitchService>());
			Assert.NotNull(provider.GetRequiredService<EmailSwitchGeneralInitializer>());
			Assert.IsType<DevConsoleService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.DevConsole));
		}

		/// <summary>
		/// The other half: Brevo must still refuse to start without credentials. Resolving it is what
		/// triggers that, which is exactly why nothing may depend on it eagerly.
		/// </summary>
		[Fact]
		public void Resolving_brevo_without_credentials_still_fails_fast()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithPriority("DevConsole"));

			Assert.ThrowsAny<Exception>(() => provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.Brevo));
		}

		// ------------------------------------------------------------------ the send itself

		/// <summary>
		/// Brevo answers 201 rather than 200 on an immediate send, so this pins that the success check
		/// is a range check. An equality test against 200 would report every successful send as failed.
		/// </summary>
		[Fact]
		public async Task An_accepted_send_reports_success()
		{
			var handler = new StubHandler(HttpStatusCode.Created, """{"messageId":"<202608121200.1234567890@smtp-relay.mailin.fr>"}""");

			var response = await Send(handler);

			Assert.True(response.IsSent);
			Assert.Equal(6, response.OtpLength);
			// Correlates a later delivery webhook back to this session. Without it the message cannot
			// participate in delivery failover.
			Assert.Equal("<202608121200.1234567890@smtp-relay.mailin.fr>", response.ProviderMessageId);
		}

		/// <summary>
		/// A body that cannot be parsed must not turn an accepted send into a failed one - the email is
		/// already gone by then, and reporting failure would invite the caller to send a second.
		/// </summary>
		[Fact]
		public async Task An_unreadable_message_id_does_not_fail_the_send()
		{
			var response = await Send(new StubHandler(HttpStatusCode.Created, "not json"));

			Assert.True(response.IsSent);
			Assert.Null(response.ProviderMessageId);
		}

		[Fact]
		public async Task A_rejected_send_reports_no_message_id()
		{
			var response = await Send(new StubHandler(HttpStatusCode.Unauthorized, """{"code":"unauthorized"}"""));

			Assert.False(response.IsSent);
			Assert.Null(response.ProviderMessageId);
		}

		/// <summary>
		/// A rejection must come back as a failed send rather than an exception, and it must be
		/// attributable: 401 bad key, 400 a rejected parameter such as an unverified sender, and 429
		/// the rate limit or the free plan's daily allowance all arrive as the same opaque failure
		/// unless the body is logged.
		/// </summary>
		[Fact]
		public async Task A_rejected_send_reports_a_failure_and_logs_the_response_body()
		{
			const string errorBody = """{"code":"unauthorized","message":"Key not found"}""";

			var log = new TestHost.LogCapture();
			var handler = new StubHandler(HttpStatusCode.Unauthorized, errorBody);

			var response = await Send(handler, log);

			Assert.False(response.IsSent);
			// Still reported, so a caller sizing its input field off the response does not get zero
			// just because the provider declined.
			Assert.Equal(6, response.OtpLength);
			Assert.Contains(log.Messages, message => message.Contains("Brevo rejected") && message.Contains("Key not found"));
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
		/// Pins the wire contract, which differs from Resend's in both auth and payload shape: Brevo
		/// takes a custom api-key header rather than a bearer token, and nests its addresses as
		/// objects rather than accepting plain strings.
		/// </summary>
		[Fact]
		public async Task The_send_request_matches_the_brevo_contract()
		{
			var handler = new StubHandler(HttpStatusCode.Created, """{"messageId":"<abc@smtp-relay.mailin.fr>"}""");

			await Send(handler);

			Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.RequestUri);
			Assert.Equal("xkeysib-fake-api-key", handler.ApiKeyHeader);
			// Brevo ignores Authorization; sending one would leak the key to the wrong header.
			Assert.Null(handler.AuthorizationHeader);

			var payload = Payload(handler);

			Assert.Equal("noreply@example.com", payload.GetProperty("sender").GetProperty("email").GetString());
			Assert.Equal("noreply@example.com", payload.GetProperty("replyTo").GetProperty("email").GetString());
			Assert.Equal("Email verification", payload.GetProperty("subject").GetString());
			Assert.Equal(Content().HtmlContent, payload.GetProperty("htmlContent").GetString());
			Assert.Equal(Content().PlainTextContent, payload.GetProperty("textContent").GetString());
			Assert.Equal(["user@example.com"], Recipients(payload));
		}

		/// <summary>
		/// The verbatim address is what gets emailed, not the normalised session key - EmailIdentifier
		/// strips plus-addressing and collapses gmail dots for keying, and mailing the collapsed form
		/// would deliver somewhere the caller never asked for.
		///
		/// Read back through a JSON parse rather than off the raw body: System.Text.Json's default
		/// encoder escapes the plus, which is the same string once decoded but would make this
		/// assertion about the encoder instead of about the recipient.
		/// </summary>
		[Fact]
		public async Task The_recipient_is_the_address_as_supplied_not_the_session_key()
		{
			var handler = new StubHandler(HttpStatusCode.Created, """{"messageId":"<abc@smtp-relay.mailin.fr>"}""");

			await Send(handler, email: "J.o.h.n+promo@Gmail.com");

			Assert.Equal(["J.o.h.n+promo@Gmail.com"], Recipients(Payload(handler)));
		}

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
				TestHost.BaseSettings().WithBrevo().WithPriority("Brevo"),
				loggerProvider: log,
				brevoHandler: handler);

			return await provider.GetRequiredService<BrevoService>().SendOTP(email, Content());
		}

		private static JsonElement Payload(StubHandler handler)
		{
			using var document = JsonDocument.Parse(handler.RequestBody);
			return document.RootElement.Clone();
		}

		private static string[] Recipients(JsonElement payload) =>
			[.. payload.GetProperty("to").EnumerateArray().Select(recipient => recipient.GetProperty("email").GetString() ?? string.Empty)];

		/// <summary>
		/// Answers with one canned response and records what it was asked, so the send path is
		/// exercised with no network and no credential. The request is read eagerly because HttpClient
		/// disposes it as soon as SendAsync returns.
		/// </summary>
		private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
		{
			internal string? ApiKeyHeader { get; private set; }
			internal string? AuthorizationHeader { get; private set; }
			internal string? RequestUri { get; private set; }
			internal string RequestBody { get; private set; } = string.Empty;

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				ApiKeyHeader = request.Headers.TryGetValues("api-key", out var apiKeyValues) ? string.Join(",", apiKeyValues) : null;
				AuthorizationHeader = request.Headers.Authorization?.ToString();
				RequestUri = request.RequestUri?.ToString();
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
