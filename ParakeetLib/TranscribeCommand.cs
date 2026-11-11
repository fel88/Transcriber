// The `transcribe` command: the flow is argument handling (CliOptions), the
// windowing math (WindowPlanner), audio loading (AudioLoader) and the
// transcript formats (TranscriptWriter), with the orchestration below.
//
// Console and stderr are injected so the whole command is unit testable: the
// entry point generated from Program.cs cannot be called from a test.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using TranscribeCppSharp.Audio;
using TranscribeCppSharp.Interop;
using TranscribeCppSharp.Models;

using TranscribeCppSharp.SharedSource;

namespace TranscribeCppSharp.Cli;

/// <summary>
/// fghfgh
/// </summary>
public static class TranscribeCommand
{
    /// <summary>The name the tool is installed as.</summary>
    internal const string ToolName = "transcribe";

    // The README "All options" section is checked against this text by
    // ReadmeExamplesTest, so the two cannot drift apart.
    internal const string HelpText = """
        Transcribe audio with a speech-to-text model. Supports every model family
        that transcribe.cpp supports (Whisper, Moonshine, Parakeet, Canary, GigaAM,
        Voxtral, Qwen3-ASR, MOSS diarization, …).

        The model's role decides what runs (transcribe.cpp 0.4): an ASR model is
        transcribed, a diarization model (Sortformer) prints speaker turns, a
        language-identification model prints ranked languages, a voice-activity
        model prints speech segments.

        Usage: transcribe <audio> [model] [options]

          <audio>        WAV (16 kHz mono 16-bit) read directly; any other
                         format (ogg, mp3, m4a, …) is decoded with ffmpeg
                         (must be installed)
          [model]        a model file path, a known alias (default:
                         moss-transcribe-diarize), or a HuggingFace spec
                         '<owner>/<repo>/<file.gguf>[@<revision>]'.
                         Aliases and any other supported GGUF are downloaded
                         from HuggingFace on first use and cached.

          --model <m>    same as the [model] argument
          --quant <q>    quantization for a known alias (e.g. Q4_K_M, Q5_K_M,
                         Q8_0, F16); default is per model (see --list-models)
          --list-models  list the known model aliases and exit
          --model-info <alias>  show details (family, params, license, upstream catalog)
          --backend <b>  compute backend: auto (default), cpu, cpu-accel,
                         metal, vulkan, cuda, rocm. 'auto' runs on the GPU
                         when one initializes (every discrete GPU is probed
                         before the integrated ones) and falls back to the CPU
                         otherwise; a forced backend fails instead of falling
                         back
          --device <n>   run on that exact device (index from --list-devices);
                         never falls back to another device
          --list-devices list the compute devices this build and machine can see
          --lang <code>  language code for the decoder (default: en)
          --allowed <codes>  language-identification model only: comma-separated
                         codes to rank (e.g. en,fr,de); default is all labels
          --chunk <sec>  max per-transcription window in seconds (default: 300);
                         long audio is split with 1 s overlap and deduplicated
          --vad <model>  run a VAD model (e.g. silero-vad-v6.2) over the file
                         first and transcribe only the speech it finds, with
                         the original timestamps. Unmeasured: it changes what
                         the model sees, so the text can differ. Off by default
          --no-diarize   disable speaker diarization
          --out <file>   write the report to a file; format controlled
                         by --format (plain/vtt/json/html/srt/ass)
          --format <fmt> report file format: plain (default, timestamped
                         lines), vtt (WebVTT, speaker cues; ASR models only),
                         json, html (numbered paragraphs with timestamps and
                         speakers; ASR models only), srt or ass (subtitles,
                         ASR models only)
          --help         show this help
        """;

