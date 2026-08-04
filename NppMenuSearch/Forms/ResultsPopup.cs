using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using NppPluginNET;

namespace NppMenuSearch.Forms
{
    public partial class ResultsPopup : Form
    {
        const int DefaultMaxMenuResults = 10;
        const int DefaultMaxPreferencesResults = 7;
        const int DefaultMaxSearchResults = 10;
        const int RecentlyUsedListCount = 5;
        const int BlinkRepeat = 4;

        // Only search file contents once the search term is at least this long, to avoid matching
        // (almost) every open file on the first keystrokes.
        const int MinContentSearchLength = 3;

        int MaxMenuResults = DefaultMaxMenuResults;
        int MaxPreferencesResults = DefaultMaxPreferencesResults;
        int MaxSearchResults = DefaultMaxSearchResults;

        public event EventHandler Finished;

        ListViewGroup resultGroupRecentlyUsed = new ListViewGroup("Recently Used",  HorizontalAlignment.Left);
        ListViewGroup resultGroupMenu         = new ListViewGroup("Menu",           HorizontalAlignment.Left);
        ListViewGroup resultGroupPreferences  = new ListViewGroup("Preferences",    HorizontalAlignment.Left);
        ListViewGroup resultGroupTabs         = new ListViewGroup("Open Files",     HorizontalAlignment.Left);
        ListViewGroup resultGroupSearchResults = new ListViewGroup("Search results", HorizontalAlignment.Left);

        readonly FileContentSearcher contentSearcher = new FileContentSearcher(Main.GetNppConfigDir());
        int contentSearchGeneration = 0;

        // The results of the last finished content search. They are kept across keystrokes and only
        // replaced once a new search finishes, so the "Search results" group does not blank out
        // while typing.
        List<SearchResultItem> contentResults = new List<SearchResultItem>();

        // Rebuilding the list means matching the search term against the whole menu and the whole
        // preferences dialog, which takes long enough to be noticeable while typing. This timer
        // defers that work: WM_TIMER has the lowest message priority, so it only fires once the
        // typed characters have been processed and painted. The list may lag one keystroke behind,
        // but the search box never does.
        readonly Timer timerRebuild = new Timer() { Interval = 1 };

        // Native ListView double-buffering (removes the flicker of the owner-drawn list on rebuild).
        const int LVM_FIRST = 0x1000;
        const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        public TextBox OwnerTextBox;
        public MenuItem MainMenu;
        private DialogItem PreferenceDialog;
        private List<TabItem> TabList;

        private int ReloadCounter = 0;

        public ResultsPopup()
        {
            InitializeComponent();

            viewResults.Groups.Add(resultGroupRecentlyUsed);
            viewResults.Groups.Add(resultGroupMenu);
            viewResults.Groups.Add(resultGroupTabs);
            viewResults.Groups.Add(resultGroupSearchResults);
            viewResults.Groups.Add(resultGroupPreferences);

            MainMenu = new MenuItem(IntPtr.Zero);
            PreferenceDialog = new DialogItem("Preferences");
            TabList = new List<TabItem>();

            InitPreferencesDialog();
            UpdateLocalizedStrings();

            Main.Localization.NativeLangChanged += Localization_NativeLangChanged;

            Main.MakeNppOwnerOf(this);
            DarkMode.Changed += DarkMode_Changed;
            DarkMode_Changed();

            viewResults.ContextMenu = popupMenu;

            timerRebuild.Tick += timerRebuild_Tick;
            components.Add(timerRebuild);

            EnableDoubleBuffering(viewResults);

            // (Re)apply the dark-mode window theme whenever the handle is (re)created — the theme
            // set via SetWindowTheme does not survive a handle recreation.
            viewResults.HandleCreated += (sender, e) => DarkMode.ApplyTheme((Control)sender);

            if (Main.PreferredResultsWindowSize.Width > 0 && Main.PreferredResultsWindowSize.Height > 0)
                Size = Main.PreferredResultsWindowSize;
        }

        private static void EnableDoubleBuffering(ListView listView)
        {
            EventHandler apply = (s, e) =>
                Win32.SendMessage(listView.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, LVS_EX_DOUBLEBUFFER, LVS_EX_DOUBLEBUFFER);

            if (listView.IsHandleCreated)
                apply(listView, EventArgs.Empty);

            // Re-apply whenever the handle is (re)created, e.g. after a dark-mode theme change.
            listView.HandleCreated += apply;
        }

        private void Localization_NativeLangChanged(object sender, EventArgs e)
        {
            InitPreferencesDialog();
            UpdateLocalizedStrings();
        }

        private void UpdateLocalizedStrings()
        {
            resultGroupRecentlyUsed.Header = Main.Localization.Strings.GroupTitle_RecentlyUsed;
            resultGroupMenu.Header         = Main.Localization.Strings.GroupTitle_Menu;
            resultGroupPreferences.Header  = Main.Localization.Strings.GroupTitle_Preferences;
            resultGroupTabs.Header         = Main.Localization.Strings.GroupTitle_OpenFiles;
            resultGroupSearchResults.Header = Main.Localization.Strings.GroupTitle_SearchResults;

            menuGotoShortcutDefinition.Text = Main.Localization.Strings.MenuTitle_ChangeShortcut;
            menuExecute.Text                = Main.Localization.Strings.MenuTitle_Execute;
            menuOpenDialog.Text             = Main.Localization.Strings.MenuTitle_OpenDialog;
            menuSelectTab.Text              = Main.Localization.Strings.MenuTitle_SelectTab;

            // Help text label is updated when visibility changes.
        }

        private void DarkMode_Changed()
        {
            DarkMode.ApplyThemeRecursive(this);
        }

