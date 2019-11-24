using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Autofac;
using CKAN.Configuration;
using log4net;

namespace CKAN
{
    /// <summary>
    /// Download lots of files at once!
    /// </summary>
    public class NetAsyncDownloader
    {

        public IUser User { get; set; }

        // Private utility class for tracking downloads
        private class NetAsyncDownloaderDownloadPart
        {
            public Uri url;
            public Uri fallbackUrl;
            public DateTime lastProgressUpdateTime;
            public string path;
            public long bytesLeft;
            public long size;
            public int bytesPerSecond;
            public bool triedFallback;
            public Exception error;
            public int lastProgressUpdateSize;

            public event DownloadProgressChangedEventHandler Progress;
            public event AsyncCompletedEventHandler          Done;

            private string mimeType;
            private WebClient agent;

            public NetAsyncDownloaderDownloadPart(Net.DownloadTarget target, string path = null)
            {
                this.url = target.url;
                this.fallbackUrl = target.fallbackUrl;
                this.mimeType = target.mimeType;
                this.triedFallback = false;
                this.path = path ?? Path.GetTempFileName();
                this.size = bytesLeft = target.size;
                this.lastProgressUpdateTime = DateTime.Now;
            }

            public void Download(Uri url, string path)
            {
                ResetAgent();
                agent.DownloadFileAsync(url, path);
            }

            public void Abort()
            {
                agent?.CancelAsync();
            }

            private void ResetAgent()
            {
                agent = new WebClient();

                agent.Headers.Add("User-Agent", Net.UserAgentString);

                // Tell the server what kind of files we want
                if (!string.IsNullOrEmpty(mimeType))
                {
                    log.InfoFormat("Setting MIME type {0}", mimeType);
                    agent.Headers.Add("Accept", mimeType);
                }

                // Check whether to use an auth token for this host
                string token;
                if (ServiceLocator.Container.Resolve<IConfiguration>().TryGetAuthToken(this.url.Host, out token)
                        && !string.IsNullOrEmpty(token))
                {
                    log.InfoFormat("Using auth token for {0}", this.url.Host);
                    // Send our auth token to the GitHub API (or whoever else needs one)
                    agent.Headers.Add("Authorization", $"token {token}");
                }

                // Forward progress and completion events to our listeners
                agent.DownloadProgressChanged += (sender, args) => {
                    if (Progress != null)
                    {
                        Progress(sender, args);
                    }
                };
                agent.DownloadFileCompleted += (sender, args) => {
                    if (Done != null)
                    {
                        Done(sender, args);
                    }
                };
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof (NetAsyncDownloader));

        private List<NetAsyncDownloaderDownloadPart> downloads;
        private int completed_downloads;

        //Used for inter-thread communication.
        private volatile bool download_canceled;
        private readonly ManualResetEvent complete_or_canceled;

        public delegate void NetAsyncOneCompleted(Uri url, string filename, Exception error);
        public NetAsyncOneCompleted onOneCompleted;

        /// <summary>
        /// Returns a perfectly boring NetAsyncDownloader.
        /// </summary>
        public NetAsyncDownloader(IUser user)
        {
            User = user;
            downloads = new List<NetAsyncDownloaderDownloadPart>();
            complete_or_canceled = new ManualResetEvent(false);
        }

        /// <summary>
        /// Downloads our files, returning an array of filenames that we're writing to.
        /// The sole argument is a collection of DownloadTargets.
        /// The .onCompleted delegate will be called on completion.
        /// </summary>
        private void Download(ICollection<Net.DownloadTarget> targets)
        {
            downloads.Clear();
            foreach (Net.DownloadTarget target in targets)
            {
                DownloadModule(target);
            }
        }

        private void DownloadModule(Net.DownloadTarget target)
        {
            // We need a new variable for our closure/lambda, hence index = 1+prev max
            int index = downloads.Count;

            var dl = new NetAsyncDownloaderDownloadPart(target);
            downloads.Add(dl);

            // Encode spaces to avoid confusing URL parsers
            User.RaiseMessage("Downloading \"{0}\"",
                dl.url.ToString().Replace(" ", "%20"));

            // Schedule for us to get back progress reports.
            dl.Progress += (sender, args) =>
                FileProgressReport(index,
                    args.ProgressPercentage,
                    args.BytesReceived,
                    args.TotalBytesToReceive);

            // And schedule a notification if we're done (or if something goes wrong)
            dl.Done += (sender, args) =>
                FileDownloadComplete(index, args.Error);

            // Start the download!
            dl.Download(dl.url, dl.path);
        }

