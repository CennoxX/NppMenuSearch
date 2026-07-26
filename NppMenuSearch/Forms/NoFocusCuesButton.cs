using System.Windows.Forms;

namespace NppMenuSearch.Forms
{
    /// <summary>
    /// A <see cref="Button"/> that does not take the focus when clicked, so it never paints a
    /// focus rectangle. Clicking it still raises <see cref="Control.Click"/> as usual; the focus
    /// simply stays in the search box.
    /// </summary>
    /// <remarks>
    /// A subclass is needed because <see cref="Control.SetStyle"/> is protected. TabStop=false
    /// would be settable in the designer, but does not help: the button still takes the focus when
    /// clicked with the mouse, and then draws the focus rectangle once opening the options menu has
    /// switched Windows into "show keyboard cues" mode.
    /// </remarks>
    class NoFocusCuesButton : Button
    {
        public NoFocusCuesButton()
        {
            SetStyle(ControlStyles.Selectable, false);
        }
    }
}