        protected void InitPreferencesDialog()
        {
#if DEBUG
            Stopwatch sw = Stopwatch.StartNew();
            int stepIndex = 0;
#endif
            var hwndDummyDialogParent = Handle;

            int myCounter = ++ReloadCounter;

            //PreferenceDialog = LoadPreferenceDialogSteps(hwndDummyDialogParent);
            var loadStepEnumerator = LoadPreferenceDialogSteps(hwndDummyDialogParent).GetEnumerator();
            EventHandler handleStep = null;
            handleStep = (object sender, EventArgs e) =>
            {
#if DEBUG
                Stopwatch swStep = Stopwatch.StartNew();
#endif

                Timer sendingTimer = (Timer)sender;
                sendingTimer.Stop();
                sendingTimer.Tick -= handleStep;

                bool done = false;
                if(myCounter != ReloadCounter)
                {
                    done = true;
#if DEBUG
                    Console.WriteLine($"InitPreferencesDialog [{myCounter}]: cancelled at step {stepIndex} due to a newer InitPreferencesDialog request");
#endif
                }
                else if (loadStepEnumerator.MoveNext())
                {
                    var current = loadStepEnumerator.Current;
                    if (current.Key == LoadStep.Finished)
                    {
#if DEBUG
                        Console.WriteLine($"InitPreferencesDialog [{myCounter}]: final deferred step {stepIndex}");
#endif
                        PreferenceDialog = current.Value;
                    }

#if DEBUG
                    Console.WriteLine($"InitPreferencesDialog [{myCounter}]: deferred step {stepIndex} took {swStep.ElapsedMilliseconds}ms; {sw.ElapsedMilliseconds}ms after begin");
                    ++stepIndex;
#endif

                    sendingTimer.Tick += handleStep;
                    sendingTimer.Start();
                }
                else
                    done = true;

                if(done)
                {

#if DEBUG
                    Console.WriteLine($"InitPreferencesDialog [{myCounter}]: no more deferred steps; {sw.ElapsedMilliseconds}ms after begin");
                    ++stepIndex;
#endif

                    loadStepEnumerator.Dispose();
                    loadStepEnumerator = null;
                }
            };

            timerIdle.Tick += handleStep;
            timerIdle.Start();

#if DEBUG
            Console.WriteLine($"InitPreferencesDialog returns after {sw.ElapsedMilliseconds}ms");
#endif
        }

        enum LoadStep { More, Finished }
        private static IEnumerable<KeyValuePair<LoadStep, DialogItem>> LoadPreferenceDialogSteps(IntPtr hwndDummyDialogParent)
        {
            var ContinueLater = new KeyValuePair<LoadStep, DialogItem>(LoadStep.More, null);

            yield return ContinueLater;

            PreferenceDialogHelper pdh = new PreferenceDialogHelper();
            pdh.LoadCurrentLocalization();

            IntPtr hwndDialogPage;
            DialogItem preferenceDialog = new DialogItem(pdh.PageTranslations[pdh.Global.InternalName]);

            hwndDialogPage = DialogHelper.LoadNppDialog(hwndDummyDialogParent, (int)pdh.Global.ResourceId);
            try
            {
                preferenceDialog = DialogItem.CreateFromDialogFlat(hwndDialogPage, 0, pdh.PageTranslations[pdh.Global.InternalName]);
            }
            finally
            {
                DialogHelper.DestroyWindow(hwndDialogPage);
            }

            foreach (var pageInfo in pdh.GetPages())
            {
                yield return ContinueLater;

                hwndDialogPage = DialogHelper.LoadNppDialog(hwndDummyDialogParent, (int)pageInfo.ResourceId);
                try
                {
                    uint pageIdx = pdh.GetPageIdx(pageInfo.InternalName);
                    DialogItem pageItem = DialogItem.CreateFromDialogFlat(hwndDialogPage, pageIdx, pdh.PageTranslation(pageInfo.InternalName));

                    pageItem.ReorderItemsByGroupBoxes(hwndDialogPage);

                    preferenceDialog.AddItem(pageItem);
                }
                finally
                {
                    DialogHelper.DestroyWindow(hwndDialogPage);
                }
            }

            yield return ContinueLater;

            preferenceDialog.Translate(pdh.ControlTranslations);
            preferenceDialog.RemoveRedundantHeadings();

            yield return new KeyValuePair<LoadStep, DialogItem>(LoadStep.Finished, preferenceDialog);
        }