    /// <summary>
    /// Prints what upstream's pinned catalog adds to what the manifest already said.
    /// </summary>
    /// <remarks>
    /// Its own section rather than more lines in <c>ModelStore.Info</c>, because
    /// the wrapper has no access to the catalog — it is embedded in this executable
    /// alone, deliberately, so the library never carries data only the two front
    /// ends display. Everything below is transcribe.cpp's own record: their
    /// published quantizations, their capability flags, their measurement. This
    /// project measures none of it, so the section says whose it is.
    /// <para>
    /// Nothing is printed when upstream has no record for the alias, and no
    /// placeholder stands in for one: an absent record is an absent record.
    /// </para>
    /// </remarks>
    private static void PrintUpstreamCatalog(string alias, TextWriter stdout)
    {
        UpstreamCatalog.Record? record = UpstreamCatalog.Read(alias);
        if (record is null)
        {
            return;
        }

        bool wroteHeader = false;
        void Line(string label, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (!wroteHeader)
            {
                stdout.WriteLine();
                stdout.WriteLine("upstream catalog (transcribe.cpp, their measurements):");
                wroteHeader = true;
            }

            stdout.WriteLine($"    {label,-14}: {value}");
        }

        List<UpstreamCatalog.Download>? downloads = record.Downloads;
        if (downloads is { Count: > 0 })
        {
            Line("quantizations", string.Join(" \u00b7 ", downloads.Select(d =>
                $"{d.Quant} {ModelSizeFormat.Format(d.SizeBytes)}")));
        }

        Dictionary<string, UpstreamCatalog.Capability>? caps = record.Capabilities;
        if (caps is not null)
        {
            List<string> supported = caps.Where(kv => kv.Value.Supported).Select(kv => kv.Key).ToList();
            if (supported.Count > 0)
            {
                bool anyVerified = caps.Any(kv => kv.Value.Supported && kv.Value.Verified);
                Line("capabilities", string.Join(", ", supported)
                    + (anyVerified ? string.Empty : " (unverified upstream)"));
            }
        }

        UpstreamCatalog.Headline? headline = record.HeadlineBenchmark;
        if (headline is not null)
        {
            // The headline dataset, and the quantization this manifest pins — the one
            // the reader would actually download. Falling back to any quantization
            // rather than reporting nothing: upstream measures several, and the row
            // still says which one it is.
            List<UpstreamCatalog.Accuracy> rows = record.AccuracyBenchmarks ?? new List<UpstreamCatalog.Accuracy>();
            UpstreamCatalog.Accuracy? row = rows
                .FirstOrDefault(a => Same(a, headline) && a.Quant == PinnedQuant(alias))
                ?? rows.FirstOrDefault(a => Same(a, headline));

            // The headline is an error rate (WER/CER/DER) for a recognizer and an
            // accuracy for a language model; both are shown, each with its metric.
            string? figure = row?.ErrPct is { } err ? $"{err:0.##} %"
                : row?.AccPct is { } acc ? $"{acc:0.##} %"
                : null;
            if (row is not null && figure is not null)
            {
                string provenance = row.MeasurementProvenance ?? "engine build not recorded upstream";
                Line("accuracy", $"{row.Metric?.ToUpperInvariant()} {figure} on {headline.Dataset} {headline.Split}, "
                    + $"{headline.Language}, {row.Quant} \u2014 {provenance}");
            }
        }

        static bool Same(UpstreamCatalog.Accuracy a, UpstreamCatalog.Headline h)
            => string.Equals(a.Dataset, h.Dataset, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Language, h.Language, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Metric, h.Metric, StringComparison.OrdinalIgnoreCase);

        static string? PinnedQuant(string alias) => ModelStore.Find(alias)?.Quant;
    }

