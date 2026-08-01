using EmailSwitch.Common;
using EmailSwitch.Database;
using EmailSwitch.Database.DTOs;
using MongoDB.Bson;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Asserts the index, not the deletion. MongoDB's TTL reaper runs roughly once a minute, so
	/// waiting for documents to disappear would be slow and flaky; what actually needs pinning is
	/// that the right index exists with the right expiry, and that the lookup index is left alone.
	/// </summary>
	public sealed class SessionRetentionTests
	{
		private const string ExpiryField = nameof(EmailSwitchSession.ExpiryTimeUTC);
		private const string EmailField = nameof(EmailSwitchSession.EmailId);

		/// <summary>Indexes are created on first use, so a read has to happen first.</summary>
		private static async Task<List<BsonDocument>> IndexesAfterFirstUse(EmailSwitchIntegrationFixture fixture, EmailSwitchDbService? dbService = null)
		{
			await (dbService ?? fixture.DbService).GetLatestSession("user@example.com");

			using var cursor = await fixture.Database
				.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession))
				.Indexes.ListAsync();

			return await cursor.ToListAsync();
		}

		private static BsonDocument? SingleFieldExpiryIndex(List<BsonDocument> indexes) =>
			indexes.FirstOrDefault(index =>
				index["key"].AsBsonDocument.ElementCount == 1
				&& index["key"].AsBsonDocument.Contains(ExpiryField));

		private static BsonDocument? CompoundLookupIndex(List<BsonDocument> indexes) =>
			indexes.FirstOrDefault(index =>
				index["key"].AsBsonDocument.ElementCount == 2
				&& index["key"].AsBsonDocument.Contains(EmailField)
				&& index["key"].AsBsonDocument.Contains(ExpiryField));

		[Fact]
		public async Task The_default_retention_is_ninety_days()
		{
			await using var fixture = new EmailSwitchIntegrationFixture();

			var ttlIndex = SingleFieldExpiryIndex(await IndexesAfterFirstUse(fixture));

			Assert.NotNull(ttlIndex);
			Assert.Equal(TimeSpan.FromDays(90).TotalSeconds, ttlIndex!["expireAfterSeconds"].ToDouble());
		}

		[Theory]
		[InlineData(1)]
		[InlineData(30)]
		[InlineData(365)]
		public async Task A_configured_retention_is_applied(int sessionRetentionDays)
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: sessionRetentionDays);

			var ttlIndex = SingleFieldExpiryIndex(await IndexesAfterFirstUse(fixture));

			Assert.NotNull(ttlIndex);
			Assert.Equal(TimeSpan.FromDays(sessionRetentionDays).TotalSeconds, ttlIndex!["expireAfterSeconds"].ToDouble());
		}

		/// <summary>
		/// The guard that matters most. The compound index also contains ExpiryTimeUTC, so a matcher
		/// that forgot to require a single key would put an expiry on the index every read depends on
		/// and start reaping sessions on the wrong schedule.
		/// </summary>
		[Fact]
		public async Task The_lookup_index_survives_and_never_gains_an_expiry()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: 30);

			var lookupIndex = CompoundLookupIndex(await IndexesAfterFirstUse(fixture));

			Assert.NotNull(lookupIndex);
			Assert.False(lookupIndex!.Contains("expireAfterSeconds"));
		}

		/// <summary>
		/// The upgrade path. MongoDB refuses to recreate an index with different options, so changing
		/// the setting has to be amended in place with collMod rather than thrown.
		/// </summary>
		[Fact]
		public async Task Changing_the_retention_amends_the_existing_index_in_place()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: 90);
			await IndexesAfterFirstUse(fixture);

			var indexes = await IndexesAfterFirstUse(fixture, fixture.WithRetention(30));
			var ttlIndex = SingleFieldExpiryIndex(indexes);

			Assert.NotNull(ttlIndex);
			Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, ttlIndex!["expireAfterSeconds"].ToDouble());
			// Amended, not duplicated.
			Assert.Single(indexes, index => index["key"].AsBsonDocument.ElementCount == 1 && index["key"].AsBsonDocument.Contains(ExpiryField));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task A_non_positive_retention_keeps_sessions_indefinitely(int sessionRetentionDays)
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: sessionRetentionDays);

			var indexes = await IndexesAfterFirstUse(fixture);

			Assert.Null(SingleFieldExpiryIndex(indexes));
			// Disabling retention must not cost the lookup index.
			Assert.NotNull(CompoundLookupIndex(indexes));
		}

		/// <summary>
		/// The case the test above cannot reach, because it starts on a brand new database with no
		/// index to leave behind. Turning retention off used to return early, so on a collection that
		/// already had the TTL index the sessions went on being reaped on the old schedule while the
		/// configuration said to keep them forever.
		/// </summary>
		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		public async Task Turning_retention_off_drops_the_existing_ttl_index(int sessionRetentionDays)
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: 90);
			Assert.NotNull(SingleFieldExpiryIndex(await IndexesAfterFirstUse(fixture)));

			var indexes = await IndexesAfterFirstUse(fixture, fixture.WithRetention(sessionRetentionDays));

			Assert.Null(SingleFieldExpiryIndex(indexes));
			// And the index every read depends on is untouched - it also contains ExpiryTimeUTC, so a
			// matcher that forgot to require a single key would have dropped it instead.
			var lookupIndex = CompoundLookupIndex(indexes);
			Assert.NotNull(lookupIndex);
			Assert.False(lookupIndex!.Contains("expireAfterSeconds"));
		}

		/// <summary>Turning retention back on after disabling it must recreate the index.</summary>
		[Fact]
		public async Task Retention_can_be_turned_off_and_on_again()
		{
			await using var fixture = new EmailSwitchIntegrationFixture(sessionRetentionDays: 90);
			await IndexesAfterFirstUse(fixture);

			Assert.Null(SingleFieldExpiryIndex(await IndexesAfterFirstUse(fixture, fixture.WithRetention(0))));

			var ttlIndex = SingleFieldExpiryIndex(await IndexesAfterFirstUse(fixture, fixture.WithRetention(30)));

			Assert.NotNull(ttlIndex);
			Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, ttlIndex!["expireAfterSeconds"].ToDouble());
		}
	}

	public sealed class SessionRetentionConfigurationTests
	{
		private static EmailControls Controls(string? sessionRetentionDays)
		{
			var settings = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:Controls:Priority:0"] = "SendGrid",
				["EmailSwitchSettings:Controls:SessionRetentionDays"] = sessionRetentionDays
			};

			var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

			return new EmailSwitchInitializer(configuration, NullLogger<EmailSwitchInitializer>.Instance).EmailControls;
		}

		[Fact]
		public void The_default_is_ninety_days()
		{
			Assert.Equal(90, Controls(null).SessionRetentionDays);
		}

		[Theory]
		[InlineData("30", 30)]
		[InlineData("1", 1)]
		[InlineData("3650", 3650)]
		public void A_configured_value_is_read(string configured, int expected)
		{
			Assert.Equal(expected, Controls(configured).SessionRetentionDays);
		}

		/// <summary>
		/// Unlike SessionTimeoutInSeconds this must not fail startup: keeping the audit trail
		/// indefinitely and pruning it some other way is a legitimate operator choice.
		/// </summary>
		[Theory]
		[InlineData("0", 0)]
		[InlineData("-1", -1)]
		public void A_non_positive_value_is_honoured_rather_than_rejected(string configured, int expected)
		{
			Assert.Equal(expected, Controls(configured).SessionRetentionDays);
		}

		[Theory]
		[InlineData("not-a-number")]
		[InlineData("90 days")]
		public void An_unparseable_value_falls_back_to_the_default(string configured)
		{
			Assert.Equal(90, Controls(configured).SessionRetentionDays);
		}
	}
}
