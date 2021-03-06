//
// Author:
//   Aaron Bockover <abock@microsoft.com>
//
// Copyright (c) Aaron Bockover. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

using Mono.Options;
using Mono.Options.Reflection;

using Xamarin.ProcessControl;

using Latham.Project.Model;
using Latham.Project;
using Latham.UniFi;

namespace Latham.Commands
{
    sealed class RecordCommandSet : CommandSet
    {
        public RecordCommandSet() : base("record")
        {
            Add(new ScheduleCommand(this));
            Add(new BackfillCommand(this));
        }

        sealed class ScheduleCommand : IndexCommand
        {
            [Option("d|daemon", "Run as a background daemon")]
            public bool Daemonize { get; set; }

            [Option("start", "Run as a background daemon", Hidden = true)]
            public bool Start { get; set; }

            [Option("stop", "Stop the running background instance")]
            public bool Stop { get; set; }

            [Option(
                "status",
                "Return the status of a possible running background instance. " +
                "If an instance is running, its PID is written to stdout and exits 0. " +
                "Otherwise 'stopped' is writen to stdout and exits 1.")]
            public bool Status { get; set; }

            [Option("ffmpeg=", "Which ffmpeg binary to use. Defaults to 'ffmpeg' from PATH.")]
            public string FFMpegPath { get; set; } = "ffmpeg";

            Daemon? daemon;

            public ScheduleCommand(CommandSet commandSet) : base(
                commandSet,
                "schedule",
                "Run a scheduled recording session as described by the project")
            {
            }

            protected override int Invoke(ProjectInfo project, IngestionIndex index)
            {
                FFMpeg.Path = FFMpegPath;

                project = project.Evaluate();

                daemon = Daemon.Create(
                    Path.Combine(project.BasePath ?? ".", "latham.pid"),
                    () => "running");

                if ((Stop || Status || Daemonize) && !daemon.IsSupported)
                    throw new NotSupportedException("Daemon mode is not supported on this system.");

                if (Status)
                {
                    if (daemon.GetExistingProcess() is Process process)
                    {
                        Console.WriteLine("{PID}", process.Id);
                        return 0;
                    }

                    Console.WriteLine("stopped");
                    return 1;
                }
                else if (Stop)
                {
                    if (daemon.Stop(out var process) && process is object)
                    {
                        Console.WriteLine($"[PID {process.Id}] SIGINT was sent");
                    }
                    else
                    {
                        Console.WriteLine("Record daemon is not running or unable to find PID for it.");
                    }
                    return 0;
                }
                else if (Daemonize)
                {
                    var startAction = daemon.Start(out var process);
                    switch (startAction)
                    {
                        case DaemonStartAction.StartDaemonProcess:
                            process = daemon.StartProcess();
                            Console.WriteLine($"[PID {process.Id}] started");
                            return 0;
                        case DaemonStartAction.AlreadyRunning:
                            Console.WriteLine($"[PID {process.Id}] instance already running");
                            return 1;
                        case DaemonStartAction.StartNormally:
                            break;
                        default:
                            throw new NotImplementedException($"{nameof(DaemonStartAction)}.{startAction}");
                    }
                }

                return RealInvoke(project, index);
            }

            int RealInvoke(ProjectInfo project, IngestionIndex index)
            {
                if (project.Recordings is null)
                    throw new Exception("No recordings configured for this project.");

                var runner = new CrontabRunner();

                foreach (var recording in project.Recordings.Sources)
                {
                    if (!recording.Duration.HasValue || recording.Duration.Value <= TimeSpan.Zero)
                        throw new Exception($"Recording '{recording}' does not specify a duration.");

                    var schedule = (NCrontab.CrontabSchedule?)recording.Schedule;
                    if (schedule is object)
                        runner.AddJob(
                            schedule,
                            (scheduledTime, cancellationToken) => RecordSource(recording, cancellationToken));
                }

                runner.Run();

                return 0;
            }

