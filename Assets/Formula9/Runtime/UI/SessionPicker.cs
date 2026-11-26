using System;
using System.Collections.Generic;
using Formula9.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // The session list in the setup panel: a field that opens the sessions as a list, each with a
    // trash icon beside its name. Unity's DropdownField menu takes only text, so this draws its own.
    // Picking a row raises Picked; a trash icon raises DeleteRequested, and is greyed out for a session
    // that may not be deleted. The list opens over the app (the element with class "app"), so it
    // floats over the map below and the app's stylesheet reaches it; a click anywhere else closes it.
    [UxmlElement]
    public sealed partial class SessionPicker : VisualElement
    {
        private readonly Button field;
        private readonly Label shown;
        private readonly ScrollView list = new ScrollView(ScrollViewMode.Vertical);
        private readonly VisualElement overlay = new VisualElement();
        private List<string> labels = new List<string>();
        private Func<int, bool> canDelete = _ => false;
        private string placeholder = "";

        // The row picked or shown, or -1 while the placeholder shows
        public int Index { get; private set; } = -1;
        public event Action<int> Picked;
        public event Action<int> DeleteRequested;

        public SessionPicker()
        {
            AddToClassList("session-picker");
            field = new Button(Toggle);
            field.AddToClassList("session-picker-field");
            shown = new Label { pickingMode = PickingMode.Ignore };
            shown.AddToClassList("session-picker-text");
            field.Add(shown);
            var arrow = new Label("▾") { pickingMode = PickingMode.Ignore };
            arrow.AddToClassList("session-picker-arrow");
            field.Add(arrow);
            Add(field);
            overlay.AddToClassList("session-picker-overlay");
            // Only a click outside the list lands on the overlay itself
            overlay.RegisterCallback<PointerDownEvent>(e => { if (e.target == overlay) Close(); });
            list.AddToClassList("session-picker-list");
            overlay.Add(list);
            RegisterCallback<DetachFromPanelEvent>(_ => Close());
        }

        // -----------------------------------------------------------------------------------
        // Function: SetChoices
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function replaces the sessions listed, and what decides each one's trash icon.
        //     The open list, if any, is redrawn.
        //
        // Args:
        //     choices (IEnumerable<string>): Each session's label, in list order.
        //     deletable (Func<int, bool>): Whether the session at an index may be deleted now.
        public void SetChoices(IEnumerable<string> choices, Func<int, bool> deletable)
        {
            labels = new List<string>(choices);
            canDelete = deletable ?? (_ => false);
            if (Index >= labels.Count) Index = -1;
            Show();
            if (overlay.parent != null) BuildRows();
        }

        // -----------------------------------------------------------------------------------
        // Function: Select
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows a session as picked, or the placeholder, without raising Picked.
        //
        // Args:
        //     index (int): The session's index, or -1 for none.
        //     whenNone (string): The text shown while none is picked.
        public void Select(int index, string whenNone)
        {
            placeholder = whenNone ?? "";
            Index = index >= 0 && index < labels.Count ? index : -1;
            Show();
        }

        private void Show() => shown.text = Index >= 0 ? labels[Index] : placeholder;

        private void Toggle()
        {
            if (overlay.parent != null) Close(); else Open();
        }

        private void Open()
        {
            var host = parent;
            while (host != null && !host.ClassListContains("app")) host = host.parent;
            host ??= panel?.visualTree;
            if (host == null || labels.Count == 0) return;
            BuildRows();
            // Under the field, as wide as it, and no lower than the bottom of the app
            var bounds = field.worldBound;
            var at = host.WorldToLocal(new Vector2(bounds.xMin, bounds.yMax));
            list.style.left = at.x;
            list.style.top = at.y + 2;
            list.style.width = bounds.width;
            list.style.maxHeight = Mathf.Max(120, host.layout.height - at.y - 12);
            host.Add(overlay);
            overlay.BringToFront();
        }

        private void Close() => overlay.RemoveFromHierarchy();

        // One row per session: its name, which picks it, and a trash icon, which asks to delete it
        private void BuildRows()
        {
            list.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var at = i;
                var row = new VisualElement();
                row.AddToClassList("session-picker-row");
                var option = new Button(() => { Close(); Index = at; Show(); Picked?.Invoke(at); }) { text = labels[i] };
                option.AddToClassList("session-picker-option");
                option.EnableInClassList("session-picker-selected", i == Index);
                row.Add(option);
                var delete = new Button(() => { Close(); DeleteRequested?.Invoke(at); });
                delete.AddToClassList("session-icon-button");
                delete.tooltip = "Delete this session permanently";
                delete.Add(new Glyph(GlyphKind.Trash, new Color(0.69f, 0.2f, 0.2f), 16));
                delete.SetEnabled(canDelete(i));
                HoverTip.Attach(delete);
                row.Add(delete);
                list.Add(row);
            }
        }
    }
}
