using MaSatellite.Core.Audio;

namespace MaSatellite.Core.Tests;

public sealed class AnnouncementMixerTests
{
    [Fact]
    public void Announcement_ducks_music_and_mixes_over_it_then_restores_music_gain()
    {
        var mixer = new AnnouncementMixer();
        mixer.SetOutputFormat(new AudioOutputFormat(1000, 1));
        mixer.Enqueue(Enumerable.Repeat(0.25f, 400).ToArray(), 1000, 1);

        var first = Enumerable.Repeat(0.5f, 200).ToArray();
        mixer.Mix(first);

        // By frame 150 the 120 ms fade has reached the ducked music level.
        Assert.InRange(first[150], 0.369f, 0.371f);

        var second = Enumerable.Repeat(0.5f, 300).ToArray();
        mixer.Mix(second);
        Assert.InRange(second[^1], 0.42f, 0.45f);

        var third = Enumerable.Repeat(0.5f, 200).ToArray();
        mixer.Mix(third);
        Assert.InRange(third[^1], 0.499f, 0.501f);
    }

    [Fact]
    public void Queues_announcements_in_order_and_rejects_excessive_backlog()
    {
        var mixer = new AnnouncementMixer();
        mixer.SetOutputFormat(new AudioOutputFormat(10, 1));
        mixer.Enqueue(Enumerable.Repeat(0.2f, 900).ToArray(), 10, 1);
        mixer.Enqueue(Enumerable.Repeat(0.1f, 900).ToArray(), 10, 1);

        Assert.Throws<InvalidOperationException>(() =>
            mixer.Enqueue([0.3f], 10, 1));

        var output = new float[1800];
        mixer.Mix(output);
        Assert.True(output[0] > output[901]);
    }

    [Fact]
    public void Clear_discards_pending_announcement_and_restores_duck_gain()
    {
        var mixer = new AnnouncementMixer();
        mixer.SetOutputFormat(new AudioOutputFormat(1000, 2));
        mixer.Enqueue(Enumerable.Repeat(0.5f, 400).ToArray(), 1000, 2);
        mixer.Clear();

        var music = Enumerable.Repeat(0.5f, 400).ToArray();
        mixer.Mix(music);
        Assert.All(music, sample => Assert.InRange(sample, 0.499f, 0.501f));
    }
}
