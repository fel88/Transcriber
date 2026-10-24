using NAudio.Wave;
using System.Net.Security;
using TranscribeCppSharp;
using TranscribeCppSharp.Audio;


namespace Transcriber
{
    public class StreamSTTMicrophoneRecorder
    {
        public StreamSTTMicrophoneRecorder()
        {
        }

        public StreamSession STTStream;
        public WaveIn waveSource = null;
        //private WaveFileWriter waveFile = null;
        public MemoryStream Stream = new MemoryStream();
        public WaveFormat WaveFormat;
        public string CurrentText;
        public Action TextChanged;
        public void StartRecording()
        {
            // Use default microphone (DeviceNumber = 0) and 16 kHz, 16-bit mono format
            waveSource = new WaveIn();
            waveSource.DeviceNumber = 0;
            waveSource.WaveFormat = WaveFormat = new WaveFormat(16000, waveSource.WaveFormat.Channels);
            Stream = new MemoryStream();
            // Hook up the data available event to write data to file
            waveSource.DataAvailable += (sender, e) =>
            {
                Stream.Write(e.Buffer, 0, e.BytesRecorded);

                if (!Configs.RuntimeTranscribing)
                    return;

                MemoryStream ms = new MemoryStream();
                using (WaveFileWriter writer = new WaveFileWriter(ms, WaveFormat))
                {
                    writer.Write(e.Buffer, 0, e.Buffer.Length);

                }
                var w = WavPcmSource.TryOpen(ms.ToArray());
                var span = w.ReadWindow(0, (int)w.LengthSamples);
                STTStream.Feed(span);

                var newText = STTStream.GetCurrentText().FullText;
                if (newText != CurrentText)
                {
                    CurrentText = newText;
                    TextChanged?.Invoke();
                }
                /*if (waveFile != null)
                {
                    waveFile.Write(e.Buffer, 0, e.BytesRecorded);
                    waveFile.Flush();
                }*/
            };

            // Handle recording stopped event
            waveSource.RecordingStopped += (sender, e) =>
            {
                /*  if (waveFile != null)
                  {
                      waveFile.Dispose();
                      waveFile = null;
                  }*/
                if (waveSource != null)
                {
                    waveSource.Dispose();
                    waveSource = null;
                }
            };

            // waveFile = new WaveFileWriter(outputFilePath, waveSource.WaveFormat);
            waveSource.StartRecording();
        }

        public void StopRecording()
        {
            waveSource?.StopRecording();
        }
    }

}