using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimatedCovers;
using AsyncKeyedLock;
// using ImageMagick;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo
{
    public class AnimatedImageProvider : IDynamicImageProvider, IHasOrder, IDisposable
    {
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly object _runningProcessesLock = new object();
        private readonly List<ProcessWrapper> _runningProcesses = new List<ProcessWrapper>();
        private readonly AsyncNonKeyedLocker _thumbnailResourcePool;
        public AnimatedImageProvider(IMediaSourceManager mediaSourceManager, IMediaEncoder mediaEncoder)
        {
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _thumbnailResourcePool = new(4);
        }

        public string Name => "Animated Cover";

        public int Order => 80;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary, ImageType.Thumb };
        }

        public bool Supports(BaseItem item)
        {
            if (item.IsShortcut)
            {
                return false;
            }

            if (!item.IsFileProtocol)
            {
                return false;
            }

            return item is Video video && !video.IsPlaceHolder && video.IsCompleteMedia;
        }

        public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
        {
            var video = (Video)item;

            // No support for these
            if (video.IsPlaceHolder || video.VideoType == VideoType.Dvd || video.VideoType == VideoType.BluRay || video.VideoType == VideoType.Iso || video.Video3DFormat is not null)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            // Can't extract if we didn't find a video stream in the file
            if (!video.DefaultVideoStreamIndex.HasValue)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            var totalMilliseconds = TimeSpan.FromTicks(0).TotalMilliseconds;

            if (video.RunTimeTicks != null)
            {
                totalMilliseconds = TimeSpan.FromTicks(video.RunTimeTicks.Value).TotalMilliseconds;
            }

            MediaSourceInfo mediaSource = new MediaSourceInfo
            {
                VideoType = video.VideoType,
                IsoType = video.IsoType,
                Protocol = video.PathProtocol ?? MediaProtocol.File,
            };

            var query = new MediaStreamQuery { ItemId = video.Id, Index = video.DefaultVideoStreamIndex };
            var videoStream = _mediaSourceManager.GetMediaStreams(query).FirstOrDefault();
            if (videoStream is null)
            {
                query.Type = MediaStreamType.Video;
                query.Index = null;
                videoStream = _mediaSourceManager.GetMediaStreams(query).FirstOrDefault();
            }

            if (videoStream is null)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            return this.GetVideoImage(item.Path, video.Container, videoStream, totalMilliseconds, cancellationToken);
        }

        private async Task<DynamicImageResponse> GetVideoImage(string inputPath, string container, MediaStream videoStream, double totalMilliseconds, CancellationToken cancellationToken)
        {
            var result = await this.ProduceAnimatedImage(inputPath, container, videoStream, totalMilliseconds, cancellationToken).ConfigureAwait(false);

            return new DynamicImageResponse
            {
                Format = ImageFormat.Jpg,
                HasImage = true,
                Path = result,
                Protocol = MediaProtocol.File
            };
        }

        private async Task<string> ProduceAnimatedImage(string inputPath, string container, MediaStream videoStream, double totalMilliseconds, CancellationToken cancellationToken)
        {
            var tempExtractPath = Path.Combine(Plugin.Instance.AppPaths.TempDirectory, Guid.NewGuid().ToString(), "output.webp");
            var dir = Path.GetDirectoryName(tempExtractPath);
            if (dir is null)
            {
                throw new ArgumentNullException(inputPath, "Path cannot be invalid");
            }

            Directory.CreateDirectory(dir);

            // deint -> scale -> thumbnail -> tonemap.
            // put the SW tonemap right after the thumbnail to do it only once to reduce cpu usage.
            var filters = new List<string>();

            // deinterlace using bwdif algorithm for video stream.
            if (videoStream is not null && videoStream.IsInterlaced)
            {
                filters.Add("bwdif=0:-1:0");
            }

            // Use SW tonemap on HDR10/HLG video stream only when the zscale filter is available.
            if ((string.Equals(videoStream?.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoStream?.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
                && _mediaEncoder.SupportsFilter("zscale"))
            {
                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709,format=yuv420p");
            }

            var offset = TimeSpan.FromMilliseconds(totalMilliseconds / 10);

            string args = string.Empty;
            int parts = 10;
            var duration = TimeSpan.FromSeconds(2);
            int fps = 15;
            string filter_complex = string.Empty;
            string concat = string.Empty;
            for (int i = 1; i <= parts; i++)
            {
                double pos = (totalMilliseconds / parts) * i;
                args += string.Format(CultureInfo.InvariantCulture, " -ss {0} -t {1} -i \"{2}\"", _mediaEncoder.GetTimeParameter(TimeSpan.FromMilliseconds(pos).Ticks), _mediaEncoder.GetTimeParameter(duration.Ticks), inputPath);
                concat += "[" + (i - 1) + ":0]";
            }

            filter_complex += concat + "concat=n=" + parts + ":v=1:a=0[out],[out]fps=" + fps + "[out1]";

            args += string.Format(CultureInfo.InvariantCulture, " -filter_complex \"{0}\"", filter_complex);
            args += string.Format(CultureInfo.InvariantCulture, " -threads {0}", 2);
            args += string.Format(CultureInfo.InvariantCulture, " -vcodec libwebp");
            args += string.Format(CultureInfo.InvariantCulture, " -lossless 0 -compression_level 4 -q:v {0} -loop 1 -preset picture -an -vsync 0", 30);
            args += string.Format(CultureInfo.InvariantCulture, " -v quiet");
            args += string.Format(CultureInfo.InvariantCulture, " -s {0}", "426x240");
            args += string.Format(CultureInfo.InvariantCulture, " -map [out1] \"{0}\"", tempExtractPath);

            if (!string.IsNullOrWhiteSpace(container))
            {
                var inputFormat = EncodingHelper.GetInputFormat(container);
                if (!string.IsNullOrWhiteSpace(inputFormat))
                {
                    args = "-f " + inputFormat + " " + args;
                }
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = args,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                },
                EnableRaisingEvents = true
            };

            Plugin.Log.LogInformation("{ProcessFileName} {ProcessArguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

            using (var processWrapper = new ProcessWrapper(process, this))
            {
                bool ranToCompletion;

                using (await _thumbnailResourcePool.LockAsync(cancellationToken).ConfigureAwait(false))
                {
                    StartProcess(processWrapper);

                    try
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        ranToCompletion = true;
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(true);
                        ranToCompletion = false;
                    }
                }

                var exitCode = ranToCompletion ? processWrapper.ExitCode ?? 0 : -1;

                if (exitCode == -1 || !File.Exists(tempExtractPath))
                {
                    // Plugin.Log.LogError(string.Concat("ffmpeg image extraction failed for ", inputPath));

                    throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "ffmpeg image extraction failed for {0}", inputPath));
                }

                return tempExtractPath;
            }
        }

        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                StopProcesses();
                _thumbnailResourcePool.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void StartProcess(ProcessWrapper process)
        {
            process.Process.Start();

            lock (_runningProcessesLock)
            {
                _runningProcesses.Add(process);
            }
        }

        private void StopProcess(ProcessWrapper process, int waitTimeMs)
        {
            try
            {
                if (process.Process.WaitForExit(waitTimeMs))
                {
                    return;
                }

                process.Process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process has already exited or
                // there is no process associated with this Process object.
            }
            catch (Exception)
            {
            }
        }

        private void StopProcesses()
        {
            List<ProcessWrapper> proceses;
            lock (_runningProcessesLock)
            {
                proceses = _runningProcesses.ToList();
                _runningProcesses.Clear();
            }

            foreach (var process in proceses)
            {
                if (!process.HasExited)
                {
                    StopProcess(process, 500);
                }
            }
        }

        private sealed class ProcessWrapper : IDisposable
        {
            private readonly AnimatedImageProvider _mediaEncoder;

            private bool _disposed = false;

            public ProcessWrapper(Process process, AnimatedImageProvider mediaEncoder)
            {
                Process = process;
                _mediaEncoder = mediaEncoder;
                Process.Exited += OnProcessExited;
            }

            public Process Process { get; }

            public bool HasExited { get; private set; }

            public int? ExitCode { get; private set; }

            private void OnProcessExited(object sender, EventArgs e)
            {
                var process = (Process)sender;

                HasExited = true;

                try
                {
                    ExitCode = process.ExitCode;
                }
                catch
                {
                }

                DisposeProcess(process);
            }

            private void DisposeProcess(Process process)
            {
                lock (_mediaEncoder._runningProcessesLock)
                {
                    _mediaEncoder._runningProcesses.Remove(this);
                }

                process.Dispose();
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    if (Process is not null)
                    {
                        Process.Exited -= OnProcessExited;
                        DisposeProcess(Process);
                    }
                }

                _disposed = true;
            }
        }
    }
}
