﻿using MediaBrowser.Common.Logging;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Net.Handlers;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace MediaBrowser.Api.HttpHandlers
{
    public abstract class BaseMediaHandler<T> : BaseHandler
        where T : BaseItem, new()
    {
        /// <summary>
        /// Supported values: mp3,flac,ogg,wav,asf,wma,aac
        /// </summary>
        protected virtual IEnumerable<string> OutputFormats
        {
            get
            {
                return QueryString["outputformats"].Split(',');
            }
        }

        /// <summary>
        /// These formats can be outputted directly but cannot be encoded to
        /// </summary>
        protected virtual IEnumerable<string> UnsupportedOutputEncodingFormats
        {
            get
            {
                return new string[] { };
            }
        }

        private T _LibraryItem;
        /// <summary>
        /// Gets the library item that will be played, if any
        /// </summary>
        protected T LibraryItem
        {
            get
            {
                if (_LibraryItem == null)
                {
                    string id = QueryString["id"];

                    if (!string.IsNullOrEmpty(id))
                    {
                        _LibraryItem = Kernel.Instance.GetItemById(Guid.Parse(id)) as T;
                    }
                }

                return _LibraryItem;
            }
        }

        public int? AudioChannels
        {
            get
            {
                string val = QueryString["audiochannels"];

                if (string.IsNullOrEmpty(val))
                {
                    return null;
                }

                return int.Parse(val);
            }
        }

        public int? AudioSampleRate
        {
            get
            {
                string val = QueryString["audiosamplerate"];

                if (string.IsNullOrEmpty(val))
                {
                    return 44100;
                }

                return int.Parse(val);
            }
        }

        public override Task<string> GetContentType()
        {
            return Task.FromResult<string>(MimeTypes.GetMimeType("." + GetConversionOutputFormat()));
        }

        public override bool ShouldCompressResponse(string contentType)
        {
            return false;
        }

        public override Task ProcessRequest(HttpListenerContext ctx)
        {
            HttpListenerContext = ctx;

            if (!RequiresConversion())
            {
                return new StaticFileHandler() { Path = LibraryItem.Path }.ProcessRequest(ctx);
            }
            else
            {
                return base.ProcessRequest(ctx);
            }
        }

        protected abstract string GetCommandLineArguments();

        /// <summary>
        /// Gets the format we'll be converting to
        /// </summary>
        protected virtual string GetConversionOutputFormat()
        {
            return OutputFormats.First(f => !UnsupportedOutputEncodingFormats.Any(s => s.Equals(f, StringComparison.OrdinalIgnoreCase)));
        }

        protected virtual bool RequiresConversion()
        {
            string currentFormat = Path.GetExtension(LibraryItem.Path).Replace(".", string.Empty);

            if (OutputFormats.Any(f => currentFormat.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
            {
                // We can output these files directly, but we can't encode them
                if (UnsupportedOutputEncodingFormats.Any(f => currentFormat.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            else
            {
                // If it's not in a format the consumer accepts, return true
                return true;
            }

            return false;
        }

        private FileStream LogFileStream { get; set; }

        protected async override Task WriteResponseToOutputStream(Stream stream)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();

            startInfo.CreateNoWindow = true;

            startInfo.UseShellExecute = false;

            // Must consume both or ffmpeg may hang due to deadlocks. See comments below.
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            startInfo.FileName = Kernel.Instance.ApplicationPaths.FFMpegPath;
            startInfo.WorkingDirectory = Kernel.Instance.ApplicationPaths.FFMpegDirectory;
            startInfo.Arguments = GetCommandLineArguments();

            Logger.LogInfo(startInfo.FileName + " " + startInfo.Arguments);

            Process process = new Process();
            process.StartInfo = startInfo;

            // FFMpeg writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
            LogFileStream = new FileStream(Path.Combine(Kernel.Instance.ApplicationPaths.LogDirectoryPath, "ffmpeg-" + Guid.NewGuid().ToString() + ".txt"), FileMode.Create);

            process.EnableRaisingEvents = true;

            process.Exited += process_Exited;

            try
            {
                process.Start();

                // MUST read both stdout and stderr asynchronously or a deadlock may occurr

                // Kick off two tasks
                Task mediaTask = process.StandardOutput.BaseStream.CopyToAsync(stream);
                Task debugLogTask = process.StandardError.BaseStream.CopyToAsync(LogFileStream);

                await mediaTask.ConfigureAwait(false);
                //await debugLogTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);

                // Hate having to do this
                try
                {
                    process.Kill();
                }
                catch
                {
                }
            }
        }

        void process_Exited(object sender, EventArgs e)
        {
            if (LogFileStream != null)
            {
                LogFileStream.Dispose();
            }

            Process process = sender as Process;

            Logger.LogInfo("FFMpeg exited with code " + process.ExitCode);

            process.Dispose();
        }

        /// <summary>
        /// Gets the number of audio channels to specify on the command line
        /// </summary>
        protected int? GetNumAudioChannelsParam(int libraryItemChannels)
        {
            // If the user requested a max number of channels
            if (AudioChannels.HasValue)
            {
                // Only specify the param if we're going to downmix
                if (AudioChannels.Value < libraryItemChannels)
                {
                    return AudioChannels.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the number of audio channels to specify on the command line
        /// </summary>
        protected int? GetSampleRateParam(int libraryItemSampleRate)
        {
            // If the user requested a max value
            if (AudioSampleRate.HasValue)
            {
                // Only specify the param if we're going to downmix
                if (AudioSampleRate.Value < libraryItemSampleRate)
                {
                    return AudioSampleRate.Value;
                }
            }

            return null;
        }
    }
}