            async Task RecordSource(
                RecordingSourceInfo recordingSourceInfo,
                CancellationToken cancellationToken)
            {
                string? outputPath = null;

                try
                {
                    if (!recordingSourceInfo.Duration.HasValue)
                        return;

                    outputPath = recordingSourceInfo.CreateOutputPath();

                    await new StreamRecorder(
                        recordingSourceInfo.Uri,
                        outputPath,
                        recordingSourceInfo.Duration.Value)
                        .InvokeAsync()
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.Error(
                        e,
                        "failed to record {URI} to {OutputPath}",
                        recordingSourceInfo.Uri,
                        outputPath);
                }
            }
        }

        sealed class BackfillCommand : IndexCommand
        {
            readonly ConcurrentQueue<(RecordingSourceInfo RecordingSource, IngestionItem Item)> backfillQueue
                = new ConcurrentQueue<(RecordingSourceInfo, IngestionItem)>();

            [Option("t|tolerance=", "An item must exist within +/- tolerance from the item's expected scheduled recording time. Default is 10 seconds.")]
            public TimeSpan Tolerance { get; set; } = TimeSpan.FromSeconds(10);

            [Option("d|dry-run", "Don't actually download any recordings or update the index")]
            public bool DryRun { get; set; }

            public BackfillCommand(CommandSet commandSet) : base(
                commandSet,
                "backfill",
                "Find any missing recordings in an index as computed from a project's schedule.")
            {
            }

            protected override int Invoke(ProjectInfo project, IngestionIndex index)
            {
                project = project.Evaluate(expandPaths: false);

                if (project.Recordings is null || project.Recordings.Sources.Count == 0)
                {
                    Console.Error.WriteLine("No recording sources are configured on this project.");
                    return 1;
                }

                var oldestTimestamp = DateTimeOffset.MaxValue;
                var newestTimestamp = DateTimeOffset.MinValue;

                var items = new LinkedList<IngestionItem>();

                foreach (var item in index.SelectAll())
                {
                    if (item.Timestamp.HasValue && item.Timestamp < oldestTimestamp)
                        oldestTimestamp = item.Timestamp.Value;

                    if (item.Timestamp.HasValue && item.Timestamp > newestTimestamp)
                        newestTimestamp = item.Timestamp.Value;

                    items.AddLast(item);
                }

                foreach (var recordingSource in project.Recordings.Sources)
                {
                    if (!recordingSource.Schedule.HasValue)
                        continue;

                    // Unfortunately NCrontab doesn't work with DateTimeOffset, so normalize
                    // our min and max to UTC. It's very possible that the min could be in one
                    // timezone (e.g. EST) and the max in another (e.g. EDT) ...
                    var expectedRecordingTimes = recordingSource
                        .Schedule
                        .Value
                        .GetNextOccurrences(
                            oldestTimestamp.UtcDateTime,
                            newestTimestamp.UtcDateTime);

                    foreach (var expectedRecordingTime in expectedRecordingTimes)
                    {
                        // ... however, it's desirable to express the timestamps on
                        // disk (file name) and in the index/metadata using local time, but
                        // since the purpose of backfilling is to literally fill in data we
                        // missed, we have to guess a little at computing the offset ...
                        var utcOffset = TimeSpan.Zero;

                        var node = items.First;

                        while (node is object)
                        {
                            var item = node.Value;

                            if (item.Tag == recordingSource.Tag && item.Timestamp.HasValue)
                            {
                                // ... so keep track of the nearest offset before where our data
                                // went missing, and hope that the outage didn't occur during
                                // a timezone change.
                                var timestamp = item.Timestamp.Value.UtcDateTime;
                                utcOffset = item.Timestamp.Value.LocalDateTime - timestamp;

                                if (new DateTimeOffsetRange(
                                    timestamp - Tolerance,
                                    timestamp + Tolerance).Includes(expectedRecordingTime))
                                {
                                    break;
                                }
                            }

                            node = node.Next;
                        }

                        if (node is null)
                        {
                            var itemTime = new DateTimeOffset(
                                expectedRecordingTime.Ticks + utcOffset.Ticks,
                                utcOffset);

                            var outputPath = recordingSource.CreateOutputPath(itemTime);
                            if (outputPath is string)
                                CollectMissing(
                                    recordingSource,
                                    new IngestionItem(
                                        outputPath,
                                        fileSize: null,
                                        tag: recordingSource.Tag,
                                        timestamp: itemTime,
                                        duration: null));
                        }
                        else
                        {
                            var item = node.Value;
                            if (!item.FileSize.HasValue || item.FileSize.Value <= 0)
                            {
                                var outputPath = Path.Combine(project.BasePath ?? ".", item.FilePath);
                                if (!File.Exists(outputPath))
                                    CollectMissing(recordingSource, item);
                            }

                            items.Remove(node);
                        }
                    }
                }

                if (backfillQueue.Count > 0)
                    ProcessBackfillQueue(index).GetAwaiter().GetResult();

                return 0;

                void CollectMissing(RecordingSourceInfo recordingSource, IngestionItem item)
                {
                    if (recordingSource.UnifiProtectEndpoint is null)
                        return;

                    backfillQueue.Enqueue((recordingSource, item));
                }
            }

