using EmailSwitch.Common;
using EmailSwitch.EmailTemplates.DTOs;
using MongoDB.Bson.Serialization.Attributes;

namespace EmailSwitch.Database.DTOs
{
	/// <summary>
	/// Timestamps are stored as UTC <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/>
	/// deliberately. The driver serialises a DateTimeOffset as a subdocument, which cannot be range
	/// queried or indexed as an instant - that forced expiry filtering to happen client side over
	/// every session ever recorded for an address. A plain BSON date lets the server do it.
	/// </summary>
	public sealed class EmailSwitchSession
	{
		[BsonId]
		public required string SessionId { get; init; }
		public required string EmailId { get; init; }

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public required DateTime StartTimeUTC { get; init; }

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public DateTime? SuccessfullyVerifiedTimestampUTC { get; set; }

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public required DateTime ExpiryTimeUTC { get; init; }

		public Queue<EmailProvider>? EmailProvidersQueue { get; set; }
		public List<AttemptDetailsSendOTP> SentAttempts { get; set; } = [];

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public List<DateTime> LogoRenderedAttemptsUTC { get; set; } = [];

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public List<DateTime> FailedVerificationAttemptsUTC { get; set; } = [];

		public required EmailContent SendOTPEmail { get; set; }

		/// <summary>
		/// The expiry and already-verified conditions are also applied server side by
		/// EmailSwitchDbService.GetLatestSession. They are repeated here so the rule holds for any
		/// session, however it was loaded.
		/// </summary>
		internal bool HasNotExpired(byte maximumFailedAttemptsToVerify) =>
			(EmailProvidersQueue?.Any() ?? true) && // if it has become empty from failed attempts then it has expired
			FailedVerificationAttemptsUTC.Count < maximumFailedAttemptsToVerify &&
			SuccessfullyVerifiedTimestampUTC == null &&
			DateTime.UtcNow < ExpiryTimeUTC;
	}
}
