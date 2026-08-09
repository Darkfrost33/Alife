using Alife.Function.Speech.FishAudio;

namespace Alife.Test.Speech;

[TestFixture]
public class FishAudioMessagePackTests
{
    [Test]
    public void SerializeText_UsesExpectedProtocolShape()
    {
        byte[] actual = FishAudioMessagePack.SerializeText("Hi");
        byte[] expected = [
            0x82,
            0xa5, (byte)'e', (byte)'v', (byte)'e', (byte)'n', (byte)'t',
            0xa4, (byte)'t', (byte)'e', (byte)'x', (byte)'t',
            0xa4, (byte)'t', (byte)'e', (byte)'x', (byte)'t',
            0xa2, (byte)'H', (byte)'i',
        ];

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void DeserializeResponse_ReadsAudioEvent()
    {
        byte[] payload = [
            0x82,
            0xa5, (byte)'e', (byte)'v', (byte)'e', (byte)'n', (byte)'t',
            0xa5, (byte)'a', (byte)'u', (byte)'d', (byte)'i', (byte)'o',
            0xa5, (byte)'a', (byte)'u', (byte)'d', (byte)'i', (byte)'o',
            0xc4, 0x03, 0x01, 0x02, 0x03,
        ];

        FishAudioMessagePack.Response response = FishAudioMessagePack.DeserializeResponse(payload);

        Assert.That(response.Event, Is.EqualTo("audio"));
        Assert.That(response.Audio, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void SerializeStart_IncludesPcmStreamingSettings()
    {
        byte[] payload = FishAudioMessagePack.SerializeStart(
            24000,
            100,
            "balanced",
            "voice_id",
            1.0,
            0.0);

        Assert.That(payload, Is.Not.Empty);
        Assert.That(payload[0], Is.EqualTo(0x82));
    }
}
