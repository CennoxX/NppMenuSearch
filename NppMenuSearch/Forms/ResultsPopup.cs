﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NppPluginNET;
using System.Xml;

namespace NppMenuSearch.Forms
{
	public partial class ResultsPopup : Form
	{
		const int DefaultMaxMenuResults 	   = 15;
		const int DefaultMaxPreferencesResults = 10;
		const int BlinkRepeat 				   = 4;

		int MaxMenuResults 		  = DefaultMaxMenuResults;
		int MaxPreferencesResults = DefaultMaxPreferencesResults;

		public TextBox OwnerTextBox;
		public MenuItem MainMenu;
		public DialogItem PreferenceDialog;

		ListViewGroup resultGroupMenu 		 = new ListViewGroup("Menu", 		HorizontalAlignment.Left);
		ListViewGroup resultGroupPreferences = new ListViewGroup("Preferences", HorizontalAlignment.Left);

		public ResultsPopup()
		{
			InitializeComponent();
			MainMenu = new MenuItem(IntPtr.Zero);

			viewResults.Groups.Add(resultGroupMenu);
			viewResults.Groups.Add(resultGroupPreferences);

			Main.MakeNppOwnerOf(this);
			try
			{
				XmlDocument doc = new XmlDocument();
				doc.Load(Main.GetNativeLangXml());

				XmlElement preferenceDialogXml = (XmlElement)doc.SelectSingleNode("/NotepadPlus/Native-Langue/Dialog/Preference");
				PreferenceDialog = new DialogItem(preferenceDialogXml);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
			}
		}

		protected override void WndProc(ref Message m)
		{
			switch (m.Msg)
			{
				case Win32.WM_ACTIVATEAPP:
					if (m.WParam == IntPtr.Zero)
						Hide();
					break;
			}

			base.WndProc(ref m);
		}

		protected override bool ShowWithoutActivation
		{
			get
			{
				return true;
			}
		}

		public void ShowMoreResults()
		{
			MaxMenuResults 		  = int.MaxValue;
			MaxPreferencesResults = int.MaxValue;
			panInfo.Visible 	  = false;
			RebuildResultsList();
		}

		private void ResultsPopup_VisibleChanged(object sender, EventArgs e)
		{
			if (Visible)
			{
				MaxMenuResults 		  = DefaultMaxMenuResults;
				MaxPreferencesResults = DefaultMaxPreferencesResults;
				panInfo.Visible 	  = true;

				MainMenu = new MenuItem(Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_INTERNAL_GETMENU, 0, 0));

				if (OwnerTextBox != null)
				{
					OwnerTextBox.TextChanged += OwnerTextBox_TextChanged;
					OwnerTextBox.KeyDown += OwnerTextBox_KeyDown;
				}
				RebuildResultsList();
			}
			else
			{
				if (OwnerTextBox != null)
				{
					OwnerTextBox.TextChanged -= OwnerTextBox_TextChanged;
					OwnerTextBox.KeyDown -= OwnerTextBox_KeyDown;
				}
			}
		}

