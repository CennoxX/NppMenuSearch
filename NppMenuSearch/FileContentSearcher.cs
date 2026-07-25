using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace NppMenuSearch
{
    /// <summary>
    /// A single content match: an open file whose text contains the search term.
    /// </summary>
    public class SearchResultItem
    {
        public string FullFileName { get; set; }
        public string DisplayName { get; set; }
        public int LineNumber { get; set; }      // 1-based line of the first match
        public string LinePreview { get; set; }  // trimmed text of that line
        public int MatchCount { get; set; }

        public string ToolTipText
        {
            get { return string.Format("{0}\nLine {1}: {2}", FullFileName, LineNumber, LinePreview); }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// Searches the contents of the files Notepad++ currently has open.
    ///
    /// Instead of pulling the text out of each Scintilla buffer (which would also miss unsaved
    /// changes unless we round-tripped through the plugin messaging API), we read the text that
    /// Notepad++'s session snapshot already persists to disk: <c>session.xml</c> lists the open
    /// files and, for buffers with unsaved changes, a <c>backupFilePath</c> pointing at a snapshot
    /// under <c>%AppData%\Notepad++\backup</c>. Reading the backup snapshot therefore gives us the
    /// current (including unsaved) content of each file.
    ///
    /// This only works while "Enable session snapshot and periodic backup" is turned on
    /// (Settings &gt; Preferences &gt; Backup); otherwise the snapshots do not exist and
    /// <see cref="BackupSnapshotEnabled"/> stays <c>false</c>, so no content search is offered.
    ///
    /// The actual scan runs on a background thread and is cancellable: each new search cancels the
    /// previous one, so typing does not pile up work or block the UI thread.
    /// </summary>
    class FileContentSearcher
    {
        class Document
        {
            public string FullFileName;
            public string DisplayName;
            public string Content;
        }

        // Files are loaded once per popup session (from session.xml + the backup snapshots) and
        // reused across keystrokes. The load is tied to the popup lifetime rather than to a single
        // search, so cancelling a search does not throw the already-read text away.
        class LoadState
        {
            public List<string> OpenFiles = new List<string>();
            public List<Document> Documents;
            public readonly ManualResetEvent Ready = new ManualResetEvent(false);
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public bool Started;
        }

        // Skip files larger than this to avoid choking on huge logs etc.
        const long MaxFileSize = 20 * 1024 * 1024;
        const int MaxLinePreview = 200;

        // Upper bound on match-lines collected per search, so a very common term across large files
        // can't blow up memory. The popup only ever displays a small slice of these anyway.
        const int MaxTotalMatches = 1000;

        readonly string nppConfigDir;
        readonly object sync = new object();

        LoadState state;
        CancellationTokenSource currentSearchCts;

        public bool BackupSnapshotEnabled { get; private set; }

        public FileContentSearcher(string nppConfigDir)
        {
            this.nppConfigDir = nppConfigDir;
            Refresh(null);
        }

        /// <summary>
        /// Re-reads the backup setting and drops the cached file contents. Call this whenever the
        /// results popup is shown so the next search reflects the current session and settings.
        /// </summary>
        /// <param name="openFiles">
        /// The full names of the files Notepad++ currently has open (from the live plugin API). The
        /// search is driven off this list so that freshly-opened, unedited files are included even
        /// when Notepad++ has not written them to session.xml yet.
        /// </param>
        public void Refresh(IEnumerable<string> openFiles)
        {
            CancelCurrentSearch();

            lock (sync)
            {
                if (state != null)
                {
                    try { state.Cts.Cancel(); } catch { }
                }
                state = new LoadState
                {
                    OpenFiles = (openFiles == null) ? new List<string>() : openFiles.ToList()
                };
            }

            BackupSnapshotEnabled =
                !string.IsNullOrEmpty(nppConfigDir) &&
                ReadBackupSnapshotEnabled(nppConfigDir);
        }

        /// <summary>Cancels the running search (if any), e.g. when the popup is hidden.</summary>
        public void Cancel()
        {
            CancelCurrentSearch();
        }

        void CancelCurrentSearch()
        {
            CancellationTokenSource cts;
            lock (sync)
            {
                cts = currentSearchCts;
                currentSearchCts = null;
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        /// <summary>
        /// Starts a background scan for <paramref name="term"/>. When it finishes,
        /// <paramref name="onCompleted"/> is invoked on the background thread with the same
        /// <paramref name="generation"/> value, so the caller can drop results from a stale search.
        /// A cancelled search never calls back.
        /// </summary>
        public void BeginSearch(string term, int generation, Action<int, List<SearchResultItem>> onCompleted)
        {
            CancelCurrentSearch();

            if (!BackupSnapshotEnabled || string.IsNullOrEmpty(term))
                return;

            LoadState st;
            CancellationTokenSource cts = new CancellationTokenSource();
            lock (sync)
            {
                currentSearchCts = cts;
                st = state;
                if (!st.Started)
                {
                    st.Started = true;
                    LoadState loaderState = st;
                    Thread loader = new Thread(() => LoaderProc(loaderState))
                    {
                        IsBackground = true,
                        Name = "NppMenuSearch content loader"
                    };
                    loader.Start();
                }
            }

            CancellationToken token = cts.Token;
            Thread searchThread = new Thread(() =>
            {
                try
                {
                    int signaled = WaitHandle.WaitAny(new WaitHandle[] { st.Ready, token.WaitHandle });
                    if (signaled != 0)
                        return; // cancelled before the documents were ready

                    List<Document> docs = st.Documents;
                    List<SearchResultItem> results = (docs == null)
                        ? new List<SearchResultItem>()
                        : ScanDocuments(docs, term, token);

                    if (token.IsCancellationRequested)
                        return;

                    onCompleted(generation, results);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
#if DEBUG
                    Console.WriteLine(ex);
#endif
                }
            })
            {
                IsBackground = true,
                Name = "NppMenuSearch content search"
            };
            searchThread.Start();
        }

        void LoaderProc(LoadState st)
        {
            List<Document> loaded = null;
            try
            {
                loaded = LoadDocuments(st.OpenFiles, st.Cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
#endif
            }
            finally
            {
                st.Documents = loaded;
                st.Ready.Set();
            }
        }

        List<Document> LoadDocuments(List<string> openFiles, CancellationToken token)
        {
            var list = new List<Document>();

            // session.xml only tells us where the unsaved-change snapshots live; the set of files we
            // actually search comes from the live open-files list, so unedited files that Notepad++
            // has not written to session.xml yet are searched too (read directly from disk).
            Dictionary<string, string> backupByName = BuildBackupMap();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in openFiles)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    continue;

                // Prefer the snapshot (it has unsaved changes); otherwise read the file from disk.
                string contentPath = null;
                string backup;
                if (backupByName.TryGetValue(name, out backup) && !string.IsNullOrEmpty(backup) && File.Exists(backup))
                    contentPath = backup;
                else if (File.Exists(name))
                    contentPath = name;

                if (contentPath == null)
                    continue;

                try
                {
                    var info = new FileInfo(contentPath);
                    if (info.Length > MaxFileSize)
                        continue;

                    list.Add(new Document
                    {
                        FullFileName = name,
                        DisplayName = GetDisplayName(name),
                        Content = ReadAllTextShared(contentPath),
                    });
                }
                catch (Exception ex)
                {
#if DEBUG
                    Console.WriteLine(ex);
#endif
                }
            }

            return list;
        }

        // Maps an open file's name to its unsaved-change snapshot under %AppData%\Notepad++\backup,
        // as recorded in session.xml. Only files with pending unsaved changes have such a snapshot.
        Dictionary<string, string> BuildBackupMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sessionFile = Path.Combine(nppConfigDir, "session.xml");
                if (!File.Exists(sessionFile))
                    return map;

                var doc = new XmlDocument();
                using (var fs = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    doc.Load(fs);

                var nodes = doc.SelectNodes("/NotepadPlus/Session/mainView/File | /NotepadPlus/Session/subView/File");
                if (nodes == null)
                    return map;

                foreach (XmlElement file in nodes.OfType<XmlElement>())
                {
                    string filename = file.GetAttribute("filename");
                    string backupFilePath = file.HasAttribute("backupFilePath") ? file.GetAttribute("backupFilePath") : "";

                    if (!string.IsNullOrEmpty(filename) && !string.IsNullOrEmpty(backupFilePath))
                        map[filename] = backupFilePath;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
#endif
            }
            return map;
        }

        static List<SearchResultItem> ScanDocuments(List<Document> docs, string term, CancellationToken token)
        {
            var results = new List<SearchResultItem>();
            foreach (var doc in docs)
            {
                token.ThrowIfCancellationRequested();

                if (results.Count >= MaxTotalMatches)
                    break;

                AddMatches(results, doc, term, token);
            }
            return results;
        }

        // Emits one result per matching line (several matches on the same line are folded into a
        // single entry with a count). The line number is tracked incrementally as we walk the
        // matches so we don't rescan the file from the start for every hit.
        static void AddMatches(List<SearchResultItem> results, Document doc, string term, CancellationToken token)
        {
            string content = doc.Content;

            int searchPos = 0;
            int scanPos = 0;                 // how far we've counted newlines
            int lineNumber = 1;              // 1-based line number at scanPos
            int lastEmittedLineStart = -1;
            SearchResultItem current = null;

            int idx;
            while ((idx = content.IndexOf(term, searchPos, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                token.ThrowIfCancellationRequested();

                for (; scanPos < idx; ++scanPos)
                    if (content[scanPos] == '\n')
                        ++lineNumber;

                int lineStart = LineStart(content, idx);
                if (lineStart != lastEmittedLineStart)
                {
                    if (results.Count >= MaxTotalMatches)
                        return;

                    current = new SearchResultItem
                    {
                        FullFileName = doc.FullFileName,
                        DisplayName = doc.DisplayName,
                        LineNumber = lineNumber,
                        LinePreview = LinePreviewAt(content, lineStart),
                        MatchCount = 1,
                    };
                    results.Add(current);
                    lastEmittedLineStart = lineStart;
                }
                else
                {
                    ++current.MatchCount;
                }

                searchPos = idx + term.Length;
            }
        }

        static int LineStart(string content, int idx)
        {
            if (idx <= 0)
                return 0;

            int nl = content.LastIndexOf('\n', idx - 1);
            return nl + 1; // nl == -1 -> lineStart == 0
        }

        static string LinePreviewAt(string content, int lineStart)
        {
            int lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = content.Length;

            string text = content.Substring(lineStart, lineEnd - lineStart).Trim();
            if (text.Length > MaxLinePreview)
                text = text.Substring(0, MaxLinePreview) + "…"; // ellipsis

            return text;
        }

        static string GetDisplayName(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return "";
            try { return Path.GetFileName(filename); }
            catch { return filename; }
        }

        static string ReadAllTextShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        static bool ReadBackupSnapshotEnabled(string nppConfigDir)
        {
            try
            {
                string configFile = Path.Combine(nppConfigDir, "config.xml");
                if (!File.Exists(configFile))
                    return false;

                var doc = new XmlDocument();
                using (var fs = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    doc.Load(fs);

                var backup = doc.SelectSingleNode("/NotepadPlus/GUIConfigs/GUIConfig[@name='Backup']") as XmlElement;
                return backup != null &&
                       string.Equals(backup.GetAttribute("isSnapshotMode"), "yes", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
#endif
                return false;
            }
        }
    }
}
