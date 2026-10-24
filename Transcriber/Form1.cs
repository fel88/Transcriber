using Microsoft.VisualBasic;
using NAudio.Wave;
using System.Xml.Linq;
using TranscribeCppSharp.Audio;
using TranscribeCppSharp.Cli;

namespace Transcriber
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            LoadConfig();
        }

        SimpleMicrophoneRecorder rec = new SimpleMicrophoneRecorder();
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

            var data = listView1.SelectedItems[0].Tag as byte[];
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.ShowDialog();

            using (WaveFileWriter writer = new WaveFileWriter(sfd.FileName, rec.WaveFormat))
            {
                writer.Write(data, 0, data.Length);
            }
        }

        private void playToolStripMenuItem_Click(object sender, EventArgs e)
        {

            if (listView1.SelectedItems.Count == 0)
                return;

            var data = listView1.SelectedItems[0].Tag as byte[];

            IWaveProvider provider = new RawSourceWaveStream(
                                     new MemoryStream(data), rec.WaveFormat);

            WaveOut waveOut = new WaveOut();
            waveOut.Init(provider);
            waveOut.Play();
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            toolStripStatusLabel1.Text = "Recording...";
            rec.StartRecording();
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            toolStripStatusLabel1.Text = "Stopped";
            rec.StopRecording();
            string res = string.Empty;
            if (Configs.AutoTranscribing)
            {
                var data = rec.Stream.ToArray();
                                

                MemoryStream ms = new MemoryStream();
                using (WaveFileWriter writer = new WaveFileWriter(ms, rec.WaveFormat))
                {
                    writer.Write(data, 0, data.Length);
                }

                res = TranscribeCommand.DecodeWavFile(Configs.GGUFPath, WavPcmSource.TryOpen(ms.ToArray()));
            }

            listView1.Items.Add(new ListViewItem(new string[] { DateTime.Now.ToShortTimeString(), SizeSuffix(rec.Stream.Length), res }) { Tag = rec.Stream.ToArray() });
        }

        public void LoadConfig()
        {
            if (!File.Exists("config.xml"))
                return;

            var doc = XDocument.Load("config.xml");
            foreach (var item in doc.Descendants("setting"))
            {
                var name = item.Attribute("name").Value;
                if (name == "ggufPath")
                {
                    Configs.GGUFPath = item.Value;
                }
                if (name == "autoTranscribing")
                {
                    Configs.AutoTranscribing = bool.Parse(item.Value);
                }
            }

        }

        public void SaveConfig()
        {
            XElement root = new XElement("root");
            root.Add(new XElement("setting", new XAttribute("name", "ggufPath"), new XCData(Configs.GGUFPath)));
            root.Add(new XElement("setting", new XAttribute("name", "autoTranscribing"), Configs.AutoTranscribing));
            File.WriteAllText("config.xml", root.ToString());
        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            var d = AutoDialog.DialogHelpers.StartDialog();
            d.AddStringField("ggufPath", "GGUF path", Configs.GGUFPath);
            d.AddBoolField("transcribeEnabled", "Auto transcribing", Configs.AutoTranscribing);

            if (!d.ShowDialog())
                return;

            Configs.GGUFPath = d.GetStringField("ggufPath");
            Configs.AutoTranscribing = d.GetBoolField("transcribeEnabled");
            SaveConfig();
        }

        private void transcribeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
                return;

            var lvi = listView1.SelectedItems[0];
            var data = lvi.Tag as byte[];

            MemoryStream ms = new MemoryStream();
            using (WaveFileWriter writer = new WaveFileWriter(ms, rec.WaveFormat))
            {
                writer.Write(data, 0, data.Length);
            }

            var res = TranscribeCommand.DecodeWavFile(Configs.GGUFPath, WavPcmSource.TryOpen(ms.ToArray()));
            lvi.SubItems[2].Text = res;
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

            if (listView1.SelectedItems.Count == 0)
                return;

            for (int i = 0; i < listView1.SelectedItems.Count; i++)
            {
                var lvi = listView1.SelectedItems[i];
                listView1.Items.Remove(lvi);
            }
            
        }
    }
}
