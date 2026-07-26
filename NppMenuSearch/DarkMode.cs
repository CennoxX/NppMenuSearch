using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using NppPluginNET;

namespace NppMenuSearch
{
    static class DarkMode
    {
        public static event Action Changed;
        public static bool Enabled { get; private set; }
        public static Color[] DarkModeColors { get; private set; }

        private static Color GetDarkModeColor(NppDarkModeColorIndex index, Color fallback)
        {
            if (DarkModeColors.Length > (int)index)
                return DarkModeColors[(int)index];

            return fallback;
        }
        public static Color TextBackColor { get { return Enabled ? GetDarkModeColor(NppDarkModeColorIndex.SofterBackground, SystemColors.Window) : SystemColors.Window; } }
        public static Color TextForeColor { get { return Enabled ? GetDarkModeColor(NppDarkModeColorIndex.Text, SystemColors.WindowText) : SystemColors.WindowText; } }
        public static Color ControlBackColor { get { return Enabled ? GetDarkModeColor(NppDarkModeColorIndex.Background, SystemColors.Control) : SystemColors.Control; } }
        public static Color ControlForeColor { get { return Enabled ? GetDarkModeColor(NppDarkModeColorIndex.Text, SystemColors.ControlText) : SystemColors.ControlText; } }

        public static Color SelectedItemBackColor { get { return Enabled ? Color.DodgerBlue : Color.LightGray; } }
        public static Color SelectedItemForeColor { get { return Enabled ? Color.White : Color.Black; } }

        // Used for the ListView group-header caption and its separator line (see
        // ResultsPopup.HandleGroupHeaderCustomDraw). DarkerText is Notepad++'s own dimmed text
        // colour (0xC0C0C0 in the default dark theme) as opposed to the full-brightness item text
        // (0xE0E0E0), so headers read as clearly secondary to the result rows without being hard to
        // read.
        public static Color GroupHeaderColor { get { return Enabled ? GetDarkModeColor(NppDarkModeColorIndex.DarkerText, SystemColors.GrayText) : SystemColors.GrayText; } }

        public static Bitmap GearIcon { get { return Enabled ? Properties.Resources.Gear_DarkMode : Properties.Resources.Gear; } }
        public static Bitmap SelectedGearIcon { get { return Properties.Resources.Gear; } }

        public static Bitmap ClearNormalIcon { get { return Enabled ? Properties.Resources.ClearNormal_DarkMode : Properties.Resources.ClearNormal; } }
        public static Bitmap ClearPressedIcon { get { return Enabled ? Properties.Resources.ClearPressed_DarkMode : Properties.Resources.ClearPressed; } }

        internal static void OnChanged()
        {
            Enabled = IntPtr.Zero != Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_ISDARKMODEENABLED, 0, 0);
            DarkModeColors = GetDarkModeColors();

#if DEBUG
            Console.WriteLine($"DarkMode.Enabled = {DarkMode.Enabled}");
            for (int i = 0; i < DarkMode.DarkModeColors.Length; ++i)
                Console.WriteLine($"  DarkModeColors[{(NppDarkModeColorIndex)i}] = {DarkMode.DarkModeColors[i]}");
#endif

            Changed?.Invoke();
        }

        public static void ApplyThemeRecursive(Control control)
        {
            ApplyTheme(control);
            foreach (Control child in control.Controls)
                ApplyThemeRecursive(child);
        }

        //static bool Has_AllowDarkModeForWindow = true;

        public static void ApplyTheme(Control control)
        {
            // // Does not help:
            // NppDarkModeFlags flags = NppDarkModeFlags.SetThemeDirectly | NppDarkModeFlags.SetTitleBar;// | NppDarkModeFlags.SetThemeChildren;
            // Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_DARKMODESUBCLASSANDTHEME, (int)flags, control.Handle);

            //// Does not help
            //if (Has_AllowDarkModeForWindow)
            //{
            //    try
            //    {
            //        _AllowDarkModeForWindow(control.Handle, Enabled);
            //    }
            //    catch (MissingMethodException ex)
            //    {
            //#if DEBUG
            //         Console.WriteLine(ex);
            //#endif
            //        Has_AllowDarkModeForWindow = false;
            //    }
            //}
            //
            //Win32.SendMessage(control.Handle, Win32.WM_THEMECHANGED, 0, 0);
            
            ApplyCustomColors(control);
        }

        private static void ApplyCustomColors(Control control)
        {
            if (control is TextBoxBase)
            {
                // Note that the Cue banner color can not be changed (only via SetWindowTheme, but NPPM_DARKMODESUBCLASSANDTHEME does not work for that?)
                control.BackColor = ControlBackColor;
                control.ForeColor = TextForeColor;
            }
            else if (control is ListView)
            {
                control.BackColor = TextBackColor;
                control.ForeColor = TextForeColor;

                // The group headers are drawn by the theme engine from theme data, so neither
                // ForeColor nor NM_CUSTOMDRAW can recolor them. "DarkMode_ItemsView" is the
                // (undocumented) theme dark-mode Explorer uses for its list views; it renders the
                // group-header text in a light colour. Works because Notepad++ enables dark app
                // mode for the whole process when its dark mode is on.
                if (control.IsHandleCreated)
                    SetWindowTheme(control.Handle, Enabled ? "DarkMode_ItemsView" : null, null);
            }
            else if (control is Form form)
            {
                form.BackColor = ControlBackColor;
            }
            else
            {
                control.BackColor = ControlBackColor;
                control.ForeColor = ControlForeColor;
            }
        }

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        private extern static bool _AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private extern static int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);


        static readonly Color[] NoColors = new Color[0];
        static Color[] GetDarkModeColors()
        {
            uint[] colorCodes = new uint[30]; // 30 should be enough for the next couple of versions
            for (int count = 5; count < colorCodes.Length; ++count)
            {
                var success = Win32.SendMessage(PluginBase.nppData._nppHandle, NppMsg.NPPM_GETDARKMODECOLORS, count * 4, colorCodes);
                if (success != IntPtr.Zero)
                {
                    var colors = new Color[count];
                    for (int i = 0; i < count; ++i)
                    {
                        uint bgr = colorCodes[i];
                        uint red = bgr & 0xFF;
                        uint green = (bgr >> 8) & 0xFF;
                        uint blue = (bgr >> 16) & 0xFF;
                        colors[i] = Color.FromArgb((int)red, (int)green, (int)blue);
                    }

                    return colors;
                }
            }

            return NoColors;
        }

    }
}
