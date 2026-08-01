using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDbTokenManager.Database.DTOs;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// MongoDbTokenManager 10.2.0 moved token expiry out of <c>TokenValue.ValidUntilUtc</c> and into
	/// <c>Tokens.ExpiresAt</c>. Neither class opts into <c>[BsonIgnoreExtraElements]</c>, so the
	/// driver's default of throwing on an unmapped element leaves every token document written by
	/// 10.0.0 unreadable. That is why VerifyOTP wraps its body in a catch - see
	/// <see cref="VerifyOtpFailureContainmentTests"/>.
	///
	/// If the upstream package ever starts ignoring extra elements, the first test here will fail
	/// and the containment workaround can be reconsidered.
	/// </summary>
	public sealed class StoredTokenCompatibilityTests
	{
		private const string SessionId = "0f1c9e6c-1d8a-4f3b-9c02-7a5e1b6d4c8f";

		/// <summary>Shape written by MongoDbTokenManager 10.0.0.</summary>
		private static BsonDocument PreviousFormatToken() => new()
		{
			{ "_id", SessionId },
			{ "Token", new BsonDocument
				{
					{ "OneTimeTokenHash", "d1f4a0c9" },
					{ "ValidUntilUtc", DateTime.UtcNow.AddMinutes(4) }
				}
			},
			{ "LogId", "EmailSwitch.Database.EmailSwitchDbService" }
		};

		/// <summary>Shape written by MongoDbTokenManager 10.2.0.</summary>
		private static BsonDocument CurrentFormatToken() => new()
		{
			{ "_id", SessionId },
			{ "Token", new BsonDocument { { "OneTimeTokenHash", "d1f4a0c9" } } },
			{ "LogId", "EmailSwitch.Database.EmailSwitchDbService" },
			{ "ExpiresAt", DateTime.UtcNow.AddMinutes(4) }
		};

		[Fact]
		public void A_token_written_by_the_previous_package_version_cannot_be_deserialized()
		{
			var exception = Record.Exception(() => BsonSerializer.Deserialize<Tokens>(PreviousFormatToken()));

			var formatException = Assert.IsType<FormatException>(exception);
			Assert.Contains("ValidUntilUtc", formatException.Message);
		}

		[Fact]
		public void A_token_written_by_the_current_package_version_deserializes()
		{
			var token = BsonSerializer.Deserialize<Tokens>(CurrentFormatToken());

			Assert.Equal(SessionId, token.Id);
			Assert.Equal("d1f4a0c9", token.Token.OneTimeTokenHash);
			Assert.NotEqual(default, token.ExpiresAt);
		}

		/// <summary>
		/// A document missing ExpiresAt entirely deserializes to the DateTime default, which reads as
		/// long expired rather than never expiring - so a stale document can never be treated as a
		/// live token. This is the safe direction, and worth pinning.
		/// </summary>
		[Fact]
		public void A_token_without_an_expiry_defaults_to_expired()
		{
			var withoutExpiry = new BsonDocument
			{
				{ "_id", SessionId },
				{ "Token", new BsonDocument { { "OneTimeTokenHash", "d1f4a0c9" } } },
				{ "LogId", "log" }
			};

			var token = BsonSerializer.Deserialize<Tokens>(withoutExpiry);

			Assert.Equal(default, token.ExpiresAt);
			Assert.True(DateTime.UtcNow > token.ExpiresAt);
		}
	}
}
