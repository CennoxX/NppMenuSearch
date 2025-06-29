﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using NppMenuSearch.Forms;
using NppMenuSearch.Localization;
using NppPluginNET;

namespace NppMenuSearch
{
    class Main
    {
        public static LinkedList<UniqueControlIdx> RecentlyUsedCommands { get; } = new LinkedList<UniqueControlIdx>();
        public static int PreferredToolbarWidth = 0;
        public static bool FixedToolbarWidth = false;
        public static Size PreferredResultsWindowSize = new Size(0, 0);
        public static bool IsClosing { get; private set; }

        internal const string PluginName = "NppMenuSearch";
        static string xmlFilePath = null;

        internal static Localizations Localization;
        internal const string RepeatPreviousCommandLabel = "Repeat previous command"; // Not localized, so that Npp Shortcut mapper can find it.
        
        internal static NppListener NppListener { get; private set; }

        internal static ToolbarSearchForm ToolbarSearchForm { get; private set; }
        private static FlyingSearchForm FlyingSearchForm { get; set; }


        internal static void CommandMenuInit()
        {
#if DEBUG
			Win32.AllocConsole();
			Console.WriteLine(PluginName + " debug mode");
            //MessageBox.Show($"{PluginName}: CommandMenuInit, waiting to attach debugger", PluginName);
            Stopwatch sw = Stopwatch.StartNew();
#endif

            StringBuilder sbXmlFilePath = new StringBuilder(Win32.MAX_PATH);
            Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_GETPLUGINSCONFIGDIR, Win32.MAX_PATH, sbXmlFilePath);
            xmlFilePath = sbXmlFilePath.ToString();
            if (!Directory.Exists(xmlFilePath)) Directory.CreateDirectory(xmlFilePath);
            xmlFilePath = Path.Combine(xmlFilePath, PluginName + ".xml");
            Settings.Load(xmlFilePath);

            PluginBase.SetCommand(0, "Menu Search...", MenuSearchFunction, new ShortcutKey(true, false, false, Keys.F1));
            PluginBase.SetCommand(1, "Clear “Recently Used” List", ClearRecentlyUsedList, new ShortcutKey(false, false, false, Keys.None));
            PluginBase.SetCommand(2, RepeatPreviousCommandLabel, RepeatLastCommandFunction, new ShortcutKey(false, false, false, Keys.None));
            PluginBase.SetCommand(3, "---", null);
            PluginBase.SetCommand(4, "About", AboutFunction, new ShortcutKey(false, false, false, Keys.None));

#if DEBUG
            Console.WriteLine($"{PluginName}:CommandMenuInit took {sw.ElapsedMilliseconds}ms");
#endif
        }

        internal static string GetNativeLangXml()
        {
            // %appdata%\Notepad++\nativeLang.xml

            string result = Path.Combine(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Notepad++"),
                "nativeLang.xml");

            if (File.Exists(result))
                return result;

            StringBuilder sb = new StringBuilder(1024);
            Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_GETNPPDIRECTORY, sb.Capacity, sb);

            string nppDir = sb.ToString();
            result = Path.Combine(nppDir, "nativeLang.xml");
            if (File.Exists(result))
                return result;

            result = Path.Combine(Path.Combine(nppDir, "localization"), "english.xml");
            if (File.Exists(result))
                return result;