		void OwnerTextBox_KeyDown(object sender, KeyEventArgs e)
		{
			switch (e.KeyCode)
			{
				case Keys.Down:
					e.Handled = true;
					if (viewResults.Items.Count > 0)
					{
						if (viewResults.SelectedItems.Count == 0)
							viewResults.Items[0].Selected = true;
						else if(viewResults.SelectedIndices[0] + 1 < viewResults.Items.Count)
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

				case Keys.Escape:
					e.Handled = true;
					Hide();
					break;

				case Keys.Enter:
					e.Handled = true;
					ItemSelected();
					break;
			}
		}

		void RebuildResultsList()
		{
			var words = OwnerTextBox.Text.SplitAt(' ');

			MenuItem[] menuItems = MainMenu
				.EnumFinalItems()
				.Select(item => new KeyValuePair<double, HierarchyItem>(item.MatchingSimilarity(words), item))
				.Where(kv => kv.Key > 0.0)
				.OrderByDescending(kv => kv.Key)
				.Take(MaxMenuResults)
				.Select(kv => (MenuItem)kv.Value)
				.ToArray();

			DialogItem[] prefDialogItems = PreferenceDialog
				.EnumFinalItems()
				.Select(item => new KeyValuePair<double, HierarchyItem>(item.MatchingSimilarity(words), item))
				.Where(kv => kv.Key > 0.0)
				.OrderByDescending(kv => kv.Key)
				.Take(MaxPreferencesResults)
				.Select(kv => (DialogItem)kv.Value)
				.ToArray();

			viewResults.Items.Clear();

			for (int i = 0; i < menuItems.Length; ++i)
			{
				ListViewItem item = new ListViewItem();
				item.Tag = menuItems[i];
				item.Text = menuItems[i] + "";
				item.Group = resultGroupMenu;
				viewResults.Items.Add(item);
			}

			if (menuItems.Length == MaxMenuResults)
			{
				viewResults.Items[viewResults.Items.Count - 1].Tag = null;
				viewResults.Items[viewResults.Items.Count - 1].Text = "...";
			}

			for (int i = 0; i < prefDialogItems.Length; ++i)
			{
				ListViewItem item = new ListViewItem();
				item.Tag = prefDialogItems[i];
				item.Text = prefDialogItems[i] + "";
				item.Group = resultGroupPreferences;
				viewResults.Items.Add(item);
			}

			if (prefDialogItems.Length == MaxPreferencesResults)
			{
				viewResults.Items[viewResults.Items.Count - 1].Tag = null;
				viewResults.Items[viewResults.Items.Count - 1].Text = "...";
			}

			if (viewResults.Items.Count > 0)
				viewResults.Items[0].Selected = true;
		}

		void OwnerTextBox_TextChanged(object sender, EventArgs e)
		{
			MaxMenuResults 		  = DefaultMaxMenuResults;
			MaxPreferencesResults = DefaultMaxPreferencesResults;
			panInfo.Visible 	  = true;

			RebuildResultsList();
		}

		void ItemSelected()
		{
			if (viewResults.SelectedItems.Count == 0)
				return;

			MenuItem menuItem = viewResults.SelectedItems[0].Tag as MenuItem;
			if (menuItem != null)
			{
				//Console.WriteLine("Selected {0}", item.CommandId);
				Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)menuItem.CommandId, 0);
				Hide();
				if (OwnerTextBox != null)
					OwnerTextBox.Text = "";

				Win32.SetFocus(PluginBase.GetCurrentScintilla());
				return;
			}

