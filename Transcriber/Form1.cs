using Microsoft.VisualBasic;
using NAudio.Wave;
using System.Diagnostics;
using System.Xml.Linq;
using TranscribeCppSharp;
using TranscribeCppSharp.Audio;
using TranscribeCppSharp.Cli;
using TranscribeCppSharp.Interop;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static TranscribeCppSharp.Cli.TranscribeCommand;

namespace Transcriber
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            LoadConfig();
        }

        StreamSTTMicrophoneRecorder rec = new StreamSTTMicrophoneRecorder();
        private void button1_Click(object sender, EventArgs e)
        {
        }

        private void button2_Click(object sender, EventArgs e)
        {

        }

        private void saveWavToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var data = listView1.SelectedItems[0].Tag as TranscribeResult;
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.ShowDialog();

            using (WaveFileWriter writer = new WaveFileWriter(sfd.FileName, rec.WaveFormat))
            {
                writer.Write(data.Data, 0, data.Data.Length);
            }
        }

        private void playToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PlaySelected();
        }

        void PlaySelected()
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var data = listView1.SelectedItems[0].Tag as TranscribeResult;

            if (currentPlaywaveOut != null && currentPlaywaveOut.PlaybackState == PlaybackState.Playing)
                currentPlaywaveOut.Stop();

            RawSourceWaveStream provider = lastWavProvider = new RawSourceWaveStream(
                                     new MemoryStream(data.Data), data.Format);

            WaveOut waveOut = currentPlaywaveOut = new WaveOut();
            waveOut.Init(provider);
            waveOut.Play();

        }

        RawSourceWaveStream lastWavProvider;
        WaveOut currentPlaywaveOut;

        Session RuntimeSession;
        Model model;
        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            if (Configs.RuntimeTranscribing)
            {
                if (model == null)
                {
                    model = TranscribeCommand.LoadModel(Configs.GGUFPath);
                }
                RuntimeSession = model.CreateSession();
                rec.STTStream = RuntimeSession.CreateStream();
                rec.STTStream.Begin();
            }

            toolStripStatusLabel1.Text = "Recording...";
            rec.TextChanged = () =>
            {

                toolStripStatusLabel1.Text = "Current transcription: " + rec.CurrentText;
            };

            rec.StartRecording();
        }

        static async Task<string> DecodeRawWav(byte[] data, WaveFormat format)
        {
            MemoryStream ms = new MemoryStream();
            using (WaveFileWriter writer = new WaveFileWriter(ms, format))
            {
                writer.Write(data, 0, data.Length);
            }

            return await System.Threading.Tasks.Task.Run(() =>
            {
                var source = WavPcmSourceExt.TryOpen(ms.ToArray());
                return TranscribeCommand.DecodeWavFile(Configs.GGUFPath, source);
            });

        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Delete)
            {
                DeleteSelected();
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async void toolStripButton2_Click(object sender, EventArgs e)
        {
            currentPlaywaveOut?.Stop();

            if (rec == null || rec.WaveFormat == null)
                return;

            toolStripStatusLabel1.Text = "Stopped";
            rec.StopRecording();
            string res = string.Empty;
            var data = rec.Stream.ToArray();
            RawSourceWaveStream provider = new RawSourceWaveStream(
                                     new MemoryStream(data), rec.WaveFormat);
            var total = provider.TotalTime;

            if (Configs.AutoTranscribing)
                res = await DecodeRawWav(data, rec.WaveFormat);

            AddLine(data, rec.WaveFormat, total, res);
        }

        public void LoadConfig()
        {
            if (!File.Exists("config.xml"))
                return;

            var doc = XDocument.Load("config.xml");
            foreach (var item in doc.Descendants("setting"))
            {
                var name = item.Attribute("name").Value;
                switch (name)
                {
                    case "ggufPath":
                        Configs.GGUFPath = item.Value;
                        break;
                    case "autoTranscribing":
                        Configs.AutoTranscribing = bool.Parse(item.Value);
                        break;
                    case "runtimeTranscribing":
                        Configs.RuntimeTranscribing = bool.Parse(item.Value);
                        break;
                }
            }
        }

        public void SaveConfig()
        {
            XElement root = new XElement("root");
            root.Add(new XElement("setting", new XAttribute("name", "ggufPath"), new XCData(Configs.GGUFPath)));
            root.Add(new XElement("setting", new XAttribute("name", "autoTranscribing"), Configs.AutoTranscribing));
            root.Add(new XElement("setting", new XAttribute("name", "runtimeTranscribing"), Configs.RuntimeTranscribing));
            File.WriteAllText("config.xml", root.ToString());
        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            var d = AutoDialog.DialogHelpers.StartDialog();
            var ggufPath = d.AddString("GGUF path", Configs.GGUFPath);
            var transcribeEnabled = d.AddBool("Auto transcribing", Configs.AutoTranscribing);
            var runtimeTranscribing = d.AddBool("Runtime transcribing", Configs.RuntimeTranscribing);

            if (!d.ShowDialog())
                return;

            Configs.GGUFPath = ggufPath.Value;
            Configs.AutoTranscribing = transcribeEnabled.Value;
            Configs.RuntimeTranscribing = runtimeTranscribing.Value;
            SaveConfig();
        }

        private async void transcribeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var lvi = listView1.SelectedItems[0];
            var data = lvi.Tag as TranscribeResult;
            var res = await DecodeRawWav(data.Data, rec.WaveFormat);
            lvi.SubItems[3].Text = res;
        }

        private void copyTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var lvi = listView1.SelectedItems[0];
            Clipboard.SetText(lvi.SubItems[2].Text);
        }

        static readonly string[] SizeSuffixes =
                  { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };

        static string SizeSuffix(Int64 value, int decimalPlaces = 1)
        {
            if (value < 0) { return "-" + SizeSuffix(-value, decimalPlaces); }

            int i = 0;
            decimal dValue = (decimal)value;
            while (Math.Round(dValue, decimalPlaces) >= 1000)
            {
                dValue /= 1024;
                i++;
            }

            return string.Format("{0:n" + decimalPlaces + "} {1}", dValue, SizeSuffixes[i]);
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DeleteSelected();
        }

        void DeleteSelected()
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            List<ListViewItem> toDel = new List<ListViewItem>();
            for (int i = 0; i < listView1.SelectedItems.Count; i++)
            {
                var lvi = listView1.SelectedItems[i];
                toDel.Add(lvi);
            }

            if (MessageBox.Show($"Are you sure you want to delete: {toDel.Count} items?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (var item in toDel)
            {
                listView1.Items.Remove(item);
            }
        }

        public static (byte[] Data, WaveFormat Format, TimeSpan totalTime) GetRawAudioBytes(string wavFilePath)
        {
            using (var reader = new WaveFileReader(wavFilePath))
            {
                byte[] buffer = new byte[reader.Length];
                reader.Read(buffer, 0, buffer.Length);

                return (buffer, reader.WaveFormat, reader.TotalTime);
            }
        }

        private async void toolStripButton4_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "Wav files (*.wav)|*.wav";
            if (ofd.ShowDialog() != DialogResult.OK)
                return;

            var result = GetRawAudioBytes(ofd.FileName);

            Stopwatch timer = Stopwatch.StartNew();
            var res = await DecodeRawWav(result.Data, result.Format);
            timer.Stop();
            toolStripStatusLabel1.Text = $"Processed in {timer.ElapsedMilliseconds}ms";
            AddLine(result.Data, result.Format, result.totalTime, res);
        }

        private void AddLine(byte[] Data, WaveFormat Format, TimeSpan totalTime, string res)
        {
            listView1.Items.Add(new ListViewItem([DateTime.Now.ToShortTimeString(),
                $"{Math.Round((totalTime.TotalSeconds),2)}s",
                SizeSuffix(Data.Length),
                res])
            {
                Tag = new TranscribeResult(res, Data, Format)
            });
        }



        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var lvi = listView1.SelectedItems[0].Tag as TranscribeResult;
            richTextBox1.Text = lvi.Text;
        }

        private void toolStripButton5_Click(object sender, EventArgs e)
        {
            if (currentPlaywaveOut == null)
                return;

            if (currentPlaywaveOut.PlaybackState == PlaybackState.Paused)
                currentPlaywaveOut?.Play();
            else
            if (currentPlaywaveOut.PlaybackState == PlaybackState.Playing)
                currentPlaywaveOut?.Pause();
        }

        private void toolStripButton6_Click(object sender, EventArgs e)
        {
            if (currentPlaywaveOut != null && currentPlaywaveOut.PlaybackState == PlaybackState.Paused)
                currentPlaywaveOut?.Play();
            else
                PlaySelected();
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (lastWavProvider != null && currentPlaywaveOut.PlaybackState == PlaybackState.Playing)
            {
                double currentMs = lastWavProvider.CurrentTime.TotalSeconds;
                double total = lastWavProvider.TotalTime.TotalSeconds;

                double percentage = (double)lastWavProvider.Position / lastWavProvider.Length * 100;

                toolStripStatusLabel3.Text = $"{Math.Round(currentMs, 2)}s / {Math.Round(total, 2)}s   {Math.Round(percentage, 2)}%";
            }
        }
    }
}
