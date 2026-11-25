using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // Shows an element's tooltip in the running app. UI Toolkit draws tooltips only in Editor windows,
    // so in a player (or the Game view) a tooltip set on an element never appears; this shows it as a
    // small dark label under the element once the pointer has rested on it, and hides it when the
    // pointer leaves or presses. The label is placed over the app (the element with class "app"), so
    // nothing clips it, and kept inside the app's right edge.
    public static class HoverTip
    {
        private const long DelayMs = 400;
        private const float Gap = 6, EdgeMargin = 8;

        // -----------------------------------------------------------------------------------
        // Function: Attach
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes an element show its tooltip on hover. The text is read when the
        //     tip opens, so a tooltip changed later shows as it is then; an empty one shows nothing.
        //
        // Args:
        //     target (VisualElement): The element whose tooltip to show.
        public static void Attach(VisualElement target)
        {
            Label tip = null;
            IVisualElementScheduledItem pending = null;
            void Hide()
            {
                pending?.Pause();
                pending = null;
                tip?.RemoveFromHierarchy();
            }
            void Show()
            {
                pending = null;
                var host = AppRoot(target);
                if (host == null || string.IsNullOrEmpty(target.tooltip)) return;
                if (tip == null)
                {
                    tip = new Label { pickingMode = PickingMode.Ignore };
                    tip.AddToClassList("hover-tip");
                    // Laid out only once it is added: then it is moved in if it runs past the app's right edge
                    tip.RegisterCallback<GeometryChangedEvent>(_ =>
                    {
                        var room = tip.parent == null ? float.NaN : tip.parent.layout.width - EdgeMargin;
                        if (!float.IsNaN(room) && tip.layout.xMax > room) tip.style.left = Mathf.Max(EdgeMargin, room - tip.layout.width);
                    });
                }
                tip.text = target.tooltip;
                var bounds = target.worldBound;
                var at = host.WorldToLocal(new Vector2(bounds.xMin, bounds.yMax + Gap));
                tip.style.left = at.x;
                tip.style.top = at.y;
                host.Add(tip);
                tip.BringToFront();
            }
            target.RegisterCallback<PointerEnterEvent>(_ =>
            {
                Hide();
                if (!string.IsNullOrEmpty(target.tooltip)) pending = target.schedule.Execute(Show).StartingIn(DelayMs);
            });
            target.RegisterCallback<PointerLeaveEvent>(_ => Hide());
            target.RegisterCallback<PointerDownEvent>(_ => Hide(), TrickleDown.TrickleDown);
            target.RegisterCallback<DetachFromPanelEvent>(_ => Hide());
        }

        // The app's root element (class "app"), which the stylesheet styles, or the panel's root
        private static VisualElement AppRoot(VisualElement from)
        {
            var host = from.parent;
            while (host != null && !host.ClassListContains("app")) host = host.parent;
            return host ?? from.panel?.visualTree;
        }
    }
}