        protected override void WndProc(ref Message m)
        {
            // Recolor the ListView group headers in dark mode (they are drawn by the OS in a dark,
            // hard-to-read colour that neither BackColor/ForeColor nor owner-draw can influence).
            if (m.Msg == Win32.WM_NOTIFY && HandleGroupHeaderCustomDraw(ref m))
                return;

            switch (m.Msg)
            {
                case Win32.WM_ACTIVATEAPP:
                    if (m.WParam == IntPtr.Zero)
                        Hide();
                    break;

                case Win32.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Win32.MA_ACTIVATE;
                    return;

                case Win32.WM_ACTIVATE:
                    if (((int)m.WParam & 0xFFFF) == Win32.WA_CLICKACTIVE)
                    {
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;
            }

            base.WndProc(ref m);
        }

        // Handles the ListView's NM_CUSTOMDRAW notification just for the group-header stage, giving
        // the headers a readable (light) text colour in dark mode. Everything else falls through so
        // the normal owner-drawing of the result items is untouched.
        private bool HandleGroupHeaderCustomDraw(ref Message m)
        {
            if (!DarkMode.Enabled || m.LParam == IntPtr.Zero)
                return false;

            Win32.NMHDR hdr = (Win32.NMHDR)Marshal.PtrToStructure(m.LParam, typeof(Win32.NMHDR));
            if (hdr.hwndFrom != viewResults.Handle || hdr.code != Win32.NM_CUSTOMDRAW)
                return false;

            Win32.NMLVCUSTOMDRAW nmlv = (Win32.NMLVCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(Win32.NMLVCUSTOMDRAW));

            // Group headers are reported at CDDS_PREPAINT with dwItemType == LVCDI_GROUP — NOT at
            // CDDS_ITEMPREPAINT (that stage only ever carries LVCDI_ITEM). Result items therefore
            // keep their normal owner-draw path and only the header drawing is taken over here.
            if (nmlv.nmcd.dwDrawStage != Win32.CDDS_PREPAINT || nmlv.dwItemType != Win32.LVCDI_GROUP)
                return false;

            // We draw the header ourselves and skip the default drawing, because merely setting
            // clrText would recolour the caption but leave the theme's separator line untouched.
            int groupId = (int)nmlv.nmcd.dwItemSpec.ToInt64();
            string header = GetGroupHeaderText(groupId) ?? "";

            // rcText is the header row itself; nmcd.rc spans the whole group (header + its items),
            // so only the former may be painted over.
            bool haveHeaderRect = nmlv.rcText.Right > nmlv.rcText.Left && nmlv.rcText.Bottom > nmlv.rcText.Top;
            Rectangle bounds = haveHeaderRect
                ? Rectangle.FromLTRB(nmlv.rcText.Left, nmlv.rcText.Top, nmlv.rcText.Right, nmlv.rcText.Bottom)
                : Rectangle.FromLTRB(nmlv.nmcd.rc.Left, nmlv.nmcd.rc.Top, nmlv.nmcd.rc.Right, nmlv.nmcd.rc.Bottom);

            if (bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            // One solid colour for both the caption and the rule. Earlier this used a translucent
            // pen (alpha blended over DarkMode.TextForeColor) for the rule, but GDI+ does not
            // reliably alpha-blend onto a raw HDC-backed Graphics (as opposed to a memory bitmap):
            // it came out solid near-black instead of a dim line. A plain solid colour sidesteps
            // that entirely.
            Color headerColor = DarkMode.GroupHeaderColor;

            using (Graphics g = Graphics.FromHdc(nmlv.nmcd.hdc))
            {
                TextFormatFlags flags =
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

                // Clear the header row first, so nothing of the default rendering can show through.
                if (haveHeaderRect)
                {
                    using (Brush back = new SolidBrush(DarkMode.TextBackColor))
                        g.FillRectangle(back, bounds);
                }

                // Mimic the themed header layout: small left indent, then the caption, then a
                // horizontal rule filling the rest of the line.
                Rectangle textRect = new Rectangle(bounds.Left + 9, bounds.Top, bounds.Width - 9, bounds.Height);
                TextRenderer.DrawText(g, header, viewResults.Font, textRect, headerColor, flags);

                Size textSize = TextRenderer.MeasureText(g, header, viewResults.Font, new Size(int.MaxValue, int.MaxValue), flags);
                int lineLeft = textRect.Left + textSize.Width + 6;
                int lineRight = bounds.Right - 6;
                if (lineRight > lineLeft)
                {
                    int y = bounds.Top + bounds.Height / 2;
                    using (Pen pen = new Pen(headerColor))
                        g.DrawLine(pen, lineLeft, y, lineRight, y);
                }
            }

            m.Result = (IntPtr)Win32.CDRF_SKIPDEFAULT;
            return true;
        }

        // Fetches a group's header text from the native control by group ID (the ID reported in
        // NMLVCUSTOMDRAW is comctl's, which does not necessarily match the Groups collection index).
        private string GetGroupHeaderText(int groupId)
        {
            const int bufferChars = 512;
            IntPtr buffer = Marshal.AllocHGlobal(bufferChars * sizeof(char));
            try
            {
                Win32.LVGROUP group = new Win32.LVGROUP();
                group.cbSize = Marshal.SizeOf(typeof(Win32.LVGROUP));
                group.mask = Win32.LVGF_HEADER;
                group.pszHeader = buffer;
                group.cchHeader = bufferChars;

                IntPtr result = Win32.SendMessage(viewResults.Handle, Win32.LVM_GETGROUPINFO, groupId, ref group);
                if (result == (IntPtr)(-1))
                    return null;

                return Marshal.PtrToStringUni(group.pszHeader);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ShowMoreResults()
        {
            MaxMenuResults = int.MaxValue;
            MaxPreferencesResults = int.MaxValue;
            MaxSearchResults = int.MaxValue;
            lblHelp.Visible = false;
            RebuildResultsList();
        }

        private void ResultsPopup_VisibleChanged(object sender, EventArgs e)
        {
            if (Visible)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (area.IntersectsWith(Bounds))
                {
                    area.Intersect(Bounds);
                    Bounds = area;
                }

                int toolbarButtonHeight = 1;
                if (Main.ToolbarSearchForm != null && Main.ToolbarSearchForm.HwndToolbar != IntPtr.Zero)
                {
                    IntPtr hImgList = Win32.SendMessage(Main.ToolbarSearchForm.HwndToolbar, Win32.TB_GETIMAGELIST, 0, 0);
                    if (hImgList != IntPtr.Zero)
                    {
                        if (Win32.ImageList_GetIconSize(hImgList, out int cx, out int cy))
                            toolbarButtonHeight = cy;
                    }
                }

                viewResults.TileSize = new Size(
                    viewResults.TileSize.Width,
                    Math.Max(toolbarButtonHeight, (int)(1.2 * viewResults.Font.Height)));

                string helpText = Main.Localization.Strings.SwitchGroupHelpText;
                string shortcut = Main.GetMenuSearchShortcut();
                if (shortcut != "")
                {
                    string shortcutHelp = Main.Localization.Strings.ShortcutHelpText_arg.Replace("{0}", shortcut);
                    if(shortcutHelp.Length > 0)
                    {
                        char lastChar = shortcutHelp[shortcutHelp.Length - 1];
                        if(lastChar <= ' ' || lastChar == '\u2028' /* line separator */ || lastChar == '\u2029' /* paragraph separator */ || lastChar == '\u200B' /* zero width space */)
                            helpText = shortcutHelp + helpText;
                        else
                            helpText = shortcutHelp + " " + helpText;
                    }
                }

                lblHelp.Text = helpText;

                MaxMenuResults = DefaultMaxMenuResults;
                MaxPreferencesResults = DefaultMaxPreferencesResults;
                MaxSearchResults = DefaultMaxSearchResults;
                lblHelp.Visible = true;
                LineBreakHelpText();

                MainMenu = new MenuItem(Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_INTERNAL_GETMENU, 0, 0));

                FillTabList();
                contentSearcher.Refresh(TabList.Select(t => t.FullFileName));

                //NeedPreferencesDialog();

                OwnerTextBox.TextChanged += OwnerTextBox_TextChanged;
                OwnerTextBox.KeyDown += OwnerTextBox_KeyDown;
                OwnerTextBox.LostFocus += OwnerTextBox_LostFocus;
                RebuildResultsList();
            }
            else
            {
                timerRebuild.Stop();
                contentSearcher.Cancel();

                // Forget the previous session's content results so a reopened popup starts fresh.
                contentResults = new List<SearchResultItem>();

                OwnerTextBox.TextChanged -= OwnerTextBox_TextChanged;
                OwnerTextBox.KeyDown -= OwnerTextBox_KeyDown;
                OwnerTextBox.LostFocus -= OwnerTextBox_LostFocus;
            }
        }

        void OwnerTextBox_LostFocus(object sender, EventArgs e)
        {
            // Check only after the current message has been processed: clicking a result briefly
            // activates this popup (so the search box loses the focus) before the focus is handed
            // back to it, and that must not be mistaken for a click somewhere else.
            BeginInvoke((Action)HideIfFocusLeftSearch);
        }

        // Closes the popup when the focus went to some other part of Notepad++ (the editor, a tab,
        // a docked panel, ...). Clicks inside the popup or the search widget itself keep it open.
        void HideIfFocusLeftSearch()
        {
            if (!Visible)
                return;

            IntPtr focus = Win32.GetFocus();
            if (focus != IntPtr.Zero && BelongsToSearchUi(focus))
                return;

            Hide();
        }

        bool BelongsToSearchUi(IntPtr hwnd)
        {
            if (hwnd == Handle || Win32.IsChild(Handle, hwnd))
                return true;

            ToolbarSearchForm toolbar = Main.ToolbarSearchForm;
            if (toolbar != null && toolbar.IsHandleCreated &&
                (hwnd == toolbar.Handle || Win32.IsChild(toolbar.Handle, hwnd)))
            {
                return true;
            }

            return false;
        }

        private void FillTabList()
        {
            TabList = EnumOpenFileTabs(true).Concat(EnumOpenFileTabs(false)).ToList();
        }

        private static IEnumerable<TabItem> EnumOpenFileTabs(bool primaryView)
        {
            int count = Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_GETNBOPENFILES, 0, primaryView ? (int)NppMsg.PRIMARY_VIEW : (int)NppMsg.SECOND_VIEW).ToInt32();

            using (ClikeStringArray nativeStringList = new ClikeStringArray(count, 2 * 1024))
            {
                int listFileCount = Win32.SendMessage(PluginBase.nppData._nppHandle, primaryView ? NppMsg.NPPM_GETOPENFILENAMESPRIMARY : NppMsg.NPPM_GETOPENFILENAMESSECOND, nativeStringList.NativePointer, count).ToInt32();

                List<string> filenameList = nativeStringList.ManagedStringsUnicode;

                for (int i = 0; i < listFileCount; i++)
                {
                    yield return new TabItem()
                    {
                        ViewNumber = primaryView ? (int)NppMsg.MAIN_VIEW : (int)NppMsg.SUB_VIEW,
                        Index = i,
                        FullFileName = filenameList[i]
                    };
                }
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value);
            if (value)
            {
                /* fix for Issue 9: Results window behind Npp until Npp is deactivated and then activated again.
                 * I have no idea, why this happens.
                 * 
                 * We do not use BringToFront() because that tries to activate the window which causes
                 * short flickering.
                 */
                Win32.SetWindowPos(
                    Handle, Win32.HWND_TOP,
                    0, 0, 0, 0,
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
            }
        }

        void OwnerTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // The keys below act on the list, so it must be up to date before they are handled.
            switch (e.KeyCode)
            {
                case Keys.Down:
                case Keys.Up:
                case Keys.Tab:
                case Keys.Enter:
                case Keys.Apps:
                    FlushPendingRebuild();
                    break;
            }

            switch (e.KeyCode)
            {
                case Keys.Down:
                    e.Handled = true;
                    if (viewResults.Items.Count > 0)
                    {
                        if (viewResults.SelectedItems.Count == 0)
                            viewResults.Items[0].Selected = true;
                        else if (viewResults.SelectedIndices[0] + 1 < viewResults.Items.Count)
                            viewResults.Items[viewResults.SelectedIndices[0] + 1].Selected = true;
                        else
                            viewResults.Items[0].Selected = true;

                        viewResults.SelectedItems[0].EnsureVisible();
                    }
                    break;

                case Keys.Up:
                    e.Handled = true;
                    if (viewResults.Items.Count > 0)
                    {
                        if (viewResults.SelectedItems.Count == 0)
                            viewResults.Items[0].Selected = true;
                        else if (viewResults.SelectedIndices[0] > 0)
                            viewResults.Items[viewResults.SelectedIndices[0] - 1].Selected = true;
                        else
                            viewResults.Items[viewResults.Items.Count - 1].Selected = true;

                        viewResults.SelectedItems[0].EnsureVisible();
                    }
                    break;

                case Keys.Tab:
                    e.Handled = true;
                    if (viewResults.Items.Count > 0)
                    {
                        int groupIndex = viewResults.Groups.IndexOf(viewResults.SelectedItems[0].Group);
                        if (e.Shift)
                        {
                            --groupIndex;
                            while (groupIndex >= 0 && viewResults.Groups[groupIndex].Items.Count == 0)
                                --groupIndex;

                            if (groupIndex < 0)
                            {
                                groupIndex = viewResults.Groups.Count - 1;
                                while (groupIndex >= 0 && viewResults.Groups[groupIndex].Items.Count == 0)
                                    --groupIndex;
                            }

                            if (groupIndex >= 0)
                                viewResults.Groups[groupIndex].Items[0].Selected = true;

                            viewResults.SelectedItems[0].EnsureVisible();
                        }
                        else
                        {
                            ++groupIndex;
                            while (groupIndex < viewResults.Groups.Count && viewResults.Groups[groupIndex].Items.Count == 0)
                                ++groupIndex;

                            if (groupIndex >= viewResults.Groups.Count)
                            {
                                groupIndex = 0;
                                while (groupIndex < viewResults.Groups.Count && viewResults.Groups[groupIndex].Items.Count == 0)
                                    ++groupIndex;
                            }

                            if (groupIndex < viewResults.Groups.Count)
                                viewResults.Groups[groupIndex].Items[0].Selected = true;

                            viewResults.SelectedItems[0].EnsureVisible();
                        }
                    }
                    break;

                case Keys.Enter:
                    e.Handled = true;
                    ItemSelected();
                    break;

                case Keys.Apps:
                    if (viewResults.SelectedItems.Count > 0)
                    {
                        var item = viewResults.SelectedItems[0];

                        e.Handled = true;
                        popupMenu.Show(viewResults, new Point(item.Bounds.Right, item.Bounds.Bottom), LeftRightAlignment.Left);
                    }
                    break;
            }
        }

        // One row of the results list, as it should look for the current search term. Comparing
        // these against the rows the ListView already shows lets us leave unchanged rows completely
        // untouched, which is what keeps the list from flickering while typing.
        struct RowSpec
        {
            public readonly object Tag;
            public readonly string Text;
            public readonly string ToolTipText;
            public readonly ListViewGroup Group;

            public RowSpec(object tag, string text, string toolTipText, ListViewGroup group)
            {
                Tag = tag;
                Text = text;
                ToolTipText = toolTipText ?? "";
                Group = group;
            }
        }

        void RebuildResultsList()
        {
            timerRebuild.Stop();

            // Decide about the content search first: when the term is too short it drops the
            // previous results, and UpdateResultsList() below has to see that.
            StartContentSearch();
            UpdateResultsList(resetSelection: true);
        }

        // Runs the rebuild that OwnerTextBox_TextChanged deferred. Reached only once no keystrokes
        // are waiting any more, because WM_TIMER is delivered after all other pending messages.
        void timerRebuild_Tick(object sender, EventArgs e)
        {
            RebuildResultsList();
        }

        // Makes sure the list matches the current search term before it is acted upon (activating an
        // item, navigating with the arrow keys, ...), in case a deferred rebuild is still pending.
        // ENTER can outrun the rebuild timer: key messages are delivered before WM_TIMER.
        public void FlushPendingRebuild()
        {
            if (timerRebuild.Enabled)
                RebuildResultsList();
        }

        // Recomputes what the results list should show and applies only the differences to the
        // ListView. An unchanged list is not touched at all and therefore does not repaint.
        void UpdateResultsList(bool resetSelection)
        {
            var words = OwnerTextBox.Text.SplitAt(' ');

            MenuItem[] menuItems = MainMenu
                .EnumFinalItems()
                .Select(item => new KeyValuePair<double, HierarchyItem>(item.MatchingSimilarity(words), item))
                .Where(kv => kv.Key > 0.0)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => (MenuItem)kv.Value)
                .ToArray();

            DialogItem[] prefDialogItems = PreferenceDialog
                .EnumFinalItems()
                .Select(item => new KeyValuePair<double, HierarchyItem>(item.MatchingSimilarity(words), item))
                .Where(kv => kv.Key > 0.0)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => (DialogItem)kv.Value)
                .ToArray();