        /// <summary>
        /// Start a new batch of downloads
        /// </summary>
        /// <param name="urls">The downloads to begin</param>
        public void DownloadAndWait(ICollection<Net.DownloadTarget> urls)
        {
            if (downloads.Count > completed_downloads)
            {
                // Some downloads are still in progress, add to the current batch
                foreach (Net.DownloadTarget target in urls)
                {
                    DownloadModule(target);
                }
                // Wait for completion along with original caller
                // so we can handle completion tasks for the added mods
                complete_or_canceled.WaitOne();
                return;
            }

            completed_downloads = 0;
            // Make sure we are ready to start a fresh batch
            complete_or_canceled.Reset();

            // Start the download!
            Download(urls);

            log.Debug("Waiting for downloads to finish...");
            complete_or_canceled.WaitOne();

            var old_download_canceled = download_canceled;
            // Set up the inter-thread comms for next time. Can not be done at the start
            // of the method as the thread could pause on the opening line long enough for
            // a user to cancel.

            download_canceled = false;
            complete_or_canceled.Reset();


            // If the user cancelled our progress, then signal that.
            if (old_download_canceled)
            {
                // Abort all our traditional downloads, if there are any.
                foreach (var download in downloads.ToList())
                {
                    download.Abort();
                }

                // Signal to the caller that the user cancelled the download.
                throw new CancelledActionKraken("Download cancelled by user");
            }

            // Check to see if we've had any errors. If so, then release the kraken!
            List<KeyValuePair<int, Exception>> exceptions = new List<KeyValuePair<int, Exception>>();
            for (int i = 0; i < downloads.Count; ++i)
            {
                if (downloads[i].error != null)
                {
                    // Check if it's a certificate error. If so, report that instead,
                    // as this is common (and user-fixable) under Linux.
                    if (downloads[i].error is WebException)
                    {
                        WebException wex = downloads[i].error as WebException;
                        if (certificatePattern.IsMatch(wex.Message))
                        {
                            throw new MissingCertificateKraken();
                        }
                        else switch ((wex.Response as HttpWebResponse)?.StatusCode)
                        {
                            // Handle HTTP 403 used for throttling
                            case HttpStatusCode.Forbidden:
                                Uri infoUrl;
                                if (Net.ThrottledHosts.TryGetValue(downloads[i].url.Host, out infoUrl))
                                {
                                    throw new DownloadThrottledKraken(downloads[i].url, infoUrl);
                                }
                                break;
                        }
                    }
                    // Otherwise just note the error and which download it came from,
                    // then throw them all at once later.
                    exceptions.Add(new KeyValuePair<int, Exception>(i, downloads[i].error));
                }
            }
            if (exceptions.Count > 0)
            {
                throw new DownloadErrorsKraken(exceptions);
            }

            // Yay! Everything worked!
        }

        private static readonly Regex certificatePattern = new Regex(
            @"authentication or decryption has failed",
            RegexOptions.Compiled
        );

        /// <summary>
        /// <see cref="IDownloader.CancelDownload()"/>
        /// This will also call onCompleted with all null arguments.
        /// </summary>
        public void CancelDownload()
        {
            log.Info("Cancelling download");
            download_canceled = true;
            triggerCompleted();
        }

        private void triggerCompleted()
        {
            // Signal that we're done.
            complete_or_canceled.Set();
        }

        /// <summary>
        /// Generates a download progress reports, and sends it to
        /// onProgressReport if it's set. This takes the index of the file
        /// being downloaded, the percent complete, the bytes downloaded,
        /// and the total amount of bytes we expect to download.
        /// </summary>
        private void FileProgressReport(int index, int percent, long bytesDownloaded, long bytesToDownload)
        {
            if (download_canceled)
            {
                return;
            }

            NetAsyncDownloaderDownloadPart download = downloads[index];

            DateTime now = DateTime.Now;
            TimeSpan timeSpan = now - download.lastProgressUpdateTime;
            if (timeSpan.Seconds >= 3.0)
            {
                long bytesChange = bytesDownloaded - download.lastProgressUpdateSize;
                download.lastProgressUpdateSize = (int) bytesDownloaded;
                download.lastProgressUpdateTime = now;
                download.bytesPerSecond = (int) bytesChange/timeSpan.Seconds;
            }

            download.size = bytesToDownload;
            download.bytesLeft = download.size - bytesDownloaded;
            downloads[index] = download;

            int totalBytesPerSecond = 0;
            long totalBytesLeft = 0;
            long totalSize = 0;

            foreach (NetAsyncDownloaderDownloadPart t in downloads.ToList())
            {
                if (t.bytesLeft > 0)
                {
                    totalBytesPerSecond += t.bytesPerSecond;
                }

                totalBytesLeft += t.bytesLeft;
                totalSize += t.size;
            }

            int totalPercentage = (int)(((totalSize - totalBytesLeft) * 100) / (totalSize));

            if (!download_canceled)
            {
                // Math.Ceiling was added to avoid showing 0 MiB left when finishing
                User.RaiseProgress(
                    String.Format("{0}/sec - downloading - {1} left",
                        CkanModule.FmtSize(totalBytesPerSecond),
                        CkanModule.FmtSize(totalBytesLeft)),
                    totalPercentage);
            }
        }

        /// <summary>
        /// This method gets called back by `WebClient` when a download is completed.
        /// It in turncalls the onCompleted hook when *all* downloads are finished.
        /// </summary>
        private void FileDownloadComplete(int index, Exception error)
        {
            if (error != null)
            {
                log.InfoFormat("Error downloading {0}: {1}", downloads[index].url, error);

                // Check whether we were already downloading the fallback url
                if (!downloads[index].triedFallback && downloads[index].fallbackUrl != null)
                {
                    log.InfoFormat("Trying fallback URL: {0}", downloads[index].fallbackUrl);
                    // Encode spaces to avoid confusing URL parsers
                    User.RaiseMessage("Failed to download \"{0}\", trying fallback \"{1}\"",
                        downloads[index].url.ToString().Replace(" ", "%20"),
                        downloads[index].fallbackUrl.ToString().Replace(" ", "%20")
                    );
                    // Try the fallbackUrl
                    downloads[index].triedFallback = true;
                    downloads[index].Download(downloads[index].fallbackUrl, downloads[index].path);
                    // Short circuit the completion process so the fallback can run
                    return;
                }
                else
                {
                    downloads[index].error = error;
                }
            }
            else
            {
                log.InfoFormat("Finished downloading {0}", downloads[index].url);
            }
            onOneCompleted.Invoke(downloads[index].url, downloads[index].path, downloads[index].error);

            if (++completed_downloads >= downloads.Count)
            {
                triggerCompleted();
            }
        }
    }
}
