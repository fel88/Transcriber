using NAudio.Wave;

namespace Transcriber
{
    public class TranscribeResult
    {
        public TranscribeResult(string text, byte[] data, WaveFormat format)
        {
            Text = text;
            Data = data;
            Format = format;
        }

        public WaveFormat Format;
        public string Text;
        public byte[] Data;
    }
}