            HierarchyItem[] recentlyUsed = Main.RecentlyUsedCommands
                .Select(id =>
                    (HierarchyItem)menuItems.Where(item => item.CommandId == id.ControlId).FirstOrDefault() ??
                    (HierarchyItem)prefDialogItems.Where(item => item.CtrlIdx == id).FirstOrDefault())
                .Where(item => item != null)
                .Take(RecentlyUsedListCount)
                .ToArray();

            List<TabItem> openTabsFiltered = TabList
                .Where(item => item.MatchesSearchTerm(OwnerTextBox.Text))
                .Take(MaxMenuResults)
                .ToList();


            // The rows are collected in the order they are displayed in: the Up/Down keys navigate
            // by Items index, so that order has to match the group order of the ListView.
            List<RowSpec> rows = new List<RowSpec>();

            foreach (var hi in recentlyUsed)
                rows.Add(new RowSpec(hi, ItemText(hi, words), null, resultGroupRecentlyUsed));

            int i = 0;
            foreach (var item in menuItems)
            {
                if (recentlyUsed.Contains(item))
                    continue;

                if (i++ == MaxMenuResults)
                    break;

                rows.Add(new RowSpec(item, ItemText(item, words), null, resultGroupMenu));
            }

            foreach (var item in openTabsFiltered)
                rows.Add(new RowSpec(item, item.ToString(), item.ToolTipText, resultGroupTabs));