            async Task ProcessBackfillQueue(
                IngestionIndex index,
                CancellationToken cancellationToken = default)
            {
                while (backfillQueue.Count > 0)
                {
                    if (backfillQueue.TryDequeue(out var recording))
                    {
                        try
                        {
                            var item = await DownloadRecording(
                                recording.RecordingSource,
                                recording.Item,
                                cancellationToken).ConfigureAwait(false);

                            if (!DryRun)
                                index.Insert(item);
                        }
                        catch (Exception e)
                        {
                            Log.Error(
                                e,
                                "unable to download {Recording} from {Source}",
                                recording.Item,
                                recording.RecordingSource);
                        }
                    }
                }
            }

            readonly Dictionary<Uri, ProtectApiClient> apiClients = new Dictionary<Uri, ProtectApiClient>();
            readonly ReaderWriterLockSlim apiClientsLock = new ReaderWriterLockSlim();

            ProtectApiClient? GetProtectApiClient(Uri? endpoint)
            {
                if (endpoint is null)
                    return null;

                ProtectApiClient? apiClient;

                apiClientsLock.EnterUpgradeableReadLock();
                try
                {
                    if (!apiClients.TryGetValue(endpoint, out apiClient))
                    {
                        apiClientsLock.EnterWriteLock();
                        try
                        {
                            apiClient = new ProtectApiClient(endpoint);
                            apiClients.Add(endpoint, apiClient);
                        }
                        finally
                        {
                            apiClientsLock.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    apiClientsLock.ExitUpgradeableReadLock();
                }

                return apiClient;
            }

            int downloadIndex;

            async Task<IngestionItem?> DownloadRecording(
                RecordingSourceInfo recordingSource,
                IngestionItem item,
                CancellationToken cancellationToken)
            {
                var apiClient = GetProtectApiClient(recordingSource.UnifiProtectEndpoint);
                if (apiClient is null)
                    return null;

                var protect = await apiClient.BootstrapAsync(cancellationToken);
                if (protect is null)
                    return null;

                var (camera, channel) = protect
                    .Cameras
                    .SelectMany(camera => camera.Channels.Select(
                        channel => (camera, channel)))
                    .FirstOrDefault(pair => pair.channel.IsRtspEnabled &&
                        pair.channel.RtspAlias == Path.GetFileName(recordingSource.Uri.LocalPath));

                if (camera is null ||
                    channel is null ||
                    !item.Timestamp.HasValue ||
                    !recordingSource.Duration.HasValue)
                    return null;

                var basePath = recordingSource.Project?.BasePath ?? ".";
                var fullPath = Path.Combine(basePath, item.FilePath);

                Console.WriteLine($"[{++downloadIndex}] {(DryRun ? "" : "Downloading ")}{fullPath}");

                if (!DryRun)
                {
                    var downloadPath = fullPath + ".download";

                    File.Delete(downloadPath);

                    await apiClient.DownloadVideoAsync(
                        camera,
                        channel,
                        item.Timestamp.Value,
                        recordingSource.Duration.Value,
                        downloadPath,
                        cancellationToken);

                    File.Delete(fullPath);

                    var execStatus = await FFMpeg.RunAsync(
                        cancellationToken,
                        "-i", downloadPath,
                        "-vcodec", "copy",
                        "-acodec", "copy",
                        fullPath);

                    File.Delete(downloadPath);

                    item = item.WithDurationAndFileSizeByReadingFile(basePath);

                    Console.WriteLine("        size: {0}, duration: {1}", item.FileSize, item.Duration);
                }

                return item;
            }
        }
    }
}