			DialogItem dialogItem = viewResults.SelectedItems[0].Tag as DialogItem;
			if (dialogItem != null)
			{
				OpenPreferences(dialogItem.ControlId);
				Hide();
				if (OwnerTextBox != null)
					OwnerTextBox.Text = "";

				return;
			}
		}

		private void lstResults_Click(object sender, EventArgs e)
		{
			ItemSelected();
		}



		static void ChangeTabPage(IntPtr hwndDialog, IntPtr hwndTabControl, int index)
		{
			Win32.NMHDR nmhdr = new Win32.NMHDR();
			nmhdr.hwndFrom = hwndTabControl;
			nmhdr.idFrom = (uint)Win32.GetDlgCtrlID(hwndTabControl);

			// does not send a TCN_SELCHANGING or TCN_SELCHANGE notification code:
			Win32.SendMessage(hwndTabControl, (NppMsg)Win32.TCM_SETCURSEL, index, 0);

			nmhdr.code = unchecked((uint)Win32.TCN_SELCHANGE);
			Win32.SendMessage(hwndDialog, Win32.WM_NOTIFY, (int)nmhdr.idFrom, ref nmhdr);
		}

		// does not work with nested/multiple tab controls!
		static void NavigateToChild(IntPtr hwndForm, IntPtr hwndChild)
		{
			if (Win32.IsWindowVisible(hwndChild))
				return;

			IntPtr hwndTab = IntPtr.Zero;
			Win32.EnumChildWindows(hwndForm, hwndFormChild =>
			{
				StringBuilder sb = new StringBuilder(256);
				Win32.GetClassName(hwndFormChild, sb, sb.Capacity);

				if (sb.ToString() == "SysTabControl32")
				{
					hwndTab = hwndFormChild;
					return false;
				}
				return true;
			});

			Console.WriteLine("tab control = {0:x}", (int)hwndTab);

			if (hwndTab == IntPtr.Zero)
				return;

			int count = (int)Win32.SendMessage(hwndTab, (NppMsg)Win32.TCM_GETITEMCOUNT, 0, 0);
			int sel = (int)Win32.SendMessage(hwndTab, (NppMsg)Win32.TCM_GETCURSEL, 0, 0);

			for (int i = 0; i < count; ++i)
			{
				ChangeTabPage(hwndForm, hwndTab, i);

				if (Win32.IsWindowVisible(hwndChild))
					return;
			}

			Win32.SendMessage(hwndTab, (NppMsg)Win32.TCM_SETCURSEL, sel, 0);
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
		static IntPtr FindDialogWithControl(uint controlId, out IntPtr hwndControl)
		{
			IntPtr form = Win32.GetForegroundWindow();

			hwndControl = IntPtr.Zero;
			if (controlId == 0)
				return form;

			IntPtr control = IntPtr.Zero;
			Predicate<IntPtr> callback = hwndChild =>
			{
				if (Win32.GetDlgCtrlID(hwndChild) == controlId)
				{
					control = hwndChild;
					hwndPreferences = form;
					return false;
				}
				return true;
			};

			Win32.EnumChildWindows(form, callback);
			if (control == IntPtr.Zero && hwndPreferences != IntPtr.Zero)
			{
				form = hwndPreferences;
				Win32.EnumChildWindows(form, callback);
			}

			hwndControl = control;
			return hwndPreferences;
		}

		public void OpenPreferences(uint destinationControlId)
		{
			/* WM_TIMER messages have the lowest priority, so the following EventHandler will be called 
			 * (immediately) after the Preferences Dialog is shown [becuase we use a tick count of 1ms]
			 * 
			 * This does not work when the Preferences window is already visible, because it wont be 
			 * activated by Notepad++
			 */
			EventHandler tick = null;
			tick = (s, ev) =>
			{
				timerIdle.Stop();
				timerIdle.Tick -= tick;

				IntPtr hwndDestinationControl;
				IntPtr hwndPreferences = FindDialogWithControl(destinationControlId, out hwndDestinationControl);

				Console.WriteLine("preference window = {0:x}", (int)hwndPreferences);
				Console.WriteLine("destination control = {0:x}, visibility = {1}",
					(int)hwndDestinationControl,
					hwndDestinationControl != IntPtr.Zero && Win32.IsWindowVisible(hwndDestinationControl));

				if (hwndDestinationControl != IntPtr.Zero)
				{
					NavigateToChild(hwndPreferences, hwndDestinationControl);
					if (Win32.IsWindowVisible(hwndDestinationControl))
						Highlight(hwndDestinationControl);
				}
			};

			timerIdle.Tick += tick;

			timerIdle.Start();
			Win32.SendMessage(PluginBase.nppData._nppHandle, (NppMsg)Win32.WM_COMMAND, (int)NppMenuCmd.IDM_SETTING_PREFERECE, 0);
		}

		private void viewResults_Resize(object sender, EventArgs e)
		{
			viewResults.TileSize = new Size(viewResults.ClientSize.Width - 20, viewResults.TileSize.Height);
		}

		private void viewResults_Click(object sender, EventArgs e)
		{
			ItemSelected();
		}

		private void viewResults_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
		{
			// Work around bug that causes artefacts.

			Rectangle bounds = e.Item.Bounds;
			bounds.Inflate(1, 1);
			Console.WriteLine(bounds);
			e.Item.ListView.Invalidate(bounds);
		}
	}
}
