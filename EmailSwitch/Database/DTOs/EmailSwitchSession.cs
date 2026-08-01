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

		/// <summary>
		/// Holds <see cref="EmailId"/> while this session is the live one for that address, and is
		/// unset once it no longer is. A sparse unique index on it makes creation atomic: two requests
		/// racing to open a session for one address cannot both insert, so a user cannot be sent two
		/// different codes and then find that one of them does not work.
		///
		/// It has to be released explicitly rather than inferred, because a unique index cannot
		/// express "live" - liveness depends on <see cref="ExpiryTimeUTC"/> against the current time,
		/// and a partialFilterExpression cannot reference now. Indexing unverified sessions instead
		/// was measured to reject the successor a user is entitled to once the predecessor times out.
		///
		/// <c>[BsonIgnoreIfNull]</c> is load-bearing, not tidiness. A sparse index skips documents
		/// where the field is <em>absent</em>, not where it is BSON null - so writing an explicit null
		/// would put every such session into the unique index under the same key and let only one of
		/// them exist at a time, across all addresses. Releasing the claim is a <c>$unset</c> for the
		/// same reason.
		/// </summary>
		[BsonIgnoreIfNull]
		public string? LiveClaimKey { get; set; }

		public Queue<EmailProvider>? EmailProvidersQueue { get; set; }
		public List<AttemptDetailsSendOTP> SentAttempts { get; set; } = [];

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public List<DateTime> LogoRenderedAttemptsUTC { get; set; } = [];

		[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
		public List<DateTime> FailedVerificationAttemptsUTC { get; set; } = [];

		/// <summary>
		/// Verification attempts claimed against this session, incremented <em>before</em> the guess is
		/// checked so the cap holds under concurrency.
		///
		/// Deliberately separate from <see cref="FailedVerificationAttemptsUTC"/>. That list is the
		/// audit record of guesses that were actually wrong; this is a rate-limit reservation, and a
		/// correct guess claims one too. Conflating them meant either recording a successful
		/// verification as a failure or giving the reservation back, and the counter has to survive a
		/// correct guess for the audit trail to stay honest.
		///
		/// Absent on sessions written before this field existed, where it deserialises to zero - which
		/// is why <see cref="HasNotExpired"/> and the reservation filter both also consider the length
		/// of the audit list.
		/// </summary>
		public int VerificationAttemptsCount { get; set; }

		/// <summary>
		/// The rendered email, which contains the verification code in cleartext.
		///
		/// Nullable because it is retired - <c>$unset</c> - as soon as it can no longer be needed: when
		/// the session is verified, and when the send budget is spent. MongoDbTokenManager deliberately
		/// stores only a hash of the code, and keeping the rendered body alongside it defeated that;
		/// anyone able to read this collection from a backup, a replica or a restored snapshot could
		/// read live codes directly. Retention made it worse, since a code valid for four minutes
		/// otherwise sat here for <c>SessionRetentionDays</c> - 90 by default - along with the
		/// recipient's verified mobile numbers and emails, which the same body embeds.
		///
		/// Retiring it costs nothing: verification goes through the token, not the body, and the audit
		/// value of the session is in its timestamps and <see cref="SentAttempts"/>.
		///
		/// <c>required</c> was dropped with the nullability: it is a C# construct the BSON deserializer
		/// does not enforce, so once the element is unset a reloaded session hands back null through
		/// whatever type this is declared as. Better that the type admits it.
		/// </summary>
		public EmailContent? SendOTPEmail { get; set; }

		/// <summary>
		/// Whether this session can still have a code verified against it.
		///
		/// Deliberately says nothing about <see cref="EmailProvidersQueue"/>. That queue is the send
		/// budget, and it drains as emails go out - including on success. Treating an empty one as
		/// expiry meant a delivered, in-date code stopped verifying the moment the budget ran out,
		/// so a single resend could lock the holder out of a code already sitting in their inbox.
		/// Running out of sends is handled where sends happen, in EmailSwitchService.SendOTP.
		///
		/// The expiry and already-verified conditions are also applied server side by
		/// EmailSwitchDbService.GetLatestSession. They are repeated here so the rule holds for any
		/// session, however it was loaded.
		///
		/// This is a read, so it can only ever be advisory: it is what the session looked like when it
		/// was loaded. The cap is actually enforced by
		/// EmailSwitchDbService.TryReserveVerificationAttempt, which claims a slot server side before
		/// the guess is checked. Deciding here and incrementing afterwards is a check-then-act race -
		/// concurrent guesses all passed this test before any of them had been counted.
		/// </summary>
		internal bool HasNotExpired(byte maximumFailedAttemptsToVerify) =>
			AttemptsClaimed < maximumFailedAttemptsToVerify &&
			SuccessfullyVerifiedTimestampUTC == null &&
			DateTime.UtcNow < ExpiryTimeUTC;

		/// <summary>
		/// The larger of the reservation counter and the audit list. A session written before
		/// <see cref="VerificationAttemptsCount"/> existed carries its attempts only in the list, so
		/// taking the maximum keeps the cap intact across the upgrade rather than handing every
		/// in-flight session a fresh set of guesses.
		/// </summary>
		internal int AttemptsClaimed => Math.Max(VerificationAttemptsCount, FailedVerificationAttemptsUTC.Count);
	}
}