            // The rows of the last finished search stay until a new search replaces them.
            i = 0;
            foreach (var item in contentResults)
            {
                if (i++ == MaxSearchResults)
                    break;

                rows.Add(new RowSpec(item, FormatSearchResultText(item), item.ToolTipText, resultGroupSearchResults));
            }

            i = 0;
            foreach (var item in prefDialogItems)
            {
                if (recentlyUsed.Contains(item))
                    continue;

                if (i++ == MaxPreferencesResults)
                    break;

                rows.Add(new RowSpec(item, ItemText(item, words), null, resultGroupPreferences));
            }

            SetGroupHeader(resultGroupTabs,        string.Format("{0} ({1})", Main.Localization.Strings.GroupTitle_OpenFiles,   openTabsFiltered.Count));
            SetGroupHeader(resultGroupMenu,        string.Format("{0} ({1})", Main.Localization.Strings.GroupTitle_Menu,        menuItems.Length - recentlyUsed.Where(hi => hi is MenuItem).Count()));
            SetGroupHeader(resultGroupPreferences, string.Format("{0} ({1})", Main.Localization.Strings.GroupTitle_Preferences, prefDialogItems.Length - recentlyUsed.Where(hi => hi is DialogItem).Count()));

            // The content search only reports a count once it has actually run.
            SetGroupHeader(resultGroupSearchResults, contentResults.Count == 0
                ? Main.Localization.Strings.GroupTitle_SearchResults
                : string.Format("{0} ({1})", Main.Localization.Strings.GroupTitle_SearchResults, contentResults.Count));

            ApplyRows(rows, resetSelection);
        }

        static string ItemText(HierarchyItem item, IEnumerable<string> words)
        {
#if DEBUG
            return string.Format("[{1:0.0000}] {0}", item, item.MatchingSimilarity(words));
#else
            return item.ToString();
#endif
        }

        // Assigning the same header again would repaint it, so only do it when it really changed.
        static void SetGroupHeader(ListViewGroup group, string header)
        {
            if (group.Header != header)
                group.Header = header;
        }

