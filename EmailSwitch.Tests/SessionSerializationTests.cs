using EmailSwitch.Common;
using EmailSwitch.Database.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// The session timestamps are UTC DateTime rather than DateTimeOffset so that MongoDB can range
	/// query and index them. These tests pin that, because the type is easy to "tidy up" back to
	/// DateTimeOffset without realising it silently pushes expiry filtering back into memory.
	/// </summary>
	public sealed class SessionSerializationTests
	{
		private static EmailSwitchSession CreateSession() => new()
		{
			SessionId = Guid.NewGuid().ToString(),
			EmailId = "user@example.com",
			StartTimeUTC = DateTime.UtcNow,
			ExpiryTimeUTC = DateTime.UtcNow.AddMinutes(4),
			SuccessfullyVerifiedTimestampUTC = DateTime.UtcNow,
			EmailProvidersQueue = new Queue<EmailProvider>([EmailProvider.SendGrid, EmailProvider.DevConsole]),
			SentAttempts = [new AttemptDetailsSendOTP(DateTime.UtcNow, EmailProvider.SendGrid, true)],
			FailedVerificationAttemptsUTC = [DateTime.UtcNow],
			LogoRenderedAttemptsUTC = [DateTime.UtcNow],
			SendOTPEmail = new EmailContent
			{
				Subject = "Email verification",
				PlainTextContent = "Verification Code: 123456",
				HtmlContent = "<h1>Verification Code: 123456</h1>"
			}
		};

		[Theory]
		[InlineData("StartTimeUTC")]
		[InlineData("ExpiryTimeUTC")]
		[InlineData("SuccessfullyVerifiedTimestampUTC")]
		public void Session_timestamps_serialize_as_queryable_bson_dates(string element)
		{
			var document = CreateSession().ToBsonDocument();

			Assert.Equal(BsonType.DateTime, document[element].BsonType);
		}

		[Fact]
		public void Timestamp_collections_serialize_as_arrays_of_bson_dates()
		{
			var document = CreateSession().ToBsonDocument();

			Assert.Equal(BsonType.DateTime, document["FailedVerificationAttemptsUTC"].AsBsonArray[0].BsonType);
			Assert.Equal(BsonType.DateTime, document["LogoRenderedAttemptsUTC"].AsBsonArray[0].BsonType);
			Assert.Equal(BsonType.DateTime, document["SentAttempts"].AsBsonArray[0]["AttemptTimeInUTC"].BsonType);
		}

		/// <summary>
		/// The contrast that motivated the change: the driver stores a DateTimeOffset as a
		/// subdocument, so <c>$gt</c> against it compares documents rather than instants.
		/// </summary>
		[Fact]
		public void A_DateTimeOffset_would_not_have_been_a_bson_date()
		{
			var document = new WithOffset { When = DateTimeOffset.UtcNow }.ToBsonDocument();

			Assert.NotEqual(BsonType.DateTime, document["When"].BsonType);
		}

		[Fact]
		public void Timestamps_round_trip_as_utc()
		{
			var session = CreateSession();

			var roundTripped = BsonSerializer.Deserialize<EmailSwitchSession>(session.ToBsonDocument());

			Assert.Equal(DateTimeKind.Utc, roundTripped.StartTimeUTC.Kind);
			Assert.Equal(DateTimeKind.Utc, roundTripped.ExpiryTimeUTC.Kind);
			Assert.Equal(DateTimeKind.Utc, roundTripped.SuccessfullyVerifiedTimestampUTC!.Value.Kind);
			// BSON dates carry millisecond precision, so compare at that resolution.
			Assert.Equal(session.ExpiryTimeUTC, roundTripped.ExpiryTimeUTC, TimeSpan.FromMilliseconds(1));
		}

		[Fact]
		public void A_session_round_trips_without_losing_its_payload()
		{
			var session = CreateSession();

			var roundTripped = BsonSerializer.Deserialize<EmailSwitchSession>(session.ToBsonDocument());

			Assert.Equal(session.SessionId, roundTripped.SessionId);
			Assert.Equal(session.EmailId, roundTripped.EmailId);
			Assert.Equal(session.SendOTPEmail.Subject, roundTripped.SendOTPEmail.Subject);
			Assert.Equal(session.SendOTPEmail.HtmlContent, roundTripped.SendOTPEmail.HtmlContent);
			Assert.Single(roundTripped.SentAttempts);
			Assert.Equal(EmailProvider.SendGrid, roundTripped.SentAttempts[0].EmailProvider);
		}

		/// <summary>
		/// The send budget only works if the queue survives a round trip, and SendOTP distinguishes a
		/// null queue (not started) from an empty one (spent) - so both states have to come back as
		/// they went in, not collapse into each other.
		/// </summary>
		[Fact]
		public void The_provider_queue_round_trips_including_the_difference_between_empty_and_null()
		{
			var session = CreateSession();

			var withSlots = BsonSerializer.Deserialize<EmailSwitchSession>(session.ToBsonDocument());
			Assert.NotNull(withSlots.EmailProvidersQueue);
			Assert.Equal(2, withSlots.EmailProvidersQueue!.Count);
			Assert.Equal(EmailProvider.SendGrid, withSlots.EmailProvidersQueue.Peek());

			session.EmailProvidersQueue = new Queue<EmailProvider>();
			var spent = BsonSerializer.Deserialize<EmailSwitchSession>(session.ToBsonDocument());
			Assert.NotNull(spent.EmailProvidersQueue);
			Assert.Empty(spent.EmailProvidersQueue!);

			session.EmailProvidersQueue = null;
			var notStarted = BsonSerializer.Deserialize<EmailSwitchSession>(session.ToBsonDocument());
			Assert.Null(notStarted.EmailProvidersQueue);
		}

		private sealed class WithOffset
		{
			public DateTimeOffset When { get; init; }
		}
	}
}
