using EmailSwitch.Webhooks.Brevo;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDbService;
using MongoDbTokenManager;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Brevo does not sign its webhooks - Resend uses Svix HMAC and SendGrid uses ECDSA, but Brevo
	/// offers only IP allowlisting. The shared secret in the path is therefore the only thing between
	/// an endpoint that can send email and anyone who finds the URL, which is too much to leave
	/// verified by reading the code alone.
	/// </summary>
	public sealed class BrevoWebhookTokenTests
	{
		private const string ConfiguredToken = "a-long-random-string-from-a-csprng";

		[Fact]
		public void The_configured_token_matches_itself()
		{
			Assert.True(BrevoWebhookEndpoint.TokenMatches(ConfiguredToken, ConfiguredToken));
		}

		[Theory]
		[InlineData("wrong-token-entirely")]
		// One character different, same length - the case a naive comparison still gets right but a
		// timing-unsafe one leaks.
		[InlineData("a-long-random-string-from-a-csprnG")]
		// A prefix, which is what an attacker walking the token one character at a time would send.
		[InlineData("a-long-random-string-from-a-csprn")]
		[InlineData("a")]
		[InlineData("")]
		public void Anything_else_is_rejected(string suppliedToken)
		{
			Assert.False(BrevoWebhookEndpoint.TokenMatches(ConfiguredToken, suppliedToken));
		}

		[Fact]
		public void The_comparison_is_case_sensitive()
		{
			Assert.False(BrevoWebhookEndpoint.TokenMatches(ConfiguredToken, ConfiguredToken.ToUpperInvariant()));
		}

		/// <summary>
		/// The failure that matters most. Mapping is supposed to fail at startup before this is
		/// reachable, but if that guard were ever lost, a blank configured token must reject everything
		/// rather than accept everything - including a blank supplied one.
		/// </summary>
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void A_blank_configured_token_never_matches(string? configuredToken)
		{
			Assert.False(BrevoWebhookEndpoint.TokenMatches(configuredToken, "anything"));
			Assert.False(BrevoWebhookEndpoint.TokenMatches(configuredToken, string.Empty));
			Assert.False(BrevoWebhookEndpoint.TokenMatches(configuredToken, configuredToken ?? string.Empty));
		}

		[Fact]
		public void A_null_supplied_token_is_rejected()
		{
			Assert.False(BrevoWebhookEndpoint.TokenMatches(ConfiguredToken, null));
		}
	}

	/// <summary>
	/// The startup guard. An unauthenticated endpoint that can send email is worse than a host that
	/// refuses to start, so mapping without a token has to throw rather than warn.
	/// </summary>
	public sealed class BrevoWebhookMappingTests
	{
		/// <summary>
		/// InvalidOperationException specifically, not just any exception: BrevoInitializer throws
		/// ArgumentException for missing credentials, and this must fail for the missing token rather
		/// than incidentally for something else.
		/// </summary>
		[Fact]
		public async Task Mapping_without_a_webhook_token_fails_startup()
		{
			await using var app = BuildApp(TestHost.BaseSettings().WithBrevo().WithPriority("Brevo"));

			var exception = Assert.Throws<InvalidOperationException>(() => app.AddEmailSwitchWebhookEndpoints());

			Assert.Contains("WebhookToken", exception.Message);
		}

		[Fact]
		public async Task Mapping_with_a_webhook_token_succeeds()
		{
			var settings = TestHost.BaseSettings().WithBrevo().WithPriority("Brevo");
			settings["EmailSwitchSettings:Brevo:WebhookToken"] = "a-long-random-string-from-a-csprng";

			await using var app = BuildApp(settings);

			// The assertion is that this does not throw; mapping a route needs no server to be running.
			app.AddEmailSwitchWebhookEndpoints();
		}

		/// <summary>
		/// The webhook endpoints are opt-in, so the ordinary mapping call must not require Brevo
		/// credentials - a DevConsole-only host still has to start.
		/// </summary>
		[Fact]
		public async Task The_ordinary_endpoints_map_with_no_brevo_configuration_at_all()
		{
			var settings = TestHost.BaseSettings().WithPriority("DevConsole");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("Brevo"));

			await using var app = BuildApp(settings);

			app.AddEmailSwitchApiEndpoints();
		}

		private static WebApplication BuildApp(Dictionary<string, string?> settings)
		{
			var builder = WebApplication.CreateSlimBuilder();

			builder.Configuration.AddInMemoryCollection(settings);
			builder.Services.AddMongoDbServices();
			builder.Services.AddMongoDbTokenServices();
			builder.Services.AddEmailSwitchServices();

			return builder.Build();
		}
	}
}