            return null;
        }

        internal static IntPtr FindPluginMenuItem(uint commandId, out uint index)
        {
            IntPtr pluginsMenu = Win32.SendMessage(
                PluginBase.nppData._nppHandle,
                NppMsg.NPPM_GETMENUHANDLE,
                (int)NppMsg.NPPPLUGINMENU,
                0);

            index = 0;

            if (pluginsMenu == IntPtr.Zero)
                return IntPtr.Zero;

            for (uint i = 0; i < Win32.GetMenuItemCount(pluginsMenu); ++i)
            {
                IntPtr subMenu = Win32.GetSubMenu(pluginsMenu, i, true);
                if (subMenu == IntPtr.Zero)
                    continue;

                for (uint j = 0; j < Win32.GetMenuItemCount(subMenu); ++j)
                {
                    if (Win32.GetMenuItemId(subMenu, j, true) == commandId)
                    {
                        index = j;
                        return subMenu;
                    }
                }
            }

            return IntPtr.Zero;
        }

        internal static string GetMenuSearchShortcut()
        {
            uint cmdId = (uint)PluginBase._funcItems.Items[0]._cmdID;

            if (cmdId == 0)
                return "";

            uint index;
            IntPtr menu = FindPluginMenuItem(cmdId, out index);

            string text = Win32.GetMenuItemString(menu, index, true);
            if (text == null)
                return "";

            return text.After("\t");
        }

        internal static string GetMenuSearchTitle()
        {
            string title = Localization.Strings.SearchWidgetTitle;
            string shortcut = GetMenuSearchShortcut();
            if (shortcut != "")
                title = $"{title} ({shortcut})";
            return title;
        }

        internal static IntPtr FindRepeatLastCommandMenuItem(out uint cmdId, out uint index)
        {
            cmdId = (uint)PluginBase._funcItems.Items[2]._cmdID;

            if (cmdId == 0)
            {
                index = 0;
                return IntPtr.Zero;
            }

            return FindPluginMenuItem(cmdId, out index);
        }

        internal static MenuItem GetLastUsedMenuItem()
        {
            uint rlcId, rlcIndex;
            IntPtr menu = FindRepeatLastCommandMenuItem(out rlcId, out rlcIndex);

            MenuItem mainMenu = ToolbarSearchForm.ResultsPopup.MainMenu;
            if (!mainMenu.EnumItems().Any())
                mainMenu = new MenuItem(Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_INTERNAL_GETMENU, 0, 0));

            return RecentlyUsedCommands
                .Where(id => id.ControlId != rlcId)
                .Select(id => mainMenu
                    .EnumFinalItems()
                    .Cast<MenuItem>()
                    .Where(item => item.CommandId == id.ControlId)
                    .FirstOrDefault())
                .FirstOrDefault();
        }

        internal static void RecalcRepeatLastCommandMenuItem()
        {
            uint rlcId, rlcIndex;
            IntPtr menu = FindRepeatLastCommandMenuItem(out rlcId, out rlcIndex);

            if (menu == IntPtr.Zero)
                return;

            MenuItem lastUsedItem = GetLastUsedMenuItem();

            Win32.MENUITEMINFO info = new Win32.MENUITEMINFO();
            info.cbSize = Win32.MENUITEMINFO.Size;
            info.fMask = Win32.MIIM_FTYPE;
            Win32.GetMenuItemInfoW(menu, rlcIndex, true, ref info);

            string caption;

            if (lastUsedItem == null)
            {
                Win32.EnableMenuItem(menu, rlcIndex, Win32.MF_BYPOSITION | Win32.MF_DISABLED | Win32.MF_GRAYED);
                caption = Localization.Strings.MenuTitle_RepeatCommand_Previous; //Main.RepeatPreviousCommandLabel;
            }
            else
            {
                Win32.EnableMenuItem(menu, rlcIndex, Win32.MF_BYPOSITION | Win32.MF_ENABLED);
                caption = Localization.Strings.MenuTitle_RepeatCommand_arg.Replace("{0}", lastUsedItem.ToString());
            }

            IntPtr sPtr = Marshal.StringToHGlobalUni(caption);
            info.dwTypeData = sPtr;
            info.fMask = Win32.MIIM_STRING | Win32.MIIM_FTYPE;
            Win32.SetMenuItemInfoW(menu, rlcIndex, true, ref info);

            Marshal.FreeHGlobal(sPtr);
        }

        internal static void PluginReady()
        {
#if DEBUG
            Stopwatch sw = Stopwatch.StartNew();
#endif

            DarkMode.OnChanged();

            NppListener = new NppListener();
            Localization = new Localizations();

            ToolbarSearchForm = new ToolbarSearchForm();
            FlyingSearchForm = null;

            Localization.NativeLangChanged += Localization_NativeLangChanged;
            ToolbarSearchForm.AfterCompleteInit += Localization_NativeLangChanged;

            NppListener.AssignHandle(PluginBase.nppData._nppHandle);

            ToolbarSearchForm.CheckToolbarVisiblity();

#if DEBUG
            Console.WriteLine($"{PluginName}:PluginReady took {sw.ElapsedMilliseconds}ms");
#endif
        }

        private static void Localization_NativeLangChanged(object sender, EventArgs e)
        {
            RecalcRepeatLastCommandMenuItem();
        }

        private static FlyingSearchForm NeedFlyingSearchForm()
        {
            if (FlyingSearchForm == null)
            {
                FlyingSearchForm = new FlyingSearchForm();
                FlyingSearchForm.Disposed += FlyingSearchForm_Disposed;
                MakeNppOwnerOf(FlyingSearchForm);
                FlyingSearchForm.ResultsPopup.Finished += FlyingSearchForm_ResultsPopup_Finished;
            }
            return FlyingSearchForm;
        }

        private static void FlyingSearchForm_Disposed(object sender, EventArgs e)
        {
            FlyingSearchForm = null;
        }

        static void FlyingSearchForm_ResultsPopup_Finished(object sender, EventArgs e)
        {
            FlyingSearchForm?.Hide();
        }

        internal static void PluginCleanUp()
        {
            Settings.Save(xmlFilePath);

            // Woraround/fix for issue #13 (Plugin causes notepad++.exe process to remain open after quitting app):
            // Calling NppListener.ReleaseHandle() was an atempt to clean up resources. However, if other code 
            // (e.g. other plugins) also subclass N++'s main window, then unsubclassing here will probably 
            // restore the wrong window procedure.
            // Instead, NppListener now simply ignores messages. This is OK, because N++ does not actually this DLL.
            ////NppListener.ReleaseHandle();
            IsClosing = true;
        }

        public static IntPtr GetNppMainWindow()
        {
            IntPtr dummy;
            IntPtr thisThread = Win32.GetWindowThreadProcessId(PluginBase.nppData._nppHandle, out dummy);
            IntPtr parent = PluginBase.nppData._nppHandle;
            while (parent != IntPtr.Zero)
            {
                IntPtr grandParent = Win32.GetParent(parent);

                if (Win32.GetWindowThreadProcessId(grandParent, out dummy) != thisThread)
                    break;

                parent = grandParent;
            }

            return parent;
        }

        internal static void MakeNppOwnerOf(Form form)
        {
            Win32.SetWindowLongPtr(form.Handle, Win32.GWL_HWNDPARENT, GetNppMainWindow());
        }

        internal static void MenuSearchFunction()
        {
            if (Win32.IsWindowVisible(ToolbarSearchForm.Handle))
                ToolbarSearchForm.SelectSearchField();
            else
                NeedFlyingSearchForm().SelectSearchField();
        }

        internal static void ClearRecentlyUsedList()
        {
            RecentlyUsedCommands.Clear();
            RecalcRepeatLastCommandMenuItem();
        }

        internal static void RepeatLastCommandFunction()
        {
            MenuItem lastUsedItem = GetLastUsedMenuItem();

            if (lastUsedItem == null)
            {
                Win32.MessageBeep(Win32.BeepType.MB_ICONERROR);
                return;
            }

            Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)lastUsedItem.CommandId, 0);
        }

        internal static void AboutFunction()
        {
            MessageBox.Show(
                string.Format(
                    "Notepad++ Menu Search Plugin, version {0}\r\n" +
                    "by Peter Frentrup",
                    typeof(Main).Assembly.GetName().Version),
                "NppMenuSearch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}