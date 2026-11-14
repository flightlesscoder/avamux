#if EASTER_EGGS
using Avamux.Core.EasterEggs;
using Avamux.Core.Theming;

namespace Avamux.Tests.EasterEggs;

public class AolTests
{
    [Fact]
    public void Buddies_answer_and_sign_off_when_told_goodbye()
    {
        var rnd = new Random(7);
        foreach (var buddy in AolTalk.NewBuddies())
        {
            var (hello, left) = buddy.Reply("hi!", rnd);
            Assert.False(left);
            Assert.Contains(hello, buddy.Hello);
            Assert.Contains(buddy.Reply("how are you", rnd).Text, buddy.Status);
            Assert.Contains(buddy.Reply("do you like pizza?", rnd).Text, buddy.Answers);
            Assert.Equal((buddy.Bye, true), buddy.Reply("ok g2g", rnd));
            Assert.Contains(buddy.Name, buddy.MailReply("hello", rnd));
        }
        Assert.Equal(AolTalk.NewBuddies().Count, AolTalk.NewBuddies().Select(b => b.Name).Distinct().Count());
    }

    [Fact]
    public void Chat_rooms_greet_the_member_by_name()
    {
        var rnd = new Random(3);
        foreach (var room in AolTalk.Rooms)
        {
            Assert.True(room.Crowd > room.People.Length / 2);
            var replies = AolTalk.Replies(room, "Tester", "hi all", rnd);
            Assert.NotEmpty(replies);
            Assert.Contains("Tester", replies[0].Text + "Tester");
            Assert.All(replies, r => Assert.Contains(r.Who, room.People));
            for (var i = 0; i < 50; i++) Assert.DoesNotContain("{me}", AolTalk.Ambient(room, "Tester", rnd).Text);
        }
    }

    [Fact]
    public void The_mailbox_starts_with_four_letters_and_bounces_strangers()
    {
        var mail = AolTalk.NewMail("Tester");
        Assert.Equal(4, mail.Count);
        Assert.All(mail, m => Assert.False(m.Read));
        Assert.Contains("Tester", mail[0].Body);
        Assert.Contains("nobody@nowhere", AolTalk.Bounce("nobody@nowhere", "hi").Body);
    }

    [Fact]
    public void The_modem_song_fits_the_connection_steps()
    {
        var song = AolSounds.Modem(false);
        var seconds = song.Length / (double)SoundService.SampleRate;
        Assert.InRange(seconds, AolSounds.ModemSteps[^2] - 0.5, AolSounds.ModemSteps[^1]);
        Assert.All(new[] { song, AolSounds.Modem(true), AolSounds.DoorOpen(), AolSounds.DoorClose(), AolSounds.Message(), AolSounds.Welcome(), AolSounds.Mail() },
            s => Assert.InRange(s.Max(Math.Abs), 0.5f, 0.81f));
        foreach (var voice in new[] { "welcome.wav", "mail.wav" })
        {
            var clip = AolSounds.Voice(voice);
            Assert.NotNull(clip);
            Assert.InRange(clip!.Length / (double)SoundService.SampleRate, 0.4, 2);
        }
        Assert.Equal(AolSounds.BusyLength, AolSounds.Modem(true).Length / (double)SoundService.SampleRate, 2);
    }
}
#endif