        // Brings the ListView in line with `rows`, changing as little as possible: rows that are
        // already correct are not written to, and only the surplus/missing rows at the end are
        // removed/added. Each write invalidates just the row it affects, so a list that stays the
        // same (or nearly the same) while typing does not visibly redraw.
        //
        // Deliberately without BeginUpdate()/EndUpdate(): EndUpdate() invalidates the whole control,
        // which repaints every row even when a single one changed — exactly the flicker this avoids.
        // Nothing pumps messages in between, so the individual invalidations still end up in one
        // WM_PAINT.
        void ApplyRows(List<RowSpec> rows, bool resetSelection)
        {
            int common = Math.Min(rows.Count, viewResults.Items.Count);
            for (int i = 0; i < common; ++i)
            {
                ListViewItem lvi = viewResults.Items[i];
                RowSpec row = rows[i];

                lvi.Tag = row.Tag;
                if (lvi.Group != row.Group)
                    lvi.Group = row.Group;
                if (lvi.Text != row.Text)
                    lvi.Text = row.Text;
                if (lvi.ToolTipText != row.ToolTipText)
                    lvi.ToolTipText = row.ToolTipText;
            }

            for (int i = viewResults.Items.Count - 1; i >= rows.Count; --i)
                viewResults.Items.RemoveAt(i);

            for (int i = viewResults.Items.Count; i < rows.Count; ++i)
            {
                RowSpec row = rows[i];
                viewResults.Items.Add(new ListViewItem()
                {
                    Tag = row.Tag,
                    Text = row.Text,
                    ToolTipText = row.ToolTipText,
                    Group = row.Group,
                });
            }

            if (viewResults.Items.Count == 0)
                return;

            // Select the first row again after the search term changed, but leave a selection the
            // user made with the arrow keys alone when results merely arrived in the background.
            bool firstIsSelected = viewResults.SelectedIndices.Count == 1 && viewResults.SelectedIndices[0] == 0;
            if (resetSelection ? !firstIsSelected : viewResults.SelectedIndices.Count == 0)
                viewResults.Items[0].Selected = true;
        }

        // Kicks off a background scan of the open files' contents for the current search term. The
        // results arrive asynchronously and replace the "Search results" group. Only runs when
        // Notepad++'s session snapshot/periodic backup is enabled (see FileContentSearcher).
        void StartContentSearch()
        {
            // Bump the generation first so any in-flight result from a previous search is dropped.
            int generation = ++contentSearchGeneration;

            contentSearcher.Cancel();

            string term = OwnerTextBox.Text.Trim();
            if (!contentSearcher.BackupSnapshotEnabled || term.Length < MinContentSearchLength)
            {
                contentResults = new List<SearchResultItem>();
                return;
            }

            contentSearcher.BeginSearch(term, generation, OnContentSearchCompleted);
        }

