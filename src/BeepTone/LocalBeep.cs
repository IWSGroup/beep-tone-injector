using System;
using System.IO;
using System.Media;
using System.Text;

namespace BeepTone
{
    // Plays the beep through this PC's default speaker or headset, never into the call.
    static class LocalBeep
    {
        static SoundPlayer player;

        public static void Play(BeepSettings settings)
        {
            if (player != null) player.Stop();
            player = new SoundPlayer(MakeWav(settings));
            player.Load();
            player.Play();
        }

        static MemoryStream MakeWav(BeepSettings settings)
        {
            const int rate = 48000;
            float[] samples = BeepSynth.Create(rate, settings);
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + samples.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(samples.Length * 2);
            foreach (float s in samples) w.Write((short)Math.Round(Math.Max(-1.0, Math.Min(1.0, s)) * 32767.0));
            w.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}
