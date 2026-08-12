using EmailSwitch.Common;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// The provider queue is the send budget for a session: built once when the session starts, one
	/// slot spent per send attempt. Getting this wrong is what made a resend either impossible or
	/// unlimited, so the budget arithmetic is pinned here.
	/// </summary>
	public sealed class ProviderQueueTests
	{
		private static EmailControls Controls(byte maxRoundRobinAttempts) =>
			Controls(maxRoundRobinAttempts, [EmailProvider.SendGrid]);

		private static EmailControls Controls(byte maxRoundRobinAttempts, List<EmailProvider> priority) => new()
		{
			MaximumFailedAttemptsToVerify = 3,
			SessionTimeoutInSeconds = 240,
			MaxRoundRobinAttempts = maxRoundRobinAttempts,
			Priority = priority
		};

		[Theory]
		[InlineData(1, 1)]
		[InlineData(2, 2)]
		[InlineData(5, 5)]
		public void The_budget_is_one_slot_per_provider_per_round_robin_attempt(byte maxRoundRobinAttempts, int expectedSlots)
		{
			var queue = EmailSwitchService.BuildProviderQueue(Controls(maxRoundRobinAttempts));

			Assert.Equal(expectedSlots, queue.Count);
			Assert.All(queue, provider => Assert.Equal(EmailProvider.SendGrid, provider));
		}

		/// <summary>
		/// Zero would mean a session that can never send, and SendOTP reports that as IsSent = false
		/// rather than looping. Documented here so the behaviour is deliberate rather than incidental.
		/// </summary>
		[Fact]
		public void A_zero_round_robin_setting_produces_no_budget_at_all()
		{
			var queue = EmailSwitchService.BuildProviderQueue(Controls(0));

			Assert.Empty(queue);
		}

		/// <summary>
		/// A resend reuses the head of the queue rather than skipping to the next provider, so the
		/// provider that just worked is the one tried again.
		/// </summary>
		[Fact]
		public void The_head_of_the_budget_is_the_first_provider_in_priority_order()
		{
			var queue = EmailSwitchService.BuildProviderQueue(Controls(2));

			Assert.Equal(EmailProvider.SendGrid, queue.Peek());
		}

		/// <summary>
		/// A second real provider adds slots to the one shared budget; it does not get a budget of its
		/// own. The whole list repeats in configured order, so failover reaches Resend and then comes
		/// back round rather than draining one provider first.
		/// </summary>
		[Fact]
		public void The_budget_repeats_the_whole_priority_list_in_order()
		{
			var queue = EmailSwitchService.BuildProviderQueue(Controls(2, [EmailProvider.Resend, EmailProvider.SendGrid]));

			Assert.Equal(
				[EmailProvider.Resend, EmailProvider.SendGrid, EmailProvider.Resend, EmailProvider.SendGrid],
				queue);
		}

		/// <summary>
		/// Three providers, two rounds: six slots, and the list order holds across the repeat rather
		/// than draining one provider before moving on.
		/// </summary>
		[Fact]
		public void A_three_provider_budget_repeats_in_order()
		{
			var queue = EmailSwitchService.BuildProviderQueue(
				Controls(2, [EmailProvider.Brevo, EmailProvider.Resend, EmailProvider.SendGrid]));

			Assert.Equal(
				[
					EmailProvider.Brevo, EmailProvider.Resend, EmailProvider.SendGrid,
					EmailProvider.Brevo, EmailProvider.Resend, EmailProvider.SendGrid
				],
				queue);
		}

		[Fact]
		public void The_head_of_a_resend_first_budget_is_resend()
		{
			var queue = EmailSwitchService.BuildProviderQueue(Controls(2, [EmailProvider.Resend, EmailProvider.SendGrid]));

			Assert.Equal(EmailProvider.Resend, queue.Peek());
		}
	}
}
