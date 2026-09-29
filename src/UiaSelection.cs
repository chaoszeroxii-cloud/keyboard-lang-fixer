// ---------------------------------------------------------------------------
//  The UI Automation half of DirectSelection, kept in a class of its own so
//  the UIAutomationClient assemblies load only the first time a browser is
//  actually the target, never at startup.
// ---------------------------------------------------------------------------
using System;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace KbFix
{
    internal static class UiaSelection
    {
        /// Measured against Edge: the first query of a browser session takes
        /// ~160 ms and can report the page rather than the field while the
        /// browser builds its accessibility tree; after that ~2-3 ms. Hence
        /// the rules below -- an empty answer is trusted only from a text
        /// field, because "nothing selected" from a half-built tree would make
        /// a real selection look absent.
        public static bool TryRead(uint pid, out string text)
        {
            text = null;
            AutomationElement el = AutomationElement.FocusedElement;
            if (el == null || el.Current.ProcessId != (int)pid) return false;

            ControlType type = el.Current.ControlType;
            bool field = type == ControlType.Edit;
            if (!field && type != ControlType.Document) return false;

            object pattern;
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return false;
            TextPatternRange[] ranges = ((TextPattern)pattern).GetSelection();
            if (ranges == null || ranges.Length > 1) return false;

            string selected = ranges.Length == 0 ? "" : ranges[0].GetText(DirectSelection.MaxChars + 1);
            if (selected == null || selected.Length > DirectSelection.MaxChars) return false;
            if (selected.Length == 0 && !field) return false;

            text = selected;
            DirectSelection.Via = "UI Automation (" + (field ? "edit" : "document") + ")";
            return true;
        }
    }
}