        // Called on a background thread; marshal back to the UI thread.
        void OnContentSearchCompleted(int generation, List<SearchResultItem> results)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((Action)(() => ShowContentSearchResults(generation, results)));
            }
            catch (Exception)
            {
                // Window handle was destroyed between the checks above and BeginInvoke; ignore.
            }
        }

        void ShowContentSearchResults(int generation, List<SearchResultItem> results)
        {
            // Drop results from a search that has since been superseded by newer typing.
            if (generation != contentSearchGeneration || !Visible)
                return;

            contentResults = results;

            // Only the "Search results" rows can have changed, and the update leaves every other row
            // untouched — so this does not disturb what the user is currently looking at.
            UpdateResultsList(resetSelection: false);
        }

        static string FormatSearchResultText(SearchResultItem r)
        {
            string preview = (r.LinePreview ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (r.MatchCount > 1)
                return string.Format("{0}:{1} ({2}): {3}", r.DisplayName, r.LineNumber, r.MatchCount, preview);
            return string.Format("{0}:{1}: {2}", r.DisplayName, r.LineNumber, preview);
        }

        void OwnerTextBox_TextChanged(object sender, EventArgs e)
        {
            MaxMenuResults = DefaultMaxMenuResults;
            MaxPreferencesResults = DefaultMaxPreferencesResults;
            MaxSearchResults = DefaultMaxSearchResults;
            lblHelp.Visible = true;

            // Don't rebuild right here: that would run between the keystroke and its repaint and
            // make typing feel sluggish. Restarting the timer instead postpones the rebuild until
            // the message queue has run dry, so the character is on screen first and a burst of
            // keystrokes is collapsed into a single rebuild. The list shows the previous term for
            // those few milliseconds, which is far less noticeable than a lagging search box.
            timerRebuild.Stop();
            timerRebuild.Start();
        }

        public void ItemSelected()
        {
            // No FlushPendingRebuild() here: a rebuild resets the selection to the first row, which
            // would discard the row a mouse click just selected. The keyboard paths that can outrun
            // a pending rebuild (OwnerTextBox_KeyDown, ToolbarSearchForm.btnOk_Click) flush before
            // calling us instead.
            if (viewResults.SelectedItems.Count == 0)
                return;

            MenuItem menuItem = viewResults.SelectedItems[0].Tag as MenuItem;
            if (menuItem != null)
            {
                var recentCmd = new UniqueControlIdx(menuItem.CommandId, 0);
                Main.RecentlyUsedCommands.Remove(recentCmd);
                Main.RecentlyUsedCommands.AddFirst(recentCmd);

                //Console.WriteLine("Selected {0}", item.CommandId);
                Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)menuItem.CommandId, 0);
                Hide();
                OwnerTextBox.Text = "";

                if (OwnerTextBox.Focused)
                {
                    Win32.SetFocus(PluginBase.GetCurrentScintilla());
                }

                Main.RecalcRepeatLastCommandMenuItem();
                OnFinished();
                return;
            }

            DialogItem dialogItem = viewResults.SelectedItems[0].Tag as DialogItem;
            if (dialogItem != null)
            {
                var recentCmd = dialogItem.CtrlIdx;
                Main.RecentlyUsedCommands.Remove(recentCmd);
                Main.RecentlyUsedCommands.AddFirst(recentCmd);

                OpenPreferences(dialogItem.CtrlIdx);
                Hide();
                OwnerTextBox.Text = "";

                OnFinished();
                return;
            }

            TabItem tabItem = viewResults.SelectedItems[0].Tag as TabItem;
            if (tabItem != null)
            {
                int viewNumber = tabItem.ViewNumber;
                int index = tabItem.Index;
                Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_ACTIVATEDOC, viewNumber, index);

                Hide();
                OwnerTextBox.Text = "";

                OnFinished();
                return;
            }

            SearchResultItem searchItem = viewResults.SelectedItems[0].Tag as SearchResultItem;
            if (searchItem != null)
            {
                ActivateSearchResult(searchItem);

                Hide();
                OwnerTextBox.Text = "";

                OnFinished();
                return;
            }
        }

        // Activates the matching file and jumps to the first matching line (best effort: the line
        // number comes from the persisted snapshot, which may lag the live buffer by a few seconds).
        private void ActivateSearchResult(SearchResultItem item)
        {
            // Prefer the same mechanism the "Open Files" group uses (NPPM_ACTIVATEDOC by view/index),
            // which reliably switches to the document. Matching by full path also covers unsaved
            // "new N" buffers, whose session filename matches the open-file name.
            TabItem tab = TabList.FirstOrDefault(t =>
                string.Equals(t.FullFileName, item.FullFileName, StringComparison.OrdinalIgnoreCase));

            IntPtr scintilla;
            if (tab != null)
            {
                Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_ACTIVATEDOC, tab.ViewNumber, tab.Index);

                scintilla = (tab.ViewNumber == (int)NppMsg.SUB_VIEW)
                    ? PluginBase.nppData._scintillaSecondHandle
                    : PluginBase.nppData._scintillaMainHandle;
            }
            else
            {
                // Not currently open (e.g. a stale session entry): fall back to switching by path.
                if (string.IsNullOrEmpty(item.FullFileName))
                    return;

                Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_SWITCHTOFILE, 0, item.FullFileName);
                scintilla = PluginBase.GetCurrentScintilla();
            }

            if (item.LineNumber > 0 && scintilla != IntPtr.Zero)
                ScrollToLineDeferred(scintilla, item.LineNumber - 1);
        }

        // Jumping to the line immediately doesn't stick: Notepad++ finishes activating the document
        // (and restores its own scroll position) after we return here, overriding our scroll. So we
        // defer the jump to the next idle tick, once activation has settled and the popup is hidden.
        private void ScrollToLineDeferred(IntPtr scintilla, int line)
        {
            EventHandler tick = null;
            tick = (timer, ev) =>
            {
                ((Timer)timer).Stop();
                ((Timer)timer).Tick -= tick;

                Win32.SendMessage(scintilla, SciMsg.SCI_ENSUREVISIBLEENFORCEPOLICY, line, 0);
                Win32.SendMessage(scintilla, SciMsg.SCI_GOTOLINE, line, 0);
                Win32.SendMessage(scintilla, SciMsg.SCI_SCROLLCARET, 0, 0);
            };

            timerIdle.Tick += tick;
            timerIdle.Start();
        }

        public void OnFinished()
        {
            if (Finished != null)
                Finished(this, new EventArgs());
        }

        public void Highlight(IntPtr hwnd)
        {
            int counter = 2 * BlinkRepeat;

            EventHandler tick = null;
            tick = (sender, e) =>
            {
                if (--counter == 0 || !Win32.IsWindowVisible(hwnd))
                {
                    ((Timer)sender).Stop();
                    ((Timer)sender).Tick -= tick;
                }

                RECT rect;
                Win32.GetClientRect(hwnd, out rect);
                IntPtr hdc = Win32.GetWindowDC(hwnd);
                {
                    Win32.PatBlt(hdc,
                        rect.Left,
                        rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top,
                        Win32.DSTINVERT);
                }
                Win32.ReleaseDC(hwnd, hdc);
            };

            timerBlink.Tick += tick;
            timerBlink.Start();
        }


        static IntPtr hwndPreferences = IntPtr.Zero;
        public static IntPtr FindPreferencesDialog()
        {
            if (hwndPreferences != IntPtr.Zero)
                return hwndPreferences;

            List<IntPtr> hwndClosebutton;
            hwndPreferences = FindDialogByChildControlId(6001, true, out hwndClosebutton);
            return hwndPreferences;
        }

        // Collects all controls from different pages (child dialogs) where the control ID == `controlID`.
        // If `onlyFirst` is `true`, returns the very first control with the given `controlID`.
        public static IntPtr FindDialogByChildControlId(uint controlId, bool onlyFirst, out List<IntPtr> hwndControls)
        {
            IntPtr form = Win32.GetActiveWindow();//Win32.GetForegroundWindow();

            var controls = new List<IntPtr>();

            if (controlId == 0)
            {
                hwndControls = controls;
                return form;
            }

            Predicate<IntPtr> callback = hwndChild =>
            {
                if (Win32.GetDlgCtrlID(hwndChild) == controlId)
                {
                    controls.Add(hwndChild);
                    if (onlyFirst)
                        return false;
                }
                return true;
            };

            Win32.EnumChildWindows(form, callback);

            hwndControls = controls;
            return form;
        }

        public void OpenPreferences(UniqueControlIdx destinationCtrlIdx)
        {
            /* WM_TIMER messages have the lowest priority, so the following EventHandler will be called 
             * (immediately) after the Preferences Dialog is shown [becuase we use a tick count of 1ms]
             * 
             * This does not work when the Preferences window is already visible, because it wont be 
             * activated by Notepad++
             */
            EventHandler tick = null;
            tick = (timer, ev) =>
            {
                ((Timer)timer).Stop();
                ((Timer)timer).Tick -= tick;

                List<IntPtr> hwndDestinationControls;
                IntPtr hwndPreferences = FindDialogByChildControlId(destinationCtrlIdx.ControlId, false, out hwndDestinationControls);

                if (hwndDestinationControls.Count != 0)
                {
                    IntPtr hwndCtrl = DialogHelper.NavigateToChild(hwndPreferences, hwndDestinationControls, destinationCtrlIdx.PageIdx);
                    if (Win32.IsWindowVisible(hwndCtrl))
                    {
                        Win32.SetFocus(hwndCtrl);
                        Highlight(hwndCtrl);
                    }
                }
            };

            timerIdle.Tick += tick;

            timerIdle.Start();
            Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)NppMenuCmd.IDM_SETTING_PREFERECE, 0);
        }

        private void viewResults_Resize(object sender, EventArgs e)
        {
            viewResults.TileSize = new Size(Math.Max(20, viewResults.ClientSize.Width - 20), viewResults.TileSize.Height);
        }

        private void viewResults_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            Color backgroundColor;
            Color foregroundColor;

            if (e.Item.Selected)
            {
                backgroundColor = DarkMode.SelectedItemBackColor;
                foregroundColor = DarkMode.SelectedItemForeColor;
            }
            else
            {
                backgroundColor = DarkMode.TextBackColor;
                foregroundColor = DarkMode.TextForeColor;
            }

            using (Brush background = new SolidBrush(backgroundColor))
            using (Brush foreground = new SolidBrush(foregroundColor))
            {
                Rectangle bounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
                Rectangle textBounds = new Rectangle(bounds.Left + bounds.Height + 4, bounds.Top, bounds.Width - bounds.Height - 4, bounds.Height);

                e.Graphics.FillRectangle(background, bounds);

                StringFormat format = new StringFormat(
                    StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.FitBlackBox);
                format.SetTabStops(20f, new float[] { 20f });
                if (e.Item.Tag is DialogItem)
                {
                    e.Graphics.DrawImage(
                        e.Item.Selected ? DarkMode.SelectedGearIcon : DarkMode.GearIcon,
                        bounds.Left,
                        bounds.Top);
                }
                else if (e.Item.Tag is MenuItem mi)
                {
                    if (mi.NativeIcon != IntPtr.Zero) // todo: and no special icon constant
                    {
                        try
                        {
                            WithNativeIcon(mi.NativeIcon, bmp => e.Graphics.DrawImage(bmp, bounds.Left, bounds.Top));
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            Console.WriteLine(ex);
#endif
                        }
                    }
                    else if (Main.ToolbarSearchForm != null && Main.ToolbarSearchForm.HwndToolbar != IntPtr.Zero)
                    {
                        Win32.TBBUTTONINFO tbi = new Win32.TBBUTTONINFO();
                        tbi.cbSize = Win32.TBBUTTONINFO.Size;
                        tbi.dwMask = Win32.TBIF_IMAGE;
                        IntPtr index = Win32.SendMessage(Main.ToolbarSearchForm.HwndToolbar, Win32.TB_GETBUTTONINFOW, (int)mi.CommandId, ref tbi);
                        if (index != (IntPtr)(-1))
                        {
                            IntPtr hImgList = Win32.SendMessage(Main.ToolbarSearchForm.HwndToolbar, Win32.TB_GETIMAGELIST, 0, 0);
                            IntPtr hdc = e.Graphics.GetHdc();
                            try
                            {
                                Win32.ImageList_Draw(
                                    hImgList, tbi.iImage, hdc,
                                    bounds.Left, bounds.Top,
                                    //bounds.Height, bounds.Height,
                                    //Win32.CLR_NONE, Win32.CLR_NONE,
                                    Win32.ImageListDrawingStyle.Transparent);
                            }
                            finally
                            {
                                e.Graphics.ReleaseHdc(hdc);
                            }
                        }
                    }
                }

                e.Graphics.DrawString(
                    e.Item.Text.Replace('\n', ' ').Replace("\r", ""),
                    e.Item.Font ?? e.Item.ListView.Font,
                    foreground,
                    textBounds.Location,
                    format);
            }
        }

        private static void WithNativeIcon(IntPtr hBitmap, Action<Bitmap> draw)
        {
            using (var bmp = Bitmap.FromHbitmap(hBitmap))
            {
                if (bmp.PixelFormat == PixelFormat.Format32bppRgb)
                {
                    BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                    try
                    {
                        if (UsesAlphaChannel(bmpData))
                        {
                            using (var alphaBmp = new Bitmap(bmpData.Width, bmpData.Height, bmpData.Stride, PixelFormat.Format32bppArgb, bmpData.Scan0))
                            {
                                draw(alphaBmp);
                                return;
                            }
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(bmpData);
                    }
                }

                draw(bmp);
                return;
            }
        }

        private static bool UsesAlphaChannel(BitmapData bmpData)
        {
            for (int y = 0; y <= bmpData.Height - 1; y++)
            {
                for (int x = 0; x <= bmpData.Width - 1; x++)
                {
                    byte alpha = Marshal.ReadByte(bmpData.Scan0, (bmpData.Stride * y) + (4 * x) + 3);
                    if (alpha > 0)
                        return true;
                }
            }
            return false;
        }

        private void viewResults_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ItemSelected();
                return;
            }

            //OwnerTextBox.Focus();
        }

        private void menuGotoShortcutDefinition_Click(object sender, EventArgs e)
        {
            if (viewResults.SelectedItems.Count == 0)
                return;

            MenuItem menuItem = viewResults.SelectedItems[0].Tag as MenuItem;
            if (menuItem != null)
            {
                OpenShortcutMapper(menuItem);

                OnFinished();
                return;
            }

            Win32.MessageBeep(Win32.BeepType.MB_ICONERROR);
        }

        private void OpenShortcutMapper(MenuItem menuItem)
        {
#if DEBUG
            Console.WriteLine("search shortcut for {0} ({1})", menuItem.CommandId, menuItem);
#endif
            Hide();
            OwnerTextBox.Text = "";

            EventHandler tick = null;
            tick = (timer, ev) =>
            {
                ((Timer)timer).Stop();
                ((Timer)timer).Tick -= tick;

                List<IntPtr> hwndGrid;
                IntPtr hwndShortcutMapper = FindDialogByChildControlId(ShortcutMapperUtil.IDD_BABYGRID_ID1, true, out hwndGrid);

                if (hwndShortcutMapper != IntPtr.Zero && hwndGrid.Count != 0)
                {
                    if (ShortcutMapperUtil.GotoGridItem(hwndShortcutMapper, hwndGrid[0], menuItem))
                        return;
                }

                Win32.MessageBeep(Win32.BeepType.MB_ICONERROR);
            };

            timerIdle.Tick += tick;

            timerIdle.Start();
            Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)NppMenuCmd.IDM_SETTING_SHORTCUT_MAPPER, 0);
        }

        private void popupMenu_Popup(object sender, EventArgs e)
        {
            menuGotoShortcutDefinition.Enabled = false;
            menuOpenDialog.Visible = false;
            menuSelectTab.Visible = false;

            if (viewResults.SelectedItems.Count > 0)
            {
                if (viewResults.SelectedItems[0].Tag is MenuItem)
                {
                    menuGotoShortcutDefinition.Enabled = true;
                }

                if (viewResults.SelectedItems[0].Tag is DialogItem)
                {
                    menuOpenDialog.Visible = true;
                }
                else if (viewResults.SelectedItems[0].Tag is TabItem)
                {
                    menuSelectTab.Visible = true;
                }

                menuExecute.Enabled = true;
            }
            else
                menuExecute.Enabled = false;

            menuExecute.Visible = !menuOpenDialog.Visible && !menuSelectTab.Visible;
        }

        private void menuExecute_Click(object sender, EventArgs e)
        {
            ItemSelected();
        }

        private void ResultsPopup_SizeChanged(object sender, EventArgs e)
        {
            if (!Visible)
                return;

            LineBreakHelpText();
            Main.PreferredResultsWindowSize = Size;
        }

        private void LineBreakHelpText()
        {
            lblHelp.MaximumSize = new Size(ClientSize.Width, ClientSize.Height / 4);
        }
    }
}