    /// <summary>
    /// Runs one command. Returns the process exit code: 0 on success, 1 on a
    /// reported error (a message on stderr, never a stack trace).
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return RunCore(args, stdout, stderr);
        }
        catch (Exception ex)
        {
            // The documented contract is a message on stderr and exit code 1, not
            // a .NET stack trace. Everything with a useful message is caught where
            // it can be explained; this is the last line of defence for the paths
            // that were not (a corrupt model reaching Model.Load, a backend that
            // fails to initialise, and so on).
            stderr.WriteLine(ex is TranscribeException or IOException or UnauthorizedAccessException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="modelPath2"></param>
    /// <param name="wavPath"></param>
    /// <returns></returns>
    /// <exception cref="ParakeetException"></exception>
    public static string DecodeWavFile(string modelPath2, string wavPath)
    {
        CliOptions options = new CliOptions();

        options.ModelSpec = modelPath2;
        options.AudioPath = wavPath;

        // Everything that can be checked without a model is checked before the
        // model is resolved: the input file and its flags (CliOptions), then the
        // compute choice. A mistyped path, a bad option or an unavailable forced
        // backend must not cost a 600+ MB download to find out.
        Backends.InitDefault();

        var devices = Backends.EnumerateDevices();
        DeviceChoice? choice = DeviceSelection.Resolve(devices, options.Backend, options.DeviceIndex, out var deviceError);
        if (choice is null)
        {
            throw new ParakeetException();
        }

        // A forced backend that this build/machine does not have fails here rather
        // than silently landing on another one: the upstream returns ERR_BACKEND, and
        // quietly using the CPU would misreport what actually ran.
        if (choice.Device is null && options.Backend != BackendRequest.BackendAuto && !Backends.BackendAvailable(options.Backend))
        {
            throw new ParakeetException();
        }

        string modelPath = "";
        try
        {
            modelPath = ModelStore.Resolve(options.ModelSpec, options.Quant, null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Unknown alias/spec, download failure, checksum mismatch: report and stop.
            //stderr.WriteLine(ex.Message);
            throw new ParakeetException();
        }

        PcmSource pcmSource;
        try
        {
            pcmSource = AudioLoader.Open(options.AudioPath);
        }
        catch (Exception ex) when (ex is AudioLoadException or IOException or UnauthorizedAccessException)
        {
            // A file that is neither a readable WAV nor decodable by ffmpeg, or
            // no ffmpeg at all: one line, not a .NET stack trace.

            throw new ParakeetException();

        }

        // The audio is read one window at a time and never held whole: an hour
        // is 219 MiB as a single float[], and none of it is needed at once. The
        // source owns a temporary file when ffmpeg decoded the input, so it has
        // to outlive the loop below.
        using (pcmSource)
        {
            var res = Decode(options, modelPath, choice, pcmSource);

            return res;
            //return RunWithAudio(stdout, stderr, options, modelPath, choice, pcmSource);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="modelPath2"></param>
    /// <param name="wave"></param>
    /// <returns></returns>
    /// <exception cref="ParakeetException"></exception>
    public static string DecodeWavFile(string modelPath2, PcmSource wave)
    {
        CliOptions options = new CliOptions();

        options.ModelSpec = modelPath2;

        // Everything that can be checked without a model is checked before the
        // model is resolved: the input file and its flags (CliOptions), then the
        // compute choice. A mistyped path, a bad option or an unavailable forced
        // backend must not cost a 600+ MB download to find out.
        Backends.InitDefault();

        var devices = Backends.EnumerateDevices();
        DeviceChoice? choice = DeviceSelection.Resolve(devices, options.Backend, options.DeviceIndex, out var deviceError);
        if (choice is null)
        {
            throw new ParakeetException();
        }

        // A forced backend that this build/machine does not have fails here rather
        // than silently landing on another one: the upstream returns ERR_BACKEND, and
        // quietly using the CPU would misreport what actually ran.
        if (choice.Device is null && options.Backend != BackendRequest.BackendAuto && !Backends.BackendAvailable(options.Backend))
        {
            throw new ParakeetException();
        }

        string modelPath = "";
        try
        {
            modelPath = ModelStore.Resolve(options.ModelSpec, options.Quant, null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Unknown alias/spec, download failure, checksum mismatch: report and stop.
            //stderr.WriteLine(ex.Message);
            throw new ParakeetException();
        }

        PcmSource pcmSource;
        try
        {
            pcmSource = wave;
        }
        catch (Exception ex) when (ex is AudioLoadException or IOException or UnauthorizedAccessException)
        {
            // A file that is neither a readable WAV nor decodable by ffmpeg, or
            // no ffmpeg at all: one line, not a .NET stack trace.

            throw new ParakeetException();

        }

        // The audio is read one window at a time and never held whole: an hour
        // is 219 MiB as a single float[], and none of it is needed at once. The
        // source owns a temporary file when ffmpeg decoded the input, so it has
        // to outlive the loop below.
        using (pcmSource)
        {
            var res = Decode(options, modelPath, choice, pcmSource);

            return res;
            //return RunWithAudio(stdout, stderr, options, modelPath, choice, pcmSource);
        }
    }
 
    private static int RunCore(string[] args, TextWriter stdout, TextWriter stderr)
    {
        // Flags answered without touching the model or the network.
        if (Has(args, "--list-models"))
        {
            ModelStore.List(stdout);
            return 0;
        }

        if (Has(args, "--debugger"))
        {
            Debugger.Launch();
            Debugger.Break();
        }

        if (Has(args, "--model-info"))
        {
            string alias = CliOptions.ValueAfter(args, "--model-info") ?? string.Empty;
            if (!ModelStore.Info(alias, stdout, stderr))
            {
                return 1;
            }

            PrintUpstreamCatalog(alias, stdout);
            return 0;
        }

        if (Has(args, "--list-devices"))
        {
            // Needs the backends registered, so it cannot be answered from the
            // manifest like --list-models. Handled before any model resolution so
            // it never triggers a download.
            Backends.InitDefault();
            stdout.WriteLine(DeviceSelection.FormatDevices(Backends.EnumerateDevices()));
            return 0;
        }

        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
        {
            stdout.WriteLine(HelpText);
            return args.Length == 0 ? 1 : 0;
        }

        if (!CliOptions.TryParse(args, out CliOptions options, out string? parseError))
        {
            stderr.WriteLine(parseError);
            return 1;
        }

        // Everything that can be checked without a model is checked before the
        // model is resolved: the input file and its flags (CliOptions), then the
        // compute choice. A mistyped path, a bad option or an unavailable forced
        // backend must not cost a 600+ MB download to find out.
        Backends.InitDefault();

        var devices = Backends.EnumerateDevices();
        DeviceChoice? choice = DeviceSelection.Resolve(devices, options.Backend, options.DeviceIndex, out var deviceError);
        if (choice is null)
        {
            stderr.WriteLine(deviceError);
            return 1;
        }

        // A forced backend that this build/machine does not have fails here rather
        // than silently landing on another one: the upstream returns ERR_BACKEND, and
        // quietly using the CPU would misreport what actually ran.
        if (choice.Device is null && options.Backend != BackendRequest.BackendAuto && !Backends.BackendAvailable(options.Backend))
        {
            stderr.WriteLine($"--backend {options.BackendName} is not available in this build or on this machine.");
            stderr.WriteLine(devices.Count == 0 ? "No compute device is registered." : DeviceSelection.FormatDevices(devices));
            return 1;
        }

        string modelPath;
        try
        {
            modelPath = ModelStore.Resolve(options.ModelSpec, options.Quant, stderr);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Unknown alias/spec, download failure, checksum mismatch: report and stop.
            stderr.WriteLine(ex.Message);
            return 1;
        }

        PcmSource pcmSource;
        try
        {
            pcmSource = AudioLoader.Open(options.AudioPath);
        }
        catch (Exception ex) when (ex is AudioLoadException or IOException or UnauthorizedAccessException)
        {
            // A file that is neither a readable WAV nor decodable by ffmpeg, or
            // no ffmpeg at all: one line, not a .NET stack trace.
            stderr.WriteLine(ex.Message);
            return 1;
        }

        // The audio is read one window at a time and never held whole: an hour
        // is 219 MiB as a single float[], and none of it is needed at once. The
        // source owns a temporary file when ffmpeg decoded the input, so it has
        // to outlive the loop below.
        using (pcmSource)
        {
            var res = Decode(options, modelPath, choice, pcmSource);
            Console.WriteLine(res);
            return 0;
            //return RunWithAudio(stdout, stderr, options, modelPath, choice, pcmSource);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public class ParakeetException : Exception
    {
        /// <summary>
        /// 
        /// </summary>
        public ParakeetException()
        {
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        public ParakeetException(string? message) : base(message)
        {
        }

    }


    public static Model LoadModel(string modelPath)
    {
        CliOptions options = new CliOptions();
        // Everything that can be checked without a model is checked before the
        // model is resolved: the input file and its flags (CliOptions), then the
        // compute choice. A mistyped path, a bad option or an unavailable forced
        // backend must not cost a 600+ MB download to find out.
        Backends.InitDefault();

        var devices = Backends.EnumerateDevices();
        DeviceChoice? choice = DeviceSelection.Resolve(devices, options.Backend, options.DeviceIndex, out var deviceError);
        if (choice is null)
        {
            throw new ParakeetException();
        }

        // A forced backend that this build/machine does not have fails here rather
        // than silently landing on another one: the upstream returns ERR_BACKEND, and
        // quietly using the CPU would misreport what actually ran.
        if (choice.Device is null && options.Backend != BackendRequest.BackendAuto && !Backends.BackendAvailable(options.Backend))
        {
            throw new ParakeetException();
        }
        var model = Model.Load(modelPath, p =>
        {
            p.WithBackend(choice.Backend);
            if (choice.Device is not null)
            {
                p.WithDevice(choice.Device);
            }
        });
        string modelLabel = $"{model.Architecture}/{model.Variant}";
        return model;
    }

    
    private static string Decode(

     CliOptions options,
     string modelPath,
     DeviceChoice choice,
     PcmSource pcmSource)
    {
        long totalSamples = pcmSource.LengthSamples;
        if (totalSamples > int.MaxValue)
        {
            throw new ParakeetException(
                $"the audio is {totalSamples} samples, more than this tool can index. Split it into shorter files.");

        }

        //      stdout.WriteLine($"audio : {Fmt.Samples(totalSamples)} samples = {totalSamples / (double)WindowPlanner.SampleRate:F1}s @ 16kHz mono");

        using var model = Model.Load(modelPath, p =>
        {
            p.WithBackend(choice.Backend);
            if (choice.Device is not null)
            {
                p.WithDevice(choice.Device);
            }
        });
        string modelLabel = $"{model.Architecture}/{model.Variant}";
        //   stdout.WriteLine($"model : {modelLabel}");
        //    stdout.WriteLine($"compute: {DescribeCompute(choice, model)}");

        // transcribe.cpp 0.4 gives a model one or more roles. An ASR model keeps
        // the transcription path; anything else is a diarize/langid/vad model and
        // runs through its own session, which is what the role reports print.
        TranscribeRole roles = model.Roles;
        //stdout.WriteLine($"roles : {DescribeRoles(roles)}");

        if (!roles.HasFlag(TranscribeRole.Asr))
        {

        }

        //     stdout.WriteLine($"diarization supported: {model.Supports(Feature.FeatureDiarization)}");

        using var session = model.CreateSession();

        var limits = session.GetLimits();
        int maxWindowMs = limits.EffectiveMaxAudioMs > 0
            ? (int)Math.Clamp(limits.EffectiveMaxAudioMs - WindowPlanner.OverlapMs, 1, int.MaxValue)
            : int.MaxValue;
        int windowMs = (int)Math.Min((long)options.ChunkSeconds * 1000, maxWindowMs);
        //  stdout.WriteLine($"window: {windowMs / 1000}s (model max audio {limits.EffectiveMaxAudioMs / 1000}s)");

        IReadOnlyList<AudioWindow> windows;
        if (options.VadSpec is null)
        {
            if (!WindowPlanner.TryPlan((int)totalSamples, windowMs, out windows, out var planError))
            {
                // The planner's message names no flag, because the GUI shares it.
                // --chunk is rejected earlier in CliOptions with its own message, so
                // reaching here means the audio is empty rather than the window too
                // short, and no flag advice applies.
                throw new ParakeetException(
planError);

            }
        }
        else
        {
            int planned = PlanFromSpeech2(options, choice, pcmSource, totalSamples, windowMs, out windows);
            if (planned != 0)
            {
                throw new ParakeetException();
            }
        }

        var merged = new StringBuilder();

        // JSON, HTML and the two subtitle formats need the segments kept: those
        // documents are written once, at the end, from the complete list. The
        // plain and VTT writers emit each segment as it arrives and their footer
        // ignores the list entirely, so accumulating it there retained a list
        // entry and a string per segment that nothing ever read — one hour of
        // speech is ~900 segments.
        var lines = options.Format is TranscriptFormat.Json or TranscriptFormat.Html
            or TranscriptFormat.Srt or TranscriptFormat.Ass
            ? new List<TranscriptLine>()
            : null;

        // The merger owns the seam bookkeeping (drop what the previous window
        // already reported, shift timestamps into file time) so the CLI and the
        // GUI cannot drift apart. It is told not to retain: segments are written
        // out as each window arrives, and keeping a second copy of ~900
        // segments an hour of speech produces is what "retain: false" avoids.
        var merger = new TranscriptMerger(retain: false);
        var overall = Stopwatch.StartNew();

        StreamWriter? outFile;
        try
        {
            outFile = OpenOutput(options.OutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The parent directory is checked while parsing, so reaching this is
            // a permission or a path problem: report it, do not dump a stack trace.
            throw new ParakeetException(
$"--out: cannot write to {options.OutPath}: {ex.Message}");

        }

        using (outFile)
        {
            // One timestamp for the whole run: the header and the footer of a
            // file must not disagree by the few seconds a long transcription
            // takes, so both read generated from here.
            var info = new TranscriptInfo(
                options.AudioPath,
                TimeSpan.FromSeconds(totalSamples / (double)WindowPlanner.SampleRate),
                modelLabel, options.Language, options.Diarize, windowMs / 1000, DateTime.Now);

            if (outFile is not null)
            {
                TranscriptWriter.WriteHeader(outFile, options.Format, info);
            }

            double audioTotalSec = totalSamples / (double)WindowPlanner.SampleRate;
            for (int chunkIndex = 0; chunkIndex < windows.Count; chunkIndex++)
            {
                AudioWindow window = windows[chunkIndex];
                int offset = window.OffsetSamples;
                int len = window.LengthSamples;

                // This window and nothing else is in memory. The previous code
                // sliced the whole-file array here, which meant an hour of audio
                // (219 MiB) was resident from the load until the last window
                // finished; now the peak is one window, 19 MiB at 300 s.
                var chunk = pcmSource.ReadWindow(offset, len);
                long chunkStartMs = window.StartMs;

                // stdout.WriteLine($"\n=== window {chunkIndex + 1} @ {Fmt.Ts(chunkStartMs)} ({Fmt.Samples(len)} samples) ===");
                var windowSw = Stopwatch.StartNew();

                Transcript transcript;
                try
                {

                    transcript = session.Run(chunk, r =>
                    {
                        r.WithDiarize(options.Diarize);
                        r.WithLanguage(options.Language);
                    });
                }
                catch (TranscribeException ex)
                {
                    throw new ParakeetException(
$"  transcription failed: {ex.Message}");

                }

                //  stdout.WriteLine($"  lang   : {transcript.DetectedLanguage}   aborted: {transcript.WasAborted}  truncated: {transcript.WasTruncated}");
                //   stdout.WriteLine($"  full   : {transcript.FullText.Trim()}");
                if (options.Diarize != DiarizeMode.DiarizeModeOff || transcript.RawText.Length > 0)
                {
                    // stdout.WriteLine($"  raw    : {transcript.RawText.Trim()}");
                }

                double windowSec = windowSw.Elapsed.TotalSeconds;
                double audioSec = len / (double)WindowPlanner.SampleRate;
                double processedSec = (offset + len) / (double)WindowPlanner.SampleRate;
                double remainingSec = audioTotalSec - processedSec;
                double rtf = windowSec / audioSec;
                //   stdout.WriteLine($"  time   : {Fmt.Dur(windowSec)}/ {audioSec:0.0}s audio (RTF {rtf:0.0}x) | done {Fmt.Dur(processedSec)} / {Fmt.Dur(audioTotalSec)} | ETA ~{Fmt.Dur(remainingSec * rtf)}");

                foreach (var seg in merger.Add(window, transcript))
                {
                    var text = Fmt.Normalize(seg.Text);
                    var start = (long)seg.Start.TotalMilliseconds;
                    var end = (long)seg.End.TotalMilliseconds;
                    var line = new TranscriptLine(start, end, seg.SpeakerId, text);
                    lines?.Add(line);

                    if (outFile is not null)
                    {
                        TranscriptWriter.WriteSegment(outFile, options.Format, line);
                        //  stdout.WriteLine($"  -> [{Fmt.Ts(start)} -> {Fmt.Ts(end)}] Speaker {seg.SpeakerId}: {text}");
                    }

                    //merged.AppendLine(CultureInfo.CurrentCulture, $"[{Fmt.Ts(start)} -> {Fmt.Ts(end)}] Speaker {seg.SpeakerId}: {text}");
                    merged.AppendLine(CultureInfo.CurrentCulture, $"{text}");
                }
            }

            if (outFile is not null)
            {
                // The plain and VTT footers ignore the segment list, so passing
                // an empty one is what the non-JSON/HTML path now has.
                TranscriptWriter.WriteFooter(outFile, options.Format, info, lines ?? []);
                //    stdout.WriteLine($"\ntranscript written to: {options.OutPath} ({FormatName(options.Format)})");
            }

            double totalSec = overall.Elapsed.TotalSeconds;
            //    stdout.WriteLine("\n=== summary ===");
            //    stdout.WriteLine($"  total  : {Fmt.Dur(totalSec)} (RTF {totalSec / audioTotalSec:0.0}x) for {Fmt.Dur(audioTotalSec)} audio");
            //   stdout.WriteLine($"  windows: {windows.Count}");
            if (options.OutPath is not null)
            {
                //   stdout.WriteLine($"  file   : {options.OutPath} ({FormatName(options.Format)})");
            }
        }

        //stdout.WriteLine("\n=== merged transcript (speaker-attributed) ===");
        return merged.ToString();
    }

    

    private static int PlanFromSpeech2(

        CliOptions options,
        DeviceChoice choice,
        PcmSource pcmSource,
        long totalSamples,
        int windowMs,
        out IReadOnlyList<AudioWindow> windows)
    {
        windows = [];

        string vadPath;
        try
        {
            vadPath = ModelStore.Resolve(options.VadSpec!, quant: null, null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            //stderr.WriteLine(ex.Message);
            return 1;
        }

        using var vadModel = Model.Load(vadPath, p => p.WithBackend(choice.Backend));
        if (!vadModel.Roles.HasFlag(TranscribeRole.Vad))
        {
            //stderr.WriteLine($"--vad: {vadModel.Architecture}/{vadModel.Variant} does not serve the VAD role "
            //   + $"(roles: {DescribeRoles(vadModel.Roles)}). Use a VAD model such as silero-vad-v6.2.");
            return 1;
        }

        using var vadSession = vadModel.CreateVadSession();
        VadWindowing.VadRunResult run = VadWindowing.Run(vadSession, pcmSource, options.ChunkSeconds);
        /*stdout.WriteLine(
            $"vad   : {vadModel.Architecture}/{vadModel.Variant}, "
            + $"{run.Segments.Count} speech interval(s), {run.FrameCount} frame(s)");
        */
        windows = SpeechWindows.Plan(
            run.Segments.Select(s => new SpeechInterval(s.StartSample, s.EndSample)).ToArray(),
            totalSamples,
            windowMs);
        // stdout.WriteLine($"windows: {windows.Count} speech window(s) of at most {windowMs / 1000}s (silence skipped)");

        if (windows.Count == 0)
        {
            //stderr.WriteLine("--vad: the VAD model found no speech in this audio; nothing to transcribe.");
            return 1;
        }

        return 0;
    }
  



    /// <summary>The --format spelling, as accepted and as echoed back.</summary>
    internal static string FormatName(TranscriptFormat format) => format switch
    {
        TranscriptFormat.Vtt => "vtt",
        TranscriptFormat.Json => "json",
        TranscriptFormat.Html => "html",
        TranscriptFormat.Srt => "srt",
        TranscriptFormat.Ass => "ass",
        _ => "plain",
    };

    private static StreamWriter? OpenOutput(string? path)
        => path is null
            ? null
            : new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };

    private static bool Has(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Reports the backend the model ACTUALLY landed on (transcribe_model_backend
    // plus the resolved device), not the one that was requested, so "GPU active"
    // is a fact on screen and not an assumption. When the default auto selection
    // ends up on the CPU, that is spelled out: it means no GPU initialized here.
    internal static string DescribeCompute(DeviceChoice choice, Model model)
    {
        string actual = string.IsNullOrEmpty(model.Backend) ? "unknown" : model.Backend;
        BackendDevice? device = model.Device;
        string where = device is null ? string.Empty : $" on {DeviceSelection.Describe(device)}";
        // Only for an automatic choice: an explicit --device 2 (the CPU) is what
        // the user asked for and must not be reported as a fallback.
        string note = choice.Device is null
            && choice.Backend == BackendRequest.BackendAuto
            && string.Equals(device?.Kind, "cpu", StringComparison.OrdinalIgnoreCase)
                ? " (no GPU initialized, CPU fallback)"
                : string.Empty;

        return $"{actual}{where} [requested: {choice.Request}]{note}";
    }
}
