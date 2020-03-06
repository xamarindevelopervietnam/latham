//
// Author:
//   Aaron Bockover <abock@microsoft.com>
//
// Copyright (c) Aaron Bockover. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Xamarin.ProcessControl;

namespace Latham
{
    public sealed class StreamRecorder
    {
        public Uri InputUri { get; }
        public string OutputFile { get; }
        public TimeSpan Duration { get; }

        public StreamRecorder(
            Uri? inputUri,
            string? outputFile,
            TimeSpan duration)
        {
            InputUri = inputUri
                ?? throw new ArgumentNullException(nameof(inputUri));
            OutputFile = outputFile
                ?? throw new ArgumentNullException(nameof(outputFile));
            Duration = duration;
        }

        public async Task InvokeAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));

            var execStatus = await Exec.RunAsync(
                output => {
                    Console.Write(output.Data);
                },
                "ffmpeg",
                "-hide_banner",
                "-rtsp_transport", "tcp",
                "-i", InputUri.ToString(),
                "-c", "copy",
                "-flags", "+global_header",
                "-t", Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                "-reset_timestamps", "1",
                OutputFile);

            if (execStatus.ExitCode != 0)
                throw new Exception(
                    $"ffmpeg exited {execStatus.ExitCode} when attempting to record " +
                    $"{InputUri} to {OutputFile} for {Duration}.");
        }
    }
}
