using NAudio.Wave;

namespace Transcriber
{
    class SimpleMicrophoneRecorder
    {
        public WaveIn waveSource = null;
        //private WaveFileWriter waveFile = null;
        public MemoryStream Stream = new MemoryStream();
        public WaveFormat WaveFormat;
